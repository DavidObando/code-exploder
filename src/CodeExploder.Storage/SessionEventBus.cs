using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace CodeExploder.Storage;

public interface ISessionEventBus
{
    /// <summary>Fire-and-forget publish; the envelope's kind values live in Domain.SessionEventKinds.</summary>
    void Publish(Guid sessionId, string kind, object data);
}

/// <summary>
/// Transport for session events (docs/02-queue-and-events.md): every process publishes
/// through pg_notify on the shared database, and the Gateway's relay LISTENs and fans
/// out to SignalR groups. Events are inserted into pipeline_events first so the row id
/// rides inside the envelope — clients track their lastEventId and replay gaps via the
/// hub's GetEventsSince. In M0 every event kind is persisted; token-delta coalescing
/// arrives with the LLM stages.
///
/// Publish never blocks: events go into an unbounded channel drained by a background
/// pump. Delivery is best-effort narration — a failed publish is logged and dropped,
/// never taking the pipeline down.
/// </summary>
public sealed class PgSessionEventBus : ISessionEventBus, IAsyncDisposable
{
    public const string Channel = "session_events";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<PgSessionEventBus>? _logger;
    private readonly Channel<(Guid SessionId, string Kind, object Data)> _queue =
        System.Threading.Channels.Channel.CreateUnbounded<(Guid, string, object)>();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pump;

    public PgSessionEventBus(NpgsqlDataSource dataSource, ILogger<PgSessionEventBus>? logger = null)
    {
        _dataSource = dataSource;
        _logger = logger;
        _pump = Task.Run(() => PumpAsync(_cts.Token));
    }

    public void Publish(Guid sessionId, string kind, object data) =>
        _queue.Writer.TryWrite((sessionId, kind, data));

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();

        // Give the pump a moment to drain buffered events before cancelling hard.
        await Task.WhenAny(_pump, Task.Delay(TimeSpan.FromSeconds(2)));
        await _cts.CancelAsync();
        try
        {
            await _pump;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        await foreach (var (sessionId, kind, data) in _queue.Reader.ReadAllAsync(ct))
        {
            try
            {
                await EmitAsync(sessionId, kind, data, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogWarning(ex, "Session event publish failed; event dropped ({Kind})", kind);
            }
        }
    }

    private async Task EmitAsync(Guid sessionId, string kind, object data, CancellationToken ct)
    {
        var at = DateTimeOffset.UtcNow;
        var envelope = new JsonObject
        {
            ["sessionId"] = sessionId,
            ["kind"] = kind,
            ["at"] = at,
            ["data"] = JsonSerializer.SerializeToNode(data, data.GetType(), JsonOpts),
        };

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        // Insert first so the durable row id rides inside the notified envelope; catch-up
        // replays then carry exactly what live listeners saw.
        long id;
        await using (var insert = new NpgsqlCommand(
            "insert into pipeline_events (session_id, kind, payload, at) values ($1, $2, $3::jsonb, $4) returning id",
            conn))
        {
            insert.Parameters.AddWithValue(sessionId);
            insert.Parameters.AddWithValue(kind);
            insert.Parameters.AddWithValue(envelope.ToJsonString());
            insert.Parameters.AddWithValue(at);
            id = (long)(await insert.ExecuteScalarAsync(ct))!;
        }

        envelope["id"] = id;
        var payload = envelope.ToJsonString();

        await using (var update = new NpgsqlCommand(
            "update pipeline_events set payload = $2::jsonb where id = $1", conn))
        {
            update.Parameters.AddWithValue(id);
            update.Parameters.AddWithValue(payload);
            await update.ExecuteNonQueryAsync(ct);
        }

        await using var notify = new NpgsqlCommand("select pg_notify($1, $2)", conn);
        notify.Parameters.AddWithValue(Channel);
        notify.Parameters.AddWithValue(payload);
        await notify.ExecuteNonQueryAsync(ct);
    }
}
