using Aurum.App.Infrastructure.AI.Ollama;

namespace Aurum.Api.Tests.Modules.AI;

/// <summary>
/// The first test reads the real prompts from this project's output, so it also proves the csproj
/// copies <c>ops/prompts/</c> through a project reference. The rest use a temp folder.
/// </summary>
public sealed class PromptTemplateTests : IDisposable
{
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("aurum-prompts-");

    public void Dispose() => _folder.Delete(recursive: true);

    private void Write(string fileName, string text) =>
        File.WriteAllText(Path.Combine(_folder.FullName, fileName), text);

    [Theory]
    [InlineData("explain")]
    [InlineData("check")]
    public void Load_ShippedPrompt_ReturnsTextAndVersionFromFileName(string name)
    {
        var (text, version) = PromptTemplate.Load(name);

        Assert.Equal("v1", version);
        Assert.Equal(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "prompts", $"{name}.v1.md")), text);
    }

    [Fact]
    public void Load_SeveralVersions_ReturnsTheHighestByNumber()
    {
        Write("explain.v2.md", "two");
        Write("explain.v10.md", "ten"); // "v10" sorts before "v2" as text
        Write("check.v99.md", "another prompt");

        Assert.Equal(("ten", "v10"), PromptTemplate.Load(_folder.FullName, "explain"));
    }

    [Theory]
    [InlineData("explain.v3.draft.md")]
    [InlineData("explain.v-3.md")]
    [InlineData("explain.vnext.md")]
    public void Load_FileNameNotExactlyAVersion_IsSkipped(string fileName)
    {
        Write("explain.v1.md", "one");
        Write(fileName, "not a version");

        Assert.Equal(("one", "v1"), PromptTemplate.Load(_folder.FullName, "explain"));
    }

    [Fact]
    public void Load_NoSuchPrompt_Throws()
    {
        Write("check.v1.md", "check");

        var ex = Assert.Throws<FileNotFoundException>(() => PromptTemplate.Load(_folder.FullName, "explain"));
        Assert.Contains("explain.v", ex.Message);
    }
}
