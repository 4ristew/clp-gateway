using SmartGlue.ClpGateway.Common.Exceptions;
using SmartGlue.ClpGateway.Modbus;
using SmartGlue.ClpGateway.Routines;
using Xunit;

namespace SmartGlue.ClpGateway.Tests;

/// <summary>
/// "Iniciar Teste" (SGF-141, criterion 7): the applicator walks a mark's trajectory with no
/// glue. What these tests pin down is the SEQUENCE — the part that needed a bench to get right
/// and that no compiler will catch if it drifts.
/// </summary>
public class DryRunTests
{
  private static DryRunRequest Request(
    IEnumerable<(decimal X, decimal Y)> points,
    string kind = "line",
    decimal applicationZ = -2m,
    decimal travelZ = 0m,
    int preDispenseMs = 0,
    int postDispenseMs = 0,
    bool homeIfNeeded = true) =>
    new(
      ProgramId: Guid.NewGuid(),
      MarkName: "Cordao superior",
      Points: points.Select(p => new TrajectoryPoint(p.X, p.Y)).ToArray(),
      Kind: kind,
      ApplicationZ: applicationZ,
      TravelZ: travelZ,
      SpeedXyPulsesPerSecond: null,
      SpeedZPulsesPerSecond: null,
      PreDispenseMs: preDispenseMs,
      PostDispenseMs: postDispenseMs,
      HomeIfNeeded: homeIfNeeded);

  /// <summary>Z moves in the order the machine saw them, in millimetres.</summary>
  private static List<decimal> ZTargets(FakeMachine machine) => machine.Log
    .Where(entry => entry.StartsWith($"dword:{ApplicationStationAddresses.TargetZ}="))
    .Select(entry => decimal.Parse(entry.Split('=')[1]) / 1000m)
    .ToList();

  [Fact]
  public async Task A_line_lifts_before_the_first_point_and_only_again_at_the_end()
  {
    var machine = new FakeMachine();
    machine.SetApplicationReferenced(true);
    var executor = TestHost.Executor(machine);

    var run = executor.StartDryRun(Request([(10m, 10m), (20m, 20m), (30m, 30m)]), "bench");
    var finished = await TestHost.WaitForTerminalAsync(executor, run.RunId);

    Assert.Equal(RoutineStatus.Completed, finished.Status);
    Assert.Equal(3, finished.CompletedPoints);
    Assert.Equal(3, machine.PulseCount(ApplicationStationAddresses.MoveXy));

    // travel, then down at each of the three points, then travel once at the end. A line keeps
    // the nozzle down between points — lifting in between would break the bead.
    Assert.Equal([0m, -2m, -2m, -2m, 0m], ZTargets(machine));
  }

  [Fact]
  public async Task Discrete_points_lift_between_every_one_of_them()
  {
    var machine = new FakeMachine();
    machine.SetApplicationReferenced(true);
    var executor = TestHost.Executor(machine);

    var run = executor.StartDryRun(Request([(10m, 10m), (20m, 20m)], kind: "point"), "bench");
    await TestHost.WaitForTerminalAsync(executor, run.RunId);

    Assert.Equal([0m, -2m, 0m, -2m, 0m], ZTargets(machine));
  }

  [Fact]
  public async Task The_nozzle_is_lifted_before_the_first_XY_move()
  {
    var machine = new FakeMachine();
    machine.SetApplicationReferenced(true);
    var executor = TestHost.Executor(machine);

    var run = executor.StartDryRun(Request([(10m, 10m)]), "bench");
    await TestHost.WaitForTerminalAsync(executor, run.RunId);

    var firstLift = machine.Log.IndexOf($"pulse:{ApplicationStationAddresses.MoveZ}");
    var firstMove = machine.Log.IndexOf($"pulse:{ApplicationStationAddresses.MoveXy}");

    // Otherwise the first move drags the nozzle across the board from wherever it was parked.
    Assert.True(firstLift >= 0 && firstMove >= 0);
    Assert.True(firstLift < firstMove);
  }

  [Fact]
  public async Task An_unreferenced_applicator_is_homed_first_and_the_run_says_so()
  {
    var machine = new FakeMachine();
    var executor = TestHost.Executor(machine);

    var run = executor.StartDryRun(Request([(10m, 10m)]), "bench");
    var finished = await TestHost.WaitForTerminalAsync(executor, run.RunId);

    Assert.True(finished.Homed);
    Assert.Equal(1, machine.PulseCount(ApplicationStationAddresses.Home));
  }

  [Fact]
  public async Task A_point_outside_the_limits_refuses_the_whole_trajectory_without_moving()
  {
    var machine = new FakeMachine();
    machine.SetApplicationReferenced(true);
    var executor = TestHost.Executor(machine);

    // The seventh point is unreachable. Discovering that after tracing six of them leaves the
    // machine mid-drawing, somewhere nobody asked for.
    var run = executor.StartDryRun(Request([(10m, 10m), (999m, 10m)]), "bench");
    var finished = await TestHost.WaitForTerminalAsync(executor, run.RunId);

    Assert.Equal(RoutineStatus.Failed, finished.Status);
    Assert.Contains("Nothing was moved", finished.Error);
    Assert.Equal(0, machine.PulseCount(ApplicationStationAddresses.MoveXy));
    Assert.Equal(0, machine.PulseCount(ApplicationStationAddresses.MoveZ));
  }

  [Fact]
  public async Task A_Z_height_outside_the_limits_refuses_the_trajectory_too()
  {
    var machine = new FakeMachine();
    machine.SetApplicationReferenced(true);
    var executor = TestHost.Executor(machine);

    var run = executor.StartDryRun(Request([(10m, 10m)], applicationZ: -80m), "bench");
    var finished = await TestHost.WaitForTerminalAsync(executor, run.RunId);

    Assert.Equal(RoutineStatus.Failed, finished.Status);
    Assert.Contains("applicationZ", finished.Error);
    Assert.Equal(0, machine.PulseCount(ApplicationStationAddresses.MoveZ));
  }

  [Fact]
  public async Task A_dry_run_and_an_inspection_routine_cannot_run_at_the_same_time()
  {
    var machine = new FakeMachine();
    machine.SetApplicationReferenced(true);
    machine.StallMoves = true;
    var executor = TestHost.Executor(machine);

    executor.StartDryRun(Request([(10m, 10m)]), "bench");

    // One machine, one routine — of either kind. The lock is about the hardware, not about
    // which screen asked.
    Assert.Throws<ConflictException>(() =>
      executor.Start(new StartRoutineRequest(null, [new InlinePoint(Guid.NewGuid(), 1, 5m, 5m, "Standard")]), "bench", null));
  }

  [Fact]
  public async Task Stopping_a_dry_run_ends_it_as_stopped()
  {
    var machine = new FakeMachine();
    machine.SetApplicationReferenced(true);

    var executor = TestHost.Executor(machine);
    var started = new TaskCompletionSource();
    machine.MovePulsed += () => started.TrySetResult();

    var run = executor.StartDryRun(Request([(10m, 10m), (20m, 20m), (30m, 30m)], preDispenseMs: 200), "bench");

    await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
    executor.RequestStop();

    var finished = await TestHost.WaitForTerminalAsync(executor, run.RunId);

    Assert.Equal(RoutineStatus.Stopped, finished.Status);
    Assert.True(finished.CompletedPoints < 3);
  }

  [Fact]
  public async Task Each_waypoint_records_where_it_was_asked_to_go_and_where_it_arrived()
  {
    var machine = new FakeMachine();
    machine.SetApplicationReferenced(true);
    var executor = TestHost.Executor(machine);

    var run = executor.StartDryRun(Request([(12.5m, -4m)]), "bench");
    var finished = await TestHost.WaitForTerminalAsync(executor, run.RunId);

    var result = Assert.Single(finished.Results);
    Assert.True(result.Succeeded);
    Assert.Equal(12.5m, result.RequestedX);
    Assert.Equal(-4m, result.RequestedY);
    Assert.Equal(12.5m, result.ReachedX);
    Assert.Equal(-4m, result.ReachedY);
    Assert.Equal(-2m, result.ReachedZ);

    // A trajectory point is a position in a drawing, not a row of a table: it has no id, and
    // saying Guid.Empty would invite someone to look it up.
    Assert.Null(result.PointId);
  }

  [Fact]
  public async Task The_run_is_labelled_as_a_dry_run_and_carries_the_mark_name()
  {
    var machine = new FakeMachine();
    machine.SetApplicationReferenced(true);
    var executor = TestHost.Executor(machine);

    var run = executor.StartDryRun(Request([(10m, 10m)]), "bench");
    var finished = await TestHost.WaitForTerminalAsync(executor, run.RunId);

    Assert.Equal(RoutineKind.ApplicationDryRun, finished.Kind);
    Assert.Equal("Cordao superior", finished.MarkName);
    Assert.StartsWith("dry-", finished.RunId);
  }

  // ─── SGF-278: pré e pós-dispensamento por unidade ─────────────────────────────────

  private static DryRunRequest Units(string kind, params TrajectoryPoint[] points) => new(
    null, null, points, kind, -2m, 0m, null, null, PreDispenseMs: 7, PostDispenseMs: 3);

  private static int[] Dwells(DryRunRequest request) => request.Points!
    .Select((_, i) => RoutineExecutor.DispenseDwellMs(request, request.Points!, i, request.Kind == "line"))
    .ToArray();

  [Fact]
  public void In_a_line_each_vertex_waits_the_post_of_the_segment_it_ends_plus_the_pre_of_the_next()
  {
    var request = Units("line",
      new TrajectoryPoint(10m, 10m, PreDispenseMs: 100, PostDispenseMs: 20),
      new TrajectoryPoint(20m, 10m, PreDispenseMs: 50, PostDispenseMs: 30),
      // The last point starts no segment: its own times are never used.
      new TrajectoryPoint(30m, 10m, PreDispenseMs: 999, PostDispenseMs: 999));

    Assert.Equal([100, 20 + 50, 30], Dwells(request));
  }

  [Fact]
  public void A_point_unit_starts_and_ends_at_the_same_place_so_it_waits_both()
  {
    var request = Units("point",
      new TrajectoryPoint(10m, 10m, PreDispenseMs: 100, PostDispenseMs: 20),
      new TrajectoryPoint(20m, 10m));

    // The second point brings no times: the request's are the fallback.
    Assert.Equal([120, 7 + 3], Dwells(request));
  }

  [Fact]
  public void A_negative_dispense_time_is_refused_on_the_request_or_on_a_point()
  {
    var validator = new SmartGlue.ClpGateway.Api.Validators.DryRunRequestValidator();

    Assert.False(validator.Validate(Request([(10m, 10m)], postDispenseMs: -1)).IsValid);
    Assert.False(validator.Validate(Units("line", new TrajectoryPoint(10m, 10m, PreDispenseMs: -5))).IsValid);
    Assert.True(validator.Validate(Units("line", new TrajectoryPoint(10m, 10m, PreDispenseMs: 0))).IsValid);
  }
}
