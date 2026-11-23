using FluentValidation;
using SmartGlue.ClpGateway.Api.DTOs;
using SmartGlue.ClpGateway.Routines;

namespace SmartGlue.ClpGateway.Api.Validators;

public class MoveAxisRequestValidator : AbstractValidator<MoveAxisRequest>
{
  public MoveAxisRequestValidator()
  {
    // PositionX/PositionY are non-nullable decimals, so model binding already guarantees they
    // are present. The bound check against the hardware-reported axis limits depends on
    // dynamic state and happens in the service (IAxisLimitsProvider), not here.
  }
}

public class MoveApplicationRequestValidator : AbstractValidator<MoveApplicationRequest>
{
  public MoveApplicationRequestValidator()
  {
    // Same as MoveAxisRequest: the bound check happens in the service, against the limits the
    // hardware reports. What can be judged without the machine is the speed — a zero or
    // negative pulses/s is not "use the default", it is a request the machine would refuse
    // with 1003 INVALID_SPEED.
    RuleFor(x => x.SpeedPulsesPerSecond)
      .GreaterThan(0)
      .When(x => x.SpeedPulsesPerSecond.HasValue)
      .WithMessage("speedPulsesPerSecond must be greater than zero. Omit it to use the configured default.");
  }
}

public class MoveApplicationZRequestValidator : AbstractValidator<MoveApplicationZRequest>
{
  public MoveApplicationZRequestValidator()
  {
    RuleFor(x => x.SpeedPulsesPerSecond)
      .GreaterThan(0)
      .When(x => x.SpeedPulsesPerSecond.HasValue)
      .WithMessage("speedPulsesPerSecond must be greater than zero. Omit it to use the configured default.");
  }
}

public class JogApplicationRequestValidator : AbstractValidator<JogApplicationRequest>
{
  public JogApplicationRequestValidator()
  {
    RuleFor(x => x.Axis)
      .Must(axis => string.Equals(axis, "X", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(axis, "Y", StringComparison.OrdinalIgnoreCase))
      .WithMessage("axis must be 'X' or 'Y'.");

    // Only the sign is read, but accepting anything else would let a caller believe a
    // magnitude means something here. It does not: jog speed comes from SPEED_XY.
    RuleFor(x => x.Direction)
      .Must(direction => direction is -1 or 1)
      .WithMessage("direction must be -1 or 1.");
  }
}

public class DryRunRequestValidator : AbstractValidator<DryRunRequest>
{
  public DryRunRequestValidator()
  {
    RuleFor(x => x.Points)
      .NotEmpty()
      .WithMessage("A dry run needs at least one point.");

    RuleFor(x => x.Kind)
      .Must(kind => kind is null
                 || string.Equals(kind, "line", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(kind, "point", StringComparison.OrdinalIgnoreCase))
      .WithMessage("kind must be 'line' or 'point'.");

    RuleFor(x => x.PreDispenseMs)
      .GreaterThanOrEqualTo(0)
      .WithMessage("preDispenseMs must not be negative.");

    RuleFor(x => x.PostDispenseMs)
      .GreaterThanOrEqualTo(0)
      .WithMessage("postDispenseMs must not be negative.");

    RuleForEach(x => x.Points)
      .Must(p => p.PreDispenseMs is null or >= 0 && p.PostDispenseMs is null or >= 0)
      .When(x => x.Points is not null)
      .WithMessage("A point's preDispenseMs/postDispenseMs must not be negative.");

    RuleFor(x => x.SpeedXyPulsesPerSecond)
      .GreaterThan(0)
      .When(x => x.SpeedXyPulsesPerSecond.HasValue)
      .WithMessage("speedXyPulsesPerSecond must be greater than zero. Omit it to use the configured default.");

    RuleFor(x => x.SpeedZPulsesPerSecond)
      .GreaterThan(0)
      .When(x => x.SpeedZPulsesPerSecond.HasValue)
      .WithMessage("speedZPulsesPerSecond must be greater than zero. Omit it to use the configured default.");

    // The travel height has to clear the board the application height touches. Which way that
    // is depends on how Z is mounted, so this only catches the case where they are equal —
    // which would drag the nozzle through the whole trajectory with no travel at all.
    RuleFor(x => x)
      .Must(x => x.TravelZ != x.ApplicationZ)
      .WithMessage("travelZ and applicationZ must differ: with the same height there is no travel, and the nozzle drags across the board.");
  }
}

public class StartRoutineRequestValidator : AbstractValidator<StartRoutineRequest>
{
  public StartRoutineRequestValidator()
  {
    RuleFor(x => x)
      .Must(x => x.ProgramId.HasValue || x.Points is { Count: > 0 })
      .WithMessage("Provide either programId or a non-empty list of points.");

    RuleFor(x => x.ProgramId)
      .NotEqual(Guid.Empty)
      .When(x => x.ProgramId.HasValue)
      .WithMessage("programId must not be an empty GUID.");

    RuleForEach(x => x.Points)
      .ChildRules(point =>
      {
        point.RuleFor(p => p.Order).GreaterThanOrEqualTo(0);
      })
      .When(x => x.Points is { Count: > 0 });
  }
}
