namespace SmartGlue.ClpGateway.Trace;

public enum FrameDetail { Off, Summary, Decoded, Raw }

public enum HeartbeatDetail { Off, Summarized, Full }

/// <summary>
/// Configuration of the machine trace, bound from the "Trace" section of appsettings.
///
/// Ported from smt_glue's MachineTraceOptions (SGF-141), which was written for the machine
/// layer that the ADR-01-R2 moved into this repository. The defaults are the ones proved
/// against the kit simulator: decoded frames, heartbeat summarized (it is 2 frames a second and
/// would drown everything else), and a bounded queue that drops rather than blocking — a slow
/// disk must never delay a command that is moving an axis.
/// </summary>
public sealed class TraceOptions
{
  public const string SectionName = "Trace";

  public bool Enabled { get; set; } = true;

  public string Directory { get; set; } = "logs/machine";

  /// <summary>Off | Summary | Decoded | Raw — how much of each frame reaches the file.</summary>
  public string Frames { get; set; } = "Decoded";

  /// <summary>Off | Summarized | Full — heartbeat frames are 2/s and would drown the file.</summary>
  public string Heartbeat { get; set; } = "Summarized";

  /// <summary>Seconds covered by a single aggregated heartbeat record.</summary>
  public int HeartbeatSummarySeconds { get; set; } = 30;

  public long MaxFileBytes { get; set; } = 33_554_432;

  public int RetentionDays { get; set; } = 30;

  /// <summary>Records buffered before the sink starts dropping instead of blocking the control loop.</summary>
  public int QueueCapacity { get; set; } = 20_000;

  public FrameDetail FrameDetail => Frames.ToLowerInvariant() switch
  {
    "off" => FrameDetail.Off,
    "summary" => FrameDetail.Summary,
    "raw" => FrameDetail.Raw,
    _ => FrameDetail.Decoded,
  };

  public HeartbeatDetail HeartbeatDetail => Heartbeat.ToLowerInvariant() switch
  {
    "off" => HeartbeatDetail.Off,
    "full" => HeartbeatDetail.Full,
    _ => HeartbeatDetail.Summarized,
  };
}
