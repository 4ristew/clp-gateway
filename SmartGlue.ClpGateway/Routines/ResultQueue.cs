using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace SmartGlue.ClpGateway.Routines;

public class ResultQueueOptions
{
  public const string SectionName = "Results";

  /// <summary>Directory holding the pending-results spool. Relative paths are resolved against the content root.</summary>
  public string Directory { get; set; } = "logs/runs";

  public bool Enabled { get; set; } = true;
}

/// <summary>
/// Where a finished run waits until the backend can take it.
///
/// This exists because of one rule (plan §3.3): results go up AFTER the routine, never
/// between two points. Writing them to a local file first is what makes that rule cheap to
/// obey — the executor never has to care whether the backend is reachable, and a cell whose
/// link to the datacenter is down keeps producing with its results retained locally
/// (SGF-140 ADR-01-R, decision 5).
///
/// The upload side is deliberately absent: the backend has no endpoint to receive a run yet
/// (plan §7.4). Until it does, this spool only accumulates — which is the honest behaviour,
/// and leaves the evidence on disk instead of dropping it.
/// </summary>
public class ResultQueue(IOptions<ResultQueueOptions> options, IHostEnvironment environment, ILogger<ResultQueue> logger)
{
  private static readonly JsonSerializerOptions Json = new()
  {
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = false,
  };

  private readonly ResultQueueOptions _options = options.Value;
  private readonly ConcurrentQueue<string> _pending = new();
  private readonly SemaphoreSlim _writeGate = new(1, 1);

  public int PendingCount => _pending.Count;

  public async Task EnqueueAsync(RoutineRun run, CancellationToken ct)
  {
    if (!_options.Enabled) return;

    var payload = JsonSerializer.Serialize(RoutineRunResponse.From(run), Json);
    _pending.Enqueue(run.RunId);

    await _writeGate.WaitAsync(ct);
    try
    {
      var directory = Path.IsPathRooted(_options.Directory)
        ? _options.Directory
        : Path.Combine(environment.ContentRootPath, _options.Directory);

      Directory.CreateDirectory(directory);

      var file = Path.Combine(directory, $"{run.StartedAt:yyyyMMdd}-runs.jsonl");
      await File.AppendAllTextAsync(file, payload + Environment.NewLine, ct);

      logger.LogInformation("Run {RunId} spooled to {File} ({Status}).", run.RunId, file, run.Status);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      // Never let bookkeeping take down a run that already finished on the machine.
      logger.LogError(ex, "Failed to spool run {RunId}; the result exists only in memory now.", run.RunId);
    }
    finally
    {
      _writeGate.Release();
    }
  }
}
