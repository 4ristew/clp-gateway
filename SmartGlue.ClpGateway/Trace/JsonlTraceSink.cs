using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace SmartGlue.ClpGateway.Trace;

/// <summary>
/// Append-only JSONL trace. One line per event, one file per layer, one folder per session.
/// The producer side only enqueues — a single background task owns the file handles, so a slow
/// disk can never delay a command that is moving an axis. When the queue fills up, records are
/// dropped and counted rather than blocking (SGF-141 §5.6).
/// </summary>
public sealed class JsonlTraceSink : IMachineTrace, IAsyncDisposable
{
  private static readonly JsonSerializerOptions Json = new()
  {
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    Converters = { new JsonStringEnumConverter() },
  };

  private readonly TraceOptions _options;
  private readonly ILogger<JsonlTraceSink> _logger;
  private readonly Channel<TraceRecord> _queue;
  private readonly Task _writer;
  private readonly CancellationTokenSource _stopping = new();
  private readonly Dictionary<string, StreamWriter> _files = [];
  private readonly Lock _sessionLock = new();

  private long _sequence;
  private long _dropped;
  private TraceSessionInfo? _session;

  // Heartbeat aggregation state, flushed every HeartbeatSummarySeconds.
  private DateTimeOffset _heartbeatWindowStart = DateTimeOffset.UtcNow;
  private int _heartbeatCount;
  private int _heartbeatFailed;
  private double _heartbeatMaxLatency;

  public JsonlTraceSink(IOptions<TraceOptions> options, ILogger<JsonlTraceSink> logger)
  {
    _options = options.Value;
    _logger = logger;

    _queue = Channel.CreateBounded<TraceRecord>(new BoundedChannelOptions(_options.QueueCapacity)
    {
      FullMode = BoundedChannelFullMode.DropWrite,
      SingleReader = true,
    });

    _writer = Task.Run(DrainAsync);
  }

  public TraceSessionInfo? CurrentSession
  {
    get { lock (_sessionLock) return _session; }
  }

  public long DroppedRecords => Interlocked.Read(ref _dropped);

  public long NextSequence() => Interlocked.Increment(ref _sequence);

  public TraceSessionInfo StartSession(TraceSessionInfo info)
  {
    if (!_options.Enabled)
    {
      lock (_sessionLock) _session = info;
      return info;
    }

    var folder = Path.Combine(
      RootDirectory,
      info.StartedAt.ToLocalTime().ToString("yyyy-MM-dd"),
      info.SessionId);

    Directory.CreateDirectory(Path.Combine(folder, "runs"));

    var session = info with { Folder = folder };

    lock (_sessionLock)
    {
      CloseFiles();
      _session = session;
    }

    WriteSessionFile(session);
    ApplyRetention();

    _logger.LogInformation("Machine trace session {SessionId} open at {Folder}.", session.SessionId, folder);
    return session;
  }

  public void SetBuildCode(int buildCode)
  {
    TraceSessionInfo? session;
    lock (_sessionLock) session = _session;
    if (session is null) return;

    var updated = session with { BuildCode = buildCode };
    lock (_sessionLock) _session = updated;

    if (_options.Enabled) WriteSessionFile(updated);
  }

  public void EndSession(int buildCode)
  {
    TraceSessionInfo? session;
    lock (_sessionLock) session = _session;
    if (session is null) return;

    var closed = session with { EndedAt = DateTimeOffset.UtcNow, BuildCode = buildCode };
    lock (_sessionLock) _session = closed;

    if (_options.Enabled) WriteSessionFile(closed);
  }

  public void Write(TraceRecord record)
  {
    if (!_options.Enabled) return;
    if (!_queue.Writer.TryWrite(record)) Interlocked.Increment(ref _dropped);
  }

  public void WriteFrame(TraceRecord record, bool isHeartbeat)
  {
    if (!_options.Enabled || _options.FrameDetail == FrameDetail.Off) return;

    if (isHeartbeat)
    {
      var failed = record.Outcome != TraceOutcome.Ok;

      // A failing heartbeat is never summarized away: losing it is fault 1009.
      if (_options.HeartbeatDetail == HeartbeatDetail.Off && !failed) return;
      if (_options.HeartbeatDetail == HeartbeatDetail.Summarized && !failed)
      {
        AggregateHeartbeat(record);
        return;
      }
    }

    Write(Shape(record));
  }

  private TraceRecord Shape(TraceRecord record) => _options.FrameDetail switch
  {
    FrameDetail.Summary => record with { Raw = null, Decoded = null },
    FrameDetail.Decoded => record with { Raw = record.Outcome == TraceOutcome.Ok ? null : record.Raw },
    _ => record,
  };

  private void AggregateHeartbeat(TraceRecord record)
  {
    _heartbeatCount++;
    if (record.Outcome != TraceOutcome.Ok) _heartbeatFailed++;
    if (record.LatencyMs is { } latency && latency > _heartbeatMaxLatency) _heartbeatMaxLatency = latency;

    var elapsed = DateTimeOffset.UtcNow - _heartbeatWindowStart;
    if (elapsed.TotalSeconds < _options.HeartbeatSummarySeconds) return;

    Write(new TraceRecord
    {
      Seq = NextSequence(),
      SessionId = record.SessionId,
      Layer = TraceLayer.Transport,
      Dir = "hb",
      Outcome = _heartbeatFailed == 0 ? TraceOutcome.Ok : TraceOutcome.SocketError,
      Message = "heartbeat window",
      Data = new Dictionary<string, object?>
      {
        ["windowSeconds"] = Math.Round(elapsed.TotalSeconds, 1),
        ["count"] = _heartbeatCount,
        ["failed"] = _heartbeatFailed,
        ["latencyMaxMs"] = Math.Round(_heartbeatMaxLatency, 2),
      },
    });

    _heartbeatWindowStart = DateTimeOffset.UtcNow;
    _heartbeatCount = 0;
    _heartbeatFailed = 0;
    _heartbeatMaxLatency = 0;
  }

  // ─── background writer ─────────────────────────────────────────────────────

  private async Task DrainAsync()
  {
    try
    {
      await foreach (var record in _queue.Reader.ReadAllAsync(_stopping.Token))
      {
        try
        {
          var writer = ResolveWriter(record);
          if (writer is null) continue;
          await writer.WriteLineAsync(JsonSerializer.Serialize(record, Json));
        }
        catch (Exception ex)
        {
          // Losing the trace must not take the process with it.
          _logger.LogError(ex, "Failed to write a machine trace record.");
        }
      }
    }
    catch (OperationCanceledException)
    {
      // Shutting down.
    }
  }

  private StreamWriter? ResolveWriter(TraceRecord record)
  {
    TraceSessionInfo? session;
    lock (_sessionLock) session = _session;
    if (session is null || string.IsNullOrEmpty(session.Folder)) return null;

    var name = record.Layer switch
    {
      TraceLayer.Transport => "frames.jsonl",
      TraceLayer.Engine => "commands.jsonl",
      TraceLayer.Run => Path.Combine("runs", $"{record.RunId ?? "unassigned"}.jsonl"),
      _ => "session.jsonl",
    };

    var key = Path.Combine(session.Folder, name);

    if (_files.TryGetValue(key, out var existing))
    {
      if (existing.BaseStream.Length < _options.MaxFileBytes) return existing;
      existing.Dispose();
      _files.Remove(key);
      RotateFile(key);
    }

    var writer = new StreamWriter(new FileStream(key, FileMode.Append, FileAccess.Write, FileShare.Read))
    {
      AutoFlush = true,
    };
    _files[key] = writer;
    return writer;
  }

  private static void RotateFile(string path)
  {
    var directory = Path.GetDirectoryName(path)!;
    var stem = Path.GetFileNameWithoutExtension(path);
    var index = 1;
    string rotated;
    do
    {
      rotated = Path.Combine(directory, $"{stem}.{index:0000}.jsonl");
      index++;
    } while (File.Exists(rotated));

    File.Move(path, rotated);
  }

  private void WriteSessionFile(TraceSessionInfo session)
  {
    try
    {
      File.WriteAllText(
        Path.Combine(session.Folder, "session.json"),
        JsonSerializer.Serialize(session, Json));
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Failed to write session.json for {SessionId}.", session.SessionId);
    }
  }

  private void ApplyRetention()
  {
    if (_options.RetentionDays <= 0) return;

    try
    {
      var cutoff = DateTime.Today.AddDays(-_options.RetentionDays);
      foreach (var day in Directory.GetDirectories(RootDirectory))
      {
        var name = Path.GetFileName(day);
        if (DateTime.TryParse(name, out var date) && date < cutoff) Directory.Delete(day, recursive: true);
      }
    }
    catch (Exception ex)
    {
      _logger.LogWarning(ex, "Machine trace retention sweep failed.");
    }
  }

  private string RootDirectory
  {
    get
    {
      var root = Path.IsPathRooted(_options.Directory)
        ? _options.Directory
        : Path.Combine(AppContext.BaseDirectory, _options.Directory);
      Directory.CreateDirectory(root);
      return root;
    }
  }

  private void CloseFiles()
  {
    foreach (var writer in _files.Values) writer.Dispose();
    _files.Clear();
  }

  public async ValueTask DisposeAsync()
  {
    _queue.Writer.TryComplete();
    await _stopping.CancelAsync();
    try { await _writer; } catch { /* already reported */ }
    lock (_sessionLock) CloseFiles();
    _stopping.Dispose();
  }
}
