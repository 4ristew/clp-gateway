using System.Text.Json;
using Microsoft.Extensions.Options;
using SmartGlue.ClpGateway.Api.DTOs;
using SmartGlue.ClpGateway.Modbus;
using SmartGlue.ClpGateway.Trace;
using System.Collections.Concurrent;
using SmartGlue.ClpGateway.Backend;
using SmartGlue.ClpGateway.Common.Exceptions;
using SmartGlue.ClpGateway.Services;

namespace SmartGlue.ClpGateway.Routines;

/// <summary>
/// Runs a whole inspection routine without the desktop driving it point by point.
///
/// The rule that shapes this entire class (plan §3.3):
///
///     BUSCAR ANTES. REPORTAR DEPOIS. NUNCA LER O BACKEND ENTRE DOIS PONTOS.
///
/// Everything the routine needs — the points, the live axis limits — is loaded and validated
/// BEFORE the first coil pulse. After that, the loop touches nothing but the machine. Results
/// are spooled locally when the run ends. If a future change puts an HTTP call inside
/// ExecuteWaypointsAsync, a network hiccup to the datacenter will stall a moving axis: that is
/// the failure this ordering exists to prevent.
///
/// One routine at a time. STOP always wins.
/// </summary>
public class RoutineExecutor(
  IAxisControlService axis,
  IAxisLimitsProvider limitsProvider,
  IApplicationAxisService applicator,
  IBackendClient backend,
  IMachineTrace trace,
  IOptions<AxisHardwareOptions> hardware,
  ResultQueue resultQueue,
  ILogger<RoutineExecutor> logger) : IRoutineExecutor, IDisposable
{
  private const int MaxRetainedRuns = 50;

  private readonly ConcurrentDictionary<string, RoutineRun> _runs = new();
  private readonly ConcurrentQueue<string> _runOrder = new();
  private readonly object _startLock = new();

  private CancellationTokenSource? _cancellation;
  private RoutineRun? _active;

  public bool IsRunning => _active is { IsTerminal: false };

  public RoutineRun Start(StartRoutineRequest request, string requestedBy, string? bearerToken)
  {
    if (request.ProgramId is null && (request.Points is null || request.Points.Count == 0))
    {
      throw new ArgumentException("Provide either a programId or a non-empty list of points.");
    }

    RoutineRun run;
    CancellationTokenSource cts;

    lock (_startLock)
    {
      if (IsRunning)
      {
        throw new ConflictException(
          $"Routine '{_active!.RunId}' is already running. Stop it before starting another one.");
      }

      run = new RoutineRun
      {
        RunId = $"run-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..6]}",
        ProgramId = request.ProgramId ?? Guid.Empty,
        RequestedBy = requestedBy,
        StartedAt = DateTimeOffset.UtcNow,
      };

      cts = new CancellationTokenSource();
      _cancellation?.Dispose();
      _cancellation = cts;
      _active = run;

      Retain(run);
    }

    logger.LogInformation("Routine {RunId} accepted for program {ProgramId} by {RequestedBy}.",
      run.RunId, run.ProgramId, requestedBy);

    _ = Task.Run(() => ExecuteAsync(run, request, bearerToken, cts.Token), CancellationToken.None);

    return run;
  }

  public RoutineRun StartDryRun(DryRunRequest request, string requestedBy)
  {
    if (request.Points is null || request.Points.Count == 0)
    {
      throw new ArgumentException("A dry run needs at least one point.");
    }

    RoutineRun run;
    CancellationTokenSource cts;

    lock (_startLock)
    {
      if (IsRunning)
      {
        throw new ConflictException(
          $"Routine '{_active!.RunId}' is already running. Stop it before starting another one.");
      }

      run = new RoutineRun
      {
        RunId = $"dry-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..6]}",
        ProgramId = request.ProgramId ?? Guid.Empty,
        RequestedBy = requestedBy,
        StartedAt = DateTimeOffset.UtcNow,
        Kind = RoutineKind.ApplicationDryRun,
        MarkName = request.MarkName,
      };

      cts = new CancellationTokenSource();
      _cancellation?.Dispose();
      _cancellation = cts;
      _active = run;

      Retain(run);
    }

    logger.LogInformation("Dry run {RunId} accepted for mark '{Mark}' ({Points} point(s)) by {RequestedBy}.",
      run.RunId, request.MarkName ?? "(unnamed)", request.Points.Count, requestedBy);

    _ = Task.Run(() => ExecuteDryRunAsync(run, request, cts.Token), CancellationToken.None);

    return run;
  }

  public bool RequestStop()
  {
    var cts = _cancellation;
    if (cts is null || !IsRunning) return false;

    logger.LogWarning("Stop requested for routine {RunId}.", _active!.RunId);
    try
    {
      cts.Cancel();
    }
    catch (ObjectDisposedException)
    {
      return false;
    }

    return true;
  }

  public RoutineRun? Get(string runId) => _runs.GetValueOrDefault(runId);

  public IReadOnlyList<RoutineRun> Recent(int take) =>
    _runOrder.Reverse().Take(Math.Clamp(take, 1, MaxRetainedRuns))
      .Select(id => _runs.GetValueOrDefault(id))
      .Where(r => r is not null)
      .Select(r => r!)
      .ToArray();

  private async Task ExecuteAsync(RoutineRun run, StartRoutineRequest request, string? bearerToken, CancellationToken ct)
  {
    try
    {
      // ─── 1. buscar antes ────────────────────────────────────────────────────────────
      var waypoints = await LoadWaypointsAsync(request, bearerToken, ct);
      run.TotalPoints = waypoints.Count;

      if (waypoints.Count == 0)
      {
        Finish(run, RoutineStatus.Failed, "The program has no inspection points to run.");
        return;
      }

      // ─── 2. validar tudo contra os limites vivos, antes de mover qualquer coisa ──────
      var limits = await limitsProvider.GetLimitsAsync(ct);
      var offending = waypoints
        .Where(p => p.PositionX < limits.XMin || p.PositionX > limits.XMax
                 || p.PositionY < limits.YMin || p.PositionY > limits.YMax)
        .ToArray();

      if (offending.Length > 0)
      {
        // Refuse the routine whole. Moving to point 1 and only then discovering that point 7
        // is unreachable leaves the machine somewhere nobody asked for.
        var detail = string.Join(", ", offending.Select(p => $"#{p.Order} (X={p.PositionX}, Y={p.PositionY})"));
        Finish(run, RoutineStatus.Failed,
          $"{offending.Length} of {waypoints.Count} point(s) fall outside the live axis limits " +
          $"(X: {limits.XMin}-{limits.XMax}, Y: {limits.YMin}-{limits.YMax}): {detail}. Nothing was moved.");
        return;
      }

      ct.ThrowIfCancellationRequested();

      // ─── 3. garantir referenciamento ────────────────────────────────────────────────
      if (!await axis.IsReferencedAsync(ct))
      {
        logger.LogInformation("Routine {RunId}: axis not referenced, homing first.", run.RunId);
        await axis.ReturnToZeroAsync(ct);
        run.Homed = true;
      }

      // ─── 4. executar ────────────────────────────────────────────────────────────────
      run.Status = RoutineStatus.Running;
      await ExecuteWaypointsAsync(run, waypoints, ct);

      Finish(run, RoutineStatus.Completed, null);
    }
    catch (OperationCanceledException)
    {
      Finish(run, RoutineStatus.Stopped, "The routine was stopped.");
    }
    catch (ConflictException ex) when (ex.Message.Contains("1006"))
    {
      // The machine itself reports STOPPED. Same outcome as a cancellation, different origin.
      Finish(run, RoutineStatus.Stopped, ex.Message);
    }
    catch (IntegrationException ex) when (ex.Message.Contains("indeterminate"))
    {
      Finish(run, RoutineStatus.Indeterminate, ex.Message);
    }
    catch (Exception ex)
    {
      logger.LogError(ex, "Routine {RunId} failed.", run.RunId);
      Finish(run, RoutineStatus.Failed, ex.Message);
    }
    finally
    {
      // ─── 5. reportar depois ───────────────────────────────────────────────────────────
      await resultQueue.EnqueueAsync(run, CancellationToken.None);
      lock (_startLock)
      {
        if (ReferenceEquals(_active, run)) _active = run;
      }
    }
  }

  // The dry run of an application trajectory — "Iniciar Teste" (SGF-141, criterion 7). Ported
  // from smt_glue/Infra/Machine/Engine (MachineEngine.RunDryRunAsync + DryRunPlanner), which
  // proved the sequence against the kit simulator before the ADR-01-R2 moved the machine layer
  // here.
  //
  // Two things the port deliberately left behind:
  //
  //   * the pixel->pulse conversion. It lived in the planner because the backend received a
  //     drawing in image pixels; the gateway receives millimetres, because the screen that owns
  //     the image is the one that can convert it (its camera frame carries the geometry). A
  //     conversion done here would be a guess made far from the evidence.
  //   * the profile-driven speed envelope. Speeds arrive in the machine's own unit and are
  //     validated by the machine, which answers 1003 INVALID_SPEED.
  //
  // What it keeps is the sequence, which is the part that took a bench to get right.
  /// <summary>
  /// How long the nozzle stays still at point <paramref name="index"/> (SGF-278, D3 option 1:
  /// every unit gets its own times). A point unit starts and ends here, so it waits pre + post. In
  /// a line, a vertex ends the previous segment (its post) and starts the next one (its pre).
  /// </summary>
  internal static int DispenseDwellMs(DryRunRequest request, IReadOnlyList<TrajectoryPoint> points, int index, bool isLine)
  {
    int Pre(int i) => points[i].PreDispenseMs ?? request.PreDispenseMs;
    int Post(int i) => points[i].PostDispenseMs ?? request.PostDispenseMs;

    if (!isLine) return Pre(index) + Post(index);

    var endsPrevious = index > 0 ? Post(index - 1) : 0;
    var startsNext = index < points.Count - 1 ? Pre(index) : 0;
    return endsPrevious + startsNext;
  }

  private async Task ExecuteDryRunAsync(RoutineRun run, DryRunRequest request, CancellationToken ct)
  {
    var points = request.Points!;
    var isLine = !string.Equals(request.Kind, "point", StringComparison.OrdinalIgnoreCase);

    // Every frame and every command phase of this run carries the run id, so the evidence
    // package can be assembled later by filtering on it alone.
    using var correlation = MachineCorrelation.Begin(runId: run.RunId);

    try
    {
      run.TotalPoints = points.Count;

      // ─── 1. validar tudo contra os limites vivos, antes de mover qualquer coisa ──────
      var limits = await applicator.GetLimitsAsync(ct);

      var offending = points
        .Select((p, index) => (Point: p, Order: index + 1))
        .Where(p => p.Point.PositionX < limits.XMin || p.Point.PositionX > limits.XMax
                 || p.Point.PositionY < limits.YMin || p.Point.PositionY > limits.YMax)
        .ToArray();

      if (offending.Length > 0)
      {
        // Refuse the trajectory whole. Tracing half a bead and stopping is worse than not
        // starting: the machine ends up somewhere nobody asked for, mid-drawing.
        var detail = string.Join(", ", offending.Select(p => $"#{p.Order} (X={p.Point.PositionX}, Y={p.Point.PositionY})"));
        Finish(run, RoutineStatus.Failed,
          $"{offending.Length} of {points.Count} point(s) fall outside the live axis limits " +
          $"(X: {limits.XMin}-{limits.XMax}, Y: {limits.YMin}-{limits.YMax}): {detail}. Nothing was moved.");
        return;
      }

      foreach (var (label, z) in new[] { ("applicationZ", request.ApplicationZ), ("travelZ", request.TravelZ) })
      {
        if (z < limits.ZMin || z > limits.ZMax)
        {
          Finish(run, RoutineStatus.Failed,
            $"{label} ({z}) is outside the Z limits ({limits.ZMin}-{limits.ZMax}). Nothing was moved.");
          return;
        }
      }

      ct.ThrowIfCancellationRequested();

      // ─── 2. garantir referenciamento ────────────────────────────────────────────────
      if (request.HomeIfNeeded && !await applicator.IsReferencedAsync(ct))
      {
        logger.LogInformation("Dry run {RunId}: applicator not referenced, homing first.", run.RunId);
        await applicator.ReturnToZeroAsync(ct);
        run.Homed = true;
      }

      run.Status = RoutineStatus.Running;

      // ─── 3. subir para a altura de locomoção ANTES do primeiro ponto ────────────────
      // Otherwise the first XY move drags the nozzle across the board from wherever it was.
      await applicator.MoveZAsync(request.TravelZ, request.SpeedZPulsesPerSecond, ct);

      // ─── 4. percorrer ───────────────────────────────────────────────────────────────
      for (var index = 0; index < points.Count; index++)
      {
        ct.ThrowIfCancellationRequested();

        var point = points[index];
        var order = index + 1;
        run.CurrentOrder = order;
        var startedAt = DateTimeOffset.UtcNow;

        try
        {
          var moved = await applicator.MoveToAsync(point.PositionX, point.PositionY, request.SpeedXyPulsesPerSecond, ct);
          var down = await applicator.MoveZAsync(request.ApplicationZ, request.SpeedZPulsesPerSecond, ct);

          // Timed here because this run is movement, never glue: with a dispenser the same times
          // go to the PLC as open/close leads (D24032/D24034, SGF-278).
          var dwellMs = DispenseDwellMs(request, points, index, isLine);
          if (dwellMs > 0) await Task.Delay(dwellMs, ct);

          run.Results.Add(new RoutineWaypointResult(
            null, order, point.PositionX, point.PositionY,
            moved.PositionX, moved.PositionY, true, null, startedAt, DateTimeOffset.UtcNow, down.PositionZ,
            moved.ReachedTarget && down.ReachedTarget));

          run.CompletedPoints++;

          TraceWaypoint(run, request, order, points.Count, point, moved, down, startedAt, TraceOutcome.Ok, null);

          // A line keeps the nozzle down between points; discrete points lift in between. The
          // last point always lifts, whatever the kind.
          var isLast = index == points.Count - 1;
          if (!isLine || isLast)
          {
            await applicator.MoveZAsync(request.TravelZ, request.SpeedZPulsesPerSecond, ct);
          }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
          run.Results.Add(new RoutineWaypointResult(
            null, order, point.PositionX, point.PositionY,
            null, null, false, ex.Message, startedAt, DateTimeOffset.UtcNow));

          TraceWaypoint(run, request, order, points.Count, point, null, null, startedAt,
            ex.Message.Contains("1006") ? TraceOutcome.Stopped : TraceOutcome.Faulted, ex.Message);

          throw;
        }
      }

      run.CurrentOrder = null;
      Finish(run, RoutineStatus.Completed, null);
    }
    catch (OperationCanceledException)
    {
      Finish(run, RoutineStatus.Stopped, "The dry run was stopped.");
    }
    catch (ArgumentException ex)
    {
      Finish(run, RoutineStatus.Failed, ex.Message);
    }
    catch (ConflictException ex) when (ex.Message.Contains("1006"))
    {
      Finish(run, RoutineStatus.Stopped, ex.Message);
    }
    catch (IntegrationException ex) when (ex.Message.Contains("indeterminate"))
    {
      Finish(run, RoutineStatus.Indeterminate, ex.Message);
    }
    catch (Exception ex)
    {
      logger.LogError(ex, "Dry run {RunId} failed.", run.RunId);
      Finish(run, RoutineStatus.Failed, ex.Message);
    }
    finally
    {
      WriteRunSummary(run, request);

      await resultQueue.EnqueueAsync(run, CancellationToken.None);
      lock (_startLock)
      {
        if (ReferenceEquals(_active, run)) _active = run;
      }
    }
  }

  /// <summary>One line per waypoint: what was asked, what was reached, and how far apart.</summary>
  private void TraceWaypoint(
    RoutineRun run,
    DryRunRequest request,
    int order,
    int total,
    TrajectoryPoint point,
    ApplicationStatusResponse? moved,
    ApplicationStatusResponse? down,
    DateTimeOffset startedAt,
    string outcome,
    string? message)
  {
    var planned = new TracePoint(ToPulses(point.PositionX), ToPulses(point.PositionY), ToPulses(request.ApplicationZ));

    var achieved = moved is null
      ? null
      : new TracePoint(ToPulses(moved.PositionX), ToPulses(moved.PositionY), ToPulses(down?.PositionZ ?? 0));

    trace.Write(new TraceRecord
    {
      Seq = trace.NextSequence(),
      SessionId = trace.CurrentSession?.SessionId ?? string.Empty,
      RunId = run.RunId,
      Layer = TraceLayer.Run,
      Outcome = outcome,
      Component = "application",
      MarkName = run.MarkName,
      Waypoint = order,
      Of = total,
      Kind = request.Kind ?? "line",
      Planned = planned,
      Achieved = achieved,
      DeltaPulses = achieved is null
        ? null
        : new TracePoint(achieved.X - planned.X, achieved.Y - planned.Y, achieved.Z - planned.Z),
      ElapsedMs = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds,
      Message = message,
    });
  }

  /// <summary>
  /// The run summary next to the trace files. It is what turns a folder of JSONL into an
  /// evidence package: TraceReader.BuildEvidence starts from this file, and without it a run
  /// leaves frames that nobody can tie back to a story (SGF-144).
  /// </summary>
  private void WriteRunSummary(RoutineRun run, DryRunRequest request)
  {
    var folder = trace.CurrentSession?.Folder;
    if (string.IsNullOrEmpty(folder)) return;

    try
    {
      var summary = new
      {
        runId = run.RunId,
        component = "application",
        markName = run.MarkName,
        kind = request.Kind ?? "line",
        status = run.Status.ToString(),
        error = run.Error,
        requestedBy = run.RequestedBy,
        programId = run.ProgramId,
        startedAt = run.StartedAt,
        finishedAt = run.FinishedAt,
        homed = run.Homed,
        totalPoints = run.TotalPoints,
        completedPoints = run.CompletedPoints,
        applicationZ = request.ApplicationZ,
        travelZ = request.TravelZ,
        preDispenseMs = request.PreDispenseMs,
        postDispenseMs = request.PostDispenseMs,
        dwellMs = request.Points!.Select((_, i) => DispenseDwellMs(request, request.Points!,
          i, !string.Equals(request.Kind, "point", StringComparison.OrdinalIgnoreCase))).ToArray(),
        // Stated in the evidence itself, not only in the story: this run is movement, and the
        // machine it ran against has no nozzle.
        dispensed = false,
        results = run.Results,
      };

      var path = Path.Combine(folder, "runs", $"{run.RunId}.summary.json");
      Directory.CreateDirectory(Path.GetDirectoryName(path)!);
      File.WriteAllText(path, JsonSerializer.Serialize(summary, SummaryJson));
    }
    catch (Exception ex)
    {
      // Evidence that could not be written must not fail a run that already happened.
      logger.LogWarning(ex, "Could not write the summary of run {RunId}.", run.RunId);
    }
  }

  private static readonly JsonSerializerOptions SummaryJson = new()
  {
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true,
  };

  private int ToPulses(decimal millimeters) =>
    (int)Math.Round(millimeters * PulsesPerMillimeter, MidpointRounding.AwayFromZero);

  /// <summary>
  /// The trace records positions in pulses, the unit the machine speaks, so a line of it can be
  /// compared to the address map without a conversion in the reader's head.
  /// </summary>
  private decimal PulsesPerMillimeter => hardware.Value.PulsesPerMillimeter;

  // No HTTP in this loop. See the class comment.
  private async Task ExecuteWaypointsAsync(RoutineRun run, IReadOnlyList<RoutineWaypoint> waypoints, CancellationToken ct)
  {
    foreach (var point in waypoints)
    {
      ct.ThrowIfCancellationRequested();

      run.CurrentOrder = point.Order;
      var startedAt = DateTimeOffset.UtcNow;

      try
      {
        var status = await axis.MoveToAsync(point.PositionX, point.PositionY, ct);

        run.Results.Add(new RoutineWaypointResult(
          point.PointId, point.Order, point.PositionX, point.PositionY,
          status.PositionX, status.PositionY, true, null, startedAt, DateTimeOffset.UtcNow, ReachedTarget: status.ReachedTarget));

        run.CompletedPoints++;
      }
      catch (Exception ex) when (ex is not OperationCanceledException)
      {
        run.Results.Add(new RoutineWaypointResult(
          point.PointId, point.Order, point.PositionX, point.PositionY,
          null, null, false, ex.Message, startedAt, DateTimeOffset.UtcNow));

        // A failed waypoint aborts the routine. Skipping ahead would mean inspecting a board
        // with a hole in it and reporting success.
        throw;
      }
    }

    run.CurrentOrder = null;
  }

  private async Task<IReadOnlyList<RoutineWaypoint>> LoadWaypointsAsync(
    StartRoutineRequest request, string? bearerToken, CancellationToken ct)
  {
    if (request.Points is { Count: > 0 })
    {
      // Contingency path: the desktop supplied the points, so the backend is not needed at all.
      return request.Points.OrderBy(p => p.Order).Select(p => p.ToWaypoint()).ToArray();
    }

    if (string.IsNullOrWhiteSpace(bearerToken))
    {
      throw new ForbiddenException(
        "No bearer token to forward: the gateway reads the program from the backend on behalf of the caller.");
    }

    var points = await backend.GetInspectionPointsAsync(request.ProgramId!.Value, bearerToken, ct);
    return points.Select(p => p.ToWaypoint()).ToArray();
  }

  private void Finish(RoutineRun run, RoutineStatus status, string? error)
  {
    run.Status = status;
    run.Error = error;
    run.CurrentOrder = null;
    run.FinishedAt = DateTimeOffset.UtcNow;

    var level = status is RoutineStatus.Completed ? LogLevel.Information : LogLevel.Warning;
    logger.Log(level, "Routine {RunId} finished as {Status} after {Completed}/{Total} point(s). {Error}",
      run.RunId, status, run.CompletedPoints, run.TotalPoints, error ?? string.Empty);
  }

  private void Retain(RoutineRun run)
  {
    _runs[run.RunId] = run;
    _runOrder.Enqueue(run.RunId);

    while (_runOrder.Count > MaxRetainedRuns && _runOrder.TryDequeue(out var oldest))
    {
      _runs.TryRemove(oldest, out _);
    }
  }

  public void Dispose()
  {
    _cancellation?.Dispose();
    GC.SuppressFinalize(this);
  }
}
