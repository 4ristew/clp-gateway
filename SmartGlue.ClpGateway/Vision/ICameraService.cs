using SmartGlue.ClpGateway.Api.DTOs;
using SmartGlue.ClpGateway.Modbus;

namespace SmartGlue.ClpGateway.Vision;

public interface ICameraService
{
  /// <summary>
  /// Captures the current field of view of the inspection camera, together with the geometry
  /// needed to map a pixel of that frame back to a X/Y target in millimetres.
  /// </summary>
  Task<CameraSnapshotResponse> CaptureAsync(CancellationToken ct);

  /// <summary>
  /// Captures the field of view of one station's camera. Each station sees its own part of the
  /// board through its own lens, so the frame is paired with THAT station's position — pairing
  /// an applicator frame with the camera's coordinates would put every click in the wrong
  /// place (SGF-141).
  /// </summary>
  Task<CameraSnapshotResponse> CaptureAsync(StationId station, CancellationToken ct);
}
