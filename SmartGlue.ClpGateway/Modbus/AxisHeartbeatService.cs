using Microsoft.Extensions.Options;
using SmartGlue.ClpGateway.Trace;

namespace SmartGlue.ClpGateway.Modbus;

// Mandatory watchdog required by the simulator/PLC contract: an external counter must be
// written to EXTERNAL_HEARTBEAT (D19980/D19981) at least every HeartbeatTimeoutMs (1000ms in
// contract v2, the real ladder's TMR T450 K10), recommended every HeartbeatIntervalMs (200ms),
// for as long as this process is the master — otherwise every station raises FAULT
// (HEARTBEAT_LOST). Runs independent of any movement command, per the kit manual: "Execute o
// heartbeat em thread/task independente de movimentos e HTTP."
//
// Ported from smt_glue/Features/Axis/Modbus/AxisHeartbeatService.cs (MR!14), with the session
// opening added (see OpenSessionAsync) — a gap the backend could tolerate and an autonomous
// routine executor cannot.
public class AxisHeartbeatService(
  IAxisConnection connection,
  MachineIdentity identity,
  IMachineTrace trace,
  IOptions<AxisHardwareOptions> options,
  ILogger<AxisHeartbeatService> logger) : BackgroundService
{
  private readonly AxisHardwareOptions _options = options.Value;
  private bool _sessionOpened;

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    if (!_options.Enabled)
    {
      logger.LogWarning("AxisHardware:Enabled is false — the gateway will not hold the machine watchdog.");
      return;
    }

    var counter = 0;
    var ticks = 0;
    long? lastWriteAt = null;

    while (!stoppingToken.IsCancellationRequested)
    {
      try
      {
        // Must change from the previous value — rewriting the same value does not renew the
        // watchdog window (manual, page 5: "aumente ou altere o contador"). A simple
        // incrementing counter is the easiest way to guarantee that on every tick.
        counter++;
        await TickAsync(counter, stoppingToken);

        // The socket is shared and serialized: a transaction that holds it for long is a
        // heartbeat written late. Say so while it is still a warning and not yet a 1009.
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (lastWriteAt is long previous)
        {
          var gapMs = System.Diagnostics.Stopwatch.GetElapsedTime(previous, now).TotalMilliseconds;
          if (gapMs > _options.HeartbeatTimeoutMs / 2.0)
          {
            logger.LogWarning(
              "Heartbeat written {Gap:F0}ms after the previous one (watchdog window {Window}ms). Something is holding the Modbus socket.",
              gapMs, _options.HeartbeatTimeoutMs);
          }
        }
        lastWriteAt = now;

        if (!_sessionOpened)
        {
          await OpenSessionAsync(stoppingToken);
        }

        if (_options.HeartbeatEchoCheckEveryTicks > 0 && ++ticks % _options.HeartbeatEchoCheckEveryTicks == 0)
        {
          await CheckEchoAsync(counter, stoppingToken);
        }
      }
      catch (OperationCanceledException)
      {
        break;
      }
      catch (Exception ex)
      {
        logger.LogWarning(ex, "Failed to write axis heartbeat counter.");
        // The next successful tick re-opens the session: a dropped socket means the machine
        // may have latched HEARTBEAT_LOST again while we were away — and may not even be the
        // same machine.
        _sessionOpened = false;
        identity.Forget();
        lastWriteAt = null;
      }

      try
      {
        await Task.Delay(_options.HeartbeatIntervalMs, stoppingToken);
      }
      catch (OperationCanceledException)
      {
        break;
      }
    }
  }

  // A machine that was left without a master sits latched in FAULT 1009 HEARTBEAT_LOST and
  // does not recover on its own once the heartbeat comes back. Clearing it here — once per
  // session, and ONLY when the error code is exactly 1009 — is what lets the gateway take
  // over a cell that was previously driven by another client (or by nothing at all).
  //
  // Deliberately narrow: any other fault code is left untouched, because the operator has to
  // see it and decide. Silently clearing faults is how a machine hurts someone.
  //
  // The watchdog is ONE counter shared by the whole machine (D19980), so a heartbeat that
  // expires latches 1009 on EVERY station at once — verified against the kit simulator, whose
  // two panels both showed "Falha 1009" after the gateway had been away. Clearing only the
  // inspection station therefore left the applicator latched forever, and the application
  // screen had no way to recover: every command it sent came back 409 FAULT, and
  // /application/fault-reset is not something an operator should have to discover.
  // One heartbeat write, flagged so the transport trace files it as heartbeat traffic.
  private async Task TickAsync(int counter, CancellationToken ct)
  {
    using var _ = MachineCorrelation.Begin(isHeartbeat: true);
    await connection.WriteDwordAsync(MachineAddresses.HeartbeatCounter, counter, ct);
  }

  // D19982 echoes the last DIFFERENT value accepted. The echo is read right after a write, so it
  // may lag by a tick; only an echo that is not even the previous value counts as stale.
  private async Task CheckEchoAsync(int counter, CancellationToken ct)
  {
    int echo;
    using (MachineCorrelation.Begin(isHeartbeat: true))
    {
      echo = await connection.ReadDwordAsync(MachineAddresses.HeartbeatEcho, ct);
    }

    var stale = echo != counter && echo != counter - 1;
    if (stale && !identity.EchoStale)
    {
      logger.LogWarning(
        "Heartbeat echo D19982 reads {Echo} while the counter is at {Counter}: the machine is not taking the heartbeat.",
        echo, counter);
    }

    identity.SetEchoStale(stale);
  }

  private async Task OpenSessionAsync(CancellationToken ct)
  {
    _sessionOpened = true;

    await ReadIdentityAsync(ct);

    if (!_options.ClearHeartbeatLostOnSessionOpen) return;

    foreach (var station in new[] { StationAddresses.Inspection, StationAddresses.Application })
    {
      await ClearHeartbeatLostAsync(station, ct);
    }
  }

  // Identity before anything else in the session: build (D19984/D19985, DWORD) and contract
  // version (D24508). Recorded in the trace session — the evidence package used to carry
  // BuildCode 0 because nothing ever read it — and in MachineIdentity, which refuses commands
  // to an incompatible machine. The heartbeat keeps running either way: a machine we will not
  // command is still a machine whose watchdog we hold, so STOP keeps meaning something.
  private async Task ReadIdentityAsync(CancellationToken ct)
  {
    var build = await connection.ReadDwordAsync(MachineAddresses.BuildCode, ct);
    var contract = await connection.ReadUInt16Async(MachineAddresses.ContractVersion, ct);

    identity.Record(build, contract);
    trace.SetBuildCode(build);

    if (identity.State == MachineIdentityState.Incompatible)
    {
      logger.LogError(
        "Incompatible machine: build {Build}, contract {Contract}. This gateway speaks build {ExpectedBuild}, contract {ExpectedContract}. Movement commands will be refused.",
        build, contract, MachineAddresses.ExpectedBuildCode, MachineAddresses.ExpectedContractVersion);
    }
    else
    {
      logger.LogInformation("Machine identified: build {Build}, contract {Contract}.", build, contract);
    }
  }

  private async Task ClearHeartbeatLostAsync(StationAddresses station, CancellationToken ct)
  {
    var fault = await connection.ReadCoilAsync(station.Fault, ct);
    if (!fault) return;

    var errorCode = await connection.ReadUInt16Async(station.ErrorCode, ct);
    if (errorCode != MachineErrors.HeartbeatLost)
    {
      logger.LogWarning(
        "The {Station} station is in FAULT ({Error}) at session open. Not clearing it — only HEARTBEAT_LOST is cleared automatically.",
        station.Name, MachineErrors.Describe(errorCode));
      return;
    }

    logger.LogInformation(
      "The {Station} station was latched in {Error} with no master. Clearing it once for this session.",
      station.Name, MachineErrors.Describe(errorCode));

    await connection.PulseCoilAsync(station.FaultReset, ct);
  }
}
