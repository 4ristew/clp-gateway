using Carter;
using SmartGlue.ClpGateway.Api.Docs;
using SmartGlue.ClpGateway.Common.Interceptors;
using SmartGlue.ClpGateway.Common.Returns;
using SmartGlue.ClpGateway.Trace;

namespace SmartGlue.ClpGateway.Api;

// Reading back what the gateway recorded of its conversation with the machine (SGF-141 §5) and
// packaging one run as evidence (SGF-144).
//
// Read-only, and file-based: the trace is append-only JSONL on the cabin PC, never a table.
// Nothing here can change what happened — which is the point of evidence.
public class TraceEndpoints : ICarterModule
{
  public void AddRoutes(IEndpointRouteBuilder app)
  {
    var group = app.MapGroup("/traces").WithTags("Trace").RequireAuthorization();

    group.MapGet("/", (TraceReader reader, int take = 25) =>
    {
      var sessions = reader.ListSessions(take);
      return CustomResults.Success(sessions, "Trace sessions retrieved successfully!");
    })
    .RequireProfileLevels(ProfileLevels.FullPermission, ProfileLevels.HighPermission, ProfileLevels.MediumPermission)
    .ListTraceSessionsDocs();

    group.MapGet("/{sessionId}/transport", (
      TraceReader reader,
      string sessionId,
      string? runId,
      string? commandId,
      string? outcome,
      byte? fc,
      int take = 500) =>
    {
      var records = reader.ReadRecords(sessionId, TraceLayer.Transport, new TraceQuery
      {
        RunId = runId,
        CommandId = commandId,
        Outcome = outcome,
        Fc = fc,
        Take = take,
      });

      return CustomResults.Success(records, "Transport frames retrieved successfully!");
    })
    .RequireProfileLevels(ProfileLevels.FullPermission, ProfileLevels.HighPermission, ProfileLevels.MediumPermission)
    .ReadTransportFramesDocs();

    group.MapGet("/{sessionId}/engine", (
      TraceReader reader,
      string sessionId,
      string? runId,
      string? commandId,
      string? outcome,
      int take = 500) =>
    {
      var records = reader.ReadRecords(sessionId, TraceLayer.Engine, new TraceQuery
      {
        RunId = runId,
        CommandId = commandId,
        Outcome = outcome,
        Take = take,
      });

      return CustomResults.Success(records, "Command phases retrieved successfully!");
    })
    .RequireProfileLevels(ProfileLevels.FullPermission, ProfileLevels.HighPermission, ProfileLevels.MediumPermission)
    .ReadCommandPhasesDocs();

    var runs = app.MapGroup("/runs").WithTags("Trace").RequireAuthorization();

    runs.MapGet("/{runId}", (TraceReader reader, string runId) =>
    {
      var summary = reader.ReadRunSummary(runId)
        ?? throw new KeyNotFoundException($"No trace summary was found for run '{runId}'.");

      return Results.Content(summary.Json, "application/json");
    })
    .RequireProfileLevels(ProfileLevels.FullPermission, ProfileLevels.HighPermission, ProfileLevels.MediumPermission)
    .ReadRunSummaryDocs();

    // Outside the success envelope on purpose: this is a file to save, not a payload to parse.
    runs.MapGet("/{runId}/evidence", (TraceReader reader, string runId) =>
    {
      var zip = reader.BuildEvidence(runId)
        ?? throw new KeyNotFoundException($"No trace was found for run '{runId}'. Traces live on disk and are kept for Trace:RetentionDays.");

      return Results.File(zip, "application/zip", $"{runId}-evidence.zip");
    })
    .RequireProfileLevels(ProfileLevels.FullPermission, ProfileLevels.HighPermission, ProfileLevels.MediumPermission)
    .DownloadRunEvidenceDocs();
  }
}
