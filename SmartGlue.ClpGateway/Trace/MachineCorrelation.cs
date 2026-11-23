namespace SmartGlue.ClpGateway.Trace;

/// <summary>
/// Ambient correlation for the trace. The transport records frames but does not know which
/// command or run asked for them; the engine scopes this before every exchange so the three
/// layers line up without threading ids through every signature.
/// </summary>
public static class MachineCorrelation
{
  private static readonly AsyncLocal<Scope?> Current = new();

  public static string? RunId => Current.Value?.RunId;
  public static string? CommandId => Current.Value?.CommandId;
  public static bool IsHeartbeat => Current.Value?.IsHeartbeat ?? false;

  public static IDisposable Begin(string? runId = null, string? commandId = null, bool isHeartbeat = false)
  {
    var previous = Current.Value;
    Current.Value = new Scope(runId ?? previous?.RunId, commandId ?? previous?.CommandId, isHeartbeat);
    return new Restore(previous);
  }

  private sealed record Scope(string? RunId, string? CommandId, bool IsHeartbeat);

  private sealed class Restore(Scope? previous) : IDisposable
  {
    public void Dispose() => Current.Value = previous;
  }
}
