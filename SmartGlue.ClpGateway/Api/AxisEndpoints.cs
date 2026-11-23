using Carter;
using SmartGlue.ClpGateway.Api.DTOs;
using SmartGlue.ClpGateway.Api.Docs;
using SmartGlue.ClpGateway.Common.Exceptions;
using SmartGlue.ClpGateway.Common.Interceptors;
using SmartGlue.ClpGateway.Common.Returns;
using SmartGlue.ClpGateway.Routines;
using SmartGlue.ClpGateway.Services;

namespace SmartGlue.ClpGateway.Api;

public class AxisEndpoints : ICarterModule
{
  public void AddRoutes(IEndpointRouteBuilder app)
  {
    var group = app.MapGroup("/axis").WithTags("Axis").RequireAuthorization();

    group.MapGet("/status", async (IAxisControlService service, CancellationToken ct) =>
    {
      var result = await service.GetStatusAsync(ct);
      return CustomResults.Success(result, "Axis status retrieved successfully!");
    })
    .RequireProfileLevels(ProfileLevels.FullPermission, ProfileLevels.HighPermission, ProfileLevels.MediumPermission)
    .GetAxisStatusDocs();

    // Ad-hoc movement is refused while a routine owns the machine. Two masters inside one
    // process is the same failure as two masters on one socket, only harder to see.
    group.MapPost("/move", async (IAxisControlService service, IRoutineExecutor routines, MoveAxisRequest request, CancellationToken ct) =>
    {
      EnsureNoRoutineRunning(routines);
      var result = await service.MoveToAsync(request.PositionX, request.PositionY, ct);
      return CustomResults.Success(result, "Axis moved successfully!");
    })
    .AddEndpointFilter<ValidationFilter<MoveAxisRequest>>()
    .RequireProfileLevels(ProfileLevels.FullPermission, ProfileLevels.HighPermission, ProfileLevels.MediumPermission)
    .MoveAxisDocs();

    group.MapPost("/zero", async (IAxisControlService service, IRoutineExecutor routines, CancellationToken ct) =>
    {
      EnsureNoRoutineRunning(routines);
      var result = await service.ReturnToZeroAsync(ct);
      return CustomResults.Success(result, "Axis returned to the zero point successfully!");
    })
    .RequireProfileLevels(ProfileLevels.FullPermission, ProfileLevels.HighPermission, ProfileLevels.MediumPermission)
    .ReturnAxisToZeroDocs();

    group.MapGet("/limits", async (IAxisLimitsProvider limitsProvider, CancellationToken ct) =>
    {
      var result = await limitsProvider.GetLimitsAsync(ct);
      return CustomResults.Success(result, "Axis limits retrieved successfully!");
    })
    .RequireProfileLevels(ProfileLevels.FullPermission, ProfileLevels.HighPermission, ProfileLevels.MediumPermission)
    .GetAxisLimitsDocs();

    // STOP carries NO profile restriction beyond being authenticated, is never refused because
    // a routine is running, and never queues behind an in-flight command. Any operator must be
    // able to stop the machine at any moment (SGF-140 ADR-01-R, decision 3).
    group.MapPost("/stop", async (IAxisControlService service, IRoutineExecutor routines, CancellationToken ct) =>
    {
      var cancelled = routines.RequestStop();
      var result = await service.StopAsync(ct);

      return CustomResults.Success(result, cancelled
        ? "Stop pulsed and the running routine was cancelled."
        : "Stop pulsed.");
    })
    .StopAxisDocs();

    group.MapPost("/fault-reset", async (IAxisControlService service, CancellationToken ct) =>
    {
      var result = await service.ResetFaultAsync(ct);
      return CustomResults.Success(result, "Axis fault reset successfully!");
    })
    .RequireProfileLevels(ProfileLevels.FullPermission, ProfileLevels.HighPermission, ProfileLevels.MediumPermission)
    .ResetAxisFaultDocs();
  }

  private static void EnsureNoRoutineRunning(IRoutineExecutor routines)
  {
    if (routines.IsRunning)
    {
      throw new ConflictException(
        "A routine is currently running. Stop it with POST /axis/stop before commanding the axis directly.");
    }
  }
}
