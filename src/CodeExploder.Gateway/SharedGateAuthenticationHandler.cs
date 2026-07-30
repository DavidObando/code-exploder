using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace CodeExploder.Gateway;

public sealed class SharedGateOptions : AuthenticationSchemeOptions
{
    /// <summary>Header carrying the proxy-authenticated username.</summary>
    public string Header { get; set; } = "X-WebAuth-User";
}

/// <summary>
/// Production auth for the home deployment (docs/04 §auth, docs/07): the reverse proxy
/// (Traefik basicAuth with headerField) authenticates the user and forwards the
/// username in a trusted header; this handler turns it into the identity. ONLY safe
/// when the app is reachable exclusively through that proxy — the compose stack
/// publishes the gateway on the LAN host port for Traefik, and the internet path is
/// TLS + basicAuth at Traefik.
/// </summary>
public sealed class SharedGateAuthenticationHandler(
    IOptionsMonitor<SharedGateOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<SharedGateOptions>(options, logger, encoder)
{
    public const string SchemeName = "SharedGate";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var user = Request.Headers[Options.Header].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(user))
        {
            return Task.FromResult(AuthenticateResult.Fail($"missing {Options.Header} header"));
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, user),
            new(ClaimTypes.NameIdentifier, user),
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}
