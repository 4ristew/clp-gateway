using SmartGlue.ClpGateway.Trace;
using System.Net;
using System.Net.Sockets;
using FluentModbus;
using Microsoft.Extensions.Options;
using SmartGlue.ClpGateway.Common.Exceptions;

namespace SmartGlue.ClpGateway.Modbus;

// Singleton owner of the single Modbus TCP socket to the axis hardware (simulator today,
// real Delta PLC later). The kit "golden rule" is exactly one write master per connection,
// so every read/write funnels through here, serialized by _ioGate — including the heartbeat,
// which otherwise could interleave with an in-flight command request/response on the same
// socket.
//
// Signed DWORDs (positions, speeds, limits, heartbeat) are stored as two 16-bit holding
// registers, low-word-first (word0 = low 16 bits, word1 = high 16 bits, two-s complement) —
// see 01_MANUAL_COMUNICACAO_CLP.pdf page 4. Decoded/encoded manually here instead of relying
// on the generic multi-register endianness handling of FluentModbus, to match the kit
// reference algorithm exactly.
//
// Ported from smt_glue/Features/Axis/Modbus/ModbusAxisConnection.cs (MR!14). Unchanged apart
// from the namespace: this is the piece that was already proven against the simulator.
public class ModbusAxisConnection(
  IOptions<AxisHardwareOptions> options,
  IMachineTrace trace,
  ILogger<ModbusAxisConnection> logger)
  : IAxisConnection, IDisposable
{
  private readonly AxisHardwareOptions _options = options.Value;
  private readonly ModbusTcpClient _client = new();
  private readonly SemaphoreSlim _connectGate = new(1, 1);
  private readonly SemaphoreSlim _ioGate = new(1, 1);
  private readonly SemaphoreSlim _commandGate = new(1, 1);

  // Serializes whole command lifecycles (verify -> write -> pulse -> poll -> ack) across
  // concurrent requests — _ioGate alone only serializes individual Modbus transactions,
  // which is not enough: two concurrent MOVE_XY calls could both read BUSY=0 and then
  // interleave their target/speed writes before either pulses the request bit. The kit
  // contract requires exactly one command in flight at a time. Callers should hold this for
  // the entire command, e.g.:
  //   using var _ = await connection.AcquireCommandLockAsync(ct);
  //
  // STOP deliberately does NOT take this lock — see AxisControlService.StopAsync. Safety
  // does not negotiate with concurrency control.
  public async Task<IDisposable> AcquireCommandLockAsync(CancellationToken ct)
  {
    await _commandGate.WaitAsync(ct);
    return new CommandLockReleaser(_commandGate);
  }

  private sealed class CommandLockReleaser(SemaphoreSlim gate) : IDisposable
  {
    private bool _released;

    public void Dispose()
    {
      if (_released) return;
      _released = true;
      gate.Release();
    }
  }

  public async Task<bool> ReadCoilAsync(int address, CancellationToken ct)
  {
    var coils = await ExecuteAsync(() => _client.ReadCoilsAsync(_options.UnitId, address, 1, ct), ct);
    return (coils.Span[0] & 0x1) != 0;
  }

  public async Task WriteCoilAsync(int address, bool value, CancellationToken ct) =>
    await ExecuteAsync(() => _client.WriteSingleCoilAsync(_options.UnitId, address, value, ct), ct);

  // Rising-edge request pulse: write 1, then write 0 (the simulator/PLC captures the request
  // on the 0->1 transition).
  public async Task PulseCoilAsync(int address, CancellationToken ct)
  {
    await WriteCoilAsync(address, true, ct);
    await WriteCoilAsync(address, false, ct);
  }

  public async Task<int> ReadDwordAsync(int address, CancellationToken ct)
  {
    var words = await ExecuteAsync(() => _client.ReadHoldingRegistersAsync<ushort>(_options.UnitId, address, 2, ct), ct);
    return DecodeDword(words.Span[0], words.Span[1]);
  }

  public async Task WriteDwordAsync(int address, int value, CancellationToken ct) =>
    await ExecuteAsync(() => _client.WriteMultipleRegistersAsync<ushort>(_options.UnitId, address, EncodeDword(value), ct), ct);

  // Single word, never two. One of the four contract corrections that only showed up against
  // the real machine: reading an error code with qty=2 returns Modbus exception 0x02.
  public async Task<ushort> ReadUInt16Async(int address, CancellationToken ct)
  {
    var words = await ExecuteAsync(() => _client.ReadHoldingRegistersAsync<ushort>(_options.UnitId, address, 1, ct), ct);
    return words.Span[0];
  }

  // Ensures the socket is connected (retrying ConnectRetryAttempts times,
  // ConnectRetryDelaySeconds apart), runs the Modbus transaction, and — if it fails for any
  // reason other than cancellation — resets the connection and retries the whole thing
  // (including the reconnect policy) exactly once more before giving up. Every transaction is
  // serialized through _ioGate: the FluentModbus TCP client expects one request in flight at
  // a time, and the heartbeat (AxisHeartbeatService) shares this same connection.
  // Write-operation overload: the FluentModbus write methods return a plain Task, so this
  // wraps them to reuse the same connect/retry/serialize logic as the reads.
  private Task ExecuteAsync(Func<Task> operation, CancellationToken ct) =>
    ExecuteAsync(async () => { await operation(); return true; }, ct);

  private async Task<T> ExecuteAsync<T>(Func<Task<T>> operation, CancellationToken ct)
  {
    await EnsureConnectedAsync(ct);

    await _ioGate.WaitAsync(ct);
    try
    {
      return await operation();
    }
    // Only reset/reconnect on connectivity-level failures. A ModbusException means the socket
    // is fine but the PLC/simulator rejected the request at the protocol level (e.g. illegal
    // address) — reconnecting would not help and would mask the real error, so let it
    // propagate as-is instead of being retried.
    catch (Exception ex) when (!ct.IsCancellationRequested && ex is not ModbusException)
    {
      ResetConnection();
    }
    finally
    {
      _ioGate.Release();
    }

    // Retry path: reconnect (with its own 3x/5s policy) and attempt the transaction once
    // more; a second failure propagates as-is.
    await EnsureConnectedAsync(ct);

    await _ioGate.WaitAsync(ct);
    try
    {
      return await operation();
    }
    finally
    {
      _ioGate.Release();
    }
  }

  private async Task EnsureConnectedAsync(CancellationToken ct)
  {
    if (_client.IsConnected) return;

    await _connectGate.WaitAsync(ct);
    try
    {
      if (_client.IsConnected) return;

      Exception? lastError = null;

      for (var attempt = 1; attempt <= _options.ConnectRetryAttempts; attempt++)
      {
        try
        {
          // Dns.GetHostAddressesAsync resolves both IP literals and hostnames — IPAddress.Parse
          // would reject the latter.
          var addresses = await Dns.GetHostAddressesAsync(_options.Host, ct);
          var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault()
            ?? throw new IntegrationException($"Could not resolve axis hardware host '{_options.Host}'.");

          var endpoint = new IPEndPoint(address, _options.ModbusPort);
          // Modbus TCP is big-endian per register on the wire (protocol-level, not
          // device-specific) — this must match that, not the kit low-word-first DWORD
          // convention, which is a separate, higher-level concern already handled manually by
          // DecodeDword/EncodeDword below. LittleEndian here made FluentModbus byte-swap every
          // individual register (confirmed live: a real ErrorCode of 1002 came back as 59907,
          // which is exactly 1002 with its two bytes swapped).
          // Every transaction holds _ioGate, the heartbeat's included: one that hangs is a
          // heartbeat not written, and contract v2's watchdog is only 1000 ms.
          if (_options.ModbusTransactionTimeoutMs > 0)
          {
            _client.ReadTimeout = _options.ModbusTransactionTimeoutMs;
            _client.WriteTimeout = _options.ModbusTransactionTimeoutMs;
          }

          _client.Connect(endpoint, ModbusEndianness.BigEndian);
          logger.LogInformation("Connected to axis hardware at {Host}:{Port}.", _options.Host, _options.ModbusPort);

          // A trace session is one connection. Opening it here, and not at process start, is
          // what makes a reconnection visible as a new folder instead of a silent gap in the
          // middle of an old one.
          trace.StartSession(new TraceSessionInfo
          {
            SessionId = $"s-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..6]}",
            StartedAt = DateTimeOffset.UtcNow,
            Transport = "ModbusTcp",
            Host = _options.Host,
            ModbusPort = _options.ModbusPort,
            UnitId = _options.UnitId,
          });

          return;
        }
        catch (Exception ex)
        {
          lastError = ex;
          logger.LogWarning(ex, "Axis hardware connection attempt {Attempt}/{MaxAttempts} failed.", attempt, _options.ConnectRetryAttempts);

          if (attempt < _options.ConnectRetryAttempts)
          {
            await Task.Delay(TimeSpan.FromSeconds(_options.ConnectRetryDelaySeconds), ct);
          }
        }
      }

      throw new IntegrationException(
        $"Failed to connect to the axis hardware at {_options.Host}:{_options.ModbusPort} after {_options.ConnectRetryAttempts} attempts.",
        lastError!);
    }
    finally
    {
      _connectGate.Release();
    }
  }

  public bool IsConnected => _client.IsConnected;

  private void ResetConnection()
  {
    try
    {
      _client.Disconnect();
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "Failed to cleanly disconnect from the axis hardware before retrying.");
    }
  }

  private static int DecodeDword(ushort low, ushort high) => unchecked((int)((uint)high << 16 | low));

  private static ushort[] EncodeDword(int value)
  {
    var raw = unchecked((uint)value);
    return [(ushort)(raw & 0xFFFF), (ushort)(raw >> 16)];
  }

  public void Dispose()
  {
    _client.Dispose();
    _connectGate.Dispose();
    _ioGate.Dispose();
    _commandGate.Dispose();
    GC.SuppressFinalize(this);
  }
}
