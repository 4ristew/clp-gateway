namespace SmartGlue.ClpGateway.Backend;

/// <summary>
/// Where the smt_glue REST lives. The gateway is a CLIENT of the backend and never a server
/// to it — that asymmetry is what lets this process keep listening on loopback only.
/// </summary>
public class BackendOptions
{
  public const string SectionName = "Backend";

  /// <summary>Base address of the backend API, including the /api path base.</summary>
  public string BaseUrl { get; set; } = "http://127.0.0.1:5113/api/";

  /// <summary>
  /// Timeout for a single backend call. Deliberately short: every backend call happens
  /// BEFORE the first coil pulse of a routine (see RoutineExecutor), so waiting a long time
  /// here only delays the start — it never stalls a moving axis. If that ever stops being
  /// true, the bug is in the caller, not in this number.
  /// </summary>
  public int TimeoutSeconds { get; set; } = 15;
}
