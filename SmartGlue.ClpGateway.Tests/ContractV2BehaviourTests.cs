using System.Diagnostics;
using FluentModbus;
using Microsoft.Extensions.Logging.Abstractions;
using SmartGlue.ClpGateway.Api.DTOs;
using SmartGlue.ClpGateway.Common.Exceptions;
using SmartGlue.ClpGateway.Modbus;
using SmartGlue.ClpGateway.Services;
using Xunit;

namespace SmartGlue.ClpGateway.Tests;

/// <summary>
/// The behaviour contract v2 changed, one test per change (spec
/// specs/planos/GATEWAY-contrato-v2-simulador-0.2.0.md, §2 items G1–G12).
/// </summary>
public class ContractV2BehaviourTests
{
  private static Microsoft.Extensions.Options.IOptions<AxisHardwareOptions> Opts(Action<AxisHardwareOptions>? tweak = null) =>
    Microsoft.Extensions.Options.Options.Create(TestHost.Options(tweak));

  private static StationController Station(FakeMachine machine, StationAddresses addresses, AxisHardwareOptions? options = null, MachineIdentity? identity = null)
  {
    var opts = Microsoft.Extensions.Options.Options.Create(options ?? TestHost.Options());
    return new StationController(
      addresses,
      new TracingAxisConnection(machine, TestHost.Trace, opts),
      TestHost.Trace,
      opts,
      NullLogger.Instance,
      identity);
  }

  // ─── G1 · limites reais ────────────────────────────────────────────────────

  [Fact]
  public async Task Application_limits_are_read_from_the_real_block_not_from_the_jog_speeds()
  {
    var machine = new FakeMachine();
    var application = TestHost.Application(machine);

    var limits = await application.GetLimitsAsync(CancellationToken.None);

    // ±100 mm / ±50 mm (1000 p/mm) — not the ±10 mm the jog speeds would give.
    Assert.Equal(-100m, limits.XMin);
    Assert.Equal(100m, limits.XMax);
    Assert.Equal(-100m, limits.YMin);
    Assert.Equal(100m, limits.YMax);
    Assert.Equal(-50m, limits.ZMin);
    Assert.Equal(50m, limits.ZMax);
  }

  [Fact]
  public async Task Each_application_limit_comes_from_its_own_register()
  {
    // Distinct values per register, so reading X min from Y min's address cannot pass.
    var machine = new FakeMachine();
    machine.SetDword(23060, -60_000); // Y min
    machine.SetDword(23070, -70_000); // X min
    machine.SetDword(23080, 8_000);   // Z max
    machine.SetDword(23100, 100_000); // Y max
    machine.SetDword(23110, 110_000); // X max
    machine.SetDword(23120, -12_000); // Z min
    var application = TestHost.Application(machine);

    var limits = await application.GetLimitsAsync(CancellationToken.None);

    Assert.Equal((-70m, 110m, -60m, 100m, -12m, 8m), (limits.XMin, limits.XMax, limits.YMin, limits.YMax, limits.ZMin, limits.ZMax));
  }

  // ─── G3 / G4 · HOME com DONE e prazo próprio ───────────────────────────────

  [Fact]
  public async Task Home_with_a_completion_left_pending_is_acknowledged_first_instead_of_refused_with_1005()
  {
    var machine = new FakeMachine();
    machine.SetReferenced(false);
    machine.SetMoveDone(true);
    var (axis, _) = TestHost.Axis(machine);

    var status = await axis.ReturnToZeroAsync(CancellationToken.None);

    Assert.Equal(AxisStatus.Ready, status.Status);
    Assert.NotEqual(MachineErrors.DoneNotAcknowledged, machine.ErrorCode);
    Assert.True(await axis.IsReferencedAsync(CancellationToken.None));
  }

  [Fact]
  public async Task Home_longer_than_the_move_deadline_still_completes_within_its_own()
  {
    var machine = new FakeMachine { HomeDurationReads = 60 };
    machine.SetReferenced(false);
    var options = TestHost.Options(o =>
    {
      o.CommandPollIntervalMs = 20;
      o.CommandTimeoutSeconds = 1;
      o.HomeTimeoutSeconds = 10;
    });
    var (axis, _) = TestHost.Axis(machine, options);

    var watch = Stopwatch.StartNew();
    var status = await axis.ReturnToZeroAsync(CancellationToken.None);

    Assert.Equal(AxisStatus.Ready, status.Status);
    Assert.True(watch.Elapsed > TimeSpan.FromSeconds(1), $"HOME should have outlived the 1 s move deadline, took {watch.Elapsed}");
  }

  [Fact]
  public async Task Home_in_referenced_mode_does_not_conclude_before_it_has_started()
  {
    // A baseline ladder without the SG layer: HOME has no DONE. The station is still
    // referenced from an earlier HOME when the edge goes out; concluding on "not BUSY and
    // referenced" at the first read would return while the machine is about to re-reference.
    var machine = new FakeMachine { HomeDurationReads = 5 };
    machine.SetReferenced(true);
    var options = TestHost.Options(o => o.HomeCompletion = HomeCompletionMode.Referenced);
    var station = Station(machine, StationAddresses.Inspection, options);

    var status = await station.HomeAsync(CancellationToken.None);

    Assert.Equal(AxisStatus.Ready, status.Status);
    Assert.True(await station.IsReferencedAsync(CancellationToken.None));
  }

  // ─── G5 · recusa sem FAULT responde na hora ────────────────────────────────

  [Fact]
  public async Task A_refusal_without_fault_is_answered_at_once_not_after_the_deadline()
  {
    // The gateway is told the inspection has no Z, so it thinks X/Y are enough; the machine
    // (z_axis_mode=simulated) disagrees and refuses with 1001 without FAULT.
    var machine = new FakeMachine();
    machine.SetReferenced(true);
    machine.SetInspectionReferencedZ(false);
    var options = TestHost.Options(o =>
    {
      o.InspectionRequiresReferencedZ = false;
      o.CommandTimeoutSeconds = 10;
    });
    var (axis, _) = TestHost.Axis(machine, options);

    var watch = Stopwatch.StartNew();
    var ex = await Assert.ThrowsAsync<ConflictException>(() => axis.MoveToAsync(10, 10, CancellationToken.None));

    Assert.Contains("1001", ex.Message);
    Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"refusal took {watch.Elapsed}");
  }

  [Fact]
  public async Task The_same_refusal_repeated_is_recognised_after_the_grace_period()
  {
    // The operator retries: 1001 is already in the register before the edge, so there is no
    // change of code to see.
    var machine = new FakeMachine();
    machine.SetReferenced(true);
    machine.SetInspectionReferencedZ(false);
    machine.ErrorCode = MachineErrors.Unhomed;
    var options = TestHost.Options(o =>
    {
      o.InspectionRequiresReferencedZ = false;
      o.CommandTimeoutSeconds = 10;
      o.RefusalGraceMs = 100;
    });
    var (axis, _) = TestHost.Axis(machine, options);

    var watch = Stopwatch.StartNew();
    await Assert.ThrowsAsync<ConflictException>(() => axis.MoveToAsync(10, 10, CancellationToken.None));

    Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"refusal took {watch.Elapsed}");
  }

  [Fact]
  public async Task A_leftover_refusal_code_does_not_fail_a_command_the_machine_accepts()
  {
    var machine = new FakeMachine();
    machine.SetReferenced(true);
    machine.ErrorCode = MachineErrors.Unhomed;
    var (axis, _) = TestHost.Axis(machine);

    var status = await axis.MoveToAsync(10, 10, CancellationToken.None);

    Assert.Equal(10m, status.PositionX);
  }

  // ─── G7 · referência de Z da inspeção ──────────────────────────────────────

  [Fact]
  public async Task Inspection_with_z_unreferenced_homes_before_moving()
  {
    // A HOME stopped after Y: X/Y referenced, Z not.
    var machine = new FakeMachine();
    machine.SetReferenced(true);
    machine.SetInspectionReferencedZ(false);
    var (axis, _) = TestHost.Axis(machine);

    var status = await axis.MoveToAsync(10, 10, CancellationToken.None);

    Assert.Equal(1, machine.PulseCount(InspectionStationAddresses.Home));
    Assert.Equal(10m, status.PositionX);
  }

  [Fact]
  public async Task Inspection_z_is_not_exposed_in_the_status()
  {
    var machine = new FakeMachine();
    machine.SetReferenced(true);
    var station = Station(machine, StationAddresses.Inspection);

    var status = await station.GetStatusAsync(CancellationToken.None);

    Assert.Null(status.PositionZ);
    Assert.Null(status.ReferencedZ);
  }

  // ─── G8 · saturação ────────────────────────────────────────────────────────

  [Fact]
  public async Task A_move_that_saturates_is_reported_as_not_reached()
  {
    // Limits handed in wider than the machine's: the gateway's barrier lets it through and the
    // machine saturates at 100 mm, raising MOVE_DONE all the same.
    var machine = new FakeMachine();
    machine.SetApplicationReferenced(true);
    var station = Station(machine, StationAddresses.Application);

    var status = await station.MoveXyAsync(150, 0, null, new StationLimits(-200, 200, -200, 200, -50, 50), CancellationToken.None);

    Assert.False(status.ReachedTarget);
    Assert.Equal(100m, status.PositionX);
  }

  [Fact]
  public async Task A_move_inside_the_limits_is_reached()
  {
    var machine = new FakeMachine();
    machine.SetApplicationReferenced(true);
    var application = TestHost.Application(machine);

    await application.MoveToAsync(12.5m, -3m, null, CancellationToken.None);

    Assert.Equal(12_500, machine.ApplicationCurrentX);
    Assert.True((await application.Station.GetStatusAsync(CancellationToken.None)).ReachedTarget);
  }

  [Fact]
  public async Task Out_of_range_is_still_refused_before_anything_moves()
  {
    var machine = new FakeMachine();
    machine.SetApplicationReferenced(true);
    var application = TestHost.Application(machine);

    await Assert.ThrowsAsync<ArgumentException>(() => application.MoveToAsync(150, 0, null, CancellationToken.None));

    Assert.Equal(0, machine.PulseCount(ApplicationStationAddresses.MoveXy));
  }

  // ─── G9 · identidade ───────────────────────────────────────────────────────

  [Fact]
  public async Task An_incompatible_machine_refuses_commands_but_not_stop()
  {
    var machine = new FakeMachine();
    machine.SetApplicationReferenced(true);
    var identity = new MachineIdentity();
    identity.Record(101, 1);
    var station = Station(machine, StationAddresses.Application, identity: identity);

    await Assert.ThrowsAsync<ConflictException>(() => station.MoveXyAsync(1, 1, null, new StationLimits(-100, 100, -100, 100, -50, 50), CancellationToken.None));
    await Assert.ThrowsAsync<ConflictException>(() => station.HomeAsync(CancellationToken.None));
    await Assert.ThrowsAsync<ConflictException>(() => station.SetJogAsync(JogAxis.X, 1, true, CancellationToken.None));
    Assert.Equal(0, machine.PulseCount(ApplicationStationAddresses.MoveXy));

    await station.StopAsync(CancellationToken.None);
    Assert.Equal(1, machine.PulseCount(ApplicationStationAddresses.Stop));
  }

  [Fact]
  public async Task An_identity_not_read_yet_does_not_block_commands()
  {
    var machine = new FakeMachine();
    machine.SetApplicationReferenced(true);
    var station = Station(machine, StationAddresses.Application, identity: new MachineIdentity());

    var status = await station.MoveXyAsync(1, 1, null, new StationLimits(-100, 100, -100, 100, -50, 50), CancellationToken.None);

    Assert.Equal(1m, status.PositionX);
  }

  [Fact]
  public async Task The_heartbeat_reads_the_identity_when_the_session_opens()
  {
    var machine = new FakeMachine();
    var identity = new MachineIdentity();
    var heartbeat = new AxisHeartbeatService(machine, identity, TestHost.Trace, Opts(o => o.HeartbeatIntervalMs = 5), NullLogger<AxisHeartbeatService>.Instance);

    await heartbeat.StartAsync(CancellationToken.None);
    await WaitUntilAsync(() => identity.State != MachineIdentityState.Unknown);
    await heartbeat.StopAsync(CancellationToken.None);

    Assert.Equal(MachineIdentityState.Compatible, identity.State);
    Assert.Equal(MachineAddresses.ExpectedBuildCode, identity.BuildCode);
    Assert.True(machine.HeartbeatCounter > 0);
  }

  [Fact]
  public async Task The_heartbeat_flags_an_incompatible_machine_and_keeps_beating()
  {
    var machine = new FakeMachine();
    machine.SetIdentity(101, 1); // what the 1.0.0 kit published
    var identity = new MachineIdentity();
    var heartbeat = new AxisHeartbeatService(machine, identity, TestHost.Trace, Opts(o => o.HeartbeatIntervalMs = 5), NullLogger<AxisHeartbeatService>.Instance);

    await heartbeat.StartAsync(CancellationToken.None);
    await WaitUntilAsync(() => identity.State != MachineIdentityState.Unknown);
    var counter = machine.HeartbeatCounter;
    await WaitUntilAsync(() => machine.HeartbeatCounter > counter + 2);
    await heartbeat.StopAsync(CancellationToken.None);

    Assert.Equal(MachineIdentityState.Incompatible, identity.State);
  }

  [Fact]
  public async Task The_heartbeat_flags_an_echo_that_stopped_following_the_counter()
  {
    var machine = new FakeMachine { FreezeHeartbeatEcho = true };
    var identity = new MachineIdentity();
    var heartbeat = new AxisHeartbeatService(machine, identity, TestHost.Trace, Opts(o =>
    {
      o.HeartbeatIntervalMs = 5;
      o.HeartbeatEchoCheckEveryTicks = 2;
    }), NullLogger<AxisHeartbeatService>.Instance);

    await heartbeat.StartAsync(CancellationToken.None);
    await WaitUntilAsync(() => identity.EchoStale);
    await heartbeat.StopAsync(CancellationToken.None);

    Assert.True(identity.EchoStale);
  }

  // ─── G12 · exceção Modbus 03 ───────────────────────────────────────────────

  [Fact]
  public async Task Modbus_exception_03_on_the_request_is_a_refusal_not_a_communication_failure()
  {
    var machine = new FakeMachine
    {
      RefuseNextPulseWith = IllegalDataValue(),
    };
    machine.SetReferenced(true);
    var (axis, _) = TestHost.Axis(machine);

    var ex = await Assert.ThrowsAsync<ConflictException>(() => axis.MoveToAsync(10, 10, CancellationToken.None));

    Assert.Contains("parameter block", ex.Message);
  }

  // ─── G11 · catálogo ────────────────────────────────────────────────────────

  [Theory]
  [InlineData(MachineErrors.InvalidState, "1010 INVALID_STATE")]
  [InlineData(MachineErrors.NeedleCollision, "retract Z")]
  [InlineData(MachineErrors.DispenseNotAllowed, "1012 DISPENSE_NOT_ALLOWED")]
  public void The_catalog_knows_the_v2_codes(ushort code, string expected)
  {
    Assert.Contains(expected, MachineErrors.Describe(code));
  }

  [Fact]
  public void Only_the_documented_codes_are_refusals_without_fault()
  {
    Assert.True(MachineErrors.IsRefusalWithoutFault(MachineErrors.Unhomed));
    Assert.True(MachineErrors.IsRefusalWithoutFault(MachineErrors.CommandWhileBusy));
    Assert.True(MachineErrors.IsRefusalWithoutFault(MachineErrors.DoneNotAcknowledged));
    Assert.True(MachineErrors.IsRefusalWithoutFault(MachineErrors.InvalidState));
    Assert.False(MachineErrors.IsRefusalWithoutFault(MachineErrors.Stopped));
    Assert.False(MachineErrors.IsRefusalWithoutFault(MachineErrors.HeartbeatLost));
    Assert.False(MachineErrors.IsRefusalWithoutFault(MachineErrors.NeedleCollision));
    Assert.False(MachineErrors.IsRefusalWithoutFault(0));
  }

  // FluentModbus builds this exception itself and does not expose a constructor that takes the
  // code, so the test builds the one the client would raise for exception 03.
  private static ModbusException IllegalDataValue()
  {
    var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
    var type = typeof(ModbusException);

    var withCode = type.GetConstructors(flags)
      .FirstOrDefault(c => c.GetParameters().Select(p => p.ParameterType).SequenceEqual([typeof(ModbusExceptionCode), typeof(string)]));
    if (withCode is not null)
    {
      return (ModbusException)withCode.Invoke([ModbusExceptionCode.IllegalDataValue, "illegal data value"]);
    }

    var withMessage = type.GetConstructors(flags)
      .First(c => c.GetParameters().Select(p => p.ParameterType).SequenceEqual([typeof(string)]));
    var ex = (ModbusException)withMessage.Invoke(["illegal data value"]);

    var property = type.GetProperty(nameof(ModbusException.ExceptionCode), flags)!;
    if (property.SetMethod is { } setter)
    {
      setter.Invoke(ex, [ModbusExceptionCode.IllegalDataValue]);
    }
    else
    {
      type.GetField($"<{nameof(ModbusException.ExceptionCode)}>k__BackingField", flags)!.SetValue(ex, ModbusExceptionCode.IllegalDataValue);
    }

    return ex;
  }

  private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
  {
    var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
    while (!condition())
    {
      if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met in time");
      await Task.Delay(5);
    }
  }
}
