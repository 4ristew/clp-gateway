namespace SmartGlue.ClpGateway.Api.DTOs;

/// <summary>
/// Status of the application (glue applicator) station. Same shape as
/// <see cref="AxisStatusResponse"/> plus what the applicator has and the camera station does
/// not: a Z axis, and the per-axis referencing flags — the desktop needs to tell "not homed
/// yet" apart from "homed and sitting at zero", which a coordinate alone cannot say.
/// </summary>
public record ApplicationStatusResponse(
  AxisStatus Status,
  decimal PositionX,
  decimal PositionY,
  decimal PositionZ,
  bool ReferencedX,
  bool ReferencedY,
  bool ReferencedZ,
  bool ReachedTarget = true);

/// <summary>Soft limits of the application station, in millimetres, read live from the machine.</summary>
public record ApplicationLimitsResponse(
  decimal XMin, decimal XMax,
  decimal YMin, decimal YMax,
  decimal ZMin, decimal ZMax);

/// <summary>
/// MOVE_XY on the application station. Speed is optional and, when given, is in the machine's
/// own unit — pulses per second. The 0-100 the screen shows is not a speed and must not be
/// sent here: what it maps to depends on a mechanical calibration that does not exist yet
/// (plano R2, D2).
/// </summary>
public record MoveApplicationRequest(decimal PositionX, decimal PositionY, int? SpeedPulsesPerSecond);

/// <summary>
/// MOVE_Z on the application station. A separate request by contract: MOVE_XY captures only
/// X, Y and SPEED_XY, and never touches the Z block.
/// </summary>
public record MoveApplicationZRequest(decimal PositionZ, int? SpeedPulsesPerSecond);

/// <summary>
/// One level-held jog step. <c>Active</c> false releases; true holds and must be refreshed
/// while the operator keeps the arrow pressed, otherwise the gateway's deadman releases the
/// axis on its own (AxisHardware:JogDeadmanMs).
/// </summary>
public record JogApplicationRequest(string Axis, int Direction, bool Active);
