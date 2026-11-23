using SmartGlue.ClpGateway.Common.Returns;
using SmartGlue.ClpGateway.Routines;

namespace SmartGlue.ClpGateway.Api.Docs;

public static class RoutineDocs
{
  public static RouteHandlerBuilder StartRoutineDocs(this RouteHandlerBuilder builder)
  {
    return builder
      .WithSummary("Run an inspection routine")
      .WithDescription(
        "Accepts a routine and returns 202 with a runId; poll GET /routines/{runId} for progress. " +
        "Supply either programId (the gateway reads the points from the backend, forwarding your bearer token) " +
        "or points (the desktop supplies them inline, which also works when the backend is unreachable). " +
        "Before the first movement the gateway loads every point and validates all of them against the live axis " +
        "limits: if any point falls outside, the whole routine is refused and nothing moves. It homes the axis first " +
        "if it is not referenced. Returns 409 if another routine is already running.")
      .Produces<SuccessResponse<RoutineRunResponse>>(StatusCodes.Status202Accepted)
      .ProducesValidationProblem()
      .Produces(StatusCodes.Status400BadRequest)
      .Produces(StatusCodes.Status403Forbidden)
      .Produces(StatusCodes.Status404NotFound)
      .Produces(StatusCodes.Status409Conflict)
      .Produces(StatusCodes.Status502BadGateway);
  }

  public static RouteHandlerBuilder GetRoutineRunDocs(this RouteHandlerBuilder builder)
  {
    return builder
      .WithSummary("Get the state of a run")
      .WithDescription(
        "Progress and per-waypoint results. Status is one of Preparing, Running, Completed, Stopped, Failed or " +
        "Indeterminate — the last meaning a command timed out mid-flight or the process restarted while the run was " +
        "live, so the machine position is unknown and no automatic recovery is attempted. Runs are kept in memory " +
        "and are lost on restart, by design: the backend is the source of truth for programs, the machine for position.")
      .Produces<SuccessResponse<RoutineRunResponse>>()
      .Produces(StatusCodes.Status404NotFound);
  }

  public static RouteHandlerBuilder ListRoutineRunsDocs(this RouteHandlerBuilder builder)
  {
    return builder
      .WithSummary("List recent runs")
      .WithDescription("The most recent runs held in memory, newest first (at most 50 are retained).")
      .Produces<SuccessResponse<RoutineRunResponse[]>>();
  }

  public static RouteHandlerBuilder StartDryRunDocs(this RouteHandlerBuilder builder)
  {
    return builder
      .WithSummary("Run an application trajectory dry")
      .WithDescription(
        "Walks a mark's trajectory with the applicator and NO glue — the \"Iniciar Teste\" of the program registration (SGF-141, criterion 7). Accepted with 202 and a runId to poll, like any routine; one routine at a time, of either kind, because the constraint is the machine.\n\n" +
        "The sequence, proved against the kit simulator: home if needed, lift to `travelZ`, then for each point move X/Y, lower to `applicationZ`, stay still for the unit's `preDispenseMs`/`postDispenseMs` (per point, falling back to the request's; in a line a vertex waits the post of the segment it ends plus the pre of the one it starts), and lift again — always on the last point, and between every point when `kind` is `point` rather than `line`.\n\n" +
        "Coordinates are in **millimetres**: converting a drawing in image pixels is the job of the screen that owns the image and its camera geometry, not of this process. Speeds, when given, are in pulses per second. Every point and both Z heights are validated against the live limits before anything moves — a trajectory is refused whole rather than abandoned half-drawn.\n\n" +
        "This run is movement, never dispensing: the simulator has no nozzle, and neither does this endpoint.")
      .Produces<SuccessResponse<RoutineRunResponse>>(StatusCodes.Status202Accepted)
      .ProducesValidationProblem()
      .Produces(StatusCodes.Status400BadRequest)
      .Produces(StatusCodes.Status409Conflict);
  }

  public static RouteHandlerBuilder StopRoutineDocs(this RouteHandlerBuilder builder)
  {
    return builder
      .WithSummary("Stop a running routine")
      .WithDescription(
        "Cancels the routine and pulses STOP on the machine. Like POST /axis/stop, it carries no profile restriction " +
        "beyond being authenticated. The machine answers a STOP by raising FAULT 1006 STOPPED, so clear it with " +
        "POST /axis/fault-reset before commanding again.")
      .Produces<SuccessResponse<RoutineRunResponse>>()
      .Produces(StatusCodes.Status404NotFound)
      .Produces(StatusCodes.Status502BadGateway);
  }
}
