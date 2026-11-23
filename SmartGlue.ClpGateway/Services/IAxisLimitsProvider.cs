using SmartGlue.ClpGateway.Api.DTOs;

namespace SmartGlue.ClpGateway.Services;

public interface IAxisLimitsProvider
{
  Task<AxisLimitsResponse> GetLimitsAsync(CancellationToken ct);
}
