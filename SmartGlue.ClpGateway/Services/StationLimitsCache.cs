using Microsoft.Extensions.Options;
using SmartGlue.ClpGateway.Common.Exceptions;
using SmartGlue.ClpGateway.Modbus;

namespace SmartGlue.ClpGateway.Services;

// Reads a station's soft limits over Modbus and keeps the last good answer in memory.
//
// Extracted from AxisLimitsProvider (MR!14, inspection only) when SGF-141 needed the same
// freshness rule for the application station. The rule is the interesting part, not the
// caching:
//
// the limits validate movement targets, so serving an arbitrarily old value would be worse
// than failing loudly — it could reject a genuinely valid target, or, on a real PLC, let one
// through that the hardware no longer considers safe. Past LimitsCacheMaxAgeSeconds the cache
// is refused.
//
// Losing the cache across a restart is not a regression, it is the correct behaviour: the
// gateway rebuilds its picture of the machine by reading the machine, never from something
// that outlived the process.
public class StationLimitsCache(
  StationController station,
  IOptions<AxisHardwareOptions> options,
  ILogger logger)
{
  private readonly AxisHardwareOptions _options = options.Value;
  private readonly SemaphoreSlim _gate = new(1, 1);

  private StationLimits? _cached;
  private DateTime _cachedAt;

  public async Task<StationLimits> GetLimitsAsync(CancellationToken ct)
  {
    await _gate.WaitAsync(ct);
    try
    {
      try
      {
        var limits = await station.ReadLimitsAsync(ct);

        _cached = limits;
        _cachedAt = DateTime.UtcNow;

        return limits;
      }
      catch (Exception ex) when (ex is not OperationCanceledException)
      {
        logger.LogWarning(ex, "Failed to refresh {Station} axis limits from the hardware.", station.Addresses.Name);

        var age = _cached is null ? (TimeSpan?)null : DateTime.UtcNow - _cachedAt;
        if (_cached is not null && age <= TimeSpan.FromSeconds(_options.LimitsCacheMaxAgeSeconds))
        {
          logger.LogWarning(
            "Serving cached axis limits from {UpdatedAt} (age {Age}) because the hardware is unreachable.",
            _cachedAt, age);
          return _cached;
        }

        throw new IntegrationException(
          "Failed to communicate with the axis hardware to retrieve the axis limits" +
          (_cached is null ? ", and no cached limits are available." : ", and the cached limits are too stale to trust."),
          ex);
      }
    }
    finally
    {
      _gate.Release();
    }
  }
}
