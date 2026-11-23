using SmartGlue.ClpGateway.Backend;
using SmartGlue.ClpGateway.Common.Exceptions;
using SmartGlue.ClpGateway.Modbus;
using SmartGlue.ClpGateway.Routines;
using Xunit;

namespace SmartGlue.ClpGateway.Tests;

public class RoutineExecutorTests
{
  private static InlinePoint Point(int order, decimal x, decimal y) =>
    new(Guid.NewGuid(), order, x, y, "Standard");

  [Fact]
  public async Task Runs_every_point_in_order_and_reports_where_the_axis_landed()
  {
    var machine = new FakeMachine();
    machine.SetReferenced(true);
    var executor = TestHost.Executor(machine);

    var request = new StartRoutineRequest(null, [Point(0, 1m, 2m), Point(1, 3m, 4m), Point(2, 5m, 6m)]);
    var started = executor.Start(request, "tester", null);

    var run = await TestHost.WaitForTerminalAsync(executor, started.RunId);

    Assert.Equal(RoutineStatus.Completed, run.Status);
    Assert.Equal(3, run.TotalPoints);
    Assert.Equal(3, run.CompletedPoints);
    Assert.Equal(3, machine.PulseCount(InspectionStationAddresses.MoveXy));
    Assert.All(run.Results, r => Assert.True(r.Succeeded));
    Assert.Equal([0, 1, 2], run.Results.Select(r => r.Order));
    Assert.Equal(5000, machine.CurrentX);
    Assert.Equal(6000, machine.CurrentY);
  }

  [Fact]
  public async Task Refuses_the_whole_routine_when_any_point_is_out_of_range_and_moves_nothing()
  {
    var machine = new FakeMachine();
    machine.SetReferenced(true);
    var executor = TestHost.Executor(machine);

    // Point #1 is reachable, #2 is not. Moving to #1 and only then discovering #2 would leave
    // the machine somewhere nobody asked for.
    var request = new StartRoutineRequest(null, [Point(0, 1m, 1m), Point(1, 999m, 0m)]);
    var started = executor.Start(request, "tester", null);

    var run = await TestHost.WaitForTerminalAsync(executor, started.RunId);

    Assert.Equal(RoutineStatus.Failed, run.Status);
    Assert.Contains("Nothing was moved", run.Error);
    Assert.Contains("#1", run.Error);
    Assert.Equal(0, run.CompletedPoints);
    Assert.Equal(0, machine.PulseCount(InspectionStationAddresses.MoveXy));
  }

  [Fact]
  public async Task Homes_the_axis_first_when_it_is_not_referenced()
  {
    var machine = new FakeMachine();
    machine.SetReferenced(false);
    var executor = TestHost.Executor(machine);

    var started = executor.Start(new StartRoutineRequest(null, [Point(0, 1m, 1m)]), "tester", null);
    var run = await TestHost.WaitForTerminalAsync(executor, started.RunId);

    Assert.Equal(RoutineStatus.Completed, run.Status);
    Assert.True(run.Homed);
    Assert.Equal(1, machine.PulseCount(InspectionStationAddresses.Home));
  }

  [Fact]
  public async Task Stopping_a_running_routine_marks_it_stopped()
  {
    var machine = new FakeMachine { StallMoves = true };
    machine.SetReferenced(true);

    var options = TestHost.Options(o => o.CommandTimeoutSeconds = 30);
    var executor = TestHost.Executor(machine, options: options);

    var stopRequested = new TaskCompletionSource();
    machine.MovePulsed += () => _ = Task.Run(() =>
    {
      executor.RequestStop();
      stopRequested.TrySetResult();
    });

    var started = executor.Start(new StartRoutineRequest(null, [Point(0, 1m, 1m)]), "tester", null);

    await stopRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var run = await TestHost.WaitForTerminalAsync(executor, started.RunId);

    Assert.Equal(RoutineStatus.Stopped, run.Status);
    Assert.False(executor.IsRunning);
  }

  [Fact]
  public async Task A_second_routine_is_refused_while_one_is_running()
  {
    var machine = new FakeMachine { StallMoves = true };
    machine.SetReferenced(true);

    var options = TestHost.Options(o => o.CommandTimeoutSeconds = 30);
    var executor = TestHost.Executor(machine, options: options);

    var moving = new TaskCompletionSource();
    machine.MovePulsed += () => moving.TrySetResult();

    var first = executor.Start(new StartRoutineRequest(null, [Point(0, 1m, 1m)]), "tester", null);
    await moving.Task.WaitAsync(TimeSpan.FromSeconds(5));

    Assert.Throws<ConflictException>(() =>
      executor.Start(new StartRoutineRequest(null, [Point(0, 2m, 2m)]), "tester", null));

    executor.RequestStop();
    await TestHost.WaitForTerminalAsync(executor, first.RunId);
  }

  [Fact]
  public async Task Reads_the_points_from_the_backend_exactly_once_when_given_a_programId()
  {
    var machine = new FakeMachine();
    machine.SetReferenced(true);

    var backend = new StubBackend(
      new InspectionPoint(Guid.NewGuid(), 0, 1m, 1m, "Standard"),
      new InspectionPoint(Guid.NewGuid(), 1, 2m, 2m, "Standard"));

    var executor = TestHost.Executor(machine, backend);

    var started = executor.Start(new StartRoutineRequest(Guid.NewGuid(), null), "tester", "a-token");
    var run = await TestHost.WaitForTerminalAsync(executor, started.RunId);

    Assert.Equal(RoutineStatus.Completed, run.Status);
    // BUSCAR ANTES, e uma vez só: nenhuma chamada HTTP entre dois pontos.
    Assert.Equal(1, backend.Calls);
  }

  [Fact]
  public async Task Inline_points_never_touch_the_backend()
  {
    var machine = new FakeMachine();
    machine.SetReferenced(true);

    // UnreachableBackend throws if called — this is the contingency path for a cell whose link
    // to the datacenter is down.
    var executor = TestHost.Executor(machine, new UnreachableBackend());

    var started = executor.Start(new StartRoutineRequest(null, [Point(0, 1m, 1m)]), "tester", null);
    var run = await TestHost.WaitForTerminalAsync(executor, started.RunId);

    Assert.Equal(RoutineStatus.Completed, run.Status);
  }

  [Fact]
  public async Task A_routine_with_a_programId_and_no_token_is_refused()
  {
    var machine = new FakeMachine();
    var executor = TestHost.Executor(machine);

    var started = executor.Start(new StartRoutineRequest(Guid.NewGuid(), null), "tester", bearerToken: null);
    var run = await TestHost.WaitForTerminalAsync(executor, started.RunId);

    Assert.Equal(RoutineStatus.Failed, run.Status);
    Assert.Contains("bearer token", run.Error);
  }

  [Fact]
  public void A_routine_with_neither_programId_nor_points_is_refused_up_front()
  {
    var executor = TestHost.Executor(new FakeMachine());

    Assert.Throws<ArgumentException>(() =>
      executor.Start(new StartRoutineRequest(null, null), "tester", null));
  }

  [Fact]
  public async Task A_failing_waypoint_aborts_the_routine_instead_of_skipping_ahead()
  {
    var machine = new FakeMachine();
    machine.SetReferenced(true);
    var executor = TestHost.Executor(machine);

    // The machine faults as soon as the first move lands.
    machine.MovePulsed += () =>
    {
      machine.Fault = true;
      machine.ErrorCode = MachineErrors.TargetOutOfRange;
    };

    var request = new StartRoutineRequest(null, [Point(0, 1m, 1m), Point(1, 2m, 2m), Point(2, 3m, 3m)]);
    var started = executor.Start(request, "tester", null);
    var run = await TestHost.WaitForTerminalAsync(executor, started.RunId);

    Assert.Equal(RoutineStatus.Failed, run.Status);
    // Inspecting a board with a hole in it and reporting success is worse than stopping.
    Assert.Equal(1, machine.PulseCount(InspectionStationAddresses.MoveXy));
    Assert.Single(run.Results);
    Assert.False(run.Results[0].Succeeded);
  }
}
