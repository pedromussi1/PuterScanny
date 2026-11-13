using PuterScanny.Knowledge;

namespace PuterScanny.Tests;

public sealed class KnowledgeBaseTests
{
    private static readonly KnowledgeBase Kb = KnowledgeBase.LoadDefault();

    [Fact]
    public void Loads_a_substantial_catalog()
    {
        Assert.True(Kb.Types.Count > 700, $"only {Kb.Types.Count} file types");
        Assert.True(Kb.Signatures.Count > 100, $"only {Kb.Signatures.Count} signatures");
        Assert.True(Kb.Rules.Count > 120, $"only {Kb.Rules.Count} location rules");
    }

    [Fact]
    public void Every_category_reference_exists()
    {
        var categories = Kb.Categories.Select(c => c.Id).ToHashSet();
        Assert.All(Kb.Types, t => Assert.Contains(t.Category, categories));
        Assert.All(Kb.Signatures, s => Assert.Contains(s.Category, categories));

        var locationCategories = Kb.LocationCategories.Select(c => c.Id).ToHashSet();
        Assert.All(Kb.Rules, r => Assert.Contains(r.Category, locationCategories));
    }

    [Fact]
    public void Extensions_are_well_formed()
    {
        Assert.All(Kb.Types, t =>
        {
            Assert.StartsWith(".", t.Extension);
            Assert.Equal(t.Extension.ToLowerInvariant(), t.Extension);
            Assert.False(string.IsNullOrWhiteSpace(t.Description));
            Assert.DoesNotContain(' ', t.Extension);
        });
    }

    [Fact]
    public void Ids_are_unique()
    {
        Assert.Equal(Kb.Rules.Count, Kb.Rules.Select(r => r.Id).Distinct().Count());
        Assert.Equal(Kb.Signatures.Count, Kb.Signatures.Select(s => s.Id).Distinct().Count());
    }

    [Fact]
    public void Every_rule_has_text_and_paths_that_expand_on_this_machine()
    {
        var tokens = PathTokens.ForThisMachine();
        Assert.All(Kb.Rules, r =>
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Name), r.Id);
            Assert.False(string.IsNullOrWhiteSpace(r.Description), r.Id);
            Assert.NotEmpty(r.Paths);
            Assert.All(r.Paths, p => Assert.NotNull(tokens.Expand(p)));
        });
    }

    [Fact]
    public void Signatures_are_not_empty_or_all_wildcards()
    {
        Assert.All(Kb.Signatures, s => Assert.Contains(s.Pattern, b => b is not null));
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\drivers", "win.drivers")]
    [InlineData(@"C:\Windows\System32", "win.system32")]
    [InlineData(@"C:\Windows\WinSxS", "win.winsxs")]
    [InlineData(@"C:\Windows\SoftwareDistribution\Download", "win.softwaredistribution.download")]
    [InlineData(@"C:\pagefile.sys", "win.pagefile")]
    [InlineData(@"D:\$Recycle.Bin", "win.recyclebin")]
    [InlineData(@"C:\Users\Sam\AppData\Local\Temp", "user.temp")]
    [InlineData(@"C:\Users\Sam\Downloads", "user.downloads")]
    [InlineData(@"C:\Users\Public", "win.users.public")]
    [InlineData(@"C:\Users\Sam\AppData\Local\Google\Chrome\User Data\Default\Cache", "browser.chromium.cache")]
    [InlineData(@"C:\Users\Sam\AppData\Local\Google\Chrome\User Data", "browser.chrome")]
    [InlineData(@"D:\code\site\node_modules", "dev.node_modules")]
    [InlineData(@"C:\Users\Sam\.nuget\packages", "dev.nuget.packages")]
    [InlineData(@"E:\SteamLibrary\steamapps\shadercache", "game.steam.shadercache")]
    public void Known_locations_match_the_most_specific_rule(string path, string expectedRule)
    {
        var tokens = new PathTokens(new Dictionary<string, string>
        {
            ["windir"] = @"C:\Windows",
            ["systemdrive"] = "C:",
            ["programfiles"] = @"C:\Program Files",
            ["programfilesx86"] = @"C:\Program Files (x86)",
            ["programdata"] = @"C:\ProgramData",
            ["users"] = @"C:\Users",
            ["anydrive"] = "?:",
        });
        var matcher = Kb.BuildLocationMatcher(tokens);
        var state = matcher.AdvancePath(path);
        Assert.True(matcher.TryGetBest(state, out var rule), $"no rule for {path}");
        Assert.Equal(expectedRule, rule.Id);
    }
}

public sealed class PathMatcherTests
{
    [Fact]
    public void Literals_wildcards_and_double_star()
    {
        var m = new PathMatcher<string>();
        m.Add(@"C:\Users\*\AppData", "appdata", KnowledgeBase.Specificity(@"C:\Users\*\AppData"));
        m.Add(@"**\node_modules", "nm", KnowledgeBase.Specificity(@"**\node_modules"));
        m.Add(@"C:\Users\*\Pro*s", "glob", KnowledgeBase.Specificity(@"C:\Users\*\Pro*s"));

        Assert.True(m.TryGetBest(m.AdvancePath(@"C:\Users\Ann\AppData"), out var a) && a == "appdata");
        Assert.True(m.TryGetBest(m.AdvancePath(@"c:\users\ann\appdata"), out a) && a == "appdata"); // case-insensitive
        Assert.False(m.TryGetBest(m.AdvancePath(@"C:\Users\Ann\AppData\Local"), out _));
        Assert.True(m.TryGetBest(m.AdvancePath(@"D:\a\b\c\node_modules"), out a) && a == "nm");
        Assert.True(m.TryGetBest(m.AdvancePath(@"node_modules"), out a) && a == "nm"); // ** matches zero folders
        Assert.True(m.TryGetBest(m.AdvancePath(@"C:\Users\Ann\Programs"), out a) && a == "glob");
        Assert.False(m.TryGetBest(m.AdvancePath(@"C:\Users\Ann\Programs\x"), out _));
    }

    [Fact]
    public void Incremental_matching_equals_whole_path_matching()
    {
        var m = new PathMatcher<string>();
        m.Add(@"C:\Windows\System32\drivers", "drivers", 30);
        var state = m.Start;
        foreach (var seg in new[] { "C:", "Windows", "System32", "drivers" }) state = m.Advance(state, seg);
        Assert.True(m.TryGetBest(state, out var v) && v == "drivers");
    }
}
