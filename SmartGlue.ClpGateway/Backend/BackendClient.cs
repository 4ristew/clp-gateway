using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using SmartGlue.ClpGateway.Common.Exceptions;

namespace SmartGlue.ClpGateway.Backend;

// The one place in this process that opens a socket to the backend.
//
// Token pass-through: the JWT that arrived on the gateway request is forwarded as-is. The
// gateway has no service identity of its own, which means (a) no new credential to distribute
// to the cabin PC and (b) the backend audit log keeps naming the real operator who started
// the routine.
public class BackendClient(HttpClient http, ILogger<BackendClient> logger) : IBackendClient
{
  public const string HttpClientName = "backend";

  private static readonly JsonSerializerOptions Json = new()
  {
    PropertyNameCaseInsensitive = true,
    Converters = { new JsonStringEnumConverter() },
  };

  public async Task<IReadOnlyList<InspectionPoint>> GetInspectionPointsAsync(
    Guid programId, string bearerToken, CancellationToken ct)
  {
    using var request = new HttpRequestMessage(HttpMethod.Get, $"programs/{programId}/inspection-points");
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

    HttpResponseMessage response;
    try
    {
      response = await http.SendAsync(request, ct);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      throw new IntegrationException(
        $"Could not reach the backend at {http.BaseAddress} to read the inspection points of program '{programId}'.", ex);
    }

    using (response)
    {
      if (response.StatusCode == HttpStatusCode.NotFound)
      {
        throw new KeyNotFoundException($"Program '{programId}' was not found in the backend.");
      }

      if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
      {
        // Surfaced verbatim rather than translated: the caller needs to know it is their own
        // token that the backend refused, not the gateway that is misconfigured.
        throw new ForbiddenException(
          $"The backend refused the forwarded token when reading program '{programId}' ({(int)response.StatusCode}).");
      }

      if (!response.IsSuccessStatusCode)
      {
        throw new IntegrationException(
          $"The backend answered {(int)response.StatusCode} when reading the inspection points of program '{programId}'.");
      }

      var body = await response.Content.ReadAsStringAsync(ct);
      var envelope = JsonSerializer.Deserialize<BackendEnvelope<List<InspectionPoint>>>(body, Json);

      var points = envelope?.Data ?? [];
      logger.LogInformation("Read {Count} inspection point(s) for program {ProgramId} from the backend.", points.Count, programId);

      // The backend already orders CodeReader first, then by Order. Re-sorting here would
      // silently diverge from what the operator sees on screen, so the order is taken as
      // given — it is part of the contract, not an implementation detail.
      return points;
    }
  }

  // smt_glue wraps every response in { statusCode, message, data }.
  private sealed record BackendEnvelope<T>(int StatusCode, string Message, T? Data);
}
