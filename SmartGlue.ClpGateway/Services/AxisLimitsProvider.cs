using SmartGlue.ClpGateway.Trace;
using Microsoft.Extensions.Options;
using SmartGlue.ClpGateway.Api.DTOs;
using SmartGlue.ClpGateway.Modbus;

namespace SmartGlue.ClpGateway.Services;

// The "inspection" station soft limits (LIMIT_X_MIN/MAX, LIMIT_Y_MIN/MAX), in mm.
//
// The reading and the freshness rule moved to StationLimitsCache when SGF-141 needed the same
// behaviour for the application station; what is left here is the inspection station's public
// shape — the DTO the desktop already consumes on GET /axis/limits.
//
// Registered as a singleton so the cache is shared: MR!14 registered it scoped, which made a
// DB row the only thing that survived a request.
public class AxisLimitsProvider : IAxisLimitsProvider
{
  private readonly StationLimitsCache _limits;

  public AxisLimitsProvider(
    IAxisConnection connection,
    IMachineTrace trace,
    IOptions<AxisHardwareOptions> options,
    ILogger<AxisLimitsProvider> logger)
  {
    var station = new StationController(StationAddresses.Inspection, connection, trace, options, logger);
    _limits = new StationLimitsCache(station, options, logger);
  }

  public async Task<AxisLimitsResponse> GetLimitsAsync(CancellationToken ct)
  {
    var limits = await _limits.GetLimitsAsync(ct);
    return new AxisLimitsResponse(limits.XMin, limits.XMax, limits.YMin, limits.YMax);
  }
}
