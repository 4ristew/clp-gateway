using SmartGlue.ClpGateway.Common.Returns;
using SmartGlue.ClpGateway.Trace;

namespace SmartGlue.ClpGateway.Api.Docs;

public static class TraceDocs
{
  public static RouteHandlerBuilder ListTraceSessionsDocs(this RouteHandlerBuilder builder)
  {
    return builder
      .WithSummary("List recorded trace sessions")
      .WithDescription(
        "One session is one connection to the machine, newest first. A reconnection opens a new session rather than continuing the old one, so a gap in the conversation is visible as a gap between folders instead of being invisible inside one.\n\n" +
        "Sessions live on the cabin PC's disk under `Trace:Directory` and are kept for `Trace:RetentionDays`.")
      .Produces<SuccessResponse<IReadOnlyList<TraceSessionListing>>>();
  }

  public static RouteHandlerBuilder ReadTransportFramesDocs(this RouteHandlerBuilder builder)
  {
    return builder
      .WithSummary("Read the Modbus frames of a session")
      .WithDescription(
        "Every exchange with the machine: direction, function code, address (named where the address map publishes a name), quantity, the decoded value and the latency. This is the layer that makes \"the command failed\" answerable at the byte level.\n\n" +
        "Filter with `runId`, `commandId`, `outcome` or `fc`. Heartbeat frames are 2 per second and are summarized rather than recorded one by one, unless `Trace:Heartbeat` says otherwise — without that they would drown everything else.")
      .Produces<SuccessResponse<IReadOnlyList<Dictionary<string, System.Text.Json.JsonElement>>>>();
  }

  public static RouteHandlerBuilder ReadCommandPhasesDocs(this RouteHandlerBuilder builder)
  {
    return builder
      .WithSummary("Read the command phases of a session")
      .WithDescription(
        "One line per phase of each command cycle — issued, completed, refused, failed — with the station, the elapsed time and the position reached. It is the layer that proves the handshake happened in the order the kit contract requires, and shows which phase it stopped at when it did not.")
      .Produces<SuccessResponse<IReadOnlyList<Dictionary<string, System.Text.Json.JsonElement>>>>();
  }

  public static RouteHandlerBuilder ReadRunSummaryDocs(this RouteHandlerBuilder builder)
  {
    return builder
      .WithSummary("Read the summary of one run")
      .WithDescription(
        "The run as it was recorded on disk: status, waypoints asked and reached, heights, dwell, and `dispensed: false` — stated in the evidence itself, because the machine this ran against has no nozzle.\n\n" +
        "Returned as raw JSON rather than inside the success envelope: it is the same bytes that go into the evidence package, and a summary that differs between the two would be worthless as evidence.")
      .Produces(StatusCodes.Status200OK)
      .Produces(StatusCodes.Status404NotFound);
  }

  public static RouteHandlerBuilder DownloadRunEvidenceDocs(this RouteHandlerBuilder builder)
  {
    return builder
      .WithSummary("Download the evidence package of a run")
      .WithDescription(
        "A zip with everything that proves what the machine did: `summary.json` and a readable `summary.md`, `session.json`, and the three trace layers filtered down to this run — `run.jsonl`, `commands.jsonl`, `frames.jsonl`. It is what SGF-144 asks to attach to the story.\n\n" +
        "Outside the success envelope on purpose: this is a file to save, not a payload to parse. 404 once the run is older than `Trace:RetentionDays`.")
      .Produces(StatusCodes.Status200OK, contentType: "application/zip")
      .Produces(StatusCodes.Status404NotFound);
  }
}
