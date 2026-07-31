using System.Text.Json;
using System.Text.Json.Nodes;
using CodeExploder.Mcp;

// Minimal MCP stdio server (docs/08 §M8): newline-delimited JSON-RPC 2.0 exposing the
// Code Exploder knowledge base as tools. Dependency-free by design — a thin adapter
// over the Gateway API. Configure with:
//   CX_BASE_URL    gateway root (default http://localhost:5080)
//   CX_BASIC_AUTH  "user:password" for the deployed edge gate (omit for DevBypass)

using var api = new CodeExploderApi(
    Environment.GetEnvironmentVariable("CX_BASE_URL") ?? "http://localhost:5080",
    Environment.GetEnvironmentVariable("CX_BASIC_AUTH"));

var tools = new (string Name, string Description, JsonObject Schema,
    Func<JsonObject, CancellationToken, Task<string>> Handler)[]
{
    ("list_sessions",
     "List the analyzed repository sessions (id, kind, status, title). Session ids feed every other tool.",
     Schema(),
     (_, ct) => api.ListSessionsAsync(ct)),
    ("get_repo_summary",
     "Repository vitals for a session: description, languages, build systems, components.",
     Schema(("sessionId", "The session id from list_sessions")),
     (a, ct) => api.GetRepoSummaryAsync(Arg(a, "sessionId"), ct)),
    ("list_sections",
     "The generated tutorial's table of contents (slug, kind, status, title).",
     Schema(("sessionId", "The session id")),
     (a, ct) => api.ListSectionsAsync(Arg(a, "sessionId"), ct)),
    ("get_section",
     "One tutorial section rendered as markdown (prose, code excerpts, diagrams with narration).",
     Schema(("sessionId", "The session id"), ("slug", "The section slug from list_sections")),
     (a, ct) => api.GetSectionAsync(Arg(a, "sessionId"), Arg(a, "slug"), ct)),
    ("search_knowledge_base",
     "Semantic + lexical search over the analyzed code and docs; returns file:line-cited snippets.",
     Schema(("sessionId", "The session id"), ("query", "What to look for")),
     (a, ct) => api.SearchAsync(Arg(a, "sessionId"), Arg(a, "query"), 8, ct)),
    ("ask_expert",
     "Ask the codebase expert a question (grounded RAG answer with file citations). Takes up to ~2 minutes.",
     Schema(("sessionId", "The session id"), ("question", "The question")),
     (a, ct) => api.AskExpertAsync(Arg(a, "sessionId"), Arg(a, "question"), ct)),
};

using var stdout = Console.OpenStandardOutput();
using var writer = new StreamWriter(stdout) { AutoFlush = true };

string? line;
while ((line = await Console.In.ReadLineAsync()) is not null)
{
    if (string.IsNullOrWhiteSpace(line))
    {
        continue;
    }

    JsonNode? request;
    try
    {
        request = JsonNode.Parse(line);
    }
    catch (JsonException)
    {
        continue;
    }

    var id = request?["id"];
    var method = request?["method"]?.GetValue<string>();
    if (method is null)
    {
        continue;
    }

    JsonObject? response = method switch
    {
        "initialize" => Result(id, new JsonObject
        {
            ["protocolVersion"] = request?["params"]?["protocolVersion"]?.GetValue<string>() ?? "2025-06-18",
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
            ["serverInfo"] = new JsonObject { ["name"] = "code-exploder", ["version"] = "0.1" },
        }),
        "tools/list" => Result(id, new JsonObject
        {
            ["tools"] = new JsonArray(tools.Select(t => (JsonNode)new JsonObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["inputSchema"] = t.Schema.DeepClone(),
            }).ToArray()),
        }),
        "tools/call" => await CallToolAsync(id, request!["params"]!.AsObject()),
        "ping" => Result(id, new JsonObject()),
        _ => id is null ? null : Error(id, -32601, $"method not found: {method}"), // notifications get no reply
    };

    if (response is not null)
    {
        await writer.WriteLineAsync(response.ToJsonString());
    }
}

async Task<JsonObject> CallToolAsync(JsonNode? id, JsonObject callParams)
{
    var name = callParams["name"]?.GetValue<string>();
    var tool = tools.FirstOrDefault(t => t.Name == name);
    if (tool.Name is null)
    {
        return Error(id, -32602, $"unknown tool: {name}");
    }

    try
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(170));
        var text = await tool.Handler(
            callParams["arguments"]?.AsObject() ?? [], timeout.Token);
        return Result(id, new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        });
    }
    catch (Exception ex)
    {
        return Result(id, new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = $"Error: {ex.Message}" }),
            ["isError"] = true,
        });
    }
}

static string Arg(JsonObject args, string name) =>
    args[name]?.GetValue<string>()
    ?? throw new ArgumentException($"missing required argument: {name}");

static JsonObject Schema(params (string Name, string Description)[] args) => new()
{
    ["type"] = "object",
    ["properties"] = new JsonObject(args.Select(a => KeyValuePair.Create(a.Name, (JsonNode?)new JsonObject
    {
        ["type"] = "string",
        ["description"] = a.Description,
    }))),
    ["required"] = new JsonArray(args.Select(a => (JsonNode)a.Name).ToArray()),
};

static JsonObject Result(JsonNode? id, JsonObject result) => new()
{
    ["jsonrpc"] = "2.0",
    ["id"] = id?.DeepClone(),
    ["result"] = result,
};

static JsonObject Error(JsonNode? id, int code, string message) => new()
{
    ["jsonrpc"] = "2.0",
    ["id"] = id?.DeepClone(),
    ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
};
