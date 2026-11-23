using SmartGlue.ClpGateway.Api.DTOs;
using SmartGlue.ClpGateway.Common.Exceptions;
using SmartGlue.ClpGateway.Modbus;
using SmartGlue.ClpGateway.Services;
using Xunit;

namespace SmartGlue.ClpGateway.Tests;

/// <summary>
/// The application (glue applicator) station — SGF-141. Same command cycle as the inspection
/// station, exercised here for what is different about this one: a third axis, two separate
/// move requests that must not touch each other's registers, and level-held jog.
/// </summary>
public class ApplicationStationTests
{
  [Fact]
  public async Task MoveXy_writes_target_and_speed_before_pulsing_and_leaves_Z_alone()
  {
    var machine = new FakeMachine();
    machine.SetApplicationReferenced(true);
    var application = TestHost.Application(machine);

    await application.MoveToAsync(12.5m, -4m, speedPulsesPerSecond: null, CancellationToken.None);

    var targetX = machine.Log.IndexOf($"dword:{ApplicationStationAddresses.TargetX}=12500");
    var targetY = machine.Log.IndexOf($"dword:{ApplicationStationAddresses.TargetY}=-4000");
    var speed = machine.Log.IndexOf($"dword:{ApplicationStationAddresses.SpeedXy}=20000");
    var pulse = machine.Log.IndexOf($"pulse:{ApplicationStationAddresses.MoveXy}");

    Assert.True(targetX >= 0 && targetY >= 0 && speed >= 0 && pulse >= 0);

    // The contract captures the parameters on the rising edge: a pulse that arrives before the
    // registers are written moves the axis to whatever was left there by the last command.
    Assert.True(targetX < pulse);
    Assert.True(targetY < pulse);
    Assert.True(speed < pulse);

    // "MOVE_XY captures only X, Y and SPEED_XY" — writing the Z block here would be a
    // contract violation that the machine would happily execute.
    Assert.DoesNotContain(machine.Log, entry => entry.StartsWith($"dword:{ApplicationStationAddresses.TargetZ}="));
    Assert.DoesNotContain(machine.Log, entry => entry.StartsWith($"dword:{ApplicationStationAddresses.SpeedZ}="));
  }

  [Fact]
  public async Task MoveZ_writes_only_the_Z_block_and_pulses_its_own_request()
  {
    var machine = new FakeMachine();
    machine.SetApplicationReferenced(true);
    var application = TestHost.Application(machine);

    var status = await application.MoveZAsync(-2m, speedPulsesPerSecond: 15000, CancellationToken.None);

    Assert.Contains($"dword:{ApplicationStationAddresses.TargetZ}=-2000", machine.Log);
    Assert.Contains($"dword:{ApplicationStationAddresses.SpeedZ}=15000", machine.Log);
    Assert.Equal(1, machine.PulseCount(ApplicationStationAddresses.MoveZ));
    Assert.Equal(0, machine.PulseCount(ApplicationStationAddresses.MoveXy));

    Assert.DoesNotContain(machine.Log, entry => entry.StartsWith($"dword:{ApplicationStationAddresses.TargetX}="));
    Assert.DoesNotContain(machine.Log, entry => entry.StartsWith($"dword:{ApplicationStationAddresses.SpeedXy}="));

    Assert.Equal(-2m, status.PositionZ);
  }

  [Fact]
  public async Task An_unreferenced_applicator_is_homed_before_the_first_move()
  {
    var machine = new FakeMachine();
    var application = TestHost.Application(machine);

    await application.MoveToAsync(10m, 10m, null, CancellationToken.None);

    Assert.Equal(1, machine.PulseCount(ApplicationStationAddresses.Home));
    Assert.Equal(1, machine.PulseCount(ApplicationStationAddresses.MoveXy));
  }

  [Fact]
  public async Task A_referenced_applicator_is_not_homed_again()
  {
    var machine = new FakeMachine();
    machine.SetApplicationReferenced(true);
    var application = TestHost.Application(machine);

    await application.MoveToAsync(10m, 10m, null, CancellationToken.None);

    Assert.Equal(0, machine.PulseCount(ApplicationStationAddresses.Home));
  }

  [Fact]
  public async Task Z_counts_for_referencing_on_a_station_that_has_it()
  {
    var machine = new FakeMachine();
    var application = TestHost.Application(machine);

    machine.SetApplicationReferenced(true);
    Assert.True(await application.IsReferencedAsync(CancellationToken.None));

    // X and Y homed, Z not: an absolute Z target would be refused by the machine, so the
    // gateway must not consider the station referenced.
    await machine.WriteCoilAsync(ApplicationStationAddresses.ReferencedZ, false, CancellationToken.None);
    Assert.False(await application.IsReferencedAsync(CancellationToken.None));
  }

  [Fact]
  public async Task Status_reports_the_three_axes_and_the_referencing_flags()
  {
    var machine = new FakeMachine();
    machine.SetApplicationPosition(30_000, 20_000, -3_000);
    machine.SetApplicationReferenced(true);
    var application = TestHost.Application(machine);

    var status = await application.GetStatusAsync(CancellationToken.None);

    Assert.Equal(AxisStatus.Ready, status.Status);
    Assert.Equal(30m, status.PositionX);
    Assert.Equal(20m, status.PositionY);
    Assert.Equal(-3m, status.PositionZ);
    Assert.True(status.ReferencedX);
    Assert.True(status.ReferencedY);
    Assert.True(status.ReferencedZ);
  }

  [Fact]
  public async Task A_machine_at_the_origin_that_was_never_homed_says_so()
  {
    var machine = new FakeMachine();
    var application = TestHost.Application(machine);

    var status = await application.GetStatusAsync(CancellationToken.None);

    // 0,0,0 and "not referenced" is exactly the state of a machine that was just powered on.
    // Without the flags the screen cannot tell it apart from a homed machine parked at zero.
    Assert.Equal(0m, status.PositionX);
    Assert.False(status.ReferencedX);
    Assert.False(status.ReferencedZ);
  }

  [Fact]
  public async Task Limits_include_Z()
  {
    var machine = new FakeMachine();
    var application = TestHost.Application(machine);

    var limits = await application.GetLimitsAsync(CancellationToken.None);

    Assert.Equal(-100m, limits.XMin);
    Assert.Equal(100m, limits.XMax);
    Assert.Equal(-50m, limits.ZMin);
    Assert.Equal(50m, limits.ZMax);
  }

  [Fact]
  public async Task A_Z_target_outside_the_limits_is_refused_without_touching_the_machine()
  {
    var machine = new FakeMachine();
    machine.SetApplicationReferenced(true);
    var application = TestHost.Application(machine);

    await Assert.ThrowsAsync<ArgumentException>(
      () => application.MoveZAsync(-80m, null, CancellationToken.None));

    Assert.Equal(0, machine.PulseCount(ApplicationStationAddresses.MoveZ));
  }

  [Fact]
  public async Task A_fault_refuses_the_next_command_and_names_the_route_that_clears_it()
  {
    var machine = new FakeMachine();
    machine.SetApplicationReferenced(true);
    machine.ApplicationFault = true;
    machine.ApplicationErrorCode = MachineErrors.Stopped;
    var application = TestHost.Application(machine);

    var error = await Assert.ThrowsAsync<ConflictException>(
      () => application.MoveToAsync(10m, 10m, null, CancellationToken.None));

    // The message has to point at a route that exists for THIS station — /axis/fault-reset
    // would be a dead end for someone on the application screen.
    Assert.Contains("/application/fault-reset", error.Message);
    Assert.Contains("1006 STOPPED", error.Message);
  }

  // ─── JOG ─────────────────────────────────────────────────────────────────────

  [Fact]
  public async Task Jog_holds_one_direction_and_releases_the_opposite_one()
  {
    var machine = new FakeMachine();
    machine.SetApplicationReferenced(true);
    var application = TestHost.Application(machine, TestHost.Options(o => o.JogDeadmanMs = 10_000));

    await application.JogAsync(JogAxis.X, direction: 1, active: true, CancellationToken.None);

    Assert.True(machine.IsJogHeld(ApplicationStationAddresses.JogXPositive));
    Assert.False(machine.IsJogHeld(ApplicationStationAddresses.JogXNegative));

    await application.JogAsync(JogAxis.X, direction: -1, active: true, CancellationToken.None);

    // Opposite directions may never be active together — the machine would have to arbitrate
    // between two commands that cancel each other.
    Assert.True(machine.IsJogHeld(ApplicationStationAddresses.JogXNegative));
    Assert.False(machine.IsJogHeld(ApplicationStationAddresses.JogXPositive));

    await application.JogAsync(JogAxis.X, direction: -1, active: false, CancellationToken.None);

    Assert.False(machine.IsJogHeld(ApplicationStationAddresses.JogXNegative));
  }

  [Fact]
  public async Task Jog_holds_the_bit_and_writes_no_speed()
  {
    // Contract v2: the DJOG runs at the frequency retained in D21600..D21750 and ignores
    // SPEED_XY. Writing D20500 before the bit (what v1 asked for) is noise; writing the jog
    // registers would be deciding the machine's jog speed from here.
    var machine = new FakeMachine();
    machine.SetApplicationReferenced(true);
    var application = TestHost.Application(machine, TestHost.Options(o => o.JogDeadmanMs = 10_000));

    await application.JogAsync(JogAxis.Y, direction: 1, active: true, CancellationToken.None);

    Assert.True(machine.IsJogHeld(ApplicationStationAddresses.JogYPositive));
    Assert.False(machine.Wrote(ApplicationStationAddresses.SpeedXy));
    Assert.False(machine.Wrote(ApplicationStationAddresses.JogSpeedYPositive));
  }

  [Theory]
  [InlineData(1, 10_000, true)]     // positive register for the + bit: fine
  [InlineData(1, -10_000, false)]   // sign swapped: the axis would run the wrong way
  [InlineData(1, 0, false)]         // zero: it would not run at all
  [InlineData(-1, -10_000, true)]
  [InlineData(-1, 10_000, false)]
  public async Task Jog_refuses_a_retained_speed_whose_sign_disagrees_with_the_direction(int direction, int retained, bool accepted)
  {
    var machine = new FakeMachine();
    machine.SetApplicationReferenced(true);
    machine.SetDword(direction < 0 ? ApplicationStationAddresses.JogSpeedXNegative : ApplicationStationAddresses.JogSpeedXPositive, retained);
    var application = TestHost.Application(machine, TestHost.Options(o => o.JogDeadmanMs = 10_000));

    var coil = direction < 0 ? ApplicationStationAddresses.JogXNegative : ApplicationStationAddresses.JogXPositive;

    if (accepted)
    {
      await application.JogAsync(JogAxis.X, direction, active: true, CancellationToken.None);
      Assert.True(machine.IsJogHeld(coil));
    }
    else
    {
      await Assert.ThrowsAsync<ConflictException>(() => application.JogAsync(JogAxis.X, direction, active: true, CancellationToken.None));
      Assert.False(machine.IsJogHeld(coil));
    }
  }

  [Fact]
  public async Task The_deadman_releases_a_jog_nobody_refreshed()
  {
    var machine = new FakeMachine();
    machine.SetApplicationReferenced(true);
    var application = TestHost.Application(machine, TestHost.Options(o => o.JogDeadmanMs = 30));

    await application.JogAsync(JogAxis.X, direction: 1, active: true, CancellationToken.None);
    Assert.True(machine.IsJogHeld(ApplicationStationAddresses.JogXPositive));

    // This is the failure the deadman exists for: the caller vanished — closed window, dropped
    // connection — and nothing else would ever write the bit back to 0.
    var deadline = DateTime.UtcNow.AddSeconds(2);
    while (machine.IsJogHeld(ApplicationStationAddresses.JogXPositive) && DateTime.UtcNow < deadline)
    {
      await Task.Delay(10);
    }

    Assert.False(machine.IsJogHeld(ApplicationStationAddresses.JogXPositive));
  }

  [Fact]
  public async Task Jog_is_refused_on_a_station_that_was_never_homed()
  {
    var machine = new FakeMachine();
    var application = TestHost.Application(machine, TestHost.Options(o => o.JogDeadmanMs = 10_000));

    await Assert.ThrowsAsync<ArgumentException>(
      () => application.JogAsync(JogAxis.X, 1, active: true, CancellationToken.None));

    Assert.False(machine.IsJogHeld(ApplicationStationAddresses.JogXPositive));
  }

  [Fact]
  public async Task Stop_releases_the_jog_before_pulsing()
  {
    var machine = new FakeMachine();
    machine.SetApplicationReferenced(true);
    var application = TestHost.Application(machine, TestHost.Options(o => o.JogDeadmanMs = 10_000));

    await application.JogAsync(JogAxis.X, direction: 1, active: true, CancellationToken.None);
    await application.StopAsync(CancellationToken.None);

    // The machine neutralizes its own jog levels on STOP, but the bit we wrote would still read
    // 1 — and clearing the fault afterwards would start the axis moving again.
    Assert.False(machine.IsJogHeld(ApplicationStationAddresses.JogXPositive));
    Assert.Equal(1, machine.PulseCount(ApplicationStationAddresses.Stop));
    Assert.True(machine.ApplicationFault);
  }

  [Fact]
  public async Task The_two_stations_do_not_share_state()
  {
    var machine = new FakeMachine();
    machine.SetApplicationReferenced(true);
    machine.SetReferenced(true);

    var application = TestHost.Application(machine);
    var (inspection, _) = TestHost.Axis(machine);

    await application.MoveToAsync(40m, 40m, null, CancellationToken.None);

    var inspectionStatus = await inspection.GetStatusAsync(CancellationToken.None);

    // Moving the applicator must not appear to have moved the camera: they are two machines
    // sharing one socket, not one machine with two names.
    Assert.Equal(0m, inspectionStatus.PositionX);
    Assert.Equal(0m, inspectionStatus.PositionY);
    Assert.Equal(40m, (await application.GetStatusAsync(CancellationToken.None)).PositionX);
  }
}
