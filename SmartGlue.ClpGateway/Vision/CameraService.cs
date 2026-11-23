using Microsoft.Extensions.Options;
using SmartGlue.ClpGateway.Api.DTOs;
using SmartGlue.ClpGateway.Common.Exceptions;
using SmartGlue.ClpGateway.Modbus;
using SmartGlue.ClpGateway.Services;

namespace SmartGlue.ClpGateway.Vision;

// Fetches the camera frame over HTTP and pairs it with the axis position read over Modbus, so
// the caller gets a frame it can actually interpret: a JPEG alone says nothing about *where*
// on the board it was taken.
//
// Position and image are read back to back, never merged from two separate desktop requests —
// while the axis is moving those two reads would describe different places, and the desktop
// would convert a click against the wrong centre.
//
// The whole capture (position + image) is retried FetchRetryAttempts times,
// FetchRetryDelaySeconds apart, before the operator is told communication failed.
public class CameraService(
  HttpClient httpClient,
  IAxisControlService axisControl,
  IApplicationAxisService applicationAxis,
  IOptions<VisionOptions> visionOptions,
  IOptions<AxisHardwareOptions> axisOptions,
  ILogger<CameraService> logger) : ICameraService
{
  private readonly VisionOptions _vision = visionOptions.Value;
  private readonly AxisHardwareOptions _axis = axisOptions.Value;

  public Task<CameraSnapshotResponse> CaptureAsync(CancellationToken ct) =>
    CaptureAsync(DefaultStation, ct);

  /// <summary>
  /// The station GET /camera/snapshot means. Configured, and "inspection" unless someone says
  /// otherwise — that route existed before there was a second camera and the desktop's
  /// inspection step still calls it.
  /// </summary>
  private StationId DefaultStation =>
    string.Equals(_vision.Station, "application", StringComparison.OrdinalIgnoreCase)
      ? StationId.Application
      : StationId.Inspection;

  public async Task<CameraSnapshotResponse> CaptureAsync(StationId station, CancellationToken ct)
  {
    if (!_vision.Enabled)
    {
      throw new IntegrationException("Camera integration is disabled (Vision:Enabled=false).");
    }

    Exception? lastError = null;

    for (var attempt = 1; attempt <= Math.Max(1, _vision.FetchRetryAttempts); attempt++)
    {
      try
      {
        return await CaptureOnceAsync(station, ct);
      }
      catch (Exception ex) when (ex is not OperationCanceledException)
      {
        lastError = ex;
        logger.LogWarning(
          ex,
          "Camera capture attempt {Attempt}/{Attempts} failed.",
          attempt, _vision.FetchRetryAttempts);

        if (attempt < _vision.FetchRetryAttempts)
        {
          await Task.Delay(TimeSpan.FromSeconds(_vision.FetchRetryDelaySeconds), ct);
        }
      }
    }

    throw new IntegrationException(
      $"Failed to communicate with the camera after {_vision.FetchRetryAttempts} attempts.",
      lastError!);
  }

  private async Task<CameraSnapshotResponse> CaptureOnceAsync(StationId station, CancellationToken ct)
  {
    var relativeUrl = $"{StationAddresses.For(station).Name}/snapshot.jpg";
    using var response = await httpClient.GetAsync(relativeUrl, ct);
    response.EnsureSuccessStatusCode();

    var bytes = await response.Content.ReadAsByteArrayAsync(ct);
    var contentType = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";

    // The simulator (contract v2) sends the carriage pose of the very instant the frame was
    // rendered, in X-Pose-Pulses: "x,y,z". That is strictly better than a Modbus read made a
    // few milliseconds before or after — which is the race this method exists to avoid. A real
    // camera will not send it; then, and only then, the position comes from Modbus.
    //
    // The centre of the image is the CAMERA (carriage + the camera offset, 0/0 on the kit),
    // not the needle, which sits 14 000/5 000 p away on the applicator. Turning a click into
    // where the needle must go is the calibration service's job, not this gateway's: no offset
    // is applied here.
    decimal positionX, positionY;
    if (TryParsePose(response, out var poseX, out var poseY))
    {
      positionX = ToMillimeters(poseX);
      positionY = ToMillimeters(poseY);
    }
    else
    {
      // GetStatusAsync swallows Modbus failures and reports CommunicationFailure at 0,0 instead
      // of throwing. A frame centred on a position we did not actually read is worse than no
      // frame: every click converted against it would target the wrong place.
      AxisStatus axisStatus;
      (positionX, positionY, axisStatus) = station == StationId.Application
        ? await ReadApplicationPositionAsync(ct)
        : await ReadInspectionPositionAsync(ct);

      if (axisStatus == AxisStatus.CommunicationFailure)
      {
        throw new IntegrationException(
          "Failed to read the camera position from the axis hardware; the captured frame could not be located on the board.");
      }
    }

    return new CameraSnapshotResponse(
      Convert.ToBase64String(bytes),
      contentType,
      _vision.OutputWidthPixels,
      _vision.OutputHeightPixels,
      positionX,
      positionY,
      ToMillimeters(_vision.FieldWidthPulses),
      ToMillimeters(_vision.FieldHeightPulses),
      DateTime.UtcNow);
  }

  private async Task<(decimal X, decimal Y, AxisStatus Status)> ReadInspectionPositionAsync(CancellationToken ct)
  {
    var status = await axisControl.GetStatusAsync(ct);
    return (status.PositionX, status.PositionY, status.Status);
  }

  private async Task<(decimal X, decimal Y, AxisStatus Status)> ReadApplicationPositionAsync(CancellationToken ct)
  {
    var status = await applicationAxis.GetStatusAsync(ct);
    return (status.PositionX, status.PositionY, status.Status);
  }

  public const string PoseHeader = "X-Pose-Pulses";

  /// <summary>Parses "x,y,z" (pulses) from <see cref="PoseHeader"/>; false when absent or malformed.</summary>
  internal static bool TryParsePose(HttpResponseMessage response, out int x, out int y)
  {
    x = 0;
    y = 0;

    if (!response.Headers.TryGetValues(PoseHeader, out var values)) return false;

    var parts = values.FirstOrDefault()?.Split(',');
    if (parts is not { Length: >= 2 }) return false;

    return int.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out x)
      && int.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out y);
  }

  private decimal ToMillimeters(int pulses) => pulses / _axis.PulsesPerMillimeter;
}
