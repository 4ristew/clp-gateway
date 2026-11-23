namespace SmartGlue.ClpGateway.Backend;

/// <summary>
/// Inspection point as published by the backend
/// (smt_glue Features/InspectionPoints/DTOs/InspectionPointResponse.cs). Only the fields the
/// gateway actually needs are modelled; the rest of the payload is ignored on purpose, so a
/// new column in the backend does not break a running cell.
/// </summary>
public record InspectionPoint(
  Guid Id,
  int Order,
  decimal PositionX,
  decimal PositionY,
  string Kind);

public interface IBackendClient
{
  /// <summary>
  /// Reads the inspection points of a program, on behalf of the caller whose bearer token is
  /// supplied. Called exactly once per routine, before the first coil pulse.
  /// </summary>
  Task<IReadOnlyList<InspectionPoint>> GetInspectionPointsAsync(Guid programId, string bearerToken, CancellationToken ct);
}
