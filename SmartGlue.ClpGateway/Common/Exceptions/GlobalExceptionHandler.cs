using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace SmartGlue.ClpGateway.Common.Exceptions;

// Ported from smt_glue/Common/Exceptions/GlobalExceptionHandler.cs so the gateway answers
// with the same problem+json shape the desktop already parses. The database cases
// (DbException, DbUpdateException) were dropped: this process has no database.
public class GlobalExceptionHandler(
  IProblemDetailsService problemDetailsService,
  ILogger<GlobalExceptionHandler> logger)
  : IExceptionHandler
{
  public async ValueTask<bool> TryHandleAsync(
      HttpContext httpContext,
      Exception exception,
      CancellationToken cancellationToken)
  {
    logger.LogError(exception, "Unhandled exception [{ExceptionType}]: {Message}",
      exception.GetType().Name, exception.Message);

    var statusCode = exception switch
    {
      UnauthorizedAccessException => StatusCodes.Status401Unauthorized,
      ArgumentException => StatusCodes.Status400BadRequest,
      KeyNotFoundException => StatusCodes.Status404NotFound,
      BadHttpRequestException => StatusCodes.Status400BadRequest,
      IntegrationException => StatusCodes.Status502BadGateway,
      ForbiddenException => StatusCodes.Status403Forbidden,
      ConflictException => StatusCodes.Status409Conflict,
      InvalidOperationException => StatusCodes.Status500InternalServerError,
      _ => StatusCodes.Status500InternalServerError
    };

    httpContext.Response.StatusCode = statusCode;

    var title = statusCode switch
    {
      400 => "Bad Request",
      401 => "Unauthorized",
      403 => "Forbidden",
      404 => "Not Found",
      409 => "Conflict",
      502 => "Bad Gateway",
      503 => "Service Unavailable",
      _ => "Internal Server Error"
    };

    return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
    {
      HttpContext = httpContext,
      ProblemDetails = new ProblemDetails
      {
        Status = statusCode,
        Title = title,
        Detail = exception.Message,
      },
      Exception = exception
    });
  }
}
