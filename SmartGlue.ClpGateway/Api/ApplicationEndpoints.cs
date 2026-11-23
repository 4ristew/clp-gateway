using Carter;
using SmartGlue.ClpGateway.Api.DTOs;
using SmartGlue.ClpGateway.Api.Docs;
using SmartGlue.ClpGateway.Common.Exceptions;
using SmartGlue.ClpGateway.Common.Interceptors;
using SmartGlue.ClpGateway.Common.Returns;
using SmartGlue.ClpGateway.Routines;
using SmartGlue.ClpGateway.Services;

namespace SmartGlue.ClpGateway.Api;

// The application (glue applicator) station — the machine behind the desktop's "2. Marcações"
// step (SGF-141). Deliberately mirrors /axis/* route by route, so the two stations read the
// same way from the outside; what is extra here is what the applicator actually has: a Z axis
// and jog arrows.
public class ApplicationEndpoints : ICarterModule
{
  public void AddRoutes(IEndpointRouteBuilder app)
  {
    var group = app.MapGroup("/application").WithTags("Application").RequireAuthorization();

    group.MapGet("/status", async (IApplicationAxisService service, CancellationToken ct) =>
    {
      var result = await service.GetStatusAsync(ct);
      return CustomResults.Success(result, "Applicator status retrieved successfully!");
    })
    .RequireProfileLevels(ProfileLevels.FullPermission, ProfileLevels.HighPermission, ProfileLevels.MediumPermission)
    .GetApplicationStatusDocs();

    group.MapGet("/limits", async (IApplicationAxisService service, CancellationToken ct) =>
    {
      var result = await service.GetLimitsAsync(ct);
      return CustomResults.Success(result, "Applicator limits retrieved successfully!");
    })
    .RequireProfileLevels(ProfileLevels.FullPermission, ProfileLevels.HighPermission, ProfileLevels.MediumPermission)
    .GetApplicationLimitsDocs();

    // Ad-hoc movement is refused while a routine owns the machine. Two masters inside one
    // process is the same failure as two masters on one socket, only harder to see.
    group.MapPost("/move", async (IApplicationAxisService service, IRoutineExecutor routines, MoveApplicationRequest request, CancellationToken ct) =>
    {
      EnsureNoRoutineRunning(routines);
      var result = await service.MoveToAsync(request.PositionX, request.PositionY, request.SpeedPulsesPerSecond, ct);
      return CustomResults.Success(result, "Applicator moved successfully!");
    })
    .AddEndpointFilter<ValidationFilter<MoveApplicationRequest>>()
    .RequireProfileLevels(ProfileLevels.FullPermission, ProfileLevels.HighPermission, ProfileLevels.MediumPermission)
    .MoveApplicationDocs();

    group.MapPost("/move-z", async (IApplicationAxisService service, IRoutineExecutor routines, MoveApplicationZRequest request, CancellationToken ct) =>
    {
      EnsureNoRoutineRunning(routines);
      var result = await service.MoveZAsync(request.PositionZ, request.SpeedPulsesPerSecond, ct);
      return CustomResults.Success(result, "Applicator Z axis moved successfully!");
    })
    .AddEndpointFilter<ValidationFilter<MoveApplicationZRequest>>()
    .RequireProfileLevels(ProfileLevels.FullPermission, ProfileLevels.HighPermission, ProfileLevels.MediumPermission)
    .MoveApplicationZDocs();

    group.MapPost("/zero", async (IApplicationAxisService service, IRoutineExecutor routines, CancellationToken ct) =>
    {
      EnsureNoRoutineRunning(routines);
      var result = await service.ReturnToZeroAsync(ct);
      return CustomResults.Success(result, "Applicator returned to the zero point successfully!");
    })
    .RequireProfileLevels(ProfileLevels.FullPermission, ProfileLevels.HighPermission, ProfileLevels.MediumPermission)
    .ReturnApplicationToZeroDocs();

    group.MapPost("/jog", async (IApplicationAxisService service, IRoutineExecutor routines, JogApplicationRequest request, CancellationToken ct) =>
    {
      // Releasing is always allowed: a routine that started while an arrow was held must not
      // leave that arrow held.
      if (request.Active) EnsureNoRoutineRunning(routines);

      var axis = string.Equals(request.Axis, "Y", StringComparison.OrdinalIgnoreCase) ? JogAxis.Y : JogAxis.X;
      var result = await service.JogAsync(axis, request.Direction, request.Active, ct);

      return CustomResults.Success(result, request.Active ? "Jogging." : "Jog released.");
    })
    .AddEndpointFilter<ValidationFilter<JogApplicationRequest>>()
    .RequireProfileLevels(ProfileLevels.FullPermission, ProfileLevels.HighPermission, ProfileLevels.MediumPermission)
    .JogApplicationDocs();

    // STOP carries NO profile restriction beyond being authenticated, is never refused because
    // a routine is running, and never queues behind an in-flight command. Any operator must be
    // able to stop the machine at any moment (SGF-140 ADR-01-R, decision 3).
    group.MapPost("/stop", async (IApplicationAxisService service, IRoutineExecutor routines, CancellationToken ct) =>
    {
      var cancelled = routines.RequestStop();
      var result = await service.StopAsync(ct);

      return CustomResults.Success(result, cancelled
        ? "Stop pulsed and the running routine was cancelled."
        : "Stop pulsed.");
    })
    .StopApplicationDocs();

    group.MapPost("/fault-reset", async (IApplicationAxisService service, CancellationToken ct) =>
    {
      var result = await service.ResetFaultAsync(ct);
      return CustomResults.Success(result, "Applicator fault reset successfully!");
    })
    .RequireProfileLevels(ProfileLevels.FullPermission, ProfileLevels.HighPermission, ProfileLevels.MediumPermission)
    .ResetApplicationFaultDocs();
  }

  private static void EnsureNoRoutineRunning(IRoutineExecutor routines)
  {
    if (routines.IsRunning)
    {
      throw new ConflictException(
        "A routine is currently running. Stop it with POST /application/stop before commanding the applicator directly.");
    }
  }
}
