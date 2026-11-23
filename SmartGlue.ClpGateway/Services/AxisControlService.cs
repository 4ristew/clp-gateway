using SmartGlue.ClpGateway.Trace;
using Microsoft.Extensions.Options;
using SmartGlue.ClpGateway.Api.DTOs;
using SmartGlue.ClpGateway.Modbus;

namespace SmartGlue.ClpGateway.Services;

// The "inspection" station — the camera that the desktop's inspection-configuration step
// drives. This is the station the gateway was extracted for (MR!14).
//
// The command lifecycle itself moved to StationController when SGF-141 needed the same
// handshake against the application station; what is left here is the inspection station's
// public shape: X/Y only, the DTOs the desktop already consumes, and the /axis/* routes.
// Behaviour is unchanged on purpose — the inspection step is not part of SGF-141 and must not
// notice this refactor.
public class AxisControlService : IAxisControlService
{
  private readonly StationController _station;
  private readonly IAxisLimitsProvider _limitsProvider;

  public AxisControlService(
    IAxisConnection connection,
    IMachineTrace trace,
    IAxisLimitsProvider limitsProvider,
    IOptions<AxisHardwareOptions> options,
    ILogger<AxisControlService> logger,
    MachineIdentity? identity = null)
  {
    _limitsProvider = limitsProvider;
    _station = new StationController(StationAddresses.Inspection, connection, trace, options, logger, identity);
  }

  public async Task<AxisStatusResponse> GetStatusAsync(CancellationToken ct) =>
    ToResponse(await _station.GetStatusAsync(ct));

  public Task<bool> IsReferencedAsync(CancellationToken ct) => _station.IsReferencedAsync(ct);

  public async Task<AxisStatusResponse> MoveToAsync(decimal positionX, decimal positionY, CancellationToken ct)
  {
    // Validated before taking the command lock / touching the hardware — a 400 for an
    // out-of-range target should not have to wait behind an in-flight command.
    var limits = await _limitsProvider.GetLimitsAsync(ct);

    return ToResponse(await _station.MoveXyAsync(
      positionX,
      positionY,
      speedPulsesPerSecond: null,
      new StationLimits(limits.XMin, limits.XMax, limits.YMin, limits.YMax, null, null),
      ct));
  }

  public async Task<AxisStatusResponse> ReturnToZeroAsync(CancellationToken ct) =>
    ToResponse(await _station.HomeAsync(ct));

  public async Task<AxisStatusResponse> StopAsync(CancellationToken ct) =>
    ToResponse(await _station.StopAsync(ct));

  public async Task<AxisStatusResponse> ResetFaultAsync(CancellationToken ct) =>
    ToResponse(await _station.ResetFaultAsync(ct));

  private static AxisStatusResponse ToResponse(StationStatus status) =>
    new(status.Status, status.PositionX, status.PositionY, status.ReachedTarget);
}
