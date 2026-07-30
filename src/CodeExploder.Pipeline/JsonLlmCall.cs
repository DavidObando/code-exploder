using System.Text.Json;
using CodeExploder.Llm;
using Microsoft.Extensions.Logging;

namespace CodeExploder.Pipeline;

public sealed class GenerationException(string message) : Exception(message);

/// <summary>
/// The strict-JSON call contract every LLM stage uses (docs/01 §1.7): prompt demands
/// JSON, the response is extracted/parsed/validated, and exactly one repair retry runs
/// with the validator errors appended. A second failure throws — callers decide
/// whether that degrades or fails the stage.
/// </summary>
public sealed class JsonLlmCall(ILlmClient llm, ILogger logger)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// validate returns a list of problems (empty = valid) and may normalize via the
    /// returned value; parse errors count as problems automatically.
    /// </summary>
    public async Task<T> CallAsync<T>(
        string systemPrompt,
        string material,
        Func<T, List<string>> validate,
        int maxOutputTokens,
        CancellationToken ct)
    {
        var messages = new List<LlmMessage>
        {
            new("system", systemPrompt),
            new("user", material),
        };

        var (result, errors) = await AttemptAsync(messages, validate, maxOutputTokens, ct);
        if (errors.Count == 0)
        {
            return result!;
        }

        logger.LogInformation("LLM JSON failed validation ({Errors}); repair retry", string.Join("; ", errors.Take(3)));
        messages.Add(new("assistant", "(previous response)"));
        messages.Add(new("user", PromptLibrary.Load(PromptLibrary.Repair)
            .Replace("{errors}", string.Join("\n", errors), StringComparison.Ordinal)));

        var (repaired, repairErrors) = await AttemptAsync(messages, validate, maxOutputTokens, ct);
        if (repairErrors.Count == 0)
        {
            return repaired!;
        }

        throw new GenerationException("LLM output failed validation twice: " + string.Join("; ", repairErrors.Take(5)));
    }

    private async Task<(T? Value, List<string> Errors)> AttemptAsync<T>(
        List<LlmMessage> messages, Func<T, List<string>> validate, int maxOutputTokens, CancellationToken ct)
    {
        var result = await llm.ChatAsync(new LlmRequest(messages, maxOutputTokens, Json: true), ct);
        var json = ExtractJson(result.Content);
        if (json is null)
        {
            return (default, ["response contained no JSON object"]);
        }

        try
        {
            var value = JsonSerializer.Deserialize<T>(json, JsonOpts);
            if (value is null)
            {
                return (default, ["JSON deserialized to null"]);
            }

            var errors = validate(value);
            return (value, errors);
        }
        catch (JsonException ex)
        {
            return (default, [$"JSON parse error: {ex.Message}"]);
        }
    }

    /// <summary>Tolerates fences and stray prose around the object.</summary>
    internal static string? ExtractJson(string content)
    {
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        return start >= 0 && end > start ? content[start..(end + 1)] : null;
    }
}
