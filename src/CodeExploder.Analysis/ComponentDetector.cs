using System.Globalization;
using CodeExploder.Domain;

namespace CodeExploder.Analysis;

/// <summary>
/// S3: components are project boundaries where manifests define them (one per csproj /
/// package.json / go.mod / …), else top-level directories; merged and split to land in
/// a sane 8–40 component range (docs/01 §S3).
/// </summary>
public sealed class ComponentDetector
{
    internal const int DefaultMinComponentFiles = 3;
    internal const int DefaultSplitThreshold = 150;
    internal const int DefaultMaxComponents = 40;

    private const string RootName = "(root)";
    private const int TopFilesCap = 5;

    private readonly int _minComponentFiles;
    private readonly int _splitThreshold;
    private readonly int _maxComponents;

    public ComponentDetector()
        : this(DefaultMinComponentFiles, DefaultSplitThreshold, DefaultMaxComponents)
    {
    }

    internal ComponentDetector(int minComponentFiles, int splitThreshold, int maxComponents)
    {
        _minComponentFiles = minComponentFiles;
        _splitThreshold = splitThreshold;
        _maxComponents = maxComponents;
    }

    /// <summary>Assigns every non-excluded file to exactly one component.</summary>
    public IReadOnlyList<Component> Detect(RepoMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        var files = map.Files.Where(f => !f.Excluded).ToList();
        var rankByPath = files.ToDictionary(f => f.Path, f => f.Rank, StringComparer.Ordinal);

        var comps = AssignFiles(files);
        SplitLarge(comps);
        MergeSmall(comps);
        CapCount(comps);

        return comps
            .Where(c => c.Files.Count > 0)
            .OrderByDescending(c => c.Files.Count)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .Select(c => new Component(
                c.Name,
                c.Roots,
                c.Files.OrderBy(p => p, StringComparer.Ordinal).ToList(),
                c.Files
                    .OrderByDescending(p => rankByPath.GetValueOrDefault(p))
                    .ThenBy(p => p, StringComparer.Ordinal)
                    .Take(TopFilesCap)
                    .ToList()))
            .ToList();
    }

    private static List<Comp> AssignFiles(List<RepoFile> files)
    {
        var candidateDirs = FindCandidateDirectories(files);
        var comps = new List<Comp>();

        if (candidateDirs.Count == 0)
        {
            // No manifests anywhere: fall back to top-level directories; root-level
            // loose files form "(root)".
            var byName = new Dictionary<string, Comp>(StringComparer.Ordinal);
            foreach (var file in files)
            {
                var slash = file.Path.IndexOf('/');
                var name = slash < 0 ? RootName : file.Path[..slash];
                if (!byName.TryGetValue(name, out var comp))
                {
                    comp = new Comp { Name = name };
                    comp.Roots.Add(slash < 0 ? "" : name);
                    byName[name] = comp;
                    comps.Add(comp);
                }

                comp.Files.Add(file.Path);
            }

            return comps;
        }

        var byDir = new Dictionary<string, Comp>(StringComparer.Ordinal);
        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (dir, name) in candidateDirs.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var finalName = name;
            if (!usedNames.Add(finalName))
            {
                finalName = dir.Length == 0 ? RootName : dir;
                var suffix = 2;
                while (!usedNames.Add(finalName))
                {
                    finalName = dir + "#" + suffix.ToString(CultureInfo.InvariantCulture);
                    suffix++;
                }
            }

            var comp = new Comp { Name = finalName };
            comp.Roots.Add(dir);
            byDir[dir] = comp;
            comps.Add(comp);
        }

        // Parent = nearest ancestor candidate; nested candidates stay separate and the
        // parent keeps only the files not claimed by a nested candidate.
        foreach (var (dir, comp) in byDir)
        {
            var ancestor = ParentOf(dir);
            while (ancestor is not null)
            {
                if (byDir.TryGetValue(ancestor, out var parent))
                {
                    comp.ParentName = parent.Name;
                    break;
                }

                ancestor = ParentOf(ancestor);
            }
        }

        var dirsByDepth = byDir.Keys.OrderByDescending(d => d.Length).ToList();
        Comp? root = null;
        foreach (var file in files)
        {
            Comp? target = null;
            foreach (var dir in dirsByDepth)
            {
                if (dir.Length == 0 || file.Path.StartsWith(dir + "/", StringComparison.Ordinal))
                {
                    target = byDir[dir];
                    break;
                }
            }

            if (target is null)
            {
                if (root is null)
                {
                    root = comps.Find(c => string.Equals(c.Name, RootName, StringComparison.Ordinal));
                    if (root is null)
                    {
                        root = new Comp { Name = RootName };
                        root.Roots.Add("");
                        comps.Add(root);
                    }
                }

                target = root;
            }

            target.Files.Add(file.Path);
        }

        return comps;
    }

    private static Dictionary<string, string> FindCandidateDirectories(List<RepoFile> files)
    {
        var candidates = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var slash = file.Path.LastIndexOf('/');
            var dir = slash < 0 ? "" : file.Path[..slash];
            var fileName = slash < 0 ? file.Path : file.Path[(slash + 1)..];

            var isCsproj = fileName.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);
            var isManifest = isCsproj || fileName is "package.json" or "go.mod" or "Cargo.toml"
                or "pyproject.toml" or "pom.xml" or "build.gradle" or "build.gradle.kts";
            if (!isManifest)
            {
                continue;
            }

            // A csproj-derived name wins over a generic directory name.
            var name = isCsproj ? fileName[..^".csproj".Length] : (dir.Length == 0 ? RootName : dir);
            if (isCsproj || !candidates.ContainsKey(dir))
            {
                candidates[dir] = name;
            }
        }

        return candidates;
    }

    private void SplitLarge(List<Comp> comps)
    {
        foreach (var comp in comps.ToList())
        {
            if (comp.Files.Count <= _splitThreshold || comp.Roots.Count != 1)
            {
                continue;
            }

            var root = comp.Roots[0];
            var prefixLength = root.Length == 0 ? 0 : root.Length + 1;
            var loose = new List<string>();
            var children = new Dictionary<string, Comp>(StringComparer.Ordinal);
            foreach (var path in comp.Files)
            {
                var relative = path[prefixLength..];
                var slash = relative.IndexOf('/');
                if (slash < 0)
                {
                    loose.Add(path);
                    continue;
                }

                var childSegment = relative[..slash];
                if (!children.TryGetValue(childSegment, out var child))
                {
                    child = new Comp { Name = comp.Name + "/" + childSegment, ParentName = comp.Name };
                    child.Roots.Add(prefixLength == 0 ? childSegment : root + "/" + childSegment);
                    children[childSegment] = child;
                    comps.Add(child);
                }

                child.Files.Add(path);
            }

            comp.Files.Clear();
            comp.Files.AddRange(loose);
        }
    }

    private void MergeSmall(List<Comp> comps)
    {
        var root = comps.Find(c => string.Equals(c.Name, RootName, StringComparison.Ordinal));
        if (root is null)
        {
            root = new Comp { Name = RootName };
            root.Roots.Add("");
            comps.Add(root);
        }

        // Deepest-first so a small child merges into its parent before the parent is
        // itself evaluated.
        var ordered = comps
            .Where(c => c != root)
            .OrderByDescending(c => c.Roots.Count == 0 ? 0 : c.Roots[0].Length)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .ToList();
        foreach (var comp in ordered)
        {
            if (comp.Files.Count >= _minComponentFiles)
            {
                continue;
            }

            var target = comp.ParentName is null
                ? null
                : comps.Find(c => c != comp && string.Equals(c.Name, comp.ParentName, StringComparison.Ordinal));
            target ??= root;
            target.Files.AddRange(comp.Files);
            comps.Remove(comp);
        }
    }

    private void CapCount(List<Comp> comps)
    {
        if (comps.Count <= _maxComponents)
        {
            return;
        }

        var root = comps.Find(c => string.Equals(c.Name, RootName, StringComparison.Ordinal));
        if (root is null)
        {
            root = new Comp { Name = RootName };
            root.Roots.Add("");
            comps.Add(root);
        }

        while (comps.Count > _maxComponents)
        {
            var smallest = comps
                .Where(c => c != root)
                .OrderBy(c => c.Files.Count)
                .ThenBy(c => c.Name, StringComparer.Ordinal)
                .First();
            root.Files.AddRange(smallest.Files);
            comps.Remove(smallest);
        }
    }

    private static string? ParentOf(string dir)
    {
        if (dir.Length == 0)
        {
            return null;
        }

        var slash = dir.LastIndexOf('/');
        return slash < 0 ? "" : dir[..slash];
    }

    private sealed class Comp
    {
        public required string Name { get; init; }

        public List<string> Roots { get; } = [];

        public List<string> Files { get; } = [];

        public string? ParentName { get; set; }
    }
}
