using Npgsql;
using Xunit;

namespace CodeExploder.Storage.Tests;

/// <summary>
/// The retry contract (docs/04-api.md): a failed session is repointed at a fresh
/// analysis (the old one and its jobs/events are gone), the original gitRef is
/// recovered from the acquire payload, and non-failed sessions are refused.
/// </summary>
public sealed class SessionStoreRetryTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private SessionStore Store => new(fixture.DataSource);

    private async Task<(Guid SessionId, Guid AnalysisId)> CreateSessionAsync(string status)
    {
        var store = Store;
        var userId = await store.GetOrCreateUserAsync("retry-tests", "Retry Tests");
        var repoId = await store.GetOrCreateRepoAsync("octo", "retry-repo", "https://github.com/octo/retry-repo");
        var analysisId = await store.CreateAnalysisAsync(repoId, "repo", null);
        var sessionId = await store.CreateSessionAsync(userId, analysisId, "repo", "octo/retry-repo");
        await store.SetSessionStatusAsync(sessionId, status, status == "failed" ? "boom" : null);
        return (sessionId, analysisId);
    }

    [Fact]
    public async Task RetrySwapsAnalysisClearsStateAndRecoversGitRef()
    {
        var store = Store;
        var (sessionId, oldAnalysisId) = await CreateSessionAsync("failed");

        var queue = new JobQueue(fixture.DataSource);
        var jobId = await queue.EnqueueAsync(
            "acquire", """{"gitRef":"release-2.x"}""", analysisId: oldAnalysisId);
        await using (var evt = fixture.DataSource.CreateCommand(
            "insert into pipeline_events (session_id, kind, payload) values ($1, 'x', '{}')"))
        {
            evt.Parameters.AddWithValue(sessionId);
            await evt.ExecuteNonQueryAsync();
        }

        var retry = await store.RetryAsync(sessionId);

        Assert.NotNull(retry);
        Assert.NotEqual(oldAnalysisId, retry.Value.AnalysisId);
        Assert.Equal("release-2.x", retry.Value.GitRef);

        await using (var check = fixture.DataSource.CreateCommand(
            """
            select (select count(*) from analyses where id = $1),
                   (select count(*) from jobs where analysis_id = $1),
                   (select count(*) from pipeline_events where session_id = $2)
            """))
        {
            check.Parameters.AddWithValue(oldAnalysisId);
            check.Parameters.AddWithValue(sessionId);
            await using var reader = await check.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(0, reader.GetInt64(0));
            Assert.Equal(0, reader.GetInt64(1));
            Assert.Equal(0, reader.GetInt64(2));
        }

        var userId = await store.GetOrCreateUserAsync("retry-tests", "Retry Tests");
        var view = await store.GetForUserAsync(sessionId, userId);
        Assert.NotNull(view);
        Assert.Equal("queued", view.Status);
        Assert.Null(view.FailureReason);
        Assert.Equal(retry.Value.AnalysisId, view.AnalysisId);
        _ = jobId;
    }

    [Fact]
    public async Task RetryRefusesNonFailedSessions()
    {
        var (sessionId, analysisId) = await CreateSessionAsync("ready");

        Assert.Null(await Store.RetryAsync(sessionId));

        await using var check = fixture.DataSource.CreateCommand(
            "select analysis_id, status from sessions where id = $1");
        check.Parameters.AddWithValue(sessionId);
        await using var reader = await check.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(analysisId, reader.GetGuid(0));
        Assert.Equal("ready", reader.GetString(1));
    }
}
