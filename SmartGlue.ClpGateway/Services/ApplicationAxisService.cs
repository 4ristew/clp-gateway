using SmartGlue.ClpGateway.Trace;
using Microsoft.Extensions.Options;
using SmartGlue.ClpGateway.Api.DTOs;
using SmartGlue.ClpGateway.Modbus;

namespace SmartGlue.ClpGateway.Services;

// The application (glue applicator) station. Owns nothing of the protocol itself — the command
// lifecycle is StationController's, pointed at StationAddresses.Application — and exists to
// give that station its own public shape: three axes, live limits, and the /application/*
// routes the desktop's "2. Marcações" step talks to.
public class ApplicationAxisService : IApplicationAxisService
{
  private readonly StationController _station;
  private readonly StationLimitsCache _limits;

  public ApplicationAxisService(
    IAxisConnection connection,
    IMachineTrace trace,
    IOptions<AxisHardwareOptions> options,
    ILogger<ApplicationAxisService> logger,
    MachineIdentity? identity = null)
  {
    _station = new StationController(StationAddresses.Application, connection, trace, options, logger, identity);
    _limits = new StationLimitsCache(_station, options, logger);
  }

  /// <summary>The routine executor drives the same station through the same controller.</summary>
  public StationController Station => _station;

  public async Task<ApplicationStatusResponse> GetStatusAsync(CancellationToken ct) =>
    ToResponse(await _station.GetStatusAsync(ct));

  public async Task<ApplicationLimitsResponse> GetLimitsAsync(CancellationToken ct)
  {
    var limits = await _limits.GetLimitsAsync(ct);

    // Z limits are only null for a station without a Z axis, which this one has — the fallback
    // keeps the DTO total rather than making every caller handle an impossible case.
    return new ApplicationLimitsResponse(
      limits.XMin, limits.XMax,
      limits.YMin, limits.YMax,
      limits.ZMin ?? 0, limits.ZMax ?? 0);
  }

  public async Task<ApplicationStatusResponse> MoveToAsync(
    decimal positionX, decimal positionY, int? speedPulsesPerSecond, CancellationToken ct)
  {
    // Validated before taking the command lock / touching the hardware — a 400 for an
    // out-of-range target should not have to wait behind an in-flight command.
    var limits = await _limits.GetLimitsAsync(ct);
    return ToResponse(await _station.MoveXyAsync(positionX, positionY, speedPulsesPerSecond, limits, ct));
  }

  public async Task<ApplicationStatusResponse> MoveZAsync(decimal positionZ, int? speedPulsesPerSecond, CancellationToken ct)
  {
    var limits = await _limits.GetLimitsAsync(ct);
    return ToResponse(await _station.MoveZAsync(positionZ, speedPulsesPerSecond, limits, ct));
  }

  public async Task<ApplicationStatusResponse> ReturnToZeroAsync(CancellationToken ct) =>
    ToResponse(await _station.HomeAsync(ct));

  public async Task<ApplicationStatusResponse> StopAsync(CancellationToken ct) =>
    ToResponse(await _station.StopAsync(ct));

  public async Task<ApplicationStatusResponse> ResetFaultAsync(CancellationToken ct) =>
    ToResponse(await _station.ResetFaultAsync(ct));

  public async Task<ApplicationStatusResponse> JogAsync(JogAxis axis, int direction, bool active, CancellationToken ct) =>
    ToResponse(await _station.SetJogAsync(axis, direction, active, ct));

  public Task<bool> IsReferencedAsync(CancellationToken ct) => _station.IsReferencedAsync(ct);

  private static ApplicationStatusResponse ToResponse(StationStatus status) => new(
    status.Status,
    status.PositionX,
    status.PositionY,
    status.PositionZ ?? 0,
    status.ReferencedX,
    status.ReferencedY,
    status.ReferencedZ ?? false,
    status.ReachedTarget);
}
