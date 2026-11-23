namespace SmartGlue.ClpGateway.Trace;

/// <summary>
/// Append-only sink for everything that travels to and from the PLC.
/// Writing must never block the control loop: implementations enqueue and return.
/// </summary>
public interface IMachineTrace
{
  /// <summary>Opens a folder for a connection and writes its session.json.</summary>
  TraceSessionInfo StartSession(TraceSessionInfo info);

  /// <summary>Records the machine build code as soon as it is known, before any command runs.</summary>
  void SetBuildCode(int buildCode);

  void EndSession(int buildCode);

  /// <summary>Current session, or null when no connection has been opened yet.</summary>
  TraceSessionInfo? CurrentSession { get; }

  /// <summary>Monotonic sequence number, shared by the three layers of the same session.</summary>
  long NextSequence();

  void Write(TraceRecord record);

  /// <summary>Frames are the noisiest layer and the only one that can be filtered down.</summary>
  void WriteFrame(TraceRecord record, bool isHeartbeat);

  /// <summary>Records dropped because the queue was full — surfaced by the health check.</summary>
  long DroppedRecords { get; }
}
