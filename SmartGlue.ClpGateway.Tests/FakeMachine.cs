using SmartGlue.ClpGateway.Modbus;

namespace SmartGlue.ClpGateway.Tests;

/// <summary>
/// An in-process stand-in for the machine, behind the same <see cref="IAxisConnection"/> the
/// real socket implements.
///
/// This is what makes the command cycle testable without the SmartGlue_Machine_Mock running —
/// the gap that MR!14 left and that matters far more now that the gateway executes routines
/// unattended. It mimics the kit simulator 0.2.0 (contract v2):
///
///   * a MOVE_XY / MOVE_Z edge lands the axis on the target and raises MOVE_DONE at once — an
///     out-of-range target SATURATES at the limit (no 1002), as the ladder does;
///   * HOME clears the reference bits, stays BUSY for <see cref="HomeDurationReads"/> reads of
///     BUSY, then references every axis and raises MOVE_DONE;
///   * an edge with BUSY=1, MOVE_DONE=1 or the station unreferenced is REFUSED WITHOUT FAULT
///     (1004, 1005, 1001): the code shows in the error register and nothing moves; an
///     accepted command clears it;
///   * STOP raises FAULT 1006;
///   * the identity registers say build 202607281, contract 2; the jog speeds are signed.
/// </summary>
public sealed class FakeMachine : IAxisConnection
{
  private readonly Dictionary<int, bool> _coils = [];
  private readonly Dictionary<int, int> _dwords = [];
  private readonly Dictionary<int, ushort> _words = [];
  private readonly SemaphoreSlim _commandGate = new(1, 1);

  // Reads of BUSY left before an in-flight HOME completes, per station (keyed by BUSY address).
  private readonly Dictionary<int, int> _homeRemaining = [];

  public List<string> Log { get; } = [];

  /// <summary>When true, MOVE_XY leaves the axis BUSY forever, so the poller times out.</summary>
  public bool StallMoves { get; set; }

  /// <summary>How many reads of BUSY a HOME stays BUSY for before it completes. 0 = at once.</summary>
  public int HomeDurationReads { get; set; }

  /// <summary>Raised on every MOVE_XY pulse, so a test can interleave with an in-flight command.</summary>
  public event Action? MovePulsed;

  /// <summary>
  /// When false, every register access throws, as an unplugged socket would. Lets a test drive
  /// the paths that only run when the machine cannot be reached at all — distinct from a
  /// machine that answers and reports FAULT.
  /// </summary>
  public bool IsReachable { get; set; } = true;

  public bool IsConnected => IsReachable;

  public FakeMachine()
  {
    _dwords[InspectionStationAddresses.LimitXMin] = -100_000;
    _dwords[InspectionStationAddresses.LimitXMax] = 100_000;
    _dwords[InspectionStationAddresses.LimitYMin] = -100_000;
    _dwords[InspectionStationAddresses.LimitYMax] = 100_000;

    // The application station (SGF-141) — same bounds in X/Y, plus the Z the applicator has.
    // Contract v2 addresses: D23060..D23120, in the ladder's order.
    _dwords[ApplicationStationAddresses.LimitXMin] = -100_000;
    _dwords[ApplicationStationAddresses.LimitXMax] = 100_000;
    _dwords[ApplicationStationAddresses.LimitYMin] = -100_000;
    _dwords[ApplicationStationAddresses.LimitYMax] = 100_000;
    _dwords[ApplicationStationAddresses.LimitZMin] = -50_000;
    _dwords[ApplicationStationAddresses.LimitZMax] = 50_000;

    // What used to be the limits block in v1: the DJOG speeds, signed, as the simulator seeds
    // them. A gateway still reading limits here would see ±10 mm.
    _dwords[ApplicationStationAddresses.JogSpeedXNegative] = -10_000;
    _dwords[ApplicationStationAddresses.JogSpeedXPositive] = 10_000;
    _dwords[ApplicationStationAddresses.JogSpeedYNegative] = -10_000;
    _dwords[ApplicationStationAddresses.JogSpeedYPositive] = 10_000;
    _dwords[ApplicationStationAddresses.JogSpeedZNegative] = -5_000;
    _dwords[ApplicationStationAddresses.JogSpeedZPositive] = 5_000;

    SetIdentity(MachineAddresses.ExpectedBuildCode, MachineAddresses.ExpectedContractVersion);
  }

  /// <summary>What the machine publishes as build (D19984) and contract version (D24508).</summary>
  public void SetIdentity(int buildCode, ushort contractVersion)
  {
    _dwords[MachineAddresses.BuildCode] = buildCode;
    _words[MachineAddresses.ContractVersion] = contractVersion;
  }

  public int HeartbeatCounter => _dwords.GetValueOrDefault(MachineAddresses.HeartbeatCounter);

  /// <summary>When true, the heartbeat echo stops following the counter.</summary>
  public bool FreezeHeartbeatEcho { get; set; }

  public void SetDword(int address, int value) => _dwords[address] = value;

  public int Dword(int address) => _dwords.GetValueOrDefault(address);

  public bool Fault
  {
    get => _coils.GetValueOrDefault(InspectionStationAddresses.Fault);
    set => _coils[InspectionStationAddresses.Fault] = value;
  }

  public bool Busy
  {
    get => _coils.GetValueOrDefault(InspectionStationAddresses.Busy);
    set => _coils[InspectionStationAddresses.Busy] = value;
  }

  public bool MoveDone => _coils.GetValueOrDefault(InspectionStationAddresses.MoveDone);

  /// <summary>Leaves a completion unacknowledged, as a master that went away would.</summary>
  public void SetMoveDone(bool value) => _coils[InspectionStationAddresses.MoveDone] = value;

  public ushort ErrorCode
  {
    get => _words.GetValueOrDefault(InspectionStationAddresses.ErrorCode);
    set => _words[InspectionStationAddresses.ErrorCode] = value;
  }

  /// <summary>X, Y and — since the machine requires it with z_axis_mode=simulated — Z.</summary>
  public void SetReferenced(bool referenced)
  {
    _coils[InspectionStationAddresses.ReferencedX] = referenced;
    _coils[InspectionStationAddresses.ReferencedY] = referenced;
    _coils[InspectionStationAddresses.ReferencedZ] = referenced;
  }

  /// <summary>The state a HOME stopped after Y leaves behind.</summary>
  public void SetInspectionReferencedZ(bool referenced) => _coils[InspectionStationAddresses.ReferencedZ] = referenced;

  public int CurrentX => _dwords.GetValueOrDefault(InspectionStationAddresses.CurrentX);
  public int CurrentY => _dwords.GetValueOrDefault(InspectionStationAddresses.CurrentY);

  /// <summary>Parks the axis at a known position, without going through a command cycle.</summary>
  public void SetPosition(int xPulses, int yPulses)
  {
    _dwords[InspectionStationAddresses.CurrentX] = xPulses;
    _dwords[InspectionStationAddresses.CurrentY] = yPulses;
  }

  // ─── Estação de aplicação (SGF-141) ────────────────────────────────────────

  public bool ApplicationFault
  {
    get => _coils.GetValueOrDefault(ApplicationStationAddresses.Fault);
    set => _coils[ApplicationStationAddresses.Fault] = value;
  }

  public bool ApplicationBusy
  {
    get => _coils.GetValueOrDefault(ApplicationStationAddresses.Busy);
    set => _coils[ApplicationStationAddresses.Busy] = value;
  }

  public bool ApplicationMoveDone => _coils.GetValueOrDefault(ApplicationStationAddresses.MoveDone);

  public ushort ApplicationErrorCode
  {
    get => _words.GetValueOrDefault(ApplicationStationAddresses.ErrorCode);
    set => _words[ApplicationStationAddresses.ErrorCode] = value;
  }

  /// <summary>The applicator has three axes, and Z counts for referencing just like X and Y.</summary>
  public void SetApplicationReferenced(bool referenced)
  {
    _coils[ApplicationStationAddresses.ReferencedX] = referenced;
    _coils[ApplicationStationAddresses.ReferencedY] = referenced;
    _coils[ApplicationStationAddresses.ReferencedZ] = referenced;
  }

  public int ApplicationCurrentX => _dwords.GetValueOrDefault(ApplicationStationAddresses.CurrentX);
  public int ApplicationCurrentY => _dwords.GetValueOrDefault(ApplicationStationAddresses.CurrentY);
  public int ApplicationCurrentZ => _dwords.GetValueOrDefault(ApplicationStationAddresses.CurrentZ);

  public void SetApplicationPosition(int xPulses, int yPulses, int zPulses)
  {
    _dwords[ApplicationStationAddresses.CurrentX] = xPulses;
    _dwords[ApplicationStationAddresses.CurrentY] = yPulses;
    _dwords[ApplicationStationAddresses.CurrentZ] = zPulses;
  }

  public bool IsJogHeld(int coil) => _coils.GetValueOrDefault(coil);

  public int PulseCount(int address) => Log.Count(entry => entry == $"pulse:{address}");

  public bool Wrote(int address) => Log.Any(entry => entry.StartsWith($"dword:{address}="));

  public async Task<IDisposable> AcquireCommandLockAsync(CancellationToken ct)
  {
    await _commandGate.WaitAsync(ct);
    return new Releaser(_commandGate);
  }

  private sealed class Releaser(SemaphoreSlim gate) : IDisposable
  {
    private bool _released;
    public void Dispose()
    {
      if (_released) return;
      _released = true;
      gate.Release();
    }
  }

  public Task<bool> ReadCoilAsync(int address, CancellationToken ct)
  {
    EnsureReachable();

    // A HOME in flight advances one step per read of its station's BUSY.
    if (_homeRemaining.TryGetValue(address, out var remaining))
    {
      if (remaining <= 0)
      {
        CompleteHome(address);
      }
      else
      {
        _homeRemaining[address] = remaining - 1;
      }
    }

    return Task.FromResult(_coils.GetValueOrDefault(address));
  }

  public Task WriteCoilAsync(int address, bool value, CancellationToken ct)
  {
    EnsureReachable();
    Log.Add($"coil:{address}={value}");
    _coils[address] = value;
    return Task.CompletedTask;
  }

  /// <summary>When set, thrown by the next request-bit pulse — as the simulator's Modbus exception 03 would.</summary>
  public Exception? RefuseNextPulseWith { get; set; }

  public async Task PulseCoilAsync(int address, CancellationToken ct)
  {
    EnsureReachable();

    if (RefuseNextPulseWith is { } refusal)
    {
      RefuseNextPulseWith = null;
      throw refusal;
    }

    Log.Add($"pulse:{address}");
    await WriteCoilAsync(address, true, ct);
    Actuate(address);
    await WriteCoilAsync(address, false, ct);
  }

  private sealed record Station(
    int Busy, int Done, int Fault, int ErrorCode,
    int[] Referenced,
    int CurrentX, int CurrentY, int? CurrentZ,
    int TargetX, int TargetY, int? TargetZ,
    int LimitXMin, int LimitXMax, int LimitYMin, int LimitYMax, int? LimitZMin, int? LimitZMax);

  private static readonly Station Inspection = new(
    InspectionStationAddresses.Busy, InspectionStationAddresses.MoveDone, InspectionStationAddresses.Fault, InspectionStationAddresses.ErrorCode,
    [InspectionStationAddresses.ReferencedX, InspectionStationAddresses.ReferencedY, InspectionStationAddresses.ReferencedZ],
    InspectionStationAddresses.CurrentX, InspectionStationAddresses.CurrentY, null,
    InspectionStationAddresses.TargetX, InspectionStationAddresses.TargetY, null,
    InspectionStationAddresses.LimitXMin, InspectionStationAddresses.LimitXMax, InspectionStationAddresses.LimitYMin, InspectionStationAddresses.LimitYMax, null, null);

  private static readonly Station Application = new(
    ApplicationStationAddresses.Busy, ApplicationStationAddresses.MoveDone, ApplicationStationAddresses.Fault, ApplicationStationAddresses.ErrorCode,
    [ApplicationStationAddresses.ReferencedX, ApplicationStationAddresses.ReferencedY, ApplicationStationAddresses.ReferencedZ],
    ApplicationStationAddresses.CurrentX, ApplicationStationAddresses.CurrentY, ApplicationStationAddresses.CurrentZ,
    ApplicationStationAddresses.TargetX, ApplicationStationAddresses.TargetY, ApplicationStationAddresses.TargetZ,
    ApplicationStationAddresses.LimitXMin, ApplicationStationAddresses.LimitXMax, ApplicationStationAddresses.LimitYMin, ApplicationStationAddresses.LimitYMax,
    ApplicationStationAddresses.LimitZMin, ApplicationStationAddresses.LimitZMax);

  private void Actuate(int address)
  {
    switch (address)
    {
      case InspectionStationAddresses.MoveXy:
        MovePulsed?.Invoke();
        MoveXy(Inspection);
        break;

      case InspectionStationAddresses.Home:
        Home(Inspection);
        break;

      case InspectionStationAddresses.Stop:
        _homeRemaining.Remove(Inspection.Busy);
        Busy = false;
        Fault = true;
        ErrorCode = MachineErrors.Stopped;
        break;

      case InspectionStationAddresses.FaultReset:
        Fault = false;
        ErrorCode = 0;
        break;

      // ─── Estação de aplicação ────────────────────────────────────────────
      // MOVE_XY captures X, Y and SPEED_XY and nothing of the Z block; MOVE_Z captures Z and
      // SPEED_Z and nothing of the XY block. Mixing them here would let a bug that writes to
      // the wrong block still pass.
      case ApplicationStationAddresses.MoveXy:
        MovePulsed?.Invoke();
        MoveXy(Application);
        break;

      case ApplicationStationAddresses.MoveZ:
        MoveZ(Application);
        break;

      case ApplicationStationAddresses.Home:
        Home(Application);
        break;

      case ApplicationStationAddresses.Stop:
        _homeRemaining.Remove(Application.Busy);
        ApplicationBusy = false;
        ApplicationFault = true;
        ApplicationErrorCode = MachineErrors.Stopped;
        break;

      case ApplicationStationAddresses.FaultReset:
        ApplicationFault = false;
        ApplicationErrorCode = 0;
        break;
    }
  }

  // PRODUCTIVE_GUARD = rising_edge AND BUSY=0 AND DONE=0 AND FAULT=0 — and a refusal never
  // raises FAULT, it only publishes the code.
  private bool Guard(Station s, bool requireReferenced)
  {
    if (_coils.GetValueOrDefault(s.Fault)) return false;

    ushort? refusal =
      _coils.GetValueOrDefault(s.Busy) ? MachineErrors.CommandWhileBusy
      : _coils.GetValueOrDefault(s.Done) ? MachineErrors.DoneNotAcknowledged
      : requireReferenced && !s.Referenced.All(r => _coils.GetValueOrDefault(r)) ? MachineErrors.Unhomed
      : null;

    if (refusal is ushort code)
    {
      _words[s.ErrorCode] = code;
      return false;
    }

    _words[s.ErrorCode] = 0;
    return true;
  }

  private void MoveXy(Station s)
  {
    if (StallMoves)
    {
      _coils[s.Busy] = true;
      return;
    }

    if (!Guard(s, requireReferenced: true)) return;

    _dwords[s.CurrentX] = Saturate(_dwords.GetValueOrDefault(s.TargetX), s.LimitXMin, s.LimitXMax);
    _dwords[s.CurrentY] = Saturate(_dwords.GetValueOrDefault(s.TargetY), s.LimitYMin, s.LimitYMax);
    _coils[s.Done] = true;
  }

  private void MoveZ(Station s)
  {
    if (StallMoves)
    {
      _coils[s.Busy] = true;
      return;
    }

    if (!Guard(s, requireReferenced: true)) return;

    _dwords[s.CurrentZ!.Value] = Saturate(_dwords.GetValueOrDefault(s.TargetZ!.Value), s.LimitZMin!.Value, s.LimitZMax!.Value);
    _coils[s.Done] = true;
  }

  // Ladder order: max first, then min.
  private int Saturate(int target, int minAddress, int maxAddress)
  {
    var max = _dwords.GetValueOrDefault(maxAddress);
    var min = _dwords.GetValueOrDefault(minAddress);
    return Math.Max(Math.Min(target, max), min);
  }

  private void Home(Station s)
  {
    if (!Guard(s, requireReferenced: false)) return;

    foreach (var r in s.Referenced) _coils[r] = false;

    if (HomeDurationReads <= 0)
    {
      CompleteHome(s.Busy);
      return;
    }

    _coils[s.Busy] = true;
    _homeRemaining[s.Busy] = HomeDurationReads;
  }

  private void CompleteHome(int busyAddress)
  {
    _homeRemaining.Remove(busyAddress);
    var s = busyAddress == Application.Busy ? Application : Inspection;

    foreach (var r in s.Referenced) _coils[r] = true;
    _dwords[s.CurrentX] = 0;
    _dwords[s.CurrentY] = 0;
    if (s.CurrentZ is int z) _dwords[z] = 0;

    _coils[s.Busy] = false;
    _coils[s.Done] = true;
  }

  public Task<int> ReadDwordAsync(int address, CancellationToken ct)
  {
    EnsureReachable();
    return Task.FromResult(_dwords.GetValueOrDefault(address));
  }

  public Task WriteDwordAsync(int address, int value, CancellationToken ct)
  {
    EnsureReachable();
    Log.Add($"dword:{address}={value}");
    _dwords[address] = value;

    if (address == MachineAddresses.HeartbeatCounter && !FreezeHeartbeatEcho)
    {
      _dwords[MachineAddresses.HeartbeatEcho] = value;
    }

    return Task.CompletedTask;
  }

  public Task<ushort> ReadUInt16Async(int address, CancellationToken ct)
  {
    EnsureReachable();
    return Task.FromResult(_words.GetValueOrDefault(address));
  }

  private void EnsureReachable()
  {
    if (!IsReachable)
    {
      throw new IOException("The fake machine is unreachable.");
    }
  }
}
