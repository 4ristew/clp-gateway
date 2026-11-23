namespace SmartGlue.ClpGateway.Api.DTOs;

public enum AxisStatus
{
  Ready,
  Moving,
  CommunicationFailure,
  // Machine-side fault (e.g. target out of range, command issued while busy) — as opposed to
  // CommunicationFailure, which means the hardware/simulator could not be reached at all.
  // Clear it with POST /axis/fault-reset once the cause has ceased.
  Fault
}

/// <param name="ReachedTarget">
/// False when a move completed away from its target: in contract v2 the machine saturates an
/// out-of-range target at the soft limit and still reports MOVE_DONE.
/// </param>
public record AxisStatusResponse(AxisStatus Status, decimal PositionX, decimal PositionY, bool ReachedTarget = true);

public record AxisLimitsResponse(decimal XMin, decimal XMax, decimal YMin, decimal YMax);

public record MoveAxisRequest(decimal PositionX, decimal PositionY);

/// <summary>
/// One captured camera frame plus the geometry that locates it on the board.
///
/// The image is base64 instead of a separate binary endpoint on purpose: the frame and the
/// position it was taken at have to travel together. Split into two calls, a frame fetched
/// while the axis is moving would be paired with a position read a moment later, and a click
/// converted against that pair would target the wrong place.
///
/// Pixel -> millimetre, as the desktop applies it:
///   x_mm = CenterXMm + (pixelX - WidthPixels  / 2) * FieldWidthMm  / WidthPixels
///   y_mm = CenterYMm + (pixelY - HeightPixels / 2) * FieldHeightMm / HeightPixels
/// </summary>
public record CameraSnapshotResponse(
  string ImageBase64,
  string ContentType,
  int WidthPixels,
  int HeightPixels,
  decimal CenterXMm,
  decimal CenterYMm,
  decimal FieldWidthMm,
  decimal FieldHeightMm,
  DateTime CapturedAt);
