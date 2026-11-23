namespace SmartGlue.ClpGateway.Common.Exceptions;

public sealed class ForbiddenException(string message) : Exception(message);
public sealed class ConflictException(string message) : Exception(message);

public sealed class IntegrationException : Exception
{
  public IntegrationException(string message) : base(message) { }
  public IntegrationException(string message, Exception innerException) : base(message, innerException) { }
}
