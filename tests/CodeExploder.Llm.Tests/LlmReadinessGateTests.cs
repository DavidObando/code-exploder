using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CodeExploder.Llm.Tests;

public sealed class LlmReadinessGateTests : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public void Dispose() => _http.Dispose();

    [Fact]
    public async Task ReadyWhenModelsEndpointReturns200()
    {
        using var server = new FakeLlmServer();
        server.Handler = ctx => FakeLlmServer.RespondJsonAsync(ctx, 200, """{"data":[]}""");
        var gate = CreateGate(new LlmOptions { BaseUrl = server.BaseUrl });

        Assert.True(await gate.IsReadyAsync());
        Assert.Equal("/v1/models", server.LastPath);
    }

    [Fact]
    public async Task NotReadyWhenConnectionRefused()
    {
        var deadPort = FakeLlmServer.GetFreePort();
        var gate = CreateGate(new LlmOptions { BaseUrl = $"http://127.0.0.1:{deadPort}" });

        Assert.False(await gate.IsReadyAsync());
    }

    [Fact]
    public async Task NotReadyWhenServerErrors()
    {
        using var server = new FakeLlmServer();
        server.Handler = ctx => FakeLlmServer.RespondJsonAsync(ctx, 503, "{}");
        var gate = CreateGate(new LlmOptions { BaseUrl = server.BaseUrl });

        Assert.False(await gate.IsReadyAsync());
    }

    [Fact]
    public async Task NotReadyWhenServerIsTooSlow()
    {
        using var server = new FakeLlmServer();
        server.Handler = async ctx =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            await FakeLlmServer.RespondJsonAsync(ctx, 200, "{}");
        };
        var gate = CreateGate(new LlmOptions { BaseUrl = server.BaseUrl, TimeoutSeconds = 1 });

        Assert.False(await gate.IsReadyAsync());
    }

    private LlmReadinessGate CreateGate(LlmOptions options) =>
        new(_http, options, NullLogger<LlmReadinessGate>.Instance);
}
