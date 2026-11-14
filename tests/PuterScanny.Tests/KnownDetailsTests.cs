using PuterScanny.Knowledge;

namespace PuterScanny.Tests;

/// <summary>known-details.json: what names and dates mean by known conventions, and only on exact matches.</summary>
public sealed class KnownDetailsTests
{
    private static readonly KnownDetails Details = KnowledgeBase.LoadDefault().Details;
    private static readonly DateTime Now = new(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);

    private static string? Date(DateTime utc) => Details.ForDate(utc, Now)?.Id;

    private static IReadOnlyList<KnownDetail> Name(string name, bool folder = false, long size = 1000, params string[] siblings) =>
        Details.ForName(name, folder, size, n => siblings.Contains(n, StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void The_file_loads_with_unique_ids()
    {
        Assert.True(Details.Count >= 20);
        Assert.Equal(Details.Ids.Count(), Details.Ids.Distinct().Count());
    }

    [Fact]
    public void Placeholder_dates_are_recognised_in_any_time_zone()
    {
        Assert.Equal("torrentzip", Date(new DateTime(1996, 12, 24, 23, 32, 0, DateTimeKind.Local).ToUniversalTime()));
        Assert.Equal("torrentzip", Date(new DateTime(1996, 12, 24, 22, 32, 0, DateTimeKind.Utc)));
        Assert.Equal("zip-minimum", Date(new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Local).ToUniversalTime()));
        Assert.Equal("unix-zero", Date(new DateTime(1970, 1, 1, 0, 0, 1, DateTimeKind.Utc)));
        Assert.Equal("windows-zero", Date(new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        Assert.Equal("future-date", Date(Now.AddYears(3)));
    }

    [Fact]
    public void Ordinary_dates_are_left_alone()
    {
        Assert.Null(Date(new DateTime(1996, 12, 24, 23, 31, 0, DateTimeKind.Local).ToUniversalTime()));
        Assert.Null(Date(new DateTime(2024, 12, 24, 23, 32, 0, DateTimeKind.Local).ToUniversalTime()));
        Assert.Null(Date(new DateTime(1980, 1, 1, 9, 30, 0, DateTimeKind.Local).ToUniversalTime()));
        Assert.Null(Date(Now.AddHours(5)));
        Assert.Null(Date(default));
    }

    [Fact]
    public void No_Intro_names_give_region_and_languages()
    {
        var d = Assert.Single(Name("Donkey Kong Country 2 - Diddy's Kong Quest (USA) (En,Fr).sfc"));
        Assert.Equal("no-intro", d.Id);
        Assert.Contains("release region (USA)", d.Text);
        Assert.Contains("languages (English and French)", d.Text);

        d = Assert.Single(Name("Legend of Zelda, The - The Minish Cap (USA).gba"));
        Assert.DoesNotContain("languages", d.Text);

        var rev = Name("Pokemon - FireRed Version (USA, Europe) (Rev 1).gba");
        Assert.Contains(rev, x => x.Id == "dump-status" && x.Text.Contains("a later revision"));
        Assert.Contains(Name("Super Mario World (USA) [!].sfc"), x => x.Id == "goodtools" && x.Text.Contains("verified good copy"));
        Assert.Contains(Name("Sonic (World) [b1].md"), x => x.Id == "goodtools" && x.Text.Contains("bad (corrupt)"));
    }

    [Fact]
    public void Conventions_only_apply_where_they_belong()
    {
        Assert.Empty(Name("My Notes (USA).txt"));                                       // not a game file
        Assert.Empty(Name("Pokemon - Emerald Version (USA, Europe)", folder: true));     // the game's folder, not the dump
        Assert.Empty(Name("IMG_20231345_101010.jpg"));                                   // month 13 isn't a date
        Assert.Empty(Name("~$port.docx", size: 50_000));                                 // a real document, not an owner file
        Assert.Empty(Name("holiday.mp4"));
    }

    [Fact]
    public void Browser_and_Windows_copies_point_at_the_original()
    {
        var d = Assert.Single(Name("report (1).pdf", siblings: "report.pdf"));
        Assert.Equal("browser-copy", d.Id);
        Assert.Contains("probably a repeat of report.pdf", d.Text);
        Assert.Contains("report.pdf is in the same folder", d.Text);
        Assert.DoesNotContain("same folder", Assert.Single(Name("report (1).pdf")).Text);
        Assert.Equal("windows-copy", Assert.Single(Name("notes - Copy.txt", siblings: "notes.txt")).Id);

        // "Screenshot (3).png" is Windows' own screenshot numbering, not a second download.
        Assert.Equal("printscreen", Assert.Single(Name("Screenshot (3).png", siblings: "Screenshot.png")).Id);
    }

    [Fact]
    public void Camera_and_app_names_give_the_date_they_carry()
    {
        Assert.Contains("taken on 12 May 2023 at 10:11", Assert.Single(Name("IMG_20230512_101112.jpg")).Text);
        Assert.Contains("12 May 2023", Assert.Single(Name("PXL_20230512_101112345.jpg")).Text);
        Assert.Contains("1 Feb 2024 at 09:05", Assert.Single(Name("WhatsApp Image 2024-02-01 at 9.05.33 AM.jpeg")).Text);
        Assert.Contains("15 Jan 2024 at 10:30", Assert.Single(Name("Screenshot 2024-01-15 103045.png")).Text);
        Assert.Equal("camera-numbering", Assert.Single(Name("DSC_0042.NEF")).Id);
    }

    [Fact]
    public void Installers_stores_videos_and_Mac_leftovers_are_recognised()
    {
        Assert.Equal("gog-installer", Assert.Single(Name("setup_witcher_3_4.04_(64bit)_(65225).exe")).Id);
        Assert.Contains("app 489830", Assert.Single(Name("appmanifest_489830.acf")).Text);
        var release = Name("Some.Movie.2019.1080p.BluRay.x264-GROUP.mkv");
        Assert.Contains(release, x => x.Id == "release-name" && x.Text.Contains("(2019)") && x.Text.Contains("(1080p)") && x.Text.Contains("(GROUP)"));
        Assert.Contains(Name("Show.Name.S02E05.720p.WEB.h264-GRP.mkv"), x => x.Id == "tv-episode" && x.Text.Contains("season 02, episode 05"));
        Assert.Equal("ds-store", Assert.Single(Name(".DS_Store")).Id);
        Assert.Equal("appledouble", Assert.Single(Name("._photo.jpg")).Id);
        Assert.Equal("macosx-folder", Assert.Single(Name("__MACOSX", folder: true)).Id);
        Assert.Equal("office-owner", Assert.Single(Name("~$port.docx", size: 162)).Id);
    }

    private sealed record Case(string Id, bool Folder, string[] Match, string[] NoMatch);

    /// <summary>Every researched entry comes with names it must match and similar names it must not (known-details-cases.json).</summary>
    [Fact]
    public void Every_entry_matches_its_examples_and_none_of_its_near_misses()
    {
        string file = Path.Combine(AppContext.BaseDirectory, "known-details-cases.json");
        var cases = System.Text.Json.JsonSerializer.Deserialize<Case[]>(File.ReadAllText(file),
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var problems = new List<string>();
        foreach (var c in cases)
        {
            Assert.Contains(c.Id, Details.Ids);
            foreach (string name in c.Match)
                if (!Details.ForName(name, c.Folder, 100).Any(d => d.Id == c.Id)) problems.Add($"{c.Id} should match \"{name}\"");
            foreach (string name in c.NoMatch)
                if (Details.ForName(name, c.Folder, 100).Any(d => d.Id == c.Id)) problems.Add($"{c.Id} should not match \"{name}\"");
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void A_folder_of_No_Intro_games_is_described_once()
    {
        var d = Assert.Single(Details.ForFolderContents([("Tetris (World).gb", 32768L), ("Kirby's Dream Land (USA, Europe).gb", 262144L)]));
        Assert.Contains("No-Intro", d.Text);
    }
}
