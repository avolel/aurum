namespace Aurum.App.Infrastructure.AI.Ollama;

/// <summary>
/// Prompts live in <c>ops/prompts/{name}.v{n}.md</c>, copied beside the app's DLLs. The version
/// comes from the file name, so a changed prompt is a new file, and each explanation can record
/// which version it was written with.
/// </summary>
internal static class PromptTemplate
{
    /// <summary>Where the csproj copies <c>ops/prompts/</c> in the build and publish output.</summary>
    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "prompts");

    /// <summary>The highest version of <paramref name="name"/>, for example ("...", "v2").</summary>
    public static (string Text, string Version) Load(string name) => Load(Folder, name);

    /// <summary>Takes the folder so a test can set up versions the repo doesn't have.</summary>
    internal static (string Text, string Version) Load(string folder, string name)
    {
        var prefix = $"{name}.v";

        // Compared as numbers, so v10 beats v9. A name that isn't exactly {name}.v{n}.md
        // ("explain.v2.draft.md") is skipped rather than loaded.
        var newest = Directory.EnumerateFiles(folder, $"{prefix}*.md")
            .Select(path => (Path: path, Number: VersionNumber(Path.GetFileNameWithoutExtension(path), prefix)))
            .Where(file => file.Number is not null)
            // Not MaxBy: on an empty list of value tuples it throws instead of returning default.
            .OrderByDescending(file => file.Number)
            .FirstOrDefault();

        if (newest.Path is null)
        {
            throw new FileNotFoundException($"No prompt '{prefix}<n>.md' in {folder}.");
        }

        return (File.ReadAllText(newest.Path), $"v{newest.Number}");
    }

    private static int? VersionNumber(string fileName, string prefix) =>
        int.TryParse(fileName.AsSpan(prefix.Length), System.Globalization.NumberStyles.None, null, out var number)
            ? number
            : null;
}
