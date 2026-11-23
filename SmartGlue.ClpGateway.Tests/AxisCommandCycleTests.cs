using SmartGlue.ClpGateway.Api.DTOs;
using SmartGlue.ClpGateway.Common.Exceptions;
using SmartGlue.ClpGateway.Modbus;
using Xunit;

namespace SmartGlue.ClpGateway.Tests;

public class AxisCommandCycleTests
{
  [Fact]
  public async Task MoveTo_writes_target_and_speed_before_pulsing_the_request()
  {
    var machine = new FakeMachine();
    machine.SetReferenced(true);
    var (axis, _) = TestHost.Axis(machine);

    await axis.MoveToAsync(12.5m, -4m, CancellationToken.None);

    // Order matters: the kit rejects a request whose parameters were not written first
    // (exception 0x03, INCOMPLETE_PARAMETERS).
    var targetX = machine.Log.IndexOf($"dword:{InspectionStationAddresses.TargetX}=12500");
    var targetY = machine.Log.IndexOf($"dword:{InspectionStationAddresses.TargetY}=-4000");
    var speed = machine.Log.IndexOf($"dword:{InspectionStationAddresses.SpeedXy}=20000");
    var pulse = machine.Log.IndexOf($"pulse:{InspectionStationAddresses.MoveXy}");

    Assert.True(targetX >= 0 && targetY >= 0 && speed >= 0 && pulse >= 0);
    Assert.True(targetX < pulse);
    Assert.True(targetY < pulse);
    Assert.True(speed < pulse);
  }

  [Fact]
  public async Task MoveTo_acknowledges_move_done_before_returning()
  {
    var machine = new FakeMachine();
    machine.SetReferenced(true);
    var (axis, _) = TestHost.Axis(machine);

    var status = await axis.MoveToAsync(10m, 20m, CancellationToken.None);

    Assert.Equal(AxisStatus.Ready, status.Status);
    Assert.Equal(10m, status.PositionX);
    Assert.Equal(20m, status.PositionY);

    // Only the external master may clear the latch, and it must do so before the next command.
    Assert.False(machine.MoveDone);
    Assert.Contains($"coil:{InspectionStationAddresses.MoveDone}=False", machine.Log);
  }

  /// <summary>
  /// Máquina recém-ligada não está referenciada. Em vez de recusar a primeira movimentação com
  /// um erro que a tela só consegue traduzir como "não deu para mover", o gateway referencia e
  /// segue — é o que o executor de rotina já fazia antes de percorrer os pontos.
  /// </summary>
  [Fact]
  public async Task MoveTo_homes_first_when_the_axis_was_never_referenced()
  {
    var machine = new FakeMachine();
    machine.SetReferenced(false);
    var (axis, _) = TestHost.Axis(machine);

    var status = await axis.MoveToAsync(1m, 1m, CancellationToken.None);

    Assert.Equal(AxisStatus.Ready, status.Status);
    Assert.Equal(1, machine.PulseCount(InspectionStationAddresses.Home));
    Assert.Equal(1, machine.PulseCount(InspectionStationAddresses.MoveXy));
    Assert.True(await axis.IsReferencedAsync(CancellationToken.None));
  }

  /// <summary>Já referenciada, nada de homing extra: só o movimento pedido.</summary>
  [Fact]
  public async Task MoveTo_onAnAlreadyReferencedAxis_doesNotHomeAgain()
  {
    var machine = new FakeMachine();
    machine.SetReferenced(true);
    var (axis, _) = TestHost.Axis(machine);

    await axis.MoveToAsync(1m, 1m, CancellationToken.None);

    Assert.Equal(0, machine.PulseCount(InspectionStationAddresses.Home));
    Assert.Equal(1, machine.PulseCount(InspectionStationAddresses.MoveXy));
  }

  [Fact]
  public async Task MoveTo_is_refused_while_the_machine_is_busy()
  {
    var machine = new FakeMachine();
    machine.SetReferenced(true);
    machine.Busy = true;
    var (axis, _) = TestHost.Axis(machine);

    await Assert.ThrowsAsync<ConflictException>(() => axis.MoveToAsync(1m, 1m, CancellationToken.None));
    Assert.Equal(0, machine.PulseCount(InspectionStationAddresses.MoveXy));
  }

  [Fact]
  public async Task MoveTo_surfaces_the_machine_error_code_when_in_fault()
  {
    var machine = new FakeMachine();
    machine.SetReferenced(true);
    machine.Fault = true;
    machine.ErrorCode = MachineErrors.TargetOutOfRange;
    var (axis, _) = TestHost.Axis(machine);

    var ex = await Assert.ThrowsAsync<ConflictException>(() => axis.MoveToAsync(1m, 1m, CancellationToken.None));
    Assert.Contains("1002 TARGET_OUT_OF_RANGE", ex.Message);
  }

  [Fact]
  public async Task MoveTo_is_refused_outside_the_live_axis_limits_without_touching_the_machine()
  {
    var machine = new FakeMachine();
    machine.SetReferenced(true);
    var (axis, _) = TestHost.Axis(machine);

    // Limits are +/-100000 pulses at 1000 pulses/mm, i.e. +/-100mm.
    await Assert.ThrowsAsync<ArgumentException>(() => axis.MoveToAsync(101m, 0m, CancellationToken.None));
    Assert.Equal(0, machine.PulseCount(InspectionStationAddresses.MoveXy));
  }

  [Fact]
  public async Task A_command_that_never_completes_reports_indeterminate_and_is_never_re_pulsed()
  {
    var machine = new FakeMachine { StallMoves = true };
    machine.SetReferenced(true);
    var (axis, _) = TestHost.Axis(machine);

    var ex = await Assert.ThrowsAsync<IntegrationException>(() => axis.MoveToAsync(1m, 1m, CancellationToken.None));

    Assert.Contains("indeterminate", ex.Message);
    // The kit contract forbids repeating a command whose completion is unknown.
    Assert.Equal(1, machine.PulseCount(InspectionStationAddresses.MoveXy));
  }

  [Fact]
  public async Task Home_completes_on_move_done_and_acknowledges_it()
  {
    // Contract v2: HOME ends with SET MOVE_DONE (v1 never set it, and this test used to assert
    // the opposite). Left unacknowledged, the next HOME would be refused with 1005.
    var machine = new FakeMachine { HomeDurationReads = 3 };
    machine.SetReferenced(false);
    var (axis, _) = TestHost.Axis(machine);

    var status = await axis.ReturnToZeroAsync(CancellationToken.None);

    Assert.Equal(AxisStatus.Ready, status.Status);
    Assert.True(await axis.IsReferencedAsync(CancellationToken.None));
    Assert.False(machine.MoveDone);
    Assert.Contains($"coil:{InspectionStationAddresses.MoveDone}=False", machine.Log);
  }
}
