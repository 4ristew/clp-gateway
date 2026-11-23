using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace SmartGlue.ClpGateway.Trace;

/// <summary>
/// Metrics and spans for the machine link, built on System.Diagnostics — part of the BCL, so
/// this adds no dependency. If the team later approves an OpenTelemetry exporter, these become
/// visible through configuration alone instead of an instrumentation rewrite (SGF-141 §5.7).
/// </summary>
public sealed class MachineMetrics : IDisposable
{
  public const string MeterName = "SmartGlue.Machine";
  public const string ActivitySourceName = "SmartGlue.Machine";

  private readonly Meter _meter = new(MeterName, "1.0.0");
  private readonly Counter<long> _frames;
  private readonly Histogram<double> _frameLatency;
  private readonly Counter<long> _commands;
  private readonly Histogram<double> _commandDuration;
  private readonly Counter<long> _faults;

  public static readonly ActivitySource Source = new(ActivitySourceName, "1.0.0");

  /// <summary>Milliseconds since the last accepted heartbeat, read by the observable gauge.</summary>
  public Func<double> HeartbeatAgeMs { get; set; } = () => double.NaN;

  /// <summary>1 when this process holds the writer lease on the PLC, 0 otherwise.</summary>
  public Func<int> WriterLease { get; set; } = () => 0;

  public MachineMetrics()
  {
    _frames = _meter.CreateCounter<long>("machine.frames", "{frame}", "Modbus frames exchanged with the machine.");
    _frameLatency = _meter.CreateHistogram<double>("machine.frame.latency", "ms", "Round trip time of a Modbus transaction.");
    _commands = _meter.CreateCounter<long>("machine.commands", "{command}", "Commands run through the six step cycle.");
    _commandDuration = _meter.CreateHistogram<double>("machine.command.duration", "ms", "Time from gate to acknowledged done.");
    _faults = _meter.CreateCounter<long>("machine.faults", "{fault}", "Faults raised by the machine.");

    _meter.CreateObservableGauge("machine.heartbeat.age", () => HeartbeatAgeMs(), "ms",
      "Age of the last accepted heartbeat echo.");
    _meter.CreateObservableGauge("machine.writer.lease", () => WriterLease(), "{held}",
      "Whether this process currently holds the writer lease.");
  }

  public void RecordFrame(string dir, byte fc, string outcome, double latencyMs)
  {
    var tags = new TagList
    {
      { "dir", dir },
      { "fc", fc },
      { "outcome", outcome },
    };

    _frames.Add(1, tags);
    _frameLatency.Record(latencyMs, tags);
  }

  public void RecordCommand(string command, string outcome, double elapsedMs)
  {
    var tags = new TagList { { "command", command }, { "outcome", outcome } };
    _commands.Add(1, tags);
    _commandDuration.Record(elapsedMs, tags);
  }

  public void RecordFault(int code, string name) =>
    _faults.Add(1, new TagList { { "code", code }, { "name", name } });

  public void Dispose() => _meter.Dispose();
}
