using SmartGlue.ClpGateway.Api.DTOs;
using SmartGlue.ClpGateway.Common.Returns;

namespace SmartGlue.ClpGateway.Api.Docs;

public static class ApplicationDocs
{
  public static RouteHandlerBuilder GetApplicationStatusDocs(this RouteHandlerBuilder builder)
  {
    return builder
      .WithSummary("Get applicator status")
      .WithDescription("Returns the current status (Ready / Moving / Fault / CommunicationFailure), the X/Y/Z position (mm) and the per-axis referencing flags of the glue applicator, read over Modbus TCP. The referencing flags matter to the screen: a machine that was just powered on sits at 0,0,0 and is NOT homed, which looks identical to a homed machine parked at the origin. 'Fault' means the machine reported an error (see POST /application/fault-reset); 'CommunicationFailure' means the hardware could not be reached at all.")
      .Produces<SuccessResponse<ApplicationStatusResponse>>();
  }

  public static RouteHandlerBuilder GetApplicationLimitsDocs(this RouteHandlerBuilder builder)
  {
    return builder
      .WithSummary("Get applicator X/Y/Z limits")
      .WithDescription("Returns the applicator's soft limits (mm), read live over Modbus and converted from pulses with the configured PulsesPerMillimeter factor (currently a placeholder). Falls back to the last in-memory reading while the hardware is momentarily unreachable, and fails with 502 once that reading is older than LimitsCacheMaxAgeSeconds. The gateway only reads these registers: although the contract lets the master write them, choosing the bounds a machine will refuse to cross is not a decision this process makes.")
      .Produces<SuccessResponse<ApplicationLimitsResponse>>()
      .Produces(StatusCodes.Status502BadGateway);
  }

  public static RouteHandlerBuilder MoveApplicationDocs(this RouteHandlerBuilder builder)
  {
    return builder
      .WithSummary("Move the applicator to an X/Y position")
      .WithDescription("MOVE_XY on the application station: writes X, Y and SPEED_XY, pulses the request and polls BUSY/MOVE_DONE/FAULT. It captures nothing of the Z block — lowering the applicator onto the point is a separate POST /application/move-z, by contract. An applicator that has never been referenced is homed first. Speed, when given, is in pulses per second, the machine's own unit; the 0-100 shown on screen is not a speed and must not be sent here. Rejects with 400 outside the limits — the only barrier in contract v2, where the machine saturates an out-of-range target at the limit instead of refusing it; reachedTarget=false in the response says a move ended away from its target. 409 if busy, in FAULT, refused by the machine without FAULT (1001/1004/1005/1010, answered at once), or while a routine owns the machine.")
      .Produces<SuccessResponse<ApplicationStatusResponse>>()
      .ProducesValidationProblem()
      .Produces(StatusCodes.Status400BadRequest)
      .Produces(StatusCodes.Status409Conflict)
      .Produces(StatusCodes.Status502BadGateway);
  }

  public static RouteHandlerBuilder MoveApplicationZDocs(this RouteHandlerBuilder builder)
  {
    return builder
      .WithSummary("Move the applicator's Z axis")
      .WithDescription("MOVE_Z on the application station: writes Z and SPEED_Z and pulses the request, touching nothing of the X/Y block. This is the command behind the application/travel heights of a program. Rejects with 400 outside the Z limits, 409 if busy, in FAULT, or while a routine owns the machine.")
      .Produces<SuccessResponse<ApplicationStatusResponse>>()
      .ProducesValidationProblem()
      .Produces(StatusCodes.Status400BadRequest)
      .Produces(StatusCodes.Status409Conflict)
      .Produces(StatusCodes.Status502BadGateway);
  }

  public static RouteHandlerBuilder ReturnApplicationToZeroDocs(this RouteHandlerBuilder builder)
  {
    return builder
      .WithSummary("Home the applicator")
      .WithDescription("Homes X, Y and Z of the applicator (Modbus HOME request). Contract v2: HOME takes time (BUSY=1 while it references X → Y → Z, one axis at a time) and ends with MOVE_DONE, which the gateway acknowledges like any move; it has its own deadline (AxisHardware:HomeTimeoutSeconds, 90 s). Returns 409 if the station is busy, in FAULT, if the machine refuses it, or while a routine is running.")
      .Produces<SuccessResponse<ApplicationStatusResponse>>()
      .Produces(StatusCodes.Status409Conflict)
      .Produces(StatusCodes.Status502BadGateway);
  }

  public static RouteHandlerBuilder StopApplicationDocs(this RouteHandlerBuilder builder)
  {
    return builder
      .WithSummary("Stop the applicator")
      .WithDescription("Releases any held jog, pulses STOP on the application station and cancels a routine in progress. Carries no profile restriction beyond being authenticated, never waits for an in-flight command and is never refused because a routine is running: any operator must be able to stop the machine at any moment. The machine answers a STOP by raising FAULT 1006 STOPPED, so clear it with POST /application/fault-reset before commanding again.")
      .Produces<SuccessResponse<ApplicationStatusResponse>>()
      .Produces(StatusCodes.Status502BadGateway);
  }

  public static RouteHandlerBuilder ResetApplicationFaultDocs(this RouteHandlerBuilder builder)
  {
    return builder
      .WithSummary("Reset an applicator fault")
      .WithDescription("Clears a machine-side FAULT (target out of range, command issued while busy, STOPPED) by pulsing FAULT_RESET. Call it only after the underlying cause has ceased — the hardware decides whether the fault actually clears.")
      .Produces<SuccessResponse<ApplicationStatusResponse>>()
      .Produces(StatusCodes.Status502BadGateway);
  }

  public static RouteHandlerBuilder JogApplicationDocs(this RouteHandlerBuilder builder)
  {
    return builder
      .WithSummary("Jog the applicator while an arrow is held")
      .WithDescription("Holds one JOG direction (axis X or Y, direction -1 or +1) at 1 while the operator keeps the arrow pressed, and releases it with active=false. Unlike every other command here, JOG is a held level and not a pulse, so the caller must keep refreshing it: if no request arrives within AxisHardware:JogDeadmanMs the gateway releases the axis itself. That deadman is deliberate — a closed window or a dropped connection must not leave an axis travelling until it hits a limit. Opposite directions are never active at the same time. Speed (contract v2): the machine jogs at the frequency retained in D21600..D21750, whose SIGN sets the direction on the PLC; the gateway does not write it, and refuses with 409 a direction whose retained speed is zero or has the wrong sign. With a ramp-down configured, the status returned by a release may still read Moving until the axis has decelerated. Returns 400 if the station has not been homed, 409 if it is in FAULT, the jog speed is wrong, or the machine is incompatible.")
      .Produces<SuccessResponse<ApplicationStatusResponse>>()
      .ProducesValidationProblem()
      .Produces(StatusCodes.Status400BadRequest)
      .Produces(StatusCodes.Status409Conflict)
      .Produces(StatusCodes.Status502BadGateway);
  }
}
