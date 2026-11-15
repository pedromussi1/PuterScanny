namespace PuterScanny.Tests;

/// <summary>Guards against invisible control characters in source and knowledge files. A "\b" that turned into a
/// backspace character once silently broke regular expressions (they compiled, but never matched).</summary>
public sealed class SourceHygieneTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PuterScanny.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    [Fact]
    public void No_source_or_knowledge_file_contains_control_characters()
    {
        string root = RepoRoot();
        var bad = new List<string>();
        foreach (string folder in new[] { "src", "tests", "data" })
        {
            foreach (string file in Directory.EnumerateFiles(Path.Combine(root, folder), "*.*", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                    file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
                if (Path.GetExtension(file) is not (".cs" or ".json" or ".xaml" or ".csproj")) continue;
                string text = File.ReadAllText(file);
                int i = text.IndexOfAny(['\b', '\f', '\v', '\0']);
                if (i >= 0) bad.Add($"{Path.GetRelativePath(root, file)} (line {text[..i].Count(c => c == '\n') + 1})");
            }
        }
        Assert.True(bad.Count == 0, "Control characters found in: " + string.Join(", ", bad));
    }
}
