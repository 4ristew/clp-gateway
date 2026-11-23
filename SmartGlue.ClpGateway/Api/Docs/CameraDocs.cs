using SmartGlue.ClpGateway.Api.DTOs;
using SmartGlue.ClpGateway.Common.Returns;

namespace SmartGlue.ClpGateway.Api.Docs;

public static class CameraDocs
{
  public static RouteHandlerBuilder CaptureCameraSnapshotDocs(this RouteHandlerBuilder builder)
  {
    return builder
      .WithSummary("Capture the current camera frame")
      .WithDescription(
        "Captures the inspection camera's current field of view and returns it base64-encoded together with the geometry that locates it on the board: the X/Y centre (mm — the pose the simulator sends with the frame itself in `X-Pose-Pulses`, or, from a camera that does not send it, a Modbus read at capture time). The centre is the CAMERA, not the needle: turning a point on the image into where the needle must go is the calibration service's job and the width/height the frame covers (mm). That geometry is what lets the engineering screen convert a click on the image into a X/Y target in millimetres.\n\n" +
        "The frame comes from the camera HTTP server configured in the `Vision` section — the SmartGlue simulator's vision server today, the real camera server later. A capture that fails is retried `Vision:FetchRetryAttempts` times, `Vision:FetchRetryDelaySeconds` apart (3 x 5s by default), before answering 502.")
      .Produces<SuccessResponse<CameraSnapshotResponse>>()
      .Produces(StatusCodes.Status502BadGateway);
  }

  public static RouteHandlerBuilder CaptureStationSnapshotDocs(this RouteHandlerBuilder builder)
  {
    return builder
      .WithSummary("Capture one station's camera frame")
      .WithDescription(
        "Same as GET /camera/snapshot, for an explicit station: `application` (the glue applicator) or `inspection` (the camera). Each station looks at the board through its own lens and from its own position, so the frame is always paired with THAT station's coordinates — a frame of the applicator's field of view centred on the camera's position would send every click to the wrong place.\n\n" +
        "An unknown station name is a 400, never the other station's frame: a valid-looking image of the wrong part of the board is the one failure a caller cannot detect.")
      .Produces<SuccessResponse<CameraSnapshotResponse>>()
      .Produces(StatusCodes.Status400BadRequest)
      .Produces(StatusCodes.Status502BadGateway);
  }
}
