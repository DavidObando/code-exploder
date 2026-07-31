using System.Reflection;

namespace CodeExploder.Pipeline;

/// <summary>
/// Versioned prompt files as embedded resources (Prompts/*.v1.txt). Every LLM output
/// row records prompt version + model for reproducibility (docs/01 §S4).
/// </summary>
public static class PromptLibrary
{
    public const string ComponentSummary = "component-summary.v1";
    public const string Architecture = "architecture.v1";
    public const string DiagramSpec = "diagram-spec.v1";
    public const string Section = "section.v1";
    public const string Quiz = "quiz.v1";
    public const string Story = "story.v1";
    public const string Grade = "grade.v1";
    public const string Repair = "repair.v1";

    private static readonly Dictionary<string, string> Cache = [];

    public static string Load(string name)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(name, out var cached))
            {
                return cached;
            }

            var assembly = Assembly.GetExecutingAssembly();
            var resource = assembly.GetManifestResourceNames()
                .FirstOrDefault(r => r.EndsWith($"Prompts.{name}.txt", StringComparison.Ordinal))
                ?? throw new InvalidOperationException($"Prompt resource not found: {name}");
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            Cache[name] = text;
            return text;
        }
    }
}

/// <summary>Priority-ordered material assembly with a hard character ceiling (pack, don't stuff).</summary>
public sealed class PackBuilder(int maxChars)
{
    private readonly System.Text.StringBuilder _sb = new();

    public bool TryAdd(string header, string content)
    {
        var addition = $"\n## {header}\n{content}\n";
        if (_sb.Length + addition.Length > maxChars)
        {
            var remaining = maxChars - _sb.Length - header.Length - 8;
            if (remaining < 200)
            {
                return false;
            }

            _sb.Append('\n').Append("## ").Append(header).Append('\n')
               .Append(content.AsSpan(0, Math.Min(content.Length, remaining))).Append("\n…[truncated]\n");
            return true;
        }

        _sb.Append(addition);
        return true;
    }

    public override string ToString() => _sb.ToString();
}
