using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartGlue.ClpGateway.Backend;
using SmartGlue.ClpGateway.Modbus;
using SmartGlue.ClpGateway.Routines;
using SmartGlue.ClpGateway.Services;
using SmartGlue.ClpGateway.Trace;
using Xunit;

namespace SmartGlue.ClpGateway.Tests;

/// <summary>
/// The three trace layers and the evidence package (SGF-141 §5, SGF-144).
///
/// Unlike the rest of the suite, these tests turn the sink ON — into a temporary folder they
/// clean up — because what is being checked is precisely the files it writes.
/// </summary>
public class TraceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"sg-trace-{Guid.NewGuid():N}");

    private JsonlTraceSink NewSink() => new(
        Options.Create(new TraceOptions
        {
            Enabled = true,
            Directory = _folder,
            // Every frame recorded: the point here is that they arrive at all.
            Frames = "Decoded",
            Heartbeat = "Full",
        }),
        NullLogger<JsonlTraceSink>.Instance);

    private (RoutineExecutor Executor, JsonlTraceSink Sink, TraceReader Reader) Build(FakeMachine machine)
    {
        var sink = NewSink();
        var opts = Options.Create(TestHost.Options());

        var wire = new TracingAxisConnection(machine, sink, opts);
        var limits = new AxisLimitsProvider(wire, sink, opts, NullLogger<AxisLimitsProvider>.Instance);
        var axis = new AxisControlService(wire, sink, limits, opts, NullLogger<AxisControlService>.Instance);
        var applicator = new ApplicationAxisService(wire, sink, opts, NullLogger<ApplicationAxisService>.Instance);

        var executor = new RoutineExecutor(
            axis, limits, applicator,
            new UnreachableBackend(),
            sink,
            opts,
            new ResultQueue(Options.Create(new ResultQueueOptions { Enabled = false }), new StubEnvironment(), NullLogger<ResultQueue>.Instance),
            NullLogger<RoutineExecutor>.Instance);

        var reader = new TraceReader(
            Options.Create(new TraceOptions { Directory = _folder }),
            NullLogger<TraceReader>.Instance);

        // The real session is opened by the Modbus connection when it connects; the fake machine
        // has no socket, so the test opens one itself.
        sink.StartSession(new TraceSessionInfo
        {
            SessionId = "s-test",
            Transport = "Fake",
            Host = "127.0.0.1",
            ModbusPort = 5020,
            UnitId = 1,
        });

        return (executor, sink, reader);
    }

    private static DryRunRequest Request() => new(
        ProgramId: Guid.NewGuid(),
        MarkName: "Cordao superior",
        Points: [new TrajectoryPoint(10m, 10m), new TrajectoryPoint(20m, 20m)],
        Kind: "line",
        ApplicationZ: -2m,
        TravelZ: 0m,
        SpeedXyPulsesPerSecond: null,
        SpeedZPulsesPerSecond: null,
        PreDispenseMs: 0);

    [Fact]
    public async Task A_dry_run_leaves_all_three_layers_tied_together_by_the_run_id()
    {
        var machine = new FakeMachine();
        machine.SetApplicationReferenced(true);

        var (executor, sink, reader) = Build(machine);

        var run = executor.StartDryRun(Request(), "bench");
        await TestHost.WaitForTerminalAsync(executor, run.RunId);

        await sink.DisposeAsync();

        var frames = reader.ReadRecords("s-test", TraceLayer.Transport, new TraceQuery { RunId = run.RunId });
        var phases = reader.ReadRecords("s-test", TraceLayer.Engine, new TraceQuery { RunId = run.RunId });
        var waypoints = reader.ReadRecords("s-test", TraceLayer.Run, new TraceQuery { RunId = run.RunId });

        // This correlation is the whole value of the trace: a question asked at the waypoint
        // level ("why did point 2 fail?") has to be answerable down at the byte level.
        Assert.NotEmpty(frames);
        Assert.NotEmpty(phases);
        Assert.Equal(2, waypoints.Count);
    }

    [Fact]
    public async Task Each_waypoint_records_what_was_planned_and_what_was_reached()
    {
        var machine = new FakeMachine();
        machine.SetApplicationReferenced(true);

        var (executor, sink, reader) = Build(machine);

        var run = executor.StartDryRun(Request(), "bench");
        await TestHost.WaitForTerminalAsync(executor, run.RunId);

        await sink.DisposeAsync();

        var waypoints = reader.ReadRecords("s-test", TraceLayer.Run, new TraceQuery { RunId = run.RunId });
        var first = waypoints[0];

        // Positions are in pulses, the unit the machine speaks, so a line of the trace can be
        // compared to the address map without a conversion in the reader's head.
        Assert.Equal(10_000, first["planned"].GetProperty("x").GetInt32());
        Assert.Equal(10_000, first["achieved"].GetProperty("x").GetInt32());
        Assert.Equal(0, first["deltaPulses"].GetProperty("x").GetInt32());
        Assert.Equal("Cordao superior", first["markName"].GetString());
    }

    [Fact]
    public async Task The_evidence_package_carries_the_summary_and_the_three_layers()
    {
        var machine = new FakeMachine();
        machine.SetApplicationReferenced(true);

        var (executor, sink, reader) = Build(machine);

        var run = executor.StartDryRun(Request(), "bench");
        await TestHost.WaitForTerminalAsync(executor, run.RunId);

        await sink.DisposeAsync();

        var zip = reader.BuildEvidence(run.RunId);
        Assert.NotNull(zip);

        using var archive = new ZipArchive(new MemoryStream(zip!), ZipArchiveMode.Read);
        var names = archive.Entries.Select(entry => entry.FullName).ToArray();

        Assert.Contains("summary.json", names);
        Assert.Contains("summary.md", names);

        // The readable half of the package has to actually say what happened. A mismatch
        // between the fields the executor writes and the fields the reader expects is silent —
        // the table just fills with zeroes — so the test reads the text, not the file list.
        var markdown = new StreamReader(archive.Entries.Single(entry => entry.FullName == "summary.md").Open()).ReadToEnd();

        Assert.Contains("| Waypoints | 2 of 2 |", markdown);
        Assert.Contains("**Completed**", markdown);
        Assert.Contains("Cordao superior", markdown);
        Assert.Contains("| Glue dispensed | **no** |", markdown);
        Assert.Contains("session.json", names);
        Assert.Contains("run.jsonl", names);
        Assert.Contains("commands.jsonl", names);
        Assert.Contains("frames.jsonl", names);
    }

    [Fact]
    public async Task The_summary_says_out_loud_that_nothing_was_dispensed()
    {
        var machine = new FakeMachine();
        machine.SetApplicationReferenced(true);

        var (executor, sink, reader) = Build(machine);

        var run = executor.StartDryRun(Request(), "bench");
        await TestHost.WaitForTerminalAsync(executor, run.RunId);

        await sink.DisposeAsync();

        var summary = reader.ReadRunSummary(run.RunId);
        Assert.NotNull(summary);

        using var document = JsonDocument.Parse(summary!.Json);

        // The machine this ran against has no nozzle. Whoever reads the evidence later must not
        // have to know that from somewhere else.
        Assert.False(document.RootElement.GetProperty("dispensed").GetBoolean());
        Assert.Equal("Completed", document.RootElement.GetProperty("status").GetString());
        Assert.Equal(2, document.RootElement.GetProperty("completedPoints").GetInt32());
    }

    [Fact]
    public async Task A_run_that_never_happened_has_no_evidence()
    {
        var machine = new FakeMachine();
        var (_, sink, reader) = Build(machine);

        await sink.DisposeAsync();

        Assert.Null(reader.BuildEvidence("dry-does-not-exist"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // A file still held open by a sink that is being torn down is not a test failure.
        }
    }
}
