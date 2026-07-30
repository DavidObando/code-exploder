using System.Text.Json;
using CodeExploder.Storage;
using Microsoft.AspNetCore.SignalR;
using Npgsql;

namespace CodeExploder.Gateway.Hubs;

/// <summary>
/// Live session events for the web UI (docs/04-api.md). Clients receive "sessionEvent"
/// messages carrying the JSON envelopes published through <see cref="PgSessionEventBus"/>.
/// Connections auto-join their user group (live left-pane session list); per-session
/// groups are ownership-verified on subscribe.
/// </summary>
public sealed class SessionHub(NpgsqlDataSource db, SessionStore sessions) : Hub
{
    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            UserGroup(CurrentUser.SubjectOf(Context.User ?? throw new HubException("unauthenticated"))));
        await base.OnConnectedAsync();
    }

    public async Task SubscribeSession(Guid sessionId)
    {
        await RequireOwnershipAsync(sessionId);
        await Groups.AddToGroupAsync(Context.ConnectionId, SessionGroup(sessionId));
    }

    public Task UnsubscribeSession(Guid sessionId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, SessionGroup(sessionId));

    /// <summary>Reconnect catch-up: replays persisted envelopes after a known id.</summary>
    public async Task<IReadOnlyList<JsonElement>> GetEventsSince(Guid sessionId, long sinceId)
    {
        await RequireOwnershipAsync(sessionId);

        var events = new List<JsonElement>();
        await using var cmd = db.CreateCommand(
            "select payload::text from pipeline_events where session_id = $1 and id > $2 order by id limit 500");
        cmd.Parameters.AddWithValue(sessionId);
        cmd.Parameters.AddWithValue(sinceId);
        await using var reader = await cmd.ExecuteReaderAsync(Context.ConnectionAborted);
        while (await reader.ReadAsync(Context.ConnectionAborted))
        {
            events.Add(JsonSerializer.Deserialize<JsonElement>(reader.GetString(0)));
        }

        return events;
    }

    internal static string SessionGroup(Guid id) => $"session:{id}";

    internal static string UserGroup(string subject) => $"user:{subject}";

    private async Task RequireOwnershipAsync(Guid sessionId)
    {
        var subject = CurrentUser.SubjectOf(Context.User ?? throw new HubException("unauthenticated"));
        if (!await sessions.IsOwnedByAsync(sessionId, subject, Context.ConnectionAborted))
        {
            throw new HubException("unknown session");
        }
    }
}
