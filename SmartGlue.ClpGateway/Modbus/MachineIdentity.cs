namespace SmartGlue.ClpGateway.Modbus;

public enum MachineIdentityState
{
  /// <summary>Not read yet: no session has opened since the process started or reconnected.</summary>
  Unknown,
  Compatible,
  Incompatible
}

/// <summary>
/// What the machine said it is when the session opened: the build of the ladder (D19984/D19985)
/// and the version of the contract (D24508). Written by <see cref="AxisHeartbeatService"/>,
/// read by every command (<c>StationController.EnsureCompatibleMachine</c>) and by the health
/// check.
///
/// A singleton, because there is one machine. Before contract v2 nothing read these registers,
/// and a gateway pointed at a simulator 0.1.0 or an older ladder "worked" halfway — reading jog
/// speeds as limits, homing without acknowledging DONE — instead of saying it could not.
/// </summary>
public sealed class MachineIdentity
{
  private readonly object _gate = new();

  public MachineIdentityState State { get; private set; } = MachineIdentityState.Unknown;
  public int BuildCode { get; private set; }
  public ushort ContractVersion { get; private set; }

  /// <summary>Last time the heartbeat echo (D19982) was seen tracking what was written.</summary>
  public bool EchoStale { get; private set; }

  public void Record(int buildCode, ushort contractVersion)
  {
    lock (_gate)
    {
      BuildCode = buildCode;
      ContractVersion = contractVersion;
      State = buildCode == MachineAddresses.ExpectedBuildCode && contractVersion == MachineAddresses.ExpectedContractVersion
        ? MachineIdentityState.Compatible
        : MachineIdentityState.Incompatible;
    }
  }

  public void SetEchoStale(bool stale)
  {
    lock (_gate)
    {
      EchoStale = stale;
    }
  }

  /// <summary>A dropped socket means the next session may be talking to a different machine.</summary>
  public void Forget()
  {
    lock (_gate)
    {
      State = MachineIdentityState.Unknown;
      EchoStale = false;
    }
  }
}
