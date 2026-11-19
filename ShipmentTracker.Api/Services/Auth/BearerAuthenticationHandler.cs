using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace ShipmentTracker.Api.Services.Auth;

/// <summary>Reads "Authorization: Bearer &lt;token&gt;" and signs the request in when the token is valid.</summary>
public sealed class BearerAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    JwtTokenService tokens) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Bearer";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header) || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(AuthenticateResult.NoResult());

        var principal = tokens.Validate(header["Bearer ".Length..].Trim(), SchemeName);
        return Task.FromResult(principal is null
            ? AuthenticateResult.Fail("Invalid or expired token.")
            : AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}
