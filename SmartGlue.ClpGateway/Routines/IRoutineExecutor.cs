namespace SmartGlue.ClpGateway.Routines;

public interface IRoutineExecutor
{
  /// <summary>True while a routine is executing. Ad-hoc axis commands are refused meanwhile.</summary>
  bool IsRunning { get; }

  /// <summary>
  /// Accepts a routine and returns immediately with the run in <see cref="RoutineStatus.Preparing"/>.
  /// The caller polls <see cref="Get"/>. Throws <see cref="Common.Exceptions.ConflictException"/>
  /// if a routine is already running.
  /// </summary>
  RoutineRun Start(StartRoutineRequest request, string requestedBy, string? bearerToken);

  /// <summary>
  /// Accepts an application dry run — the trajectory behind "Iniciar Teste" (SGF-141) — and
  /// returns immediately, same as <see cref="Start"/>. Shares the one-routine-at-a-time lock
  /// with it on purpose: the constraint is the machine, not the kind of run.
  /// </summary>
  RoutineRun StartDryRun(DryRunRequest request, string requestedBy);

  /// <summary>
  /// Cancels the running routine, if any. Does NOT pulse STOP — the caller is responsible for
  /// that, because stopping the machine must not depend on there being a routine to cancel.
  /// </summary>
  bool RequestStop();

  RoutineRun? Get(string runId);

  IReadOnlyList<RoutineRun> Recent(int take);
}
