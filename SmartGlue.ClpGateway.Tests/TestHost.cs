using SmartGlue.ClpGateway.Trace;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartGlue.ClpGateway.Backend;
using SmartGlue.ClpGateway.Modbus;
using SmartGlue.ClpGateway.Routines;
using SmartGlue.ClpGateway.Services;

namespace SmartGlue.ClpGateway.Tests;

/// <summary>Builds the real services on top of <see cref="FakeMachine"/>, with fast timings.</summary>
public static class TestHost
{
  public static AxisHardwareOptions Options(Action<AxisHardwareOptions>? tweak = null)
  {
    var options = new AxisHardwareOptions
    {
      Enabled = true,
      PulsesPerMillimeter = 1000,
      DefaultSpeedPulsesPerSecond = 20000,
      CommandPollIntervalMs = 1,
      CommandTimeoutSeconds = 1,
      LimitsCacheMaxAgeSeconds = 300,
    };

    tweak?.Invoke(options);
    return options;
  }

  /// <summary>
  /// The trace, disabled. The suite exercises the command cycle, not the evidence: a sink that
  /// wrote files would leave a folder per test run on whoever's disk is running them.
  /// <see cref="TraceTests"/> turns it on deliberately.
  /// </summary>
  public static IMachineTrace Trace { get; } = new JsonlTraceSink(
    Microsoft.Extensions.Options.Options.Create(new TraceOptions { Enabled = false }),
    NullLogger<JsonlTraceSink>.Instance);

  public static (IAxisControlService Axis, IAxisLimitsProvider Limits) Axis(FakeMachine machine, AxisHardwareOptions? options = null)
  {
    var opts = Microsoft.Extensions.Options.Options.Create(options ?? Options());
    var wire = new TracingAxisConnection(machine, Trace, opts);
    var limits = new AxisLimitsProvider(wire, Trace, opts, NullLogger<AxisLimitsProvider>.Instance);
    var axis = new AxisControlService(wire, Trace, limits, opts, NullLogger<AxisControlService>.Instance);
    return (axis, limits);
  }

  /// <summary>The application station (SGF-141), over the same fake machine.</summary>
  public static ApplicationAxisService Application(FakeMachine machine, AxisHardwareOptions? options = null)
  {
    var opts = Microsoft.Extensions.Options.Options.Create(options ?? Options());
    return new ApplicationAxisService(new TracingAxisConnection(machine, Trace, opts), Trace, opts, NullLogger<ApplicationAxisService>.Instance);
  }

  public static RoutineExecutor Executor(
    FakeMachine machine,
    IBackendClient? backend = null,
    AxisHardwareOptions? options = null)
  {
    var (axis, limits) = Axis(machine, options);

    return new RoutineExecutor(
      axis,
      limits,
      Application(machine, options),
      backend ?? new UnreachableBackend(),
      Trace,
      Microsoft.Extensions.Options.Options.Create(options ?? Options()),
      new ResultQueue(
        Microsoft.Extensions.Options.Options.Create(new ResultQueueOptions { Enabled = false }),
        new StubEnvironment(),
        NullLogger<ResultQueue>.Instance),
      NullLogger<RoutineExecutor>.Instance);
  }

  /// <summary>Waits for a run to reach a terminal state, so tests never sleep blindly.</summary>
  public static async Task<RoutineRun> WaitForTerminalAsync(IRoutineExecutor executor, string runId, int timeoutMs = 5000)
  {
    var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

    while (DateTime.UtcNow < deadline)
    {
      var run = executor.Get(runId);
      if (run is { IsTerminal: true }) return run;
      await Task.Delay(5);
    }

    throw new TimeoutException($"Run '{runId}' did not reach a terminal state within {timeoutMs}ms.");
  }
}

public sealed class StubEnvironment : IHostEnvironment
{
  public string EnvironmentName { get; set; } = "Test";
  public string ApplicationName { get; set; } = "SmartGlue.ClpGateway.Tests";
  public string ContentRootPath { get; set; } = Path.GetTempPath();
  public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
    new Microsoft.Extensions.FileProviders.NullFileProvider();
}

/// <summary>Fails loudly if a test path reaches the backend when it should not.</summary>
public sealed class UnreachableBackend : IBackendClient
{
  public Task<IReadOnlyList<InspectionPoint>> GetInspectionPointsAsync(Guid programId, string bearerToken, CancellationToken ct) =>
    throw new InvalidOperationException("The backend must not be called on this path.");
}

public sealed class StubBackend(params InspectionPoint[] points) : IBackendClient
{
  public int Calls { get; private set; }

  public Task<IReadOnlyList<InspectionPoint>> GetInspectionPointsAsync(Guid programId, string bearerToken, CancellationToken ct)
  {
    Calls++;
    return Task.FromResult<IReadOnlyList<InspectionPoint>>(points);
  }
}
