namespace SmartGlue.ClpGateway.Common.Returns;

// Same envelope as smt_glue/Common/Returns — the desktop parses both APIs with the same
// deserializer, so the shape must not drift.
public record SuccessResponse<T>(int StatusCode, string Message, T Data);
public record SuccessResponse(int StatusCode, string Message);

public static class CustomResults
{
  public static IResult Success<T>(T data, string message = "OK", int statusCode = StatusCodes.Status200OK)
  {
    var response = new SuccessResponse<T>(statusCode, message, data);
    return Results.Json(response, statusCode: statusCode);
  }

  public static IResult Success(string message = "OK", int statusCode = StatusCodes.Status200OK)
  {
    var response = new SuccessResponse(statusCode, message);
    return Results.Json(response, statusCode: statusCode);
  }
}
