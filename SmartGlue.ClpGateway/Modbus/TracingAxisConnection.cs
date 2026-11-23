using System.Diagnostics;
using FluentModbus;
using Microsoft.Extensions.Options;
using SmartGlue.ClpGateway.Trace;

namespace SmartGlue.ClpGateway.Modbus;

/// <summary>
/// Records every exchange with the machine — the transport layer of the trace (SGF-141 §5).
///
/// A decorator, not code inside <see cref="ModbusAxisConnection"/>, for two reasons. The socket
/// class stays about Modbus; and the layer becomes testable without a socket, which matters
/// most of all here: the evidence a story is signed off with cannot itself be something that
/// only a bench can exercise.
///
/// The trace never gets in the way of the machine. The sink only enqueues, and a full queue
/// drops records and counts them rather than blocking a command that is moving an axis.
/// </summary>
public class TracingAxisConnection(
  IAxisConnection inner,
  IMachineTrace trace,
  IOptions<AxisHardwareOptions> options) : IAxisConnection
{
  private readonly AxisHardwareOptions _options = options.Value;

  public bool IsConnected => inner.IsConnected;

  public Task<IDisposable> AcquireCommandLockAsync(CancellationToken ct) => inner.AcquireCommandLockAsync(ct);

  public Task<bool> ReadCoilAsync(int address, CancellationToken ct) =>
    TracedAsync("read", fc: 1, address, qty: 1, unit: "bit",
      () => inner.ReadCoilAsync(address, ct), value => value ? 1L : 0L);

  public async Task WriteCoilAsync(int address, bool value, CancellationToken ct) =>
    await TracedAsync("write", fc: 5, address, qty: 1, unit: "bit",
      async () => { await inner.WriteCoilAsync(address, value, ct); return value; },
      written => written ? 1L : 0L);

  /// <summary>
  /// A pulse is delegated whole and recorded as its two halves.
  ///
  /// Delegated whole because the rising edge is the inner connection's own semantics — taking
  /// it apart here and re-issuing two writes would mean this decorator, and not the connection,
  /// decides what a pulse is.
  ///
  /// Recorded as two lines because the contract is a 0→1→0 transition, and a trace that showed
  /// one line would hide exactly the edge the machine reacts to.
  /// </summary>
  public async Task PulseCoilAsync(int address, CancellationToken ct)
  {
    var startedAt = Stopwatch.GetTimestamp();

    try
    {
      await inner.PulseCoilAsync(address, ct);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      trace.WriteFrame(
        NewFrame("write", fc: 5, address, qty: 1, startedAt, ex is ModbusException ? TraceOutcome.ModbusException : TraceOutcome.SocketError)
          with { Message = ex.Message },
        MachineCorrelation.IsHeartbeat);

      throw;
    }

    foreach (var edge in new long[] { 1, 0 })
    {
      trace.WriteFrame(
        NewFrame("write", fc: 5, address, qty: 1, startedAt, TraceOutcome.Ok) with
        {
          Decoded = new TraceDecoded(DescribeAddress(address), edge, "bit"),
        },
        MachineCorrelation.IsHeartbeat);
    }
  }

  public Task<int> ReadDwordAsync(int address, CancellationToken ct) =>
    TracedAsync("read", fc: 3, address, qty: 2, unit: "pulse",
      () => inner.ReadDwordAsync(address, ct), value => value);

  public async Task WriteDwordAsync(int address, int value, CancellationToken ct) =>
    await TracedAsync("write", fc: 16, address, qty: 2, unit: "pulse",
      async () => { await inner.WriteDwordAsync(address, value, ct); return value; },
      written => written);

  public Task<ushort> ReadUInt16Async(int address, CancellationToken ct) =>
    TracedAsync("read", fc: 3, address, qty: 1, unit: "code",
      () => inner.ReadUInt16Async(address, ct), value => (long)value);

  private async Task<T> TracedAsync<T>(
    string direction,
    byte fc,
    int address,
    ushort qty,
    string unit,
    Func<Task<T>> exchange,
    Func<T, long> decode)
  {
    var startedAt = Stopwatch.GetTimestamp();

    try
    {
      var result = await exchange();

      trace.WriteFrame(
        NewFrame(direction, fc, address, qty, startedAt, TraceOutcome.Ok) with
        {
          Decoded = new TraceDecoded(DescribeAddress(address), decode(result), unit),
        },
        MachineCorrelation.IsHeartbeat);

      return result;
    }
    catch (ModbusException ex)
    {
      // The socket is fine; the machine refused the request at protocol level. Recording this
      // as a socket error would send whoever reads the trace hunting for a network problem.
      trace.WriteFrame(
        NewFrame(direction, fc, address, qty, startedAt, TraceOutcome.ModbusException) with { Message = ex.Message },
        MachineCorrelation.IsHeartbeat);

      throw;
    }
    catch (OperationCanceledException)
    {
      throw;
    }
    catch (Exception ex)
    {
      trace.WriteFrame(
        NewFrame(direction, fc, address, qty, startedAt, TraceOutcome.SocketError) with { Message = ex.Message },
        MachineCorrelation.IsHeartbeat);

      throw;
    }
  }

  private TraceRecord NewFrame(string direction, byte fc, int address, ushort qty, long startedAt, string outcome) => new()
  {
    Seq = trace.NextSequence(),
    SessionId = trace.CurrentSession?.SessionId ?? string.Empty,
    RunId = MachineCorrelation.RunId,
    CommandId = MachineCorrelation.CommandId,
    Layer = TraceLayer.Transport,
    Outcome = outcome,
    LatencyMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
    Dir = direction,
    Fc = fc,
    Addr = (ushort)address,
    Qty = qty,
    UnitId = _options.UnitId,
  };

  /// <summary>
  /// Names the address when the contract publishes a name for it, so a line of the trace reads
  /// as "APPLICATION_MOVE_XY" instead of "1050" — the number alone sends the reader back to the
  /// address map on every single line.
  /// </summary>
  private static string DescribeAddress(int address) => address switch
  {
    ApplicationStationAddresses.Home => "APPLICATION_HOME",
    ApplicationStationAddresses.MoveXy => "APPLICATION_MOVE_XY",
    ApplicationStationAddresses.MoveZ => "APPLICATION_MOVE_Z",
    ApplicationStationAddresses.Stop => "APPLICATION_STOP",
    ApplicationStationAddresses.FaultReset => "APPLICATION_FAULT_RESET",
    ApplicationStationAddresses.Busy => "APPLICATION_BUSY",
    ApplicationStationAddresses.MoveDone => "APPLICATION_MOVE_DONE",
    ApplicationStationAddresses.Fault => "APPLICATION_FAULT",
    ApplicationStationAddresses.ReferencedX => "APPLICATION_REFERENCED_X",
    ApplicationStationAddresses.ReferencedY => "APPLICATION_REFERENCED_Y",
    ApplicationStationAddresses.ReferencedZ => "APPLICATION_REFERENCED_Z",
    ApplicationStationAddresses.TargetX => "APPLICATION_TARGET_X",
    ApplicationStationAddresses.TargetY => "APPLICATION_TARGET_Y",
    ApplicationStationAddresses.TargetZ => "APPLICATION_TARGET_Z",
    ApplicationStationAddresses.SpeedXy => "APPLICATION_SPEED_XY",
    ApplicationStationAddresses.SpeedZ => "APPLICATION_SPEED_Z",
    ApplicationStationAddresses.CurrentX => "APPLICATION_CURRENT_X",
    ApplicationStationAddresses.CurrentY => "APPLICATION_CURRENT_Y",
    ApplicationStationAddresses.CurrentZ => "APPLICATION_CURRENT_Z",
    ApplicationStationAddresses.ErrorCode => "APPLICATION_ERROR_CODE",
    ApplicationStationAddresses.LimitXMin => "APPLICATION_LIMIT_X_MIN",
    ApplicationStationAddresses.LimitXMax => "APPLICATION_LIMIT_X_MAX",
    ApplicationStationAddresses.LimitYMin => "APPLICATION_LIMIT_Y_MIN",
    ApplicationStationAddresses.LimitYMax => "APPLICATION_LIMIT_Y_MAX",
    ApplicationStationAddresses.LimitZMin => "APPLICATION_LIMIT_Z_MIN",
    ApplicationStationAddresses.LimitZMax => "APPLICATION_LIMIT_Z_MAX",
    ApplicationStationAddresses.JogXNegative => "APPLICATION_JOG_X_NEGATIVE",
    ApplicationStationAddresses.JogXPositive => "APPLICATION_JOG_X_POSITIVE",
    ApplicationStationAddresses.JogYNegative => "APPLICATION_JOG_Y_NEGATIVE",
    ApplicationStationAddresses.JogYPositive => "APPLICATION_JOG_Y_POSITIVE",
    ApplicationStationAddresses.JogSpeedXNegative => "APPLICATION_JOG_SPEED_X_NEGATIVE",
    ApplicationStationAddresses.JogSpeedXPositive => "APPLICATION_JOG_SPEED_X_POSITIVE",
    ApplicationStationAddresses.JogSpeedYNegative => "APPLICATION_JOG_SPEED_Y_NEGATIVE",
    ApplicationStationAddresses.JogSpeedYPositive => "APPLICATION_JOG_SPEED_Y_POSITIVE",
    InspectionStationAddresses.Home => "INSPECTION_HOME",
    InspectionStationAddresses.MoveXy => "INSPECTION_MOVE_XY",
    InspectionStationAddresses.Stop => "INSPECTION_STOP",
    InspectionStationAddresses.FaultReset => "INSPECTION_FAULT_RESET",
    InspectionStationAddresses.Busy => "INSPECTION_BUSY",
    InspectionStationAddresses.MoveDone => "INSPECTION_MOVE_DONE",
    InspectionStationAddresses.Fault => "INSPECTION_FAULT",
    InspectionStationAddresses.ReferencedX => "INSPECTION_REFERENCED_X",
    InspectionStationAddresses.ReferencedY => "INSPECTION_REFERENCED_Y",
    InspectionStationAddresses.TargetX => "INSPECTION_TARGET_X",
    InspectionStationAddresses.TargetY => "INSPECTION_TARGET_Y",
    InspectionStationAddresses.SpeedXy => "INSPECTION_SPEED_XY",
    InspectionStationAddresses.CurrentX => "INSPECTION_CURRENT_X",
    InspectionStationAddresses.CurrentY => "INSPECTION_CURRENT_Y",
    InspectionStationAddresses.ErrorCode => "INSPECTION_ERROR_CODE",
    InspectionStationAddresses.ReferencedZ => "INSPECTION_REFERENCED_Z",
    InspectionStationAddresses.LimitXMin => "INSPECTION_LIMIT_X_MIN",
    InspectionStationAddresses.LimitXMax => "INSPECTION_LIMIT_X_MAX",
    InspectionStationAddresses.LimitYMin => "INSPECTION_LIMIT_Y_MIN",
    InspectionStationAddresses.LimitYMax => "INSPECTION_LIMIT_Y_MAX",
    MachineAddresses.HeartbeatCounter => "EXTERNAL_HEARTBEAT",
    MachineAddresses.HeartbeatEcho => "SIMULATOR_HEARTBEAT_ECHO",
    MachineAddresses.BuildCode => "SIMULATOR_BUILD_CODE",
    MachineAddresses.ContractVersion => "CONTRACT_VERSION",
    _ => address.ToString(),
  };
}
