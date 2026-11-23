using SmartGlue.ClpGateway.Api.DTOs;

namespace SmartGlue.ClpGateway.Services;

public interface IAxisControlService
{
  Task<AxisStatusResponse> GetStatusAsync(CancellationToken ct);
  Task<AxisStatusResponse> MoveToAsync(decimal positionX, decimal positionY, CancellationToken ct);
  Task<AxisStatusResponse> ReturnToZeroAsync(CancellationToken ct);
  Task<AxisStatusResponse> ResetFaultAsync(CancellationToken ct);

  /// <summary>
  /// Pulses STOP. Does not take the command lock and never waits for an in-flight command:
  /// a stop that queues behind the movement it is meant to interrupt is not a stop.
  /// </summary>
  Task<AxisStatusResponse> StopAsync(CancellationToken ct);

  /// <summary>True when both axes report referenced, i.e. HOME has already run.</summary>
  Task<bool> IsReferencedAsync(CancellationToken ct);
}
