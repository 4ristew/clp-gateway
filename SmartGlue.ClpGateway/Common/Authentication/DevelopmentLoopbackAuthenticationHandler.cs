using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using SmartGlue.ClpGateway.Common.Interceptors;

namespace SmartGlue.ClpGateway.Common.Authentication;

public sealed class DevelopmentLoopbackAuthenticationHandler(
  IOptionsMonitor<AuthenticationSchemeOptions> options,
  ILoggerFactory logger,
  UrlEncoder encoder)
  : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
  public const string SchemeName = "DevelopmentLoopback";

  protected override Task<AuthenticateResult> HandleAuthenticateAsync()
  {
    var remoteAddress = Context.Connection.RemoteIpAddress;
    if (remoteAddress is null
      || !IPAddress.IsLoopback(remoteAddress.IsIPv4MappedToIPv6 ? remoteAddress.MapToIPv4() : remoteAddress))
    {
      return Task.FromResult(AuthenticateResult.NoResult());
    }

    var claims = new[]
    {
      new Claim("userId", "0"),
      new Claim("name", "Local development"),
      new Claim("registration", "LOCAL-DEV"),
      new Claim("profile", "Development"),
      new Claim("profileLevel", ((int)ProfileLevels.FullPermission).ToString(CultureInfo.InvariantCulture)),
    };
    var identity = new ClaimsIdentity(claims, SchemeName);
    var principal = new ClaimsPrincipal(identity);
    var ticket = new AuthenticationTicket(principal, SchemeName);

    return Task.FromResult(AuthenticateResult.Success(ticket));
  }
}
