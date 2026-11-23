using SmartGlue.ClpGateway.Backend;

namespace SmartGlue.ClpGateway.Routines;

public enum RoutineStatus
{
  /// <summary>Accepted; loading the points and reading the live limits. Nothing has moved.</summary>
  Preparing,
  Running,
  Completed,
  /// <summary>Interrupted by STOP — by this gateway, or by the machine reporting 1006.</summary>
  Stopped,
  /// <summary>Refused or aborted. Details in <see cref="RoutineRun.Error"/>.</summary>
  Failed,
  /// <summary>
  /// A command timed out mid-flight, or the process restarted while the run was live. The
  /// machine position is unknown and no automatic recovery is attempted.
  /// </summary>
  Indeterminate
}

public record RoutineWaypoint(
  Guid PointId,
  int Order,
  decimal PositionX,
  decimal PositionY,
  string Kind);

/// <summary>
/// What a run is walking. The machine invariant is the same for both — one routine at a time,
/// STOP always wins — but the two answer to different screens and different stations.
/// </summary>
public enum RoutineKind
{
  /// <summary>Inspection points of a program, on the camera station.</summary>
  InspectionPoints,

  /// <summary>An application trajectory, dry (no glue), on the applicator (SGF-141, criterion 7).</summary>
  ApplicationDryRun
}

/// <param name="PointId">
/// Null for an application trajectory: its points are an ordered drawing, not rows of a table.
/// </param>
/// <param name="ReachedZ">Null for a station this gateway drives in X/Y only.</param>
/// <param name="ReachedTarget">
/// False when the machine completed the move somewhere else — contract v2 saturates at the limit.
/// </param>
public record RoutineWaypointResult(
  Guid? PointId,
  int Order,
  decimal RequestedX,
  decimal RequestedY,
  decimal? ReachedX,
  decimal? ReachedY,
  bool Succeeded,
  string? Error,
  DateTimeOffset StartedAt,
  DateTimeOffset? FinishedAt,
  decimal? ReachedZ = null,
  bool ReachedTarget = true);

/// <summary>
/// One execution. Lives in memory for as long as the process does and is discarded on
/// restart — the backend remains the source of truth for what a program is, and the machine
/// remains the source of truth for where the axis is.
/// </summary>
public class RoutineRun
{
  public required string RunId { get; init; }
  public required Guid ProgramId { get; init; }
  public required string RequestedBy { get; init; }
  public required DateTimeOffset StartedAt { get; init; }

  public RoutineKind Kind { get; init; } = RoutineKind.InspectionPoints;

  /// <summary>Name of the mark being traced, for a dry run. Null for inspection points.</summary>
  public string? MarkName { get; init; }

  public RoutineStatus Status { get; set; } = RoutineStatus.Preparing;
  public string? Error { get; set; }
  public DateTimeOffset? FinishedAt { get; set; }

  public int TotalPoints { get; set; }
  public int CompletedPoints { get; set; }
  public int? CurrentOrder { get; set; }
  public bool Homed { get; set; }

  public List<RoutineWaypointResult> Results { get; } = [];

  public bool IsTerminal => Status is RoutineStatus.Completed or RoutineStatus.Stopped
    or RoutineStatus.Failed or RoutineStatus.Indeterminate;
}

public record StartRoutineRequest(Guid? ProgramId, IReadOnlyList<InlinePoint>? Points);

/// <summary>
/// Contingency shape: the desktop already has the points on screen, so it can run a routine
/// even when the backend is unreachable. Costs nothing and removes the datacenter from the
/// list of things that can stop a cell from working.
/// </summary>
public record InlinePoint(Guid Id, int Order, decimal PositionX, decimal PositionY, string? Kind);

/// <summary>
/// "Iniciar Teste" (SGF-141, criterion 7): walk a mark's trajectory with the applicator, with
/// no glue. Coordinates are in millimetres — the pixel-to-millimetre conversion belongs to the
/// screen that owns the image, not to the process that owns the machine.
/// </summary>
public record DryRunRequest(
  Guid? ProgramId,
  string? MarkName,
  IReadOnlyList<TrajectoryPoint>? Points,
  /// <summary>line: the nozzle stays down between points. point: it lifts between them.</summary>
  string? Kind,
  decimal ApplicationZ,
  decimal TravelZ,
  int? SpeedXyPulsesPerSecond,
  int? SpeedZPulsesPerSecond,
  /// <summary>Fallback for points that bring no time of their own (SGF-278).</summary>
  int PreDispenseMs = 0,
  int PostDispenseMs = 0,
  bool HomeIfNeeded = true);

/// <summary>
/// PreDispenseMs/PostDispenseMs belong to the unit that starts at this point (SGF-258/278): in a
/// line, the segment to the next point; in a point mark, the point. Null = the request's value.
/// </summary>
public record TrajectoryPoint(decimal PositionX, decimal PositionY, int? PreDispenseMs = null, int? PostDispenseMs = null);

public record RoutineRunResponse(
  string RunId,
  Guid ProgramId,
  string Status,
  string? Error,
  int TotalPoints,
  int CompletedPoints,
  int? CurrentOrder,
  bool Homed,
  string RequestedBy,
  DateTimeOffset StartedAt,
  DateTimeOffset? FinishedAt,
  IReadOnlyList<RoutineWaypointResult> Results,
  string Kind = "InspectionPoints",
  string? MarkName = null)
{
  public static RoutineRunResponse From(RoutineRun run) => new(
    run.RunId,
    run.ProgramId,
    run.Status.ToString(),
    run.Error,
    run.TotalPoints,
    run.CompletedPoints,
    run.CurrentOrder,
    run.Homed,
    run.RequestedBy,
    run.StartedAt,
    run.FinishedAt,
    run.Results.ToArray(),
    run.Kind.ToString(),
    run.MarkName);
}

public static class InspectionPointExtensions
{
  public static RoutineWaypoint ToWaypoint(this InspectionPoint point) =>
    new(point.Id, point.Order, point.PositionX, point.PositionY, point.Kind);

  public static RoutineWaypoint ToWaypoint(this InlinePoint point) =>
    new(point.Id, point.Order, point.PositionX, point.PositionY, point.Kind ?? "Standard");
}
