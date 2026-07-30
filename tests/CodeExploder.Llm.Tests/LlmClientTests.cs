using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CodeExploder.Llm.Tests;

public sealed class LlmClientTests : IDisposable
{
    private const string HappyResponse = /*lang=json*/ """
        {
          "choices": [{"message": {"content": "hello there"}, "finish_reason": "stop"}],
          "usage": {"prompt_tokens": 12, "completion_tokens": 3}
        }
        """;

    private readonly FakeLlmServer _server = new();
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public void Dispose()
    {
        _server.Dispose();
        _http.Dispose();
    }

    [Fact]
    public async Task ChatParsesContentFinishReasonAndUsage()
    {
        _server.Handler = ctx => FakeLlmServer.RespondJsonAsync(ctx, 200, HappyResponse);
        var client = CreateClient();

        var result = await client.ChatAsync(Request("hi"));

        Assert.Equal("hello there", result.Content);
        Assert.Equal("stop", result.FinishReason);
        Assert.NotNull(result.Usage);
        Assert.Equal(12, result.Usage.PromptTokens);
        Assert.Equal(3, result.Usage.CompletionTokens);
    }

    [Fact]
    public async Task ChatSendsTemperatureZeroModelAndMaxTokens()
    {
        _server.Handler = ctx => FakeLlmServer.RespondJsonAsync(ctx, 200, HappyResponse);
        var client = CreateClient(new LlmOptions
        {
            BaseUrl = _server.BaseUrl,
            Model = "test-model",
            MaxOutputTokens = 1234,
        });

        await client.ChatAsync(Request("hi"));

        using var body = JsonDocument.Parse(_server.LastBody!);
        var root = body.RootElement;
        Assert.Equal(0, root.GetProperty("temperature").GetInt32());
        Assert.Equal("test-model", root.GetProperty("model").GetString());
        Assert.Equal(1234, root.GetProperty("max_tokens").GetInt32());
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.False(root.TryGetProperty("response_format", out _));
        var message = root.GetProperty("messages")[0];
        Assert.Equal("user", message.GetProperty("role").GetString());
        Assert.Equal("hi", message.GetProperty("content").GetString());
    }

    [Fact]
    public async Task ChatRequestMaxTokensOverridesOptions()
    {
        _server.Handler = ctx => FakeLlmServer.RespondJsonAsync(ctx, 200, HappyResponse);
        var client = CreateClient();

        await client.ChatAsync(Request("hi") with { MaxOutputTokens = 77 });

        using var body = JsonDocument.Parse(_server.LastBody!);
        Assert.Equal(77, body.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task ChatAddsResponseFormatWhenJsonRequested()
    {
        _server.Handler = ctx => FakeLlmServer.RespondJsonAsync(ctx, 200, HappyResponse);
        var client = CreateClient();

        await client.ChatAsync(Request("hi") with { Json = true });

        using var body = JsonDocument.Parse(_server.LastBody!);
        Assert.Equal(
            "json_object",
            body.RootElement.GetProperty("response_format").GetProperty("type").GetString());
    }

    [Fact]
    public async Task ChatOmitsResponseFormatWhenJsonModeDisabled()
    {
        _server.Handler = ctx => FakeLlmServer.RespondJsonAsync(ctx, 200, HappyResponse);
        var client = CreateClient(new LlmOptions { BaseUrl = _server.BaseUrl, JsonMode = false });

        await client.ChatAsync(Request("hi") with { Json = true });

        using var body = JsonDocument.Parse(_server.LastBody!);
        Assert.False(body.RootElement.TryGetProperty("response_format", out _));
    }

    [Fact]
    public async Task StarvedOutputThrowsLlmStarvedException()
    {
        _server.Handler = ctx => FakeLlmServer.RespondJsonAsync(ctx, 200, /*lang=json*/ """
            {"choices": [{"message": {"content": "  "}, "finish_reason": "length"}]}
            """);
        var client = CreateClient();

        await Assert.ThrowsAsync<LlmStarvedException>(() => client.ChatAsync(Request("hi")));
    }

    [Fact]
    public async Task TruncatedButNonEmptyContentDoesNotThrow()
    {
        _server.Handler = ctx => FakeLlmServer.RespondJsonAsync(ctx, 200, /*lang=json*/ """
            {"choices": [{"message": {"content": "partial answer"}, "finish_reason": "length"}]}
            """);
        var client = CreateClient();

        var result = await client.ChatAsync(Request("hi"));

        Assert.Equal("partial answer", result.Content);
        Assert.Equal("length", result.FinishReason);
    }

    [Fact]
    public async Task ServerErrorThrowsLlmExceptionWithTruncatedBodyExcerpt()
    {
        var longBody = "boom-" + new string('x', 1000);
        _server.Handler = ctx => FakeLlmServer.RespondJsonAsync(ctx, 500, longBody);
        var client = CreateClient();

        var ex = await Assert.ThrowsAsync<LlmException>(() => client.ChatAsync(Request("hi")));

        Assert.Contains("500", ex.Message, StringComparison.Ordinal);
        Assert.Contains("boom-", ex.Message, StringComparison.Ordinal);
        Assert.True(ex.Message.Length < 500, "body excerpt should be capped at 400 chars");
    }

    [Fact]
    public async Task StreamYieldsDeltasInOrderAndStopsAtDone()
    {
        _server.Handler = ctx => FakeLlmServer.RespondSseAsync(ctx,
        [
            """data: {"choices":[{"delta":{"content":"Hel"}}]}""",
            """data: {"choices":[{"delta":{"content":"lo"}}]}""",
            """data: {"choices":[{"delta":{}}]}""",
            """data: {"choices":[],"usage":{"prompt_tokens":1,"completion_tokens":2}}""",
            "data: [DONE]",
            """data: {"choices":[{"delta":{"content":"after done"}}]}""",
        ]);
        var client = CreateClient();

        var deltas = new List<string>();
        await foreach (var delta in client.ChatStreamAsync(Request("hi")))
        {
            deltas.Add(delta);
        }

        Assert.Equal(["Hel", "lo"], deltas);
    }

    [Fact]
    public async Task StreamRequestsStreamingWithUsage()
    {
        _server.Handler = ctx => FakeLlmServer.RespondSseAsync(ctx, ["data: [DONE]"]);
        var client = CreateClient();

        await foreach (var _ in client.ChatStreamAsync(Request("hi")))
        {
        }

        using var body = JsonDocument.Parse(_server.LastBody!);
        Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.True(body.RootElement.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("/v1")]
    [InlineData("/v1/")]
    public async Task BaseUrlVariantsAllPostToV1ChatCompletions(string suffix)
    {
        _server.Handler = ctx => FakeLlmServer.RespondJsonAsync(ctx, 200, HappyResponse);
        var client = CreateClient(new LlmOptions { BaseUrl = _server.BaseUrl + suffix });

        await client.ChatAsync(Request("hi"));

        Assert.Equal("/v1/chat/completions", _server.LastPath);
    }

    [Theory]
    [InlineData("http://localhost:11434", "http://localhost:11434/v1")]
    [InlineData("http://localhost:11434/", "http://localhost:11434/v1")]
    [InlineData("http://localhost:11434/v1", "http://localhost:11434/v1")]
    [InlineData("http://localhost:11434/v1/", "http://localhost:11434/v1")]
    public void NormalizeBaseUrlHandlesV1AndTrailingSlash(string input, string expected)
    {
        Assert.Equal(expected, LlmClient.NormalizeBaseUrl(input));
    }

    private static LlmRequest Request(string userContent) =>
        new([new LlmMessage("user", userContent)]);

    private LlmClient CreateClient(LlmOptions? options = null)
    {
        options ??= new LlmOptions { BaseUrl = _server.BaseUrl };
        return new LlmClient(_http, options, NullLogger<LlmClient>.Instance);
    }
}
