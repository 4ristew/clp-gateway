using Carter;
using SmartGlue.ClpGateway.Api.Docs;
using SmartGlue.ClpGateway.Common.Authentication;
using SmartGlue.ClpGateway.Common.Interceptors;
using SmartGlue.ClpGateway.Common.Returns;
using SmartGlue.ClpGateway.Routines;

namespace SmartGlue.ClpGateway.Api;

public class RoutineEndpoints : ICarterModule
{
  public void AddRoutes(IEndpointRouteBuilder app)
  {
    var group = app.MapGroup("/routines").WithTags("Routines").RequireAuthorization();

    // 202, never 200: a routine of N points takes far longer than any sensible HTTP timeout,
    // so the request returns a runId and the caller polls. Holding the socket open for the
    // duration of a physical process is how a UI ends up lying about what the machine is doing.
    group.MapPost("/run", (
      IRoutineExecutor executor,
      IRequesterContext requester,
      StartRoutineRequest request) =>
    {
      var requestedBy = $"{requester.GetUserRegistration()} ({requester.GetUserName()})";
      var run = executor.Start(request, requestedBy, requester.GetBearerToken());

      return CustomResults.Success(RoutineRunResponse.From(run), "Routine accepted!", StatusCodes.Status202Accepted);
    })
    .AddEndpointFilter<ValidationFilter<StartRoutineRequest>>()
    .RequireProfileLevels(ProfileLevels.FullPermission, ProfileLevels.HighPermission, ProfileLevels.MediumPermission)
    .StartRoutineDocs();

    // "Iniciar Teste" (SGF-141, criterion 7). Same 202-and-poll shape as /run, and the same
    // one-routine-at-a-time lock: the constraint is the machine, not the kind of run.
    group.MapPost("/dry-run", (
      IRoutineExecutor executor,
      IRequesterContext requester,
      DryRunRequest request) =>
    {
      var requestedBy = $"{requester.GetUserRegistration()} ({requester.GetUserName()})";
      var run = executor.StartDryRun(request, requestedBy);

      return CustomResults.Success(RoutineRunResponse.From(run), "Dry run accepted!", StatusCodes.Status202Accepted);
    })
    .AddEndpointFilter<ValidationFilter<DryRunRequest>>()
    .RequireProfileLevels(ProfileLevels.FullPermission, ProfileLevels.HighPermission, ProfileLevels.MediumPermission)
    .StartDryRunDocs();

    group.MapGet("/{runId}", (IRoutineExecutor executor, string runId) =>
    {
      var run = executor.Get(runId)
        ?? throw new KeyNotFoundException($"Run '{runId}' was not found. Runs are kept in memory and are lost on restart.");

      return CustomResults.Success(RoutineRunResponse.From(run), "Run retrieved successfully!");
    })
    .GetRoutineRunDocs();

    group.MapGet("/", (IRoutineExecutor executor, int take = 25) =>
    {
      var runs = executor.Recent(take).Select(RoutineRunResponse.From).ToArray();
      return CustomResults.Success(runs, "Recent runs retrieved successfully!");
    })
    .ListRoutineRunsDocs();

    // Same guarantee as POST /axis/stop, reachable from the routine screen: no profile
    // restriction, and it pulses the machine STOP rather than only cancelling the loop.
    group.MapPost("/{runId}/stop", async (
      IRoutineExecutor executor,
      Services.IAxisControlService axis,
      string runId,
      CancellationToken ct) =>
    {
      var run = executor.Get(runId)
        ?? throw new KeyNotFoundException($"Run '{runId}' was not found.");

      executor.RequestStop();
      await axis.StopAsync(ct);

      return CustomResults.Success(RoutineRunResponse.From(run), "Stop pulsed and the routine was cancelled.");
    })
    .StopRoutineDocs();
  }
}
