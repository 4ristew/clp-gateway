using System.Diagnostics;
using FluentModbus;
using SmartGlue.ClpGateway.Trace;
using Microsoft.Extensions.Options;
using SmartGlue.ClpGateway.Api.DTOs;
using SmartGlue.ClpGateway.Common.Exceptions;
using SmartGlue.ClpGateway.Modbus;

namespace SmartGlue.ClpGateway.Services;

/// <summary>Which axis a jog request drives.</summary>
public enum JogAxis
{
  X,
  Y
}

/// <summary>
/// Status of one station, in millimetres. <see cref="PositionZ"/> is null for a station this
/// gateway drives in X/Y only.
///
/// <see cref="ReachedTarget"/> is false when a move completed (MOVE_DONE) somewhere other than
/// where it was sent. In contract v2 an out-of-range target saturates at the limit instead of
/// being refused, so "completed" no longer implies "arrived".
/// </summary>
public record StationStatus(
  AxisStatus Status,
  decimal PositionX,
  decimal PositionY,
  decimal? PositionZ,
  bool ReferencedX,
  bool ReferencedY,
  bool? ReferencedZ,
  bool ReachedTarget = true);

/// <summary>Soft limits of one station, in millimetres.</summary>
public record StationLimits(
  decimal XMin, decimal XMax,
  decimal YMin, decimal YMax,
  decimal? ZMin, decimal? ZMax);

// The command lifecycle of ONE station, written once and pointed at either of them by the
// address set it is constructed with. This is the code that used to live inline in
// AxisControlService (MR!14, inspection only); SGF-141 needs the exact same cycle against the
// application station, and two copies of a handshake this exacting is how they drift.
//
// The cycle, from 01_MANUAL_COMUNICACAO_CLP.pdf / MODBUS_ADDRESS_MAP.md:
//   1. verify BUSY=0, MOVE_DONE=0, FAULT=0
//   2. write target/speed parameters
//   3. pulse the request bit 0->1->0
//   4. poll BUSY/MOVE_DONE/FAULT
//   5. on MOVE_DONE=1, read the final position
//   6. ACK by writing MOVE_DONE=0 before the next command
// A command that times out mid-flight is never re-pulsed automatically — the kit contract
// forbids repeating a command whose completion is indeterminate.
//
// Steps 1-6 are serialized through AcquireCommandLockAsync, which is process-wide and NOT
// per-station: the "exactly one write master" rule is about the machine, not about a station,
// and interleaving two stations' handshakes over one socket would have each one reading the
// other's registers mid-cycle.
public class StationController(
  StationAddresses addresses,
  IAxisConnection connection,
  IMachineTrace trace,
  IOptions<AxisHardwareOptions> options,
  ILogger logger,
  MachineIdentity? identity = null)
{
  private readonly AxisHardwareOptions _options = options.Value;

  /// <summary>Jog deadman — see <see cref="SetJogAsync"/>.</summary>
  private readonly SemaphoreSlim _jogGate = new(1, 1);
  private CancellationTokenSource? _jogDeadman;
  private int? _activeJogCoil;

  public StationAddresses Addresses => addresses;

  /// <summary>Route prefix of this station, so an error message points at a route that exists.</summary>
  private string Route => addresses.Id == StationId.Application ? "/application" : "/axis";

  // ─── Leitura de estado ─────────────────────────────────────────────────────

  public async Task<StationStatus> GetStatusAsync(CancellationToken ct)
  {
    if (!_options.Enabled)
    {
      return new StationStatus(AxisStatus.CommunicationFailure, 0, 0, addresses.HasZ ? 0 : null, false, false, addresses.HasZ ? false : null);
    }

    try
    {
      var busy = await connection.ReadCoilAsync(addresses.Busy, ct);
      var fault = await connection.ReadCoilAsync(addresses.Fault, ct);
      var currentXPulses = await connection.ReadDwordAsync(addresses.CurrentX, ct);
      var currentYPulses = await connection.ReadDwordAsync(addresses.CurrentY, ct);

      int? currentZPulses = addresses.CurrentZ is int zAddress
        ? await connection.ReadDwordAsync(zAddress, ct)
        : null;

      var referencedX = await connection.ReadCoilAsync(addresses.ReferencedX, ct);
      var referencedY = await connection.ReadCoilAsync(addresses.ReferencedY, ct);
      bool? referencedZ = addresses.ReferencedZ is int refZ
        ? await connection.ReadCoilAsync(refZ, ct)
        : null;

      var status = fault ? AxisStatus.Fault : busy ? AxisStatus.Moving : AxisStatus.Ready;

      return new StationStatus(
        status,
        ToMillimeters(currentXPulses),
        ToMillimeters(currentYPulses),
        currentZPulses is int z ? ToMillimeters(z) : null,
        referencedX,
        referencedY,
        referencedZ);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      logger.LogWarning(ex, "Failed to read {Station} station status.", addresses.Name);
      return new StationStatus(AxisStatus.CommunicationFailure, 0, 0, addresses.HasZ ? 0 : null, false, false, addresses.HasZ ? false : null);
    }
  }

  /// <summary>
  /// True when every axis the MACHINE requires before a move reports referenced. That includes
  /// the inspection Z, which this gateway never commands: with z_axis_mode=simulated the
  /// machine refuses X/Y moves with 1001 until Z is referenced too, and a HOME stopped after Y
  /// leaves exactly that state behind. <see cref="AxisHardwareOptions.InspectionRequiresReferencedZ"/>
  /// turns it off for a machine whose inspection has no Z (z_axis_mode=absent).
  /// </summary>
  public async Task<bool> IsReferencedAsync(CancellationToken ct)
  {
    var referencedX = await connection.ReadCoilAsync(addresses.ReferencedX, ct);
    var referencedY = await connection.ReadCoilAsync(addresses.ReferencedY, ct);

    if (!referencedX || !referencedY) return false;

    var requiresZ = addresses.Id == StationId.Application || _options.InspectionRequiresReferencedZ;

    if (requiresZ && addresses.ReferencingZ is int refZ)
    {
      return await connection.ReadCoilAsync(refZ, ct);
    }

    return true;
  }

  public async Task<StationLimits> ReadLimitsAsync(CancellationToken ct)
  {
    var xMin = await connection.ReadDwordAsync(addresses.LimitXMin, ct);
    var xMax = await connection.ReadDwordAsync(addresses.LimitXMax, ct);
    var yMin = await connection.ReadDwordAsync(addresses.LimitYMin, ct);
    var yMax = await connection.ReadDwordAsync(addresses.LimitYMax, ct);

    int? zMin = addresses.LimitZMin is int zMinAddress ? await connection.ReadDwordAsync(zMinAddress, ct) : null;
    int? zMax = addresses.LimitZMax is int zMaxAddress ? await connection.ReadDwordAsync(zMaxAddress, ct) : null;

    return new StationLimits(
      ToMillimeters(xMin),
      ToMillimeters(xMax),
      ToMillimeters(yMin),
      ToMillimeters(yMax),
      zMin is int zn ? ToMillimeters(zn) : null,
      zMax is int zx ? ToMillimeters(zx) : null);
  }

  // ─── Movimento ─────────────────────────────────────────────────────────────

  /// <summary>
  /// MOVE_XY: captures X, Y and SPEED_XY, and nothing of the Z block. Homes first when the
  /// station has never been referenced — a machine just powered on refuses every absolute
  /// target, and the screen can only say "could not move", which is a cause the operator has
  /// no way of guessing.
  /// </summary>
  public async Task<StationStatus> MoveXyAsync(
    decimal positionX,
    decimal positionY,
    int? speedPulsesPerSecond,
    StationLimits limits,
    CancellationToken ct)
  {
    if (positionX < limits.XMin || positionX > limits.XMax || positionY < limits.YMin || positionY > limits.YMax)
    {
      throw new ArgumentException(
        $"Position (X={positionX}, Y={positionY}) is outside the axis limits (X: {limits.XMin}-{limits.XMax}, Y: {limits.YMin}-{limits.YMax}).");
    }

    var speed = ResolveSpeed(speedPulsesPerSecond);

    if (!await IsReferencedAsync(ct))
    {
      logger.LogInformation("{Station} station not referenced; homing before the first move.", addresses.Name);
      await HomeAsync(ct);
    }

    return await ExecuteCommandAsync(
      requireReferenced: true,
      issueCommand: async ct2 =>
      {
        await connection.WriteDwordAsync(addresses.TargetX, ToPulses(positionX), ct2);
        await connection.WriteDwordAsync(addresses.TargetY, ToPulses(positionY), ct2);
        await connection.WriteDwordAsync(addresses.SpeedXy, speed, ct2);
        await connection.PulseCoilAsync(addresses.MoveXy, ct2);
      },
      ct,
      target: new CommandTarget(ToPulses(positionX), ToPulses(positionY), null));
  }

  /// <summary>
  /// MOVE_Z: captures Z and SPEED_Z, and nothing of the XY block. Separate request by
  /// contract, so lowering the applicator onto a point is always a second command.
  /// </summary>
  public async Task<StationStatus> MoveZAsync(
    decimal positionZ,
    int? speedPulsesPerSecond,
    StationLimits limits,
    CancellationToken ct)
  {
    if (addresses.MoveZ is not int moveZ || addresses.TargetZ is not int targetZ || addresses.SpeedZ is not int speedZ)
    {
      throw new ArgumentException($"The {addresses.Name} station has no Z axis on this gateway.");
    }

    if (limits.ZMin is decimal zMin && limits.ZMax is decimal zMax && (positionZ < zMin || positionZ > zMax))
    {
      throw new ArgumentException($"Position (Z={positionZ}) is outside the Z limits ({zMin}-{zMax}).");
    }

    var speed = ResolveSpeed(speedPulsesPerSecond);

    if (!await IsReferencedAsync(ct))
    {
      logger.LogInformation("{Station} station not referenced; homing before moving Z.", addresses.Name);
      await HomeAsync(ct);
    }

    return await ExecuteCommandAsync(
      requireReferenced: true,
      issueCommand: async ct2 =>
      {
        await connection.WriteDwordAsync(targetZ, ToPulses(positionZ), ct2);
        await connection.WriteDwordAsync(speedZ, speed, ct2);
        await connection.PulseCoilAsync(moveZ, ct2);
      },
      ct,
      target: new CommandTarget(null, null, ToPulses(positionZ)));
  }

  /// <summary>
  /// HOME. In contract v2 it takes time (BUSY=1 while it references X → Y → Z, one axis at a
  /// time), clears the reference bits when it starts, and ends with SET MOVE_DONE — which this
  /// master acknowledges exactly like any other move. It has its own deadline
  /// (<see cref="AxisHardwareOptions.HomeTimeoutSeconds"/>): three axes in sequence from the
  /// far end of travel take as long as the whole move deadline.
  ///
  /// <see cref="HomeCompletionMode.Referenced"/> is the fallback for a ladder without the SG
  /// layer, whose baseline HOME has neither BUSY nor DONE.
  /// </summary>
  public Task<StationStatus> HomeAsync(CancellationToken ct) =>
    ExecuteCommandAsync(
      requireReferenced: false,
      issueCommand: ct2 => connection.PulseCoilAsync(addresses.Home, ct2),
      completion: _options.HomeCompletion == HomeCompletionMode.Referenced
        ? PollForHomeByReferencingAsync
        : (ct2, errorBefore) => PollForCompletionAsync(ct2, errorBefore, _options.HomeTimeoutSeconds, "homing"),
      ct);

  // STOP takes no command lock, requires no lease and carries no profile restriction beyond
  // being authenticated. It is allowed — expected — to interrupt an in-flight command: the
  // machine answers by raising FAULT 1006 STOPPED, which the poller of the interrupted
  // command surfaces as a 409. Anything that made STOP wait for the movement it is meant to
  // interrupt would not be a stop (SGF-140 ADR-01-R, decision 3).
  public async Task<StationStatus> StopAsync(CancellationToken ct)
  {
    EnsureEnabled();

    try
    {
      // Jog first: the machine neutralizes its own jog levels on STOP, but the bits we wrote
      // would still read 1 and the next clear of the fault would start the axis moving again.
      await ReleaseJogAsync(ct);

      await connection.PulseCoilAsync(addresses.Stop, ct);
      logger.LogWarning("STOP pulsed on the {Station} station.", addresses.Name);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      throw new IntegrationException("Failed to communicate with the axis hardware while stopping it.", ex);
    }

    return await GetStatusAsync(ct);
  }

  public async Task<StationStatus> ResetFaultAsync(CancellationToken ct)
  {
    EnsureEnabled();

    using var commandLock = await connection.AcquireCommandLockAsync(ct);

    try
    {
      await connection.PulseCoilAsync(addresses.FaultReset, ct);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      throw new IntegrationException("Failed to communicate with the camera/axis hardware while resetting the fault.", ex);
    }

    return await GetStatusAsync(ct);
  }

  // ─── JOG (nível mantido) ───────────────────────────────────────────────────

  /// <summary>
  /// Holds one jog bit at 1 while the operator holds the arrow, and releases it on its own if
  /// nobody says otherwise within <see cref="AxisHardwareOptions.JogDeadmanMs"/>.
  ///
  /// The deadman is the whole point. A level-held command driven from the far side of an HTTP
  /// call has one failure mode that a pulse does not: the caller disappears — a closed window,
  /// a dropped Wi-Fi, a frozen UI — and the bit stays at 1 with the axis travelling until it
  /// hits a soft limit. So the gateway, which is the process that can actually see the
  /// machine, owns the release; the desktop only keeps saying "still pressed".
  ///
  /// Opposite directions are never active together: setting one clears the other three first,
  /// which the contract requires and which no caller should have to remember.
  ///
  /// Speed (contract v2): the application's DJOG runs at the frequency RETAINED in
  /// D21600..D21750, and the sign of that register — not the bit — sets the direction. This
  /// gateway does not write it (it is the machine's jog speed, not a decision for this
  /// process), but it refuses to jog when the register's sign disagrees with the direction
  /// asked for, or is zero: on the real PLC that axis would run the wrong way, against the
  /// wrong end stop.
  ///
  /// With a non-zero ramp-down (D22110) the axis decelerates after the release and BUSY only
  /// drops at the end of the ramp, so the status returned by a release may still be Moving.
  /// </summary>
  public async Task<StationStatus> SetJogAsync(JogAxis axis, int direction, bool active, CancellationToken ct)
  {
    EnsureEnabled();

    if (!addresses.HasJog)
    {
      throw new ArgumentException($"The {addresses.Name} station has no jog controls on this gateway.");
    }

    var coil = ResolveJogCoil(axis, direction);

    await _jogGate.WaitAsync(ct);
    try
    {
      if (!active)
      {
        await ReleaseJogCoreAsync(ct);
        return await GetStatusAsync(ct);
      }

      EnsureCompatibleMachine();

      // Same gate as any other command start: a jog into a faulted or busy station is refused
      // rather than queued.
      var fault = await connection.ReadCoilAsync(addresses.Fault, ct);
      if (fault)
      {
        var errorCode = await connection.ReadUInt16Async(addresses.ErrorCode, ct);
        throw new ConflictException(
          $"The axis is in FAULT state ({MachineErrors.Describe(errorCode)}). Call POST {Route}/fault-reset once the cause has ceased.");
      }

      if (!await IsReferencedAsync(ct))
      {
        throw new ArgumentException($"The axis has not been homed yet. Call POST {Route}/zero first.");
      }

      if (_activeJogCoil != coil)
      {
        await EnsureJogSpeedSignAsync(axis, direction, ct);
        await ClearAllJogCoilsAsync(ct);
        await connection.WriteCoilAsync(coil, true, ct);
        _activeJogCoil = coil;
      }

      ArmDeadman();

      return await GetStatusAsync(ct);
    }
    finally
    {
      _jogGate.Release();
    }
  }

  /// <summary>Releases any held jog bit. Safe to call when nothing is held.</summary>
  public async Task ReleaseJogAsync(CancellationToken ct)
  {
    if (!addresses.HasJog) return;

    await _jogGate.WaitAsync(ct);
    try
    {
      await ReleaseJogCoreAsync(ct);
    }
    finally
    {
      _jogGate.Release();
    }
  }

  private async Task ReleaseJogCoreAsync(CancellationToken ct)
  {
    CancelDeadman();

    if (_activeJogCoil is null) return;

    await ClearAllJogCoilsAsync(ct);
    _activeJogCoil = null;
  }

  private async Task ClearAllJogCoilsAsync(CancellationToken ct)
  {
    foreach (var jogCoil in new[] { addresses.JogXNegative, addresses.JogXPositive, addresses.JogYNegative, addresses.JogYPositive })
    {
      if (jogCoil is int c) await connection.WriteCoilAsync(c, false, ct);
    }
  }

  private int ResolveJogCoil(JogAxis axis, int direction) => (axis, direction) switch
  {
    (JogAxis.X, < 0) => addresses.JogXNegative!.Value,
    (JogAxis.X, _) => addresses.JogXPositive!.Value,
    (JogAxis.Y, < 0) => addresses.JogYNegative!.Value,
    (JogAxis.Y, _) => addresses.JogYPositive!.Value,
  };

  private int? ResolveJogSpeedRegister(JogAxis axis, int direction) => (axis, direction) switch
  {
    (JogAxis.X, < 0) => addresses.JogSpeedXNegative,
    (JogAxis.X, _) => addresses.JogSpeedXPositive,
    (JogAxis.Y, < 0) => addresses.JogSpeedYNegative,
    (JogAxis.Y, _) => addresses.JogSpeedYPositive,
    _ => null,
  };

  // Read every time a direction is engaged, not cached: the registers are read_write for any
  // master, and a value changed on the machine panel would otherwise go unnoticed until the
  // axis ran the wrong way.
  private async Task EnsureJogSpeedSignAsync(JogAxis axis, int direction, CancellationToken ct)
  {
    if (ResolveJogSpeedRegister(axis, direction) is not int register) return;

    var speed = await connection.ReadDwordAsync(register, ct);
    var expectedNegative = direction < 0;

    if (speed == 0 || (speed < 0) != expectedNegative)
    {
      throw new ConflictException(
        $"Refusing to jog {axis}{(expectedNegative ? "−" : "+")}: the retained jog speed in D{register} is {speed} pulses/s. " +
        $"The sign of that register sets the direction on the PLC, so it must be {(expectedNegative ? "negative" : "positive")} and non-zero. " +
        "Check the jog speeds on the machine before jogging.");
    }
  }

  private void ArmDeadman()
  {
    CancelDeadman();

    var cts = new CancellationTokenSource();
    _jogDeadman = cts;

    _ = Task.Run(async () =>
    {
      try
      {
        await Task.Delay(_options.JogDeadmanMs, cts.Token);

        logger.LogWarning(
          "Jog deadman expired on the {Station} station after {Ms}ms without a refresh; releasing the axis.",
          addresses.Name, _options.JogDeadmanMs);

        await ReleaseJogAsync(CancellationToken.None);
      }
      catch (OperationCanceledException)
      {
        // Refreshed or released in time — the normal path.
      }
      catch (Exception ex)
      {
        logger.LogError(ex, "Failed to release the jog of the {Station} station after the deadman expired.", addresses.Name);
      }
    }, CancellationToken.None);
  }

  private void CancelDeadman()
  {
    _jogDeadman?.Cancel();
    _jogDeadman?.Dispose();
    _jogDeadman = null;
  }

  // ─── Ciclo de comando ──────────────────────────────────────────────────────

  /// <summary>Where a move was sent, in pulses, so its arrival can be checked (saturation).</summary>
  private sealed record CommandTarget(int? X, int? Y, int? Z);

  private Task<StationStatus> ExecuteCommandAsync(
    bool requireReferenced,
    Func<CancellationToken, Task> issueCommand,
    CancellationToken ct,
    CommandTarget? target = null) =>
    ExecuteCommandAsync(
      requireReferenced,
      issueCommand,
      (ct2, errorBefore) => PollForCompletionAsync(ct2, errorBefore, _options.CommandTimeoutSeconds, "command"),
      ct,
      target);

  private async Task<StationStatus> ExecuteCommandAsync(
    bool requireReferenced,
    Func<CancellationToken, Task> issueCommand,
    Func<CancellationToken, ushort, Task<StationStatus>> completion,
    CancellationToken ct,
    CommandTarget? target = null)
  {
    EnsureEnabled();
    EnsureCompatibleMachine();

    using var commandLock = await connection.AcquireCommandLockAsync(ct);

    // Every frame of this command carries the same id, which is what lets a question asked at
    // the waypoint level ("why did point 3 fail?") be answered down at the byte level.
    var commandId = $"c-{Guid.NewGuid().ToString("N")[..8]}";
    using var correlation = MachineCorrelation.Begin(commandId: commandId);

    var startedAt = Stopwatch.GetTimestamp();

    try
    {
      var errorBefore = await PrepareForCommandAsync(requireReferenced, ct);
      TraceCommand(commandId, "issued", TraceOutcome.Ok, startedAt);

      await issueCommand(ct);

      var status = await completion(ct, errorBefore);
      TraceCommand(commandId, "completed", TraceOutcome.Ok, startedAt, status);

      return target is null ? status : CheckArrival(commandId, startedAt, status, target);
    }
    catch (ModbusException ex) when (ex.ExceptionCode == ModbusExceptionCode.IllegalDataValue)
    {
      // Exception 03 on a request bit is the simulator refusing an edge whose parameter block
      // (target + speed DWORDs) was not fully rewritten since the last accepted request. The
      // socket is fine: this is a bug in what this gateway wrote, and reporting it as a
      // communication failure would send the operator to check the network cable.
      var message =
        $"The machine refused the request (Modbus exception 03): the parameter block was not fully written before the request bit. This is a gateway defect, not a communication failure. ({ex.Message})";
      TraceCommand(commandId, "refused", TraceOutcome.Refused, startedAt, message: message);
      throw new ConflictException(message);
    }
    catch (ConflictException ex)
    {
      TraceCommand(commandId, "refused", ex.Message.Contains("1006") ? TraceOutcome.Stopped : TraceOutcome.Faulted, startedAt, message: ex.Message);
      throw;
    }
    catch (OperationCanceledException)
    {
      TraceCommand(commandId, "cancelled", TraceOutcome.Stopped, startedAt);
      throw;
    }
    catch (IntegrationException ex)
    {
      TraceCommand(commandId, "failed", TraceOutcome.Timeout, startedAt, message: ex.Message);
      throw;
    }
    catch (ArgumentException ex)
    {
      TraceCommand(commandId, "refused", TraceOutcome.Refused, startedAt, message: ex.Message);
      throw;
    }
    catch (Exception ex)
    {
      TraceCommand(commandId, "failed", TraceOutcome.SocketError, startedAt, message: ex.Message);
      throw new IntegrationException("Failed to communicate with the camera/axis hardware.", ex);
    }
  }

  /// <summary>
  /// One line per phase of the command cycle. This is the layer that proves the handshake
  /// happened in the order the contract requires — and, when it did not, which phase it stopped
  /// at.
  /// </summary>
  private void TraceCommand(
    string commandId,
    string phase,
    string outcome,
    long startedAt,
    StationStatus? status = null,
    string? message = null)
  {
    trace.Write(new TraceRecord
    {
      Seq = trace.NextSequence(),
      SessionId = trace.CurrentSession?.SessionId ?? string.Empty,
      RunId = MachineCorrelation.RunId,
      CommandId = commandId,
      Layer = TraceLayer.Engine,
      Outcome = outcome,
      Component = addresses.Name,
      Phase = phase,
      ElapsedMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
      Message = message,
      Achieved = status is null
        ? null
        : new TracePoint(ToPulses(status.PositionX), ToPulses(status.PositionY), ToPulses(status.PositionZ ?? 0)),
    });
  }

  /// <summary>
  /// Contract v2 saturates an out-of-range target at the limit and still raises MOVE_DONE, so a
  /// completed move may have stopped short. The limits check before the move is the barrier
  /// (the machine no longer refuses); this is the record that it held. The target register is
  /// not re-read for this: the simulator does not write the saturated value back over Modbus TCP
  /// (kit PENDENCIAS §4.4), so the comparison is against what was sent.
  /// </summary>
  private StationStatus CheckArrival(string commandId, long startedAt, StationStatus status, CommandTarget target)
  {
    var tolerance = _options.PositionTolerancePulses;
    var missed =
      (target.X is int x && Math.Abs(ToPulses(status.PositionX) - x) > tolerance) ||
      (target.Y is int y && Math.Abs(ToPulses(status.PositionY) - y) > tolerance) ||
      (target.Z is int z && Math.Abs(ToPulses(status.PositionZ ?? 0) - z) > tolerance);

    if (!missed) return status;

    var message =
      $"MOVE_DONE came at X={status.PositionX}, Y={status.PositionY}, Z={status.PositionZ} instead of the target " +
      $"(pulses X={target.X}, Y={target.Y}, Z={target.Z}); the machine probably saturated it at a soft limit.";
    logger.LogWarning("{Station}: {Message}", addresses.Name, message);
    TraceCommand(commandId, "saturated", TraceOutcome.Refused, startedAt, status, message);

    return status with { ReachedTarget = false };
  }

  /// <summary>
  /// Checks BUSY/FAULT/DONE before a command and returns the error code the station showed at
  /// that moment, so the poller can tell a NEW refusal from a code left over from earlier.
  /// </summary>
  private async Task<ushort> PrepareForCommandAsync(bool requireReferenced, CancellationToken ct)
  {
    var busy = await connection.ReadCoilAsync(addresses.Busy, ct);
    if (busy)
    {
      // Domain state, not a communication failure — the hardware is healthy and reachable,
      // just already executing another command.
      throw new ConflictException("The axis is busy executing another command.");
    }

    var fault = await connection.ReadCoilAsync(addresses.Fault, ct);
    if (fault)
    {
      var errorCode = await connection.ReadUInt16Async(addresses.ErrorCode, ct);
      throw new ConflictException(
        $"The axis is in FAULT state ({MachineErrors.Describe(errorCode)}). Call POST {Route}/fault-reset once the cause has ceased.");
    }

    // A previous command completion may not have been acknowledged yet. Per the kit error
    // 1005 recovery guidance ("read the result, write DONE=0, confirm"), read (and log) the
    // stale position before clearing the latch, instead of silently discarding it.
    var done = await connection.ReadCoilAsync(addresses.MoveDone, ct);
    if (done)
    {
      var staleXPulses = await connection.ReadDwordAsync(addresses.CurrentX, ct);
      var staleYPulses = await connection.ReadDwordAsync(addresses.CurrentY, ct);
      logger.LogWarning(
        "Acknowledging an unread MOVE_DONE from a previous command before issuing a new one. Last known position: X={X}mm, Y={Y}mm.",
        ToMillimeters(staleXPulses), ToMillimeters(staleYPulses));
      await connection.WriteCoilAsync(addresses.MoveDone, false, ct);
    }

    if (requireReferenced && !await IsReferencedAsync(ct))
    {
      throw new ArgumentException($"The axis has not been homed yet. Call POST {Route}/zero first.");
    }

    return await connection.ReadUInt16Async(addresses.ErrorCode, ct);
  }

  /// <summary>
  /// Polls until MOVE_DONE (then ACKs it), FAULT, a refusal, or the deadline.
  ///
  /// Refusals: 1001, 1004, 1005, 1008, 1010 and 1012 do NOT raise FAULT — the machine refuses
  /// the edge, publishes the code and does nothing else. Watching only FAULT and DONE turned each
  /// of them into a full-deadline wait answered as "indeterminate". A refusal is recognised when
  /// the code CHANGES to one of those after the edge, or — when the same refusal was already
  /// showing before (the operator retrying) — when it is still there, with neither BUSY nor DONE
  /// seen, after <see cref="AxisHardwareOptions.RefusalGraceMs"/>. An accepted command clears the
  /// code, so a leftover refusal code cannot outlive a command the machine took.
  /// </summary>
  private async Task<StationStatus> PollForCompletionAsync(CancellationToken ct, ushort errorBefore, int timeoutSeconds, string what)
  {
    var startedAt = Stopwatch.GetTimestamp();
    var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
    var sawBusy = false;

    while (DateTime.UtcNow < deadline)
    {
      var fault = await connection.ReadCoilAsync(addresses.Fault, ct);
      if (fault)
      {
        var errorCode = await connection.ReadUInt16Async(addresses.ErrorCode, ct);
        throw new ConflictException(
          $"The axis reported a fault while executing the {what} ({MachineErrors.Describe(errorCode)}). Call POST {Route}/fault-reset once the cause has ceased.");
      }

      var done = await connection.ReadCoilAsync(addresses.MoveDone, ct);
      if (done)
      {
        // Mandatory ACK — only the external master clears MOVE_DONE; no other action (HOME,
        // STOP, FAULT_RESET, reconnect) is allowed to do it silently.
        var reached = await ReadPositionAsync(ct);
        await connection.WriteCoilAsync(addresses.MoveDone, false, ct);
        return reached;
      }

      var busy = await connection.ReadCoilAsync(addresses.Busy, ct);
      sawBusy |= busy;

      if (!busy)
      {
        var code = await connection.ReadUInt16Async(addresses.ErrorCode, ct);
        var refused = MachineErrors.IsRefusalWithoutFault(code) &&
          (code != errorBefore ||
           (!sawBusy && Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds >= _options.RefusalGraceMs));

        if (refused)
        {
          throw new ConflictException(
            $"The machine refused the {what} ({MachineErrors.Describe(code)}); nothing moved.");
        }
      }

      await Task.Delay(_options.CommandPollIntervalMs, ct);
    }

    // Timed out with the command still in flight. Never re-pulse automatically here — the kit
    // contract forbids repeating a command whose completion is indeterminate.
    throw new IntegrationException(
      $"Timed out waiting for the axis to complete the {what}; its state is now indeterminate — check GET {Route}/status before issuing another command.");
  }

  /// <summary>
  /// HOME completion for a machine whose HOME sets neither BUSY nor DONE (the baseline ladder
  /// without the SG layer — <see cref="HomeCompletionMode.Referenced"/>). "Not BUSY and
  /// referenced" alone is not enough: right after the edge, before the machine has dropped the
  /// reference bits of a previous HOME, it reads true and would conclude at once. So it only
  /// concludes after having seen the HOME start — BUSY=1 or a reference bit at 0.
  /// </summary>
  private async Task<StationStatus> PollForHomeByReferencingAsync(CancellationToken ct, ushort errorBefore)
  {
    var deadline = DateTime.UtcNow.AddSeconds(_options.HomeTimeoutSeconds);
    var started = false;

    while (DateTime.UtcNow < deadline)
    {
      var fault = await connection.ReadCoilAsync(addresses.Fault, ct);
      if (fault)
      {
        var errorCode = await connection.ReadUInt16Async(addresses.ErrorCode, ct);
        throw new ConflictException(
          $"The axis reported a fault while homing ({MachineErrors.Describe(errorCode)}). Call POST {Route}/fault-reset once the cause has ceased.");
      }

      // A DONE here means the machine does have the SG layer after all: take and ACK it.
      if (await connection.ReadCoilAsync(addresses.MoveDone, ct))
      {
        var reached = await ReadPositionAsync(ct);
        await connection.WriteCoilAsync(addresses.MoveDone, false, ct);
        return reached;
      }

      var busy = await connection.ReadCoilAsync(addresses.Busy, ct);
      var referenced = await IsReferencedAsync(ct);
      started |= busy || !referenced;

      if (started && !busy && referenced)
      {
        return await ReadPositionAsync(ct);
      }

      await Task.Delay(_options.CommandPollIntervalMs, ct);
    }

    throw new IntegrationException(
      $"Timed out waiting for the axis to finish homing; its state is now indeterminate — check GET {Route}/status before issuing another command.");
  }

  private async Task<StationStatus> ReadPositionAsync(CancellationToken ct)
  {
    var currentXPulses = await connection.ReadDwordAsync(addresses.CurrentX, ct);
    var currentYPulses = await connection.ReadDwordAsync(addresses.CurrentY, ct);
    int? currentZPulses = addresses.CurrentZ is int zAddress ? await connection.ReadDwordAsync(zAddress, ct) : null;

    return new StationStatus(
      AxisStatus.Ready,
      ToMillimeters(currentXPulses),
      ToMillimeters(currentYPulses),
      currentZPulses is int z ? ToMillimeters(z) : null,
      true,
      true,
      addresses.HasZ ? true : null);
  }

  private void EnsureEnabled()
  {
    if (!_options.Enabled)
    {
      throw new IntegrationException("Axis hardware integration is disabled (AxisHardware:Enabled=false).");
    }
  }

  // Commanding a machine that speaks another contract is worse than not commanding it: the
  // addresses this gateway writes would mean something else there. Only a machine positively
  // identified as incompatible is refused — an identity not read yet (session still opening)
  // is not a reason to block. STOP and FAULT_RESET never come through here.
  private void EnsureCompatibleMachine()
  {
    if (identity is { State: MachineIdentityState.Incompatible } id)
    {
      throw new ConflictException(
        $"Incompatible machine: build {id.BuildCode} / contract {id.ContractVersion}, this gateway speaks build " +
        $"{MachineAddresses.ExpectedBuildCode} / contract {MachineAddresses.ExpectedContractVersion}. Commands are refused; STOP still works.");
    }
  }

  // Speed is exchanged in the machine's own unit (pulses/s), never in the 0-100 the screen
  // shows: the mapping from that slider to a real speed depends on the mechanical calibration
  // that does not exist yet (plano R2, D2), and inventing it down here would bury the decision
  // where nobody could see it.
  private int ResolveSpeed(int? requested) =>
    requested is int speed && speed > 0 ? speed : _options.DefaultSpeedPulsesPerSecond;

  private decimal ToMillimeters(int pulses) => pulses / _options.PulsesPerMillimeter;

  private int ToPulses(decimal millimeters) =>
    (int)Math.Round(millimeters * _options.PulsesPerMillimeter, MidpointRounding.AwayFromZero);
}
