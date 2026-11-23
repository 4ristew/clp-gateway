using SmartGlue.ClpGateway.Api.DTOs;
using SmartGlue.ClpGateway.Common.Returns;

namespace SmartGlue.ClpGateway.Api.Docs;

public static class AxisDocs
{
  public static RouteHandlerBuilder GetAxisStatusDocs(this RouteHandlerBuilder builder)
  {
    return builder
      .WithSummary("Get axis/camera status")
      .WithDescription("Returns the current status (Ready / Moving / Fault / CommunicationFailure) and X/Y position (mm) of the inspection camera, read over Modbus TCP from the axis hardware (SmartGlue simulator today, the real PLC later). 'Fault' means the machine itself reported an error (see POST /axis/fault-reset); 'CommunicationFailure' means the hardware could not be reached at all, or AxisHardware:Enabled is false.")
      .Produces<SuccessResponse<AxisStatusResponse>>();
  }

  public static RouteHandlerBuilder MoveAxisDocs(this RouteHandlerBuilder builder)
  {
    return builder
      .WithSummary("Move the camera to a X/Y position")
      .WithDescription("Moves the inspection camera to the given X/Y position (mm) via the Modbus MOVE_XY handshake (write target/speed, pulse the request, poll BUSY/MOVE_DONE/FAULT). An axis that has never been referenced is homed automatically before the move, so the first movement after the machine is powered on does not fail for a reason the operator cannot guess. Rejects with 400 if the position is outside the axis limits (in contract v2 the machine saturates instead of refusing, so this check is the barrier), or if the automatic homing did not raise the referencing bits. Returns 409 if the axis is busy, is in FAULT state, refused the command without FAULT (1001/1004/1005/1010, answered at once), or if a routine is currently running — stop it with POST /axis/stop first. A command that times out mid-flight is never re-pulsed automatically.")
      .Produces<SuccessResponse<AxisStatusResponse>>()
      .ProducesValidationProblem()
      .Produces(StatusCodes.Status400BadRequest)
      .Produces(StatusCodes.Status409Conflict)
      .Produces(StatusCodes.Status502BadGateway);
  }

  public static RouteHandlerBuilder ReturnAxisToZeroDocs(this RouteHandlerBuilder builder)
  {
    return builder
      .WithSummary("Return the camera to the zero point")
      .WithDescription("Homes the inspection camera (Modbus HOME request) back to its zero reference position. Contract v2: HOME takes time (BUSY=1, X → Y → Z) and ends with MOVE_DONE, which the gateway acknowledges; it has its own deadline (AxisHardware:HomeTimeoutSeconds). Returns 409 if the axis is busy, in FAULT state, if the machine refuses it, or if a routine is running.")
      .Produces<SuccessResponse<AxisStatusResponse>>()
      .Produces(StatusCodes.Status409Conflict)
      .Produces(StatusCodes.Status502BadGateway);
  }

  public static RouteHandlerBuilder GetAxisLimitsDocs(this RouteHandlerBuilder builder)
  {
    return builder
      .WithSummary("Get axis X/Y limits")
      .WithDescription("Returns the X/Y axis limits (mm), read live over Modbus TCP and converted from pulses using the configured PulsesPerMillimeter factor (currently a placeholder — see AxisHardwareOptions). Falls back to the last in-memory reading if the hardware is momentarily unreachable, and fails with 502 once that reading is older than LimitsCacheMaxAgeSeconds. This is the endpoint the desktop should call to warn an operator while registering inspection points.")
      .Produces<SuccessResponse<AxisLimitsResponse>>()
      .Produces(StatusCodes.Status502BadGateway);
  }

  public static RouteHandlerBuilder StopAxisDocs(this RouteHandlerBuilder builder)
  {
    return builder
      .WithSummary("Stop the machine")
      .WithDescription("Pulses STOP on the inspection station and cancels any routine in progress. Deliberately carries no profile restriction beyond being authenticated, never waits for an in-flight command, and is never refused because a routine is running: any operator must be able to stop the machine at any moment. The machine answers a STOP by raising FAULT 1006 STOPPED, so clear it with POST /axis/fault-reset before commanding again.")
      .Produces<SuccessResponse<AxisStatusResponse>>()
      .Produces(StatusCodes.Status502BadGateway);
  }

  public static RouteHandlerBuilder ResetAxisFaultDocs(this RouteHandlerBuilder builder)
  {
    return builder
      .WithSummary("Reset an axis fault")
      .WithDescription("Clears a machine-side FAULT (e.g. target out of range, command issued while busy, STOPPED) by pulsing FAULT_RESET. Call this only after the underlying cause has ceased — the hardware itself decides whether the fault actually clears.")
      .Produces<SuccessResponse<AxisStatusResponse>>()
      .Produces(StatusCodes.Status502BadGateway);
  }
}
