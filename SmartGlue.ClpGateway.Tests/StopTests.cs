using SmartGlue.ClpGateway.Common.Exceptions;
using SmartGlue.ClpGateway.Modbus;
using Xunit;

namespace SmartGlue.ClpGateway.Tests;

/// <summary>
/// STOP is the capability MR!14 declared an address for and never implemented. These tests
/// pin the two properties that make it a stop rather than a request: it reaches the machine,
/// and it does not queue behind the movement it is meant to interrupt.
/// </summary>
public class StopTests
{
  [Fact]
  public async Task Stop_pulses_the_stop_coil()
  {
    var machine = new FakeMachine();
    machine.SetReferenced(true);
    var (axis, _) = TestHost.Axis(machine);

    await axis.StopAsync(CancellationToken.None);

    Assert.Equal(1, machine.PulseCount(InspectionStationAddresses.Stop));
  }

  [Fact]
  public async Task Stop_does_not_wait_for_an_in_flight_command()
  {
    // The machine stalls, so a MOVE_XY sits in its polling loop holding the command lock.
    var machine = new FakeMachine { StallMoves = true };
    machine.SetReferenced(true);

    var options = TestHost.Options(o => o.CommandTimeoutSeconds = 30);
    var (axis, _) = TestHost.Axis(machine, options);

    var stopped = new TaskCompletionSource();

    // Fire the stop from inside the move, the moment the request bit is pulsed.
    machine.MovePulsed += () => _ = Task.Run(async () =>
    {
      await axis.StopAsync(CancellationToken.None);
      stopped.TrySetResult();
    });

    var move = axis.MoveToAsync(1m, 1m, CancellationToken.None);

    // If STOP took the command lock this would never complete: it would be waiting for the
    // 30-second move it is supposed to interrupt.
    var finished = await Task.WhenAny(stopped.Task, Task.Delay(5000));
    Assert.Same(stopped.Task, finished);
    Assert.Equal(1, machine.PulseCount(InspectionStationAddresses.Stop));

    // And the interrupted command surfaces the machine answer: FAULT 1006 STOPPED.
    var ex = await Assert.ThrowsAsync<ConflictException>(() => move);
    Assert.Contains("1006 STOPPED", ex.Message);
  }

  [Fact]
  public async Task Fault_reset_clears_the_stopped_fault()
  {
    var machine = new FakeMachine();
    machine.SetReferenced(true);
    var (axis, _) = TestHost.Axis(machine);

    await axis.StopAsync(CancellationToken.None);
    Assert.True(machine.Fault);

    await axis.ResetFaultAsync(CancellationToken.None);
    Assert.False(machine.Fault);
  }
}
