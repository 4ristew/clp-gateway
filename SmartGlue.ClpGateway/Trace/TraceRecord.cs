using System.Text.Json.Serialization;

namespace SmartGlue.ClpGateway.Trace;

/// <summary>Which of the three trace layers a record belongs to (SGF-141 §5).</summary>
public static class TraceLayer
{
  public const string Transport = "transport";
  public const string Engine = "engine";
  public const string Run = "run";
  public const string Session = "session";
}

/// <summary>Result of a single exchange. Never implicit: every frame carries one of these.</summary>
public static class TraceOutcome
{
  public const string Ok = "ok";
  public const string ModbusException = "modbus-exception";
  public const string Timeout = "timeout";
  public const string SocketError = "socket-error";
  public const string VerifyMismatch = "verify-mismatch";
  public const string Refused = "not-writer";
  public const string Faulted = "faulted";
  public const string Stopped = "stopped";
  public const string Degraded = "degraded";
}

/// <summary>
/// One line of the JSONL trace. The correlation ids are what allow a question asked at the
/// waypoint level ("why did point 3 fail?") to be answered down at the byte level.
/// </summary>
public sealed record TraceRecord
{
  public DateTimeOffset Ts { get; init; } = DateTimeOffset.UtcNow;
  public long Seq { get; init; }
  public string SessionId { get; init; } = string.Empty;
  public string? RunId { get; init; }
  public string? CommandId { get; init; }
  public string Layer { get; init; } = TraceLayer.Transport;
  public string Outcome { get; init; } = TraceOutcome.Ok;
  public double? LatencyMs { get; init; }

  // ─── transport layer ─────────────────────────────────────────────────────
  public string? Dir { get; init; }
  public byte? Fc { get; init; }
  public ushort? Addr { get; init; }
  public ushort? Qty { get; init; }
  public byte? UnitId { get; init; }
  public string? Raw { get; init; }
  public TraceDecoded? Decoded { get; init; }

  // ─── engine layer ────────────────────────────────────────────────────────
  public string? Component { get; init; }
  public string? Command { get; init; }
  public string? Phase { get; init; }
  public Dictionary<string, long>? Written { get; init; }
  public Dictionary<string, long>? ReadBack { get; init; }

  // ─── run layer ───────────────────────────────────────────────────────────
  public string? MarkId { get; init; }
  public string? MarkName { get; init; }
  public int? Waypoint { get; init; }
  public int? Of { get; init; }
  public string? Kind { get; init; }
  public TracePoint? Planned { get; init; }
  public TracePoint? Achieved { get; init; }
  public TracePoint? DeltaPulses { get; init; }

  // ─── shared ──────────────────────────────────────────────────────────────
  public TraceError? Error { get; init; }
  public double? ElapsedMs { get; init; }
  public Dictionary<string, object?>? Data { get; init; }
  public string? Message { get; init; }
}

public sealed record TraceDecoded(string Point, long Value, string Unit);

public sealed record TracePoint(int X, int Y, int Z);

public sealed record TraceError
{
  public int Code { get; init; }
  public string Name { get; init; } = string.Empty;
  public string Meaning { get; init; } = string.Empty;
  public string? Recovery { get; init; }
}

/// <summary>Metadata written once per connection, next to the JSONL files.</summary>
public sealed record TraceSessionInfo
{
  public string SessionId { get; init; } = string.Empty;
  public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
  public DateTimeOffset? EndedAt { get; init; }
  public string Transport { get; init; } = string.Empty;
  public string Host { get; init; } = string.Empty;
  public int ModbusPort { get; init; }
  public int HttpPort { get; init; }
  public byte UnitId { get; init; }
  public int BuildCode { get; init; }
  public string Profile { get; init; } = string.Empty;
  public string? OpenedBy { get; init; }

  [JsonIgnore]
  public string Folder { get; init; } = string.Empty;
}
