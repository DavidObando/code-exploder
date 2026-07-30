using System.Text.Json;
using CodeExploder.Domain;
using CodeExploder.Gateway.Hubs;
using CodeExploder.Storage;
using Microsoft.AspNetCore.SignalR;
using Npgsql;

namespace CodeExploder.Gateway;

/// <summary>
/// Bridges Postgres NOTIFY to SignalR: a dedicated connection LISTENs on the
/// session_events channel and fans each envelope out to the session group and the
/// owning user's group (docs/02-queue-and-events.md).
/// </summary>
public sealed class SessionEventRelayService(
    NpgsqlDataSource dataSource,
    SessionStore sessions,
    IHubContext<SessionHub> hub,
    ILogger<SessionEventRelayService> logger) : BackgroundService
{
    // Stream kinds render directly in an open progress view; the user group (live
    // left-pane list) only needs lifecycle kinds. Clients dedupe the overlap by id.
    private static readonly HashSet<string> SessionGroupOnlyKinds =
        [SessionEventKinds.AnalysisProgress, SessionEventKinds.AnalysisNarration];

    // session → owner-subject resolution cache; ownership never changes, so no eviction
    // beyond a size cap.
    private readonly Dictionary<Guid, string> _ownerBySession = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var conn = await dataSource.OpenConnectionAsync(stoppingToken);
                conn.Notification += (_, args) => _ = RelayAsync(args.Payload, stoppingToken);
                await using (var listen = new NpgsqlCommand($"listen {PgSessionEventBus.Channel}", conn))
                {
                    await listen.ExecuteNonQueryAsync(stoppingToken);
                }

                logger.LogInformation("Session event relay listening on '{Channel}'", PgSessionEventBus.Channel);
                while (!stoppingToken.IsCancellationRequested)
                {
                    await conn.WaitAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Session event relay connection lost; reconnecting in 2s");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }

    private async Task RelayAsync(string payload, CancellationToken ct)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<JsonElement>(payload);
            var sessionId = envelope.GetProperty("sessionId").GetGuid();
            var kind = envelope.GetProperty("kind").GetString() ?? string.Empty;

            var groups = new List<string> { SessionHub.SessionGroup(sessionId) };
            if (!SessionGroupOnlyKinds.Contains(kind)
                && await ResolveOwnerAsync(sessionId, ct) is { } subject)
            {
                groups.Add(SessionHub.UserGroup(subject));
            }

            await hub.Clients.Groups(groups).SendAsync("sessionEvent", envelope, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to relay session event");
        }
    }

    private async Task<string?> ResolveOwnerAsync(Guid sessionId, CancellationToken ct)
    {
        lock (_ownerBySession)
        {
            if (_ownerBySession.TryGetValue(sessionId, out var cached))
            {
                return cached;
            }
        }

        if (await sessions.GetOwnerSubjectAsync(sessionId, ct) is not { } subject)
        {
            return null;
        }

        lock (_ownerBySession)
        {
            if (_ownerBySession.Count > 10_000)
            {
                _ownerBySession.Clear();
            }

            _ownerBySession[sessionId] = subject;
        }

        return subject;
    }
}
