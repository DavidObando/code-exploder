using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CodeExploder.Llm.Tests;

/// <summary>
/// Minimal in-process HTTP server (raw HttpListener) standing in for an
/// OpenAI-compatible endpoint. Captures the last request path and body.
/// </summary>
internal sealed class FakeLlmServer : IDisposable
{
    private readonly HttpListener _listener;

    public FakeLlmServer()
    {
        var port = GetFreePort();
        BaseUrl = FormattableString.Invariant($"http://127.0.0.1:{port}");
        _listener = new HttpListener();
        _listener.Prefixes.Add(FormattableString.Invariant($"http://127.0.0.1:{port}/"));
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    public string BaseUrl { get; }

    public string? LastPath { get; private set; }

    public string? LastBody { get; private set; }

    public Func<HttpListenerContext, Task> Handler { get; set; } =
        static ctx => RespondJsonAsync(ctx, 200, "{}");

    public static async Task RespondJsonAsync(HttpListenerContext ctx, int status, string json)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        var bytes = Encoding.UTF8.GetBytes(json);
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    public static async Task RespondSseAsync(HttpListenerContext ctx, IReadOnlyList<string> lines)
    {
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/event-stream";
        var payload = string.Join("\n\n", lines) + "\n\n";
        var bytes = Encoding.UTF8.GetBytes(payload);
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    public static int GetFreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    public void Dispose()
    {
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
            // already shut down
        }
    }

    private async Task LoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return; // listener stopped
            }

            LastPath = ctx.Request.Url?.AbsolutePath;
            if (ctx.Request.HasEntityBody)
            {
                using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                LastBody = await reader.ReadToEndAsync();
            }

            try
            {
                await Handler(ctx);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or IOException)
            {
                ctx.Response.Abort();
            }
        }
    }
}
