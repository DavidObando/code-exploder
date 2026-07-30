namespace CodeExploder.Analysis;

/// <summary>
/// Embedded linguist-style extension/filename table (docs/01 §S1). Returns null for
/// anything that is not a recognized text/code type, which the mapper treats as
/// "unrecognized" (excluded but still listed).
/// </summary>
public static class LanguageMap
{
    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cs"] = "C#",
        ["csproj"] = "MSBuild",
        ["sln"] = "MSBuild",
        ["slnx"] = "MSBuild",
        ["props"] = "MSBuild",
        ["ts"] = "TypeScript",
        ["tsx"] = "TypeScript",
        ["js"] = "JavaScript",
        ["jsx"] = "JavaScript",
        ["mjs"] = "JavaScript",
        ["cjs"] = "JavaScript",
        ["py"] = "Python",
        ["go"] = "Go",
        ["rs"] = "Rust",
        ["java"] = "Java",
        ["kt"] = "Kotlin",
        ["rb"] = "Ruby",
        ["php"] = "PHP",
        ["swift"] = "Swift",
        ["c"] = "C",
        ["h"] = "C",
        ["cpp"] = "C++",
        ["cc"] = "C++",
        ["hpp"] = "C++",
        ["m"] = "Objective-C",
        ["scala"] = "Scala",
        ["sql"] = "SQL",
        ["sh"] = "Shell",
        ["bash"] = "Shell",
        ["zsh"] = "Shell",
        ["ps1"] = "PowerShell",
        ["html"] = "HTML",
        ["css"] = "CSS",
        ["scss"] = "CSS",
        ["less"] = "CSS",
        ["md"] = "Markdown",
        ["markdown"] = "Markdown",
        ["rst"] = "Text",
        ["txt"] = "Text",
        ["adoc"] = "Text",
        ["json"] = "JSON",
        ["yaml"] = "YAML",
        ["yml"] = "YAML",
        ["toml"] = "TOML",
        ["xml"] = "XML",
        ["proto"] = "Protobuf",
        ["tf"] = "Terraform",
        ["gradle"] = "Gradle",
        ["cmake"] = "CMake",
        ["vue"] = "Vue",
        ["svelte"] = "Svelte",
        ["dart"] = "Dart",
        ["ex"] = "Elixir",
        ["exs"] = "Elixir",
        ["erl"] = "Erlang",
        ["hs"] = "Haskell",
        ["lua"] = "Lua",
        ["r"] = "R",
        ["jl"] = "Julia",
    };

    /// <summary>Maps a path (forward slashes) to a language name, or null if unrecognized.</summary>
    public static string? FromPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var slash = path.LastIndexOf('/');
        var fileName = slash < 0 ? path : path[(slash + 1)..];

        if (fileName.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("Dockerfile.", StringComparison.OrdinalIgnoreCase))
        {
            return "Dockerfile";
        }

        if (fileName.Equals("Makefile", StringComparison.OrdinalIgnoreCase))
        {
            return "Make";
        }

        if (fileName.Equals("CMakeLists.txt", StringComparison.OrdinalIgnoreCase))
        {
            return "CMake";
        }

        var dot = fileName.LastIndexOf('.');
        if (dot <= 0 || dot == fileName.Length - 1)
        {
            return null;
        }

        return ByExtension.TryGetValue(fileName[(dot + 1)..], out var language) ? language : null;
    }
}
