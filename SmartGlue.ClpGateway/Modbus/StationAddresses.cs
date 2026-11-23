namespace SmartGlue.ClpGateway.Modbus;

/// <summary>
/// The two stations this gateway masters, named as the machine names them.
/// </summary>
public enum StationId
{
  Inspection,
  Application
}

/// <summary>
/// One station's address set, so the command lifecycle can be written once and pointed at
/// either station (see <c>StationController</c>).
///
/// The per-station constants stay in <see cref="InspectionStationAddresses"/> and
/// <see cref="ApplicationStationAddresses"/> — they are the published contract and are read
/// directly by tests, which need them to be compile-time constants. This record is the runtime
/// view of the same numbers, nothing more.
///
/// Z and JOG are nullable because the contract does not give every station the same machine:
/// the gateway drives the inspection station in X/Y only (a camera frames a plane), while the
/// applicator has a Z axis and jog arrows. A station that lacks a capability says so here
/// instead of answering with an address that would silently write somewhere wrong.
/// </summary>
public sealed record StationAddresses
{
  public required StationId Id { get; init; }

  /// <summary>Lowercase station name, as used in URLs and in the camera server's paths.</summary>
  public required string Name { get; init; }

  // Commands
  public required int Home { get; init; }
  public required int MoveXy { get; init; }
  public int? MoveZ { get; init; }
  public required int Stop { get; init; }
  public required int FaultReset { get; init; }

  // Status
  public required int Busy { get; init; }
  public required int MoveDone { get; init; }
  public required int Fault { get; init; }
  public required int ReferencedX { get; init; }
  public required int ReferencedY { get; init; }
  public int? ReferencedZ { get; init; }

  /// <summary>
  /// The Z reference bit that has to be on before the machine accepts a move. Separate from
  /// <see cref="ReferencedZ"/>, which is what the status reports: the inspection Z is not a
  /// capability this gateway offers (no status field, no command), but the machine still
  /// refuses X/Y moves with 1001 while it is unreferenced.
  /// </summary>
  public int? ReferencingZ { get; init; }

  // Level-held jog, when the station has it.
  public int? JogXNegative { get; init; }
  public int? JogXPositive { get; init; }
  public int? JogYNegative { get; init; }
  public int? JogYPositive { get; init; }

  // The signed DJOG frequency retained for each jog bit (contract v2). Read and checked, never
  // written — see StationController.SetJogAsync.
  public int? JogSpeedXNegative { get; init; }
  public int? JogSpeedXPositive { get; init; }
  public int? JogSpeedYNegative { get; init; }
  public int? JogSpeedYPositive { get; init; }

  // Registers
  public required int TargetX { get; init; }
  public required int TargetY { get; init; }
  public int? TargetZ { get; init; }
  public required int SpeedXy { get; init; }
  public int? SpeedZ { get; init; }
  public required int CurrentX { get; init; }
  public required int CurrentY { get; init; }
  public int? CurrentZ { get; init; }
  public required int LimitXMin { get; init; }
  public required int LimitXMax { get; init; }
  public required int LimitYMin { get; init; }
  public required int LimitYMax { get; init; }
  public int? LimitZMin { get; init; }
  public int? LimitZMax { get; init; }
  public required int ErrorCode { get; init; }

  public bool HasZ => MoveZ is not null;

  public bool HasJog => JogXNegative is not null;

  public static readonly StationAddresses Inspection = new()
  {
    Id = StationId.Inspection,
    Name = "inspection",
    Home = InspectionStationAddresses.Home,
    MoveXy = InspectionStationAddresses.MoveXy,
    // The inspection station does have a Z axis in the contract (M5002/D24004), but this
    // gateway has never commanded it and the desktop has no control for it. Declaring it here
    // would offer a capability nothing exercises; it stays out until something needs it.
    // Its REFERENCE does count, though: see ReferencingZ below.
    Stop = InspectionStationAddresses.Stop,
    FaultReset = InspectionStationAddresses.FaultReset,
    Busy = InspectionStationAddresses.Busy,
    MoveDone = InspectionStationAddresses.MoveDone,
    Fault = InspectionStationAddresses.Fault,
    ReferencedX = InspectionStationAddresses.ReferencedX,
    ReferencedY = InspectionStationAddresses.ReferencedY,
    ReferencingZ = InspectionStationAddresses.ReferencedZ,
    TargetX = InspectionStationAddresses.TargetX,
    TargetY = InspectionStationAddresses.TargetY,
    SpeedXy = InspectionStationAddresses.SpeedXy,
    CurrentX = InspectionStationAddresses.CurrentX,
    CurrentY = InspectionStationAddresses.CurrentY,
    LimitXMin = InspectionStationAddresses.LimitXMin,
    LimitXMax = InspectionStationAddresses.LimitXMax,
    LimitYMin = InspectionStationAddresses.LimitYMin,
    LimitYMax = InspectionStationAddresses.LimitYMax,
    ErrorCode = InspectionStationAddresses.ErrorCode,
  };

  public static readonly StationAddresses Application = new()
  {
    Id = StationId.Application,
    Name = "application",
    Home = ApplicationStationAddresses.Home,
    MoveXy = ApplicationStationAddresses.MoveXy,
    MoveZ = ApplicationStationAddresses.MoveZ,
    Stop = ApplicationStationAddresses.Stop,
    FaultReset = ApplicationStationAddresses.FaultReset,
    Busy = ApplicationStationAddresses.Busy,
    MoveDone = ApplicationStationAddresses.MoveDone,
    Fault = ApplicationStationAddresses.Fault,
    ReferencedX = ApplicationStationAddresses.ReferencedX,
    ReferencedY = ApplicationStationAddresses.ReferencedY,
    ReferencedZ = ApplicationStationAddresses.ReferencedZ,
    ReferencingZ = ApplicationStationAddresses.ReferencedZ,
    JogXNegative = ApplicationStationAddresses.JogXNegative,
    JogXPositive = ApplicationStationAddresses.JogXPositive,
    JogYNegative = ApplicationStationAddresses.JogYNegative,
    JogYPositive = ApplicationStationAddresses.JogYPositive,
    JogSpeedXNegative = ApplicationStationAddresses.JogSpeedXNegative,
    JogSpeedXPositive = ApplicationStationAddresses.JogSpeedXPositive,
    JogSpeedYNegative = ApplicationStationAddresses.JogSpeedYNegative,
    JogSpeedYPositive = ApplicationStationAddresses.JogSpeedYPositive,
    TargetX = ApplicationStationAddresses.TargetX,
    TargetY = ApplicationStationAddresses.TargetY,
    TargetZ = ApplicationStationAddresses.TargetZ,
    SpeedXy = ApplicationStationAddresses.SpeedXy,
    SpeedZ = ApplicationStationAddresses.SpeedZ,
    CurrentX = ApplicationStationAddresses.CurrentX,
    CurrentY = ApplicationStationAddresses.CurrentY,
    CurrentZ = ApplicationStationAddresses.CurrentZ,
    LimitXMin = ApplicationStationAddresses.LimitXMin,
    LimitXMax = ApplicationStationAddresses.LimitXMax,
    LimitYMin = ApplicationStationAddresses.LimitYMin,
    LimitYMax = ApplicationStationAddresses.LimitYMax,
    LimitZMin = ApplicationStationAddresses.LimitZMin,
    LimitZMax = ApplicationStationAddresses.LimitZMax,
    ErrorCode = ApplicationStationAddresses.ErrorCode,
  };

  public static StationAddresses For(StationId id) =>
    id == StationId.Application ? Application : Inspection;
}
