using Carter;
using SmartGlue.ClpGateway.Api.Docs;
using SmartGlue.ClpGateway.Common.Interceptors;
using SmartGlue.ClpGateway.Common.Returns;
using SmartGlue.ClpGateway.Modbus;
using SmartGlue.ClpGateway.Vision;

namespace SmartGlue.ClpGateway.Api;

public class CameraEndpoints : ICarterModule
{
  public void AddRoutes(IEndpointRouteBuilder app)
  {
    var group = app.MapGroup("/camera").WithTags("Camera").RequireAuthorization();

    // The station-less route predates the second camera and is what the desktop's inspection
    // step calls; it keeps meaning whatever Vision:Station says (inspection, unless configured
    // otherwise).
    group.MapGet("/snapshot", async (ICameraService camera, CancellationToken ct) =>
    {
      var result = await camera.CaptureAsync(ct);
      return CustomResults.Success(result, "Camera snapshot captured successfully!");
    })
    .RequireProfileLevels(ProfileLevels.FullPermission, ProfileLevels.HighPermission, ProfileLevels.MediumPermission)
    .CaptureCameraSnapshotDocs();

    group.MapGet("/{station}/snapshot", async (string station, ICameraService camera, CancellationToken ct) =>
    {
      var result = await camera.CaptureAsync(ParseStation(station), ct);
      return CustomResults.Success(result, "Camera snapshot captured successfully!");
    })
    .RequireProfileLevels(ProfileLevels.FullPermission, ProfileLevels.HighPermission, ProfileLevels.MediumPermission)
    .CaptureStationSnapshotDocs();
  }

  private static StationId ParseStation(string station) => station.ToLowerInvariant() switch
  {
    "application" => StationId.Application,
    "inspection" => StationId.Inspection,
    // A typo in the path must not silently return the other station's field of view: the
    // caller would get a perfectly valid frame of the wrong part of the board.
    _ => throw new ArgumentException($"Unknown station '{station}'. Valid stations are 'application' and 'inspection'."),
  };
}
