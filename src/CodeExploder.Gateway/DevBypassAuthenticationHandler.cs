using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace CodeExploder.Gateway;

/// <summary>
/// Development-only authentication: every request is 'dev@local'. Program.cs refuses to
/// enable this outside the Development environment (docs/04-api.md auth modes).
/// </summary>
public sealed class DevBypassAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "DevBypass";
    public const string Subject = "dev@local";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, "Dev User"),
            new(ClaimTypes.NameIdentifier, Subject),
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}

/// <summary>Resolves the stable subject + display name for the signed-in principal.</summary>
public static class CurrentUser
{
    public static string SubjectOf(ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? user.FindFirstValue("sub")
        ?? user.Identity?.Name
        ?? "unknown";

    public static string NameOf(ClaimsPrincipal user) =>
        user.Identity?.Name ?? SubjectOf(user);
}
