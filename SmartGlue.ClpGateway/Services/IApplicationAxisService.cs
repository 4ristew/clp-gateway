using SmartGlue.ClpGateway.Api.DTOs;

namespace SmartGlue.ClpGateway.Services;

/// <summary>
/// The application (glue applicator) station — the machine behind the desktop's "2. Marcações"
/// step (SGF-141).
///
/// Separate from <see cref="IAxisControlService"/> rather than a station parameter on it: the
/// two stations answer to different screens, have different axes, and the inspection one is
/// already in production use. A parameter would have made every existing caller decide
/// something it has no opinion about.
/// </summary>
public interface IApplicationAxisService
{
  Task<ApplicationStatusResponse> GetStatusAsync(CancellationToken ct);

  Task<ApplicationLimitsResponse> GetLimitsAsync(CancellationToken ct);

  /// <summary>Moves X and Y. Homes first if the station has never been referenced.</summary>
  Task<ApplicationStatusResponse> MoveToAsync(decimal positionX, decimal positionY, int? speedPulsesPerSecond, CancellationToken ct);

  /// <summary>Moves Z only — a separate request by contract.</summary>
  Task<ApplicationStatusResponse> MoveZAsync(decimal positionZ, int? speedPulsesPerSecond, CancellationToken ct);

  Task<ApplicationStatusResponse> ReturnToZeroAsync(CancellationToken ct);

  /// <summary>
  /// Pulses STOP and releases any held jog. Takes no command lock and never waits for an
  /// in-flight command.
  /// </summary>
  Task<ApplicationStatusResponse> StopAsync(CancellationToken ct);

  Task<ApplicationStatusResponse> ResetFaultAsync(CancellationToken ct);

  /// <summary>Holds or releases one jog direction. See <c>StationController.SetJogAsync</c>.</summary>
  Task<ApplicationStatusResponse> JogAsync(JogAxis axis, int direction, bool active, CancellationToken ct);

  /// <summary>True when X, Y and Z all report referenced.</summary>
  Task<bool> IsReferencedAsync(CancellationToken ct);
}
