using System.Security.Claims;
using SmartGlue.ClpGateway.Common.Interceptors;

namespace SmartGlue.ClpGateway.Common.Authentication;

// Claims only — the gateway has no user table and never asks the backend who the caller is.
// Ported from smt_glue/Common/Authentication/RequesterContext.cs; the email accessor was
// dropped (nothing here uses it) and GetBearerToken was added for the backend client.
public class RequesterContext(IHttpContextAccessor httpContextAccessor) : IRequesterContext
{
  public int GetUserId()
  {
    var id = httpContextAccessor.HttpContext?.User.FindFirstValue("userId");
    return int.TryParse(id, out var parsed) ? parsed : 0;
  }

  public string GetUserName()
    => httpContextAccessor.HttpContext?.User.FindFirstValue("name") ?? "unknown";

  public string GetUserRegistration()
    => httpContextAccessor.HttpContext?.User.FindFirstValue("registration") ?? "unknown";

  public string GetUserProfile()
    => httpContextAccessor.HttpContext?.User.FindFirstValue("profile") ?? "Unauthenticated";

  public ProfileLevels GetUserProfileLevel()
  {
    var level = httpContextAccessor.HttpContext?.User.FindFirstValue("profileLevel");
    return int.TryParse(level, out var parsed) ? (ProfileLevels)parsed : ProfileLevels.BasicPermission;
  }

  public string? GetBearerToken()
  {
    var header = httpContextAccessor.HttpContext?.Request.Headers.Authorization.ToString();
    if (string.IsNullOrWhiteSpace(header)) return null;

    const string prefix = "Bearer ";
    return header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
      ? header[prefix.Length..].Trim()
      : null;
  }
}
