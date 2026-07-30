using System.Text.RegularExpressions;

namespace CodeExploder.Analysis;

/// <summary>
/// Best-effort relative-import extraction for JS/TS and Python (docs/01 §S1). Used only
/// for ranking, so precision matters more than recall; unresolvable imports are ignored.
/// </summary>
internal static partial class ImportGraph
{
    private static readonly string[] ResolveExtensions = [".js", ".ts", ".jsx", ".tsx", ".py"];

    [GeneratedRegex(@"(?:\bfrom\s*|\brequire\s*\(\s*|^[ \t]*import\s+)['""](\.[^'""]+)['""]", RegexOptions.Multiline)]
    private static partial Regex JsImportRegex();

    [GeneratedRegex(@"^[ \t]*from\s+(\.[\w.]*)\s+import\b", RegexOptions.Multiline)]
    private static partial Regex PyImportRegex();

    /// <summary>
    /// Returns fan-in per resolved target path: the number of distinct non-excluded
    /// source files importing it via a relative path.
    /// </summary>
    internal static Dictionary<string, int> ComputeFanIn(
        IReadOnlyList<(string Path, string Language, string Text)> sources,
        IReadOnlySet<string> allPaths)
    {
        var importers = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (path, language, text) in sources)
        {
            IEnumerable<string?> targets = language switch
            {
                "JavaScript" or "TypeScript" =>
                    JsImportRegex().Matches(text).Select(m => ResolveJs(path, m.Groups[1].Value, allPaths)),
                "Python" =>
                    PyImportRegex().Matches(text).Select(m => ResolvePy(path, m.Groups[1].Value, allPaths)),
                _ => [],
            };

            foreach (var target in targets)
            {
                if (target is null || string.Equals(target, path, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!importers.TryGetValue(target, out var set))
                {
                    set = new HashSet<string>(StringComparer.Ordinal);
                    importers[target] = set;
                }

                set.Add(path);
            }
        }

        return importers.ToDictionary(kv => kv.Key, kv => kv.Value.Count, StringComparer.Ordinal);
    }

    private static string? ResolveJs(string importer, string spec, IReadOnlySet<string> allPaths)
    {
        var basePath = Normalize(DirectoryOf(importer), spec);
        if (basePath is null)
        {
            return null;
        }

        if (allPaths.Contains(basePath))
        {
            return basePath;
        }

        foreach (var ext in ResolveExtensions)
        {
            var candidate = basePath + ext;
            if (allPaths.Contains(candidate))
            {
                return candidate;
            }
        }

        foreach (var ext in ResolveExtensions)
        {
            var candidate = basePath + "/index" + ext;
            if (allPaths.Contains(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? ResolvePy(string importer, string module, IReadOnlySet<string> allPaths)
    {
        var dots = 0;
        while (dots < module.Length && module[dots] == '.')
        {
            dots++;
        }

        var segments = new List<string>();
        var dir = DirectoryOf(importer);
        if (dir.Length > 0)
        {
            segments.AddRange(dir.Split('/'));
        }

        var up = dots - 1;
        if (up > segments.Count)
        {
            return null;
        }

        if (up > 0)
        {
            segments.RemoveRange(segments.Count - up, up);
        }

        var rest = module[dots..];
        if (rest.Length > 0)
        {
            segments.AddRange(rest.Split('.'));
        }

        if (segments.Count == 0)
        {
            return null;
        }

        var basePath = string.Join('/', segments);
        if (allPaths.Contains(basePath + ".py"))
        {
            return basePath + ".py";
        }

        return allPaths.Contains(basePath + "/__init__.py") ? basePath + "/__init__.py" : null;
    }

    private static string DirectoryOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? "" : path[..slash];
    }

    private static string? Normalize(string dir, string spec)
    {
        var stack = new List<string>();
        if (dir.Length > 0)
        {
            stack.AddRange(dir.Split('/'));
        }

        foreach (var segment in spec.Split('/'))
        {
            switch (segment)
            {
                case "" or ".":
                    break;
                case "..":
                    if (stack.Count == 0)
                    {
                        return null;
                    }

                    stack.RemoveAt(stack.Count - 1);
                    break;
                default:
                    stack.Add(segment);
                    break;
            }
        }

        return stack.Count == 0 ? null : string.Join('/', stack);
    }
}
