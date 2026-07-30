using Xunit;

namespace CodeExploder.Storage.Tests;

/// <summary>
/// The counting-join contract (docs/02-queue-and-events.md): children decrement exactly
/// once, on their transition into a terminal status; retryable failures don't decrement;
/// completions are idempotent under the status='running' guard.
/// </summary>
public sealed class JobQueueTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Worker = "test-worker";

    private JobQueue Queue => new(fixture.DataSource);

    [Fact]
    public async Task DequeueRespectsPriorityThenFifo()
    {
        var queue = Queue;
        var low = await queue.EnqueueAsync("prio-test", "{}", priority: 0);
        var high = await queue.EnqueueAsync("prio-test", "{}", priority: 10);

        var first = await queue.TryDequeueAsync(["prio-test"], Worker);
        var second = await queue.TryDequeueAsync(["prio-test"], Worker);

        Assert.Equal(high, first!.Id);
        Assert.Equal(low, second!.Id);
        await queue.CompleteAsync(first.Id);
        await queue.CompleteAsync(second.Id);
    }

    [Fact]
    public async Task BlockedJobIsNotDequeuedUntilChildrenComplete()
    {
        var queue = Queue;
        var join = await queue.EnqueueBlockedAsync("join-a", "{}", blockedCount: 2);
        var child1 = await queue.EnqueueAsync("child-a", "{}", unblocksJobId: join);
        var child2 = await queue.EnqueueAsync("child-a", "{}", unblocksJobId: join);

        Assert.Null(await queue.TryDequeueAsync(["join-a"], Worker));

        var c1 = await queue.TryDequeueAsync(["child-a"], Worker);
        await queue.CompleteAsync(c1!.Id);
        Assert.Null(await queue.TryDequeueAsync(["join-a"], Worker));

        var c2 = await queue.TryDequeueAsync(["child-a"], Worker);
        await queue.CompleteAsync(c2!.Id);

        var unblocked = await queue.TryDequeueAsync(["join-a"], Worker);
        Assert.NotNull(unblocked);
        Assert.Equal(join, unblocked.Id);
        await queue.CompleteAsync(unblocked.Id);
        _ = child1;
        _ = child2;
    }

    [Fact]
    public async Task TerminalFailureDecrementsJoinButRetryableDoesNot()
    {
        var queue = Queue;
        var join = await queue.EnqueueBlockedAsync("join-b", "{}", blockedCount: 1);
        await queue.EnqueueAsync("child-b", "{}", unblocksJobId: join);

        // First failure: attempts(1) < max_attempts(3) → requeued, join stays blocked.
        var attempt1 = await queue.TryDequeueAsync(["child-b"], Worker);
        await queue.FailAsync(attempt1!.Id, "boom");
        Assert.Null(await queue.TryDequeueAsync(["join-b"], Worker));

        // Exhaust the remaining attempts; the terminal failure unblocks the join.
        var attempt2 = await queue.TryDequeueAsync(["child-b"], Worker);
        await queue.FailAsync(attempt2!.Id, "boom");
        var attempt3 = await queue.TryDequeueAsync(["child-b"], Worker);
        await queue.FailAsync(attempt3!.Id, "boom");

        var unblocked = await queue.TryDequeueAsync(["join-b"], Worker);
        Assert.NotNull(unblocked);
        Assert.Equal(join, unblocked.Id);
        Assert.Equal("failed", await StatusOfAsync(attempt3.Id));
        await queue.CompleteAsync(unblocked.Id);
    }

    [Fact]
    public async Task DoubleCompleteDoesNotDoubleDecrement()
    {
        var queue = Queue;
        var join = await queue.EnqueueBlockedAsync("join-c", "{}", blockedCount: 2);
        await queue.EnqueueAsync("child-c", "{}", unblocksJobId: join);
        await queue.EnqueueAsync("child-c", "{}", unblocksJobId: join);

        var c1 = await queue.TryDequeueAsync(["child-c"], Worker);
        await queue.CompleteAsync(c1!.Id);
        await queue.CompleteAsync(c1.Id); // second complete is a no-op (status guard)

        Assert.Equal(1, await BlockedCountOfAsync(join));
        Assert.Null(await queue.TryDequeueAsync(["join-c"], Worker));

        var c2 = await queue.TryDequeueAsync(["child-c"], Worker);
        await queue.CompleteAsync(c2!.Id);
        var unblocked = await queue.TryDequeueAsync(["join-c"], Worker);
        Assert.Equal(join, unblocked!.Id);
        await queue.CompleteAsync(unblocked.Id);
    }

    [Fact]
    public async Task ReapedTerminalChildUnblocksJoin()
    {
        var queue = Queue;
        var join = await queue.EnqueueBlockedAsync("join-d", "{}", blockedCount: 1);
        var child = await queue.EnqueueAsync("child-d", "{}", unblocksJobId: join);

        // Burn attempts 1 and 2 with retryable failures, take attempt 3, then simulate a
        // crashed worker by backdating the lease and reaping.
        for (var i = 0; i < 2; i++)
        {
            var attempt = await queue.TryDequeueAsync(["child-d"], Worker);
            await queue.FailAsync(attempt!.Id, "boom");
        }

        var last = await queue.TryDequeueAsync(["child-d"], Worker);
        await BackdateLockAsync(last!.Id, TimeSpan.FromMinutes(10));
        var reaped = await queue.RequeueExpiredAsync(TimeSpan.FromMinutes(5));

        Assert.Equal(1, reaped);
        Assert.Equal("failed", await StatusOfAsync(child));
        var unblocked = await queue.TryDequeueAsync(["join-d"], Worker);
        Assert.Equal(join, unblocked!.Id);
        await queue.CompleteAsync(unblocked.Id);
    }

    [Fact]
    public async Task AvailableAtDefersDequeue()
    {
        var queue = Queue;
        await queue.EnqueueAsync("deferred", "{}", availableAt: DateTimeOffset.UtcNow.AddHours(1));
        Assert.Null(await queue.TryDequeueAsync(["deferred"], Worker));
    }

    private async Task<string?> StatusOfAsync(Guid jobId)
    {
        await using var cmd = fixture.DataSource.CreateCommand("select status from jobs where id = $1");
        cmd.Parameters.AddWithValue(jobId);
        return await cmd.ExecuteScalarAsync() as string;
    }

    private async Task<int> BlockedCountOfAsync(Guid jobId)
    {
        await using var cmd = fixture.DataSource.CreateCommand("select blocked_count from jobs where id = $1");
        cmd.Parameters.AddWithValue(jobId);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task BackdateLockAsync(Guid jobId, TimeSpan by)
    {
        await using var cmd = fixture.DataSource.CreateCommand(
            "update jobs set locked_at = now() - $2 where id = $1");
        cmd.Parameters.AddWithValue(jobId);
        cmd.Parameters.AddWithValue(by);
        await cmd.ExecuteNonQueryAsync();
    }
}
