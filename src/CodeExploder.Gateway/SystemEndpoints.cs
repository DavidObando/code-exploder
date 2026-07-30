using CodeExploder.Gateway.Contracts;
using CodeExploder.Storage;

namespace CodeExploder.Gateway;

/// <summary>
/// Identity and client-bootstrap endpoints. /api/config is anonymous so the SPA can
/// decide its auth flow before it has a token; /api/system/status feeds the StatusBar.
/// </summary>
public static class SystemEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/config", (IConfiguration config) =>
                Results.Ok(new ConfigResponse(config["Auth:Mode"] ?? "Oidc")))
            .AllowAnonymous()
            .Produces<ConfigResponse>();

        app.MapGet("/api/me", (HttpContext http) =>
                Results.Ok(new MeResponse(CurrentUser.NameOf(http.User), CurrentUser.SubjectOf(http.User))))
            .RequireAuthorization()
            .Produces<MeResponse>();

        app.MapGet("/api/system/status", async (Npgsql.NpgsqlDataSource db, JobQueue queue, CancellationToken ct) =>
            {
                bool dbOk;
                long depth = 0, running = 0;
                try
                {
                    (depth, running) = await queue.DepthAsync(ct);
                    dbOk = true;
                }
                catch (Npgsql.NpgsqlException)
                {
                    dbOk = false;
                }

                return Results.Ok(new SystemStatusResponse(dbOk, new QueueStatus(depth, running)));
            })
            .RequireAuthorization()
            .Produces<SystemStatusResponse>();

        app.MapGet("/healthz", async (Npgsql.NpgsqlDataSource db, CancellationToken ct) =>
            {
                await using var cmd = db.CreateCommand("select 1");
                await cmd.ExecuteScalarAsync(ct);
                return Results.Ok(new HealthResponse("ok"));
            })
            .AllowAnonymous()
            .Produces<HealthResponse>();
    }
}
