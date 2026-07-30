using Xunit;

namespace CodeExploder.Analysis.Tests;

public sealed class LanguageMapTests
{
    [Theory]
    [InlineData("src/Program.cs", "C#")]
    [InlineData("App.csproj", "MSBuild")]
    [InlineData("thing.sln", "MSBuild")]
    [InlineData("thing.slnx", "MSBuild")]
    [InlineData("web/app.ts", "TypeScript")]
    [InlineData("web/app.tsx", "TypeScript")]
    [InlineData("web/index.mjs", "JavaScript")]
    [InlineData("tool.py", "Python")]
    [InlineData("cmd/main.go", "Go")]
    [InlineData("src/lib.rs", "Rust")]
    [InlineData("style.scss", "CSS")]
    [InlineData("README.md", "Markdown")]
    [InlineData("config.yml", "YAML")]
    [InlineData("schema.proto", "Protobuf")]
    [InlineData("infra/main.tf", "Terraform")]
    [InlineData("notebook.jl", "Julia")]
    [InlineData("deps.exs", "Elixir")]
    public void KnownExtensionsMapToLanguages(string path, string expected)
    {
        Assert.Equal(expected, LanguageMap.FromPath(path));
    }

    [Theory]
    [InlineData("Dockerfile", "Dockerfile")]
    [InlineData("docker/Dockerfile.dev", "Dockerfile")]
    [InlineData("Makefile", "Make")]
    [InlineData("native/CMakeLists.txt", "CMake")]
    public void FilenameBasedLanguagesAreRecognized(string path, string expected)
    {
        Assert.Equal(expected, LanguageMap.FromPath(path));
    }

    [Theory]
    [InlineData("blob.xyz")]
    [InlineData("LICENSE")]
    [InlineData(".gitignore")]
    [InlineData("archive.dat")]
    public void UnknownTypesReturnNull(string path)
    {
        Assert.Null(LanguageMap.FromPath(path));
    }
}
