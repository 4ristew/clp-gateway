namespace SmartGlue.ClpGateway.Modbus;

// Fixed Modbus register/coil addresses for the "application" station — the glue applicator —
// as published in contract v2 (simulador/contracts/contrato_publico_clp.v2.json, kit 2.0.0;
// human-readable in 01_Simulador_Maquina_CLP/codigo_fonte/docs/MODBUS_ADDRESS_MAP.md).
// ContractV2Tests checks every constant here against that JSON.
//
// Same shape as InspectionStationAddresses, with the differences that matter to SGF-141:
//
//   * a THIRD AXIS. MOVE_XY captures X, Y and SPEED_XY; MOVE_Z captures Z and SPEED_Z. The
//     contract is explicit that neither request touches the other group's block, so moving to
//     a point and then lowering the applicator is two commands, never one.
//   * JOG bits, which are LEVEL-HELD, not pulses: 1 starts and keeps the axis moving, 0
//     releases it. See StationController.SetJogAsync for why that needs a deadman here.
//   * the soft limits are read_write by the external master (the inspection station's are
//     read-only for us). This gateway only ever READS them — writing the bounds a machine
//     will refuse to cross is a decision nobody has made yet (plano R2, D4).
//
// Addresses are a protocol contract, not business configuration, so they live in code rather
// than appsettings — same rule the inspection station follows.
public static class ApplicationStationAddresses
{
  // Coils (M) — requests are rising-edge pulses (write 1, then write 0); BUSY/FAULT are
  // read-only; MOVE_DONE is a latch only the external master may clear (write 0).
  public const int Home = 1350;
  public const int MoveXy = 1050;
  public const int MoveZ = 1051;
  public const int Stop = 1052;
  public const int FaultReset = 4007;

  public const int Busy = 4000;
  public const int MoveDone = 4001;
  public const int Fault = 4002;
  public const int ReferencedX = 4003;
  public const int ReferencedY = 4004;
  public const int ReferencedZ = 4005;

  // Level-held while the operator keeps the arrow pressed. Opposite directions may never be
  // active at the same time.
  public const int JogXNegative = 570;
  public const int JogXPositive = 580;
  public const int JogYNegative = 670;
  public const int JogYPositive = 680;

  // Holding registers (D) — signed DWORD, 2 registers, low-word-first, pulses (or pulses/s
  // for speed). ErrorCode is a single UINT16 register (one of the four contract corrections
  // that only showed up against the real machine: reading it with qty=2 returns exception
  // 0x02).
  public const int TargetX = 1000;
  public const int TargetY = 1200;
  public const int TargetZ = 1400;
  public const int SpeedXy = 20500;
  public const int SpeedZ = 21500;
  public const int CurrentX = 3000;
  public const int CurrentY = 3200;
  public const int CurrentZ = 3400;
  public const int ErrorCode = 24200;

  // Soft limits — the real ones of the baseline ladder, D23060..D23120. In contract v1 they
  // sat at D21600..D21850, which v2 gives to the JOG speeds below: a gateway still reading the
  // old block validates every move against ±(jog speed). The order is NOT X/Y/Z min/max; it is
  // the order the ladder declares them in.
  public const int LimitYMin = 23060;
  public const int LimitXMin = 23070;
  public const int LimitZMax = 23080;
  public const int LimitYMax = 23100;
  public const int LimitXMax = 23110;
  public const int LimitZMin = 23120;

  // DJOG frequency retained per jog bit, in pulses/s WITH SIGN: on the real PLC the sign of
  // the register, not the bit, sets the direction. A *_NEGATIVE register holding a positive
  // value drives the axis the wrong way, against the wrong end stop. The gateway reads and
  // checks these, never writes them (they are the machine's jog speed, not ours to decide).
  public const int JogSpeedXNegative = 21600;
  public const int JogSpeedXPositive = 21650;
  public const int JogSpeedYNegative = 21700;
  public const int JogSpeedYPositive = 21750;
  public const int JogSpeedZNegative = 21800;
  public const int JogSpeedZPositive = 21850;
}
