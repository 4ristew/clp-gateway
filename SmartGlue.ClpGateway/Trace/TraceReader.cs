using Microsoft.Extensions.Options;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace SmartGlue.ClpGateway.Trace;

public sealed record TraceSessionListing(
  string SessionId,
  string Day,
  DateTimeOffset StartedAt,
  DateTimeOffset? EndedAt,
  string Transport,
  int BuildCode,
  string Profile,
  long SizeBytes,
  IReadOnlyList<string> Runs);

public sealed record TraceQuery
{
  public string? RunId { get; init; }
  public string? CommandId { get; init; }
  public string? Outcome { get; init; }
  public byte? Fc { get; init; }
  public DateTimeOffset? From { get; init; }
  public DateTimeOffset? To { get; init; }
  public int Take { get; init; } = 500;
}

/// <summary>
/// Reads back what the sink wrote. The trace is plain JSONL on purpose: this reader filters it
/// line by line without loading a file into memory, and anything else — jq, Excel, a future
/// pipeline — can do the same without a parser of its own.
/// </summary>
public sealed class TraceReader(IOptions<TraceOptions> options, ILogger<TraceReader> logger)
{
  private static readonly JsonSerializerOptions Json = new()
  {
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
  };

  public IReadOnlyList<TraceSessionListing> ListSessions(int take = 25)
  {
    var root = RootDirectory;
    if (!Directory.Exists(root)) return [];

    var sessions = new List<TraceSessionListing>();

    foreach (var day in Directory.GetDirectories(root).OrderByDescending(path => path))
    {
      foreach (var folder in Directory.GetDirectories(day).OrderByDescending(path => path))
      {
        var info = ReadSessionInfo(folder);
        if (info is null) continue;

        var size = new DirectoryInfo(folder)
          .EnumerateFiles("*", SearchOption.AllDirectories)
          .Sum(file => LiveLength(file.FullName));
        var runs = Directory.Exists(Path.Combine(folder, "runs"))
          ? Directory.GetFiles(Path.Combine(folder, "runs"), "*.summary.json")
              .Select(path => Path.GetFileName(path).Replace(".summary.json", string.Empty))
              .ToList()
          : [];

        sessions.Add(new TraceSessionListing(
          info.SessionId, Path.GetFileName(day), info.StartedAt, info.EndedAt,
          info.Transport, info.BuildCode, info.Profile, size, runs));

        if (sessions.Count >= take) return sessions;
      }
    }

    return sessions;
  }

  public IReadOnlyList<Dictionary<string, JsonElement>> ReadRecords(string sessionId, string layer, TraceQuery query)
  {
    var folder = FindSession(sessionId);
    if (folder is null) return [];

    var file = layer switch
    {
      TraceLayer.Engine => "commands.jsonl",
      TraceLayer.Run => Path.Combine("runs", $"{query.RunId}.jsonl"),
      _ => "frames.jsonl",
    };

    var results = new List<Dictionary<string, JsonElement>>();

    foreach (var path in ResolveFiles(folder, file))
    {
      foreach (var line in ReadLinesShared(path))
      {
        if (results.Count >= query.Take) return results;
        if (string.IsNullOrWhiteSpace(line)) continue;

        Dictionary<string, JsonElement>? record;
        try { record = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(line, Json); }
        catch (JsonException) { continue; }

        if (record is null || !Matches(record, query)) continue;
        results.Add(record);
      }
    }

    return results;
  }

  public DryRunSummaryFile? ReadRunSummary(string runId)
  {
    foreach (var session in ListSessions(200))
    {
      var folder = FindSession(session.SessionId);
      if (folder is null) continue;

      var path = Path.Combine(folder, "runs", $"{runId}.summary.json");
      if (!File.Exists(path)) continue;

      return new DryRunSummaryFile(session.SessionId, folder, File.ReadAllText(path));
    }

    return null;
  }

  /// <summary>
  /// Builds the evidence package a story asks for: the summary, the three trace layers filtered
  /// down to this run, and the session metadata. Zip comes from System.IO.Compression — BCL.
  /// </summary>
  public byte[]? BuildEvidence(string runId)
  {
    var found = ReadRunSummary(runId);
    if (found is null) return null;

    using var buffer = new MemoryStream();
    using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
    {
      AddEntry(archive, "summary.json", found.Json);
      AddEntry(archive, "summary.md", BuildMarkdown(found));

      var sessionPath = Path.Combine(found.Folder, "session.json");
      if (File.Exists(sessionPath)) AddEntry(archive, "session.json", File.ReadAllText(sessionPath));

      var runPath = Path.Combine(found.Folder, "runs", $"{runId}.jsonl");
      if (File.Exists(runPath))
        AddEntry(archive, "run.jsonl", string.Join(Environment.NewLine, ReadLinesShared(runPath)));

      AddEntry(archive, "commands.jsonl", Filter(found.Folder, "commands.jsonl", runId));
      AddEntry(archive, "frames.jsonl", Filter(found.Folder, "frames.jsonl", runId));
    }

    return buffer.ToArray();
  }

  private string Filter(string folder, string file, string runId)
  {
    var builder = new StringBuilder();
    var needle = $"\"runId\":\"{runId}\"";

    foreach (var path in ResolveFiles(folder, file))
    {
      foreach (var line in ReadLinesShared(path))
      {
        if (line.Contains(needle, StringComparison.Ordinal)) builder.AppendLine(line);
      }
    }

    return builder.ToString();
  }

  private static string BuildMarkdown(DryRunSummaryFile found)
  {
    var summary = JsonSerializer.Deserialize<DryRunSummaryView>(found.Json, Json) ?? new DryRunSummaryView();

    return $"""
      # Dry run {summary.RunId}

      | Field | Value |
      |---|---|
      | Session | {found.SessionId} |
      | Component | {summary.Component} |
      | Mark | {summary.MarkName ?? "-"} ({summary.Kind}) |
      | Outcome | **{summary.Status}** |
      | Waypoints | {summary.CompletedPoints} of {summary.TotalPoints} |
      | Homed first | {(summary.Homed ? "yes" : "no")} |
      | Application Z | {summary.ApplicationZ} mm |
      | Travel Z | {summary.TravelZ} mm |
      | Pre-dispense | {summary.PreDispenseMs ?? summary.DelayMs} ms |
      | Post-dispense | {summary.PostDispenseMs ?? 0} ms |
      | Glue dispensed | **{(summary.Dispensed ? "yes" : "no")}** |
      | Requested by | {summary.RequestedBy ?? "-"} |
      | Started | {summary.StartedAt:O} |
      | Finished | {summary.FinishedAt:O} |
      | Error | {summary.Error ?? "none"} |

      Files in this package: `summary.json`, `session.json`, `run.jsonl` (waypoints),
      `commands.jsonl` (command cycle phases) and `frames.jsonl` (every Modbus exchange of this run).
      """;
  }

  private static void AddEntry(ZipArchive archive, string name, string content)
  {
    var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
    using var stream = entry.Open();
    using var writer = new StreamWriter(stream, Encoding.UTF8);
    writer.Write(content);
  }

  private static bool Matches(Dictionary<string, JsonElement> record, TraceQuery query)
  {
    if (query.RunId is not null && Text(record, "runId") != query.RunId) return false;
    if (query.CommandId is not null && Text(record, "commandId") != query.CommandId) return false;
    if (query.Outcome is not null && Text(record, "outcome") != query.Outcome) return false;

    if (query.Fc is { } fc)
    {
      if (!record.TryGetValue("fc", out var value) || value.ValueKind != JsonValueKind.Number) return false;
      if (value.GetInt32() != fc) return false;
    }

    if (query.From is not null || query.To is not null)
    {
      if (!record.TryGetValue("ts", out var value) || !value.TryGetDateTimeOffset(out var ts)) return false;
      if (query.From is { } from && ts < from) return false;
      if (query.To is { } to && ts > to) return false;
    }

    return true;
  }

  private static string? Text(Dictionary<string, JsonElement> record, string key) =>
    record.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

  /// <summary>
  /// Size of a file the sink may still hold open. The directory entry keeps a stale length on
  /// Windows until the writer closes, which would report a live session as a few hundred bytes.
  /// </summary>
  private static long LiveLength(string path)
  {
    try
    {
      using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
      return stream.Length;
    }
    catch (IOException)
    {
      return 0;
    }
  }

  /// <summary>
  /// Reads a trace file that the sink may still be writing to. File.ReadLines asks for a share
  /// mode the live writer does not grant, so reading the current session would throw.
  /// </summary>
  public static IEnumerable<string> ReadLinesShared(string path)
  {
    if (!File.Exists(path)) yield break;

    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    using var reader = new StreamReader(stream);

    while (reader.ReadLine() is { } line) yield return line;
  }

  private static IEnumerable<string> ResolveFiles(string folder, string file)
  {
    var path = Path.Combine(folder, file);
    var directory = Path.GetDirectoryName(path)!;
    var stem = Path.GetFileNameWithoutExtension(path);
    if (!Directory.Exists(directory)) yield break;

    // Rotated parts first, then the live file, so the reader returns them in order.
    foreach (var rotated in Directory.GetFiles(directory, $"{stem}.*.jsonl").OrderBy(name => name))
      yield return rotated;

    if (File.Exists(path)) yield return path;
  }

  private string? FindSession(string sessionId)
  {
    var root = RootDirectory;
    if (!Directory.Exists(root)) return null;

    foreach (var day in Directory.GetDirectories(root))
    {
      var folder = Path.Combine(day, sessionId);
      if (Directory.Exists(folder)) return folder;
    }

    return null;
  }

  private TraceSessionInfo? ReadSessionInfo(string folder)
  {
    var path = Path.Combine(folder, "session.json");
    if (!File.Exists(path)) return null;

    try { return JsonSerializer.Deserialize<TraceSessionInfo>(File.ReadAllText(path), Json); }
    catch (JsonException ex)
    {
      logger.LogWarning(ex, "Unreadable session.json at {Path}.", path);
      return null;
    }
  }

  private string RootDirectory => Path.IsPathRooted(options.Value.Directory)
    ? options.Value.Directory
    : Path.Combine(AppContext.BaseDirectory, options.Value.Directory);

  public sealed record DryRunSummaryFile(string SessionId, string Folder, string Json);

  /// <summary>
  /// The fields of the summary file this reader renders as Markdown.
  ///
  /// Named after what RoutineExecutor writes, not after the backend's old MachineEngine: the
  /// two used different words for the same things (outcome/status, executed/completedPoints),
  /// and a mismatch here is invisible — the deserializer simply leaves every field at its
  /// default and the package ships a table of zeroes. Which is exactly what it did, until a
  /// bench run produced an evidence file that said "0 of 0" about a trajectory that had just
  /// been traced.
  /// </summary>
  private sealed record DryRunSummaryView
  {
    public string RunId { get; init; } = string.Empty;
    public string Component { get; init; } = string.Empty;
    public string? MarkName { get; init; }
    public string Kind { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public int TotalPoints { get; init; }
    public int CompletedPoints { get; init; }
    public bool Homed { get; init; }
    public bool Dispensed { get; init; }
    public decimal ApplicationZ { get; init; }
    public decimal TravelZ { get; init; }
    public int? PreDispenseMs { get; init; }
    public int? PostDispenseMs { get; init; }
    /// <summary>Summaries written before SGF-278 carry the single delay, which is the pre-dispense.</summary>
    public int DelayMs { get; init; }
    public string? RequestedBy { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public string? Error { get; init; }
  }
}
