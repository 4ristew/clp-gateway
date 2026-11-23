namespace SmartGlue.ClpGateway.Modbus;

// Fixed Modbus register/coil addresses for the "inspection" station, as published in contract
// v2 (simulador/contracts/contrato_publico_clp.v2.json, kit 2.0.0; human-readable in
// 01_Simulador_Maquina_CLP/codigo_fonte/docs/MODBUS_ADDRESS_MAP.md). This is a protocol
// contract, not business configuration, so it lives in code rather than appsettings.
// ContractV2Tests checks every constant here against that JSON.
//
// Ported from smt_glue/Features/Axis/Modbus/InspectionStationAddresses.cs (MR!14).
public static class InspectionStationAddresses
{
  // Coils (M) — requests are rising-edge pulses (write 1, then write 0); BUSY/FAULT are
  // read-only; MOVE_DONE is a latch only the external master may clear (write 0).
  public const int Home = 5000;
  public const int MoveXy = 5001;
  public const int Stop = 5003;
  public const int FaultReset = 5004;

  public const int Busy = 5010;
  public const int MoveDone = 5011;
  public const int Fault = 5012;
  public const int ReferencedX = 5013;
  public const int ReferencedY = 5014;

  // The inspection Z is never commanded by this gateway, but with z_axis_mode=simulated (the
  // kit default) the machine refuses every move with 1001 UNHOMED until Z is referenced too.
  // A HOME stopped after Y leaves X/Y referenced and Z not — so referencing has to look at it.
  public const int ReferencedZ = 5015;

  // Holding registers (D) — signed DWORD, 2 registers, low-word-first, pulses (or pulses/s
  // for speed). ErrorCode is a single UINT16 register.
  public const int TargetX = 24000;
  public const int TargetY = 24002;
  public const int SpeedXy = 24006;
  public const int CurrentX = 24010;
  public const int CurrentY = 24012;
  public const int LimitXMin = 24016;
  public const int LimitXMax = 24018;
  public const int LimitYMin = 24020;
  public const int LimitYMax = 24022;
  public const int ErrorCode = 24028;
}

// What belongs to the machine as a whole and not to a station: the watchdog, shared by every
// station (one counter expiring latches 1009 on all of them), and the identity the machine
// publishes so a master can tell which contract it is talking to.
public static class MachineAddresses
{
  // Watchdog — signed DWORDs, low-word-first.
  public const int HeartbeatCounter = 19980;
  public const int HeartbeatEcho = 19982;

  // Identity. The build is the real ladder's (COMANDOS_E_STATUS rede 40: DMOV K202607281
  // D19984), a DWORD since contract v2 — v1 had a UINT16 101 here. The contract version is a
  // single UINT16.
  public const int BuildCode = 19984;
  public const int ContractVersion = 24508;

  public const int ExpectedBuildCode = 202607281;
  public const ushort ExpectedContractVersion = 2;
}

// Error catalog from simulador/01_Simulador_Maquina_CLP/codigo_fonte/docs/ERROR_CATALOG.md
// (contract v2). Application and inspection share the codes; only the register differs.
public static class MachineErrors
{
  public const ushort Unhomed = 1001;
  // Never raised by the stations in contract v2: an out-of-range target saturates at the
  // limit instead. Kept because the catalog keeps it and an older machine may still send it.
  public const ushort TargetOutOfRange = 1002;
  public const ushort InvalidSpeed = 1003;
  public const ushort CommandWhileBusy = 1004;
  public const ushort DoneNotAcknowledged = 1005;
  public const ushort Stopped = 1006;
  public const ushort FaultActive = 1007;
  public const ushort IncompleteParameters = 1008;
  public const ushort HeartbeatLost = 1009;
  public const ushort InvalidState = 1010;
  // Simulator only (the ladder has no collision detection): the needle tip went past the
  // board contact + tolerance while moving +Z.
  public const ushort NeedleCollision = 1011;
  // Simulator only (RESERVED dispense addresses).
  public const ushort DispenseNotAllowed = 1012;

  /// <summary>
  /// Codes the machine answers WITHOUT raising FAULT: the request is refused, the code shows up
  /// in the error register, and nothing else happens. A poller that only watches FAULT and
  /// MOVE_DONE waits out its whole timeout for one of these.
  /// </summary>
  public static bool IsRefusalWithoutFault(ushort code) => code is
    Unhomed or CommandWhileBusy or DoneNotAcknowledged or IncompleteParameters or InvalidState or DispenseNotAllowed;

  public static string Describe(ushort code) => code switch
  {
    0 => "0 NONE",
    Unhomed => "1001 UNHOMED",
    TargetOutOfRange => "1002 TARGET_OUT_OF_RANGE",
    InvalidSpeed => "1003 INVALID_SPEED",
    CommandWhileBusy => "1004 COMMAND_WHILE_BUSY",
    DoneNotAcknowledged => "1005 DONE_NOT_ACKNOWLEDGED",
    Stopped => "1006 STOPPED",
    FaultActive => "1007 FAULT_ACTIVE",
    IncompleteParameters => "1008 INCOMPLETE_PARAMETERS",
    HeartbeatLost => "1009 HEARTBEAT_LOST",
    InvalidState => "1010 INVALID_STATE",
    NeedleCollision => "1011 NEEDLE_COLLISION — pulse fault-reset, then retract Z (towards −Z) before any other move",
    DispenseNotAllowed => "1012 DISPENSE_NOT_ALLOWED",
    _ => $"unknown error code {code}"
  };
}
