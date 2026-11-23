using SmartGlue.ClpGateway.Common.Interceptors;

namespace SmartGlue.ClpGateway.Common.Authentication;

public interface IRequesterContext
{
  int GetUserId();
  string GetUserName();
  string GetUserRegistration();
  string GetUserProfile();
  ProfileLevels GetUserProfileLevel();

  /// <summary>
  /// Raw bearer token of the current request, forwarded to the backend when the gateway
  /// reads data on behalf of the caller (see BackendClient). Null outside an HTTP request:
  /// a routine already running has no ambient request, which is why the executor captures
  /// the token at start time instead of asking for it later.
  /// </summary>
  string? GetBearerToken();
}
