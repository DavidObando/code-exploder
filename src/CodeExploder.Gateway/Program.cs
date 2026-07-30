using System.Text.Json;
using CodeExploder.Gateway;
using CodeExploder.Storage;
using Microsoft.AspNetCore.Authentication.JwtBearer;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCodeExploderStorage(builder.Configuration);
builder.Services.AddSessionEventPublishing();
builder.Services.AddSignalR();
builder.Services.AddHostedService<SessionEventRelayService>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApi(options =>
{
    // .NET emits numeric types as ["integer|number","string"(,"null")] to guard JS
    // precision. Our numbers stay in the JS-safe range; drop the string alternative so
    // the generated client types them as number, keeping any null for nullable fields.
    options.AddSchemaTransformer((schema, _, _) =>
    {
        if (schema.Type is not { } type || !type.HasFlag(Microsoft.OpenApi.JsonSchemaType.String))
        {
            return Task.CompletedTask;
        }

        var numeric = schema.Format switch
        {
            "int64" or "int32" => Microsoft.OpenApi.JsonSchemaType.Integer,
            "double" or "float" or "decimal" => Microsoft.OpenApi.JsonSchemaType.Number,
            _ => (Microsoft.OpenApi.JsonSchemaType?)null,
        };

        if (numeric is { } numericType && type.HasFlag(numericType))
        {
            schema.Type = type.HasFlag(Microsoft.OpenApi.JsonSchemaType.Null)
                ? numericType | Microsoft.OpenApi.JsonSchemaType.Null
                : numericType;
            schema.Pattern = null;
        }

        return Task.CompletedTask;
    });
});

var authMode = builder.Configuration["Auth:Mode"] ?? "Oidc";
if (string.Equals(authMode, "DevBypass", StringComparison.OrdinalIgnoreCase))
{
    // Inner-dev-loop only: every request is authenticated as a synthetic user.
    // Refuses to start outside the Development environment.
    if (!builder.Environment.IsDevelopment())
    {
        throw new InvalidOperationException("Auth:Mode=DevBypass is only allowed in the Development environment.");
    }

    builder.Services.AddAuthentication(DevBypassAuthenticationHandler.SchemeName)
        .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, DevBypassAuthenticationHandler>(
            DevBypassAuthenticationHandler.SchemeName, _ => { });
}
else
{
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.Authority = builder.Configuration["Auth:Oidc:Authority"]
                ?? throw new InvalidOperationException("Auth:Oidc:Authority is not configured.");
            options.Audience = builder.Configuration["Auth:Oidc:Audience"] ?? "code-exploder";

            // WebSockets cannot carry an Authorization header; SignalR clients pass the
            // bearer token as ?access_token= on hub paths (standard ASP.NET pattern).
            options.Events = new JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    var token = context.Request.Query["access_token"];
                    if (!string.IsNullOrEmpty(token) && context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                    {
                        context.Token = token;
                    }

                    return Task.CompletedTask;
                },
            };
        });
}

builder.Services.AddAuthorization();

var app = builder.Build();

// `--emit-openapi [path]` writes the OpenAPI document and exits without touching the
// database or binding a port — the web UI generates its TypeScript client from it.
var emitOpenApiIndex = Array.IndexOf(args, "--emit-openapi");

if (string.Equals(authMode, "DevBypass", StringComparison.OrdinalIgnoreCase))
{
    app.Logger.LogWarning("Auth:Mode=DevBypass — all requests run as 'dev@local'. Development only.");
}

// All hosts run migrations at startup; the runner serializes via advisory lock.
if (emitOpenApiIndex < 0)
{
    using var scope = app.Services.CreateScope();
    var applied = await scope.ServiceProvider.GetRequiredService<MigrationRunner>().ApplyPendingAsync();
    if (applied.Count > 0)
    {
        app.Logger.LogInformation("Applied migrations: {Versions}", string.Join(", ", applied));
    }
}

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        // Vite emits content-hashed files under /assets — cache forever. Everything else
        // (index.html) must revalidate so deploys take effect immediately.
        ctx.Context.Response.Headers.CacheControl =
            ctx.Context.Request.Path.StartsWithSegments("/assets")
                ? "public,max-age=31536000,immutable"
                : "no-cache";
    },
});
app.UseAuthentication();
app.UseAuthorization();

app.MapOpenApi().AllowAnonymous();
app.MapHub<CodeExploder.Gateway.Hubs.SessionHub>("/hubs/session").RequireAuthorization();

SessionEndpoints.Map(app);
ExperienceEndpoints.Map(app);
SystemEndpoints.Map(app);

// Unknown API routes must 404 rather than fall through to the SPA's index.html.
app.MapFallback("/api/{**path}", () => Results.NotFound());
app.MapFallback("/hubs/{**path}", () => Results.NotFound());

// Client-side routes (e.g. /sessions/{id}) resolve to the SPA entry point. The file
// ships with the published webui build; in a bare dev checkout it 404s and devs use
// the Vite dev server instead.
app.MapFallbackToFile("index.html");

if (emitOpenApiIndex >= 0)
{
    // Endpoint discovery only happens once the pipeline is built, so start on an
    // ephemeral loopback port and fetch the document through the real serving path.
    var path = args.Length > emitOpenApiIndex + 1 ? args[emitOpenApiIndex + 1] : "openapi.json";
    app.Urls.Clear();
    app.Urls.Add("http://127.0.0.1:0");
    await app.StartAsync();
    var address = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
        .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses.First();
    using (var client = new HttpClient())
    {
        var json = await client.GetStringAsync($"{address}/openapi/v1.json");

        // Drop the servers array: it embeds this run's ephemeral port, which would make
        // the checked-in document differ on every regeneration. The SPA is same-origin.
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        node.Remove("servers");
        await File.WriteAllTextAsync(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    await app.StopAsync();
    Console.WriteLine($"OpenAPI document written to {Path.GetFullPath(path)}");
    return;
}

app.Run();
