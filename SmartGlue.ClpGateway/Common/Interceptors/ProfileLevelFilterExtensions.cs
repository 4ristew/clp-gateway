using SmartGlue.ClpGateway.Common.Authentication;
using SmartGlue.ClpGateway.Common.Exceptions;

namespace SmartGlue.ClpGateway.Common.Interceptors;

public enum ProfileLevels { FullPermission, HighPermission, MediumPermission, LowPermission, BasicPermission }

public static class ProfileLevelFilterExtensions
{
  /// <summary>
  /// Restricts access to the endpoint to only the specified profile levels.
  /// Never apply this to STOP: any authenticated operator must be able to stop the machine
  /// at any moment (SGF-140 ADR-01-R, decision 3).
  /// </summary>
  public static RouteHandlerBuilder RequireProfileLevels(this RouteHandlerBuilder builder, params ProfileLevels[] allowedLevels)
  {
    return builder.AddEndpointFilter(async (context, next) =>
    {
      var requesterContext = context.HttpContext.RequestServices.GetRequiredService<IRequesterContext>();
      var userLevel = requesterContext.GetUserProfileLevel();

      if (!allowedLevels.Contains(userLevel))
      {
        throw new ForbiddenException("You do not have permission to access this resource.");
      }

      return await next(context);
    });
  }
}
