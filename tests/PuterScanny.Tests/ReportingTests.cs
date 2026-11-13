using System.Text.Json;
using PuterScanny.Analysis;
using PuterScanny.Analysis.Reporting;
using PuterScanny.Core.Layout;
using PuterScanny.Core.Models;
using PuterScanny.Knowledge;
using PuterScanny.Storage;

namespace PuterScanny.Tests;

public sealed class TreemapLayoutTests
{
    [Fact]
    public void Boxes_fill_the_area_in_proportion_without_overlapping()
    {
        double[] values = [500, 250, 120, 80, 30, 10, 6, 3, 1];
        var bounds = new LayoutRect(10, 20, 800, 500);
        var boxes = TreemapLayout.Squarify(values, bounds);

        double total = values.Sum();
        for (int i = 0; i < values.Length; i++)
        {
            var b = boxes[i];
            Assert.InRange(b.Area / bounds.Area, values[i] / total - 1e-6, values[i] / total + 1e-6);
            Assert.True(b.X >= bounds.X - 1e-6 && b.Y >= bounds.Y - 1e-6);
            Assert.True(b.X + b.Width <= bounds.X + bounds.Width + 1e-6 && b.Y + b.Height <= bounds.Y + bounds.Height + 1e-6);
        }

        for (int i = 0; i < boxes.Length; i++)
        for (int j = i + 1; j < boxes.Length; j++)
        {
            double overlapW = Math.Min(boxes[i].X + boxes[i].Width, boxes[j].X + boxes[j].Width) - Math.Max(boxes[i].X, boxes[j].X);
            double overlapH = Math.Min(boxes[i].Y + boxes[i].Height, boxes[j].Y + boxes[j].Height) - Math.Max(boxes[i].Y, boxes[j].Y);
            Assert.False(overlapW > 1e-6 && overlapH > 1e-6, $"boxes {i} and {j} overlap");
        }

        // Squarified layouts keep the big boxes reasonably square.
        double ratio = Math.Max(boxes[0].Width / boxes[0].Height, boxes[0].Height / boxes[0].Width);
        Assert.True(ratio < 3, $"largest box aspect ratio {ratio:0.0}");
    }

    [Fact]
    public void Zero_and_empty_inputs_are_handled()
    {
        var boxes = TreemapLayout.Squarify([0, 5, 0], new LayoutRect(0, 0, 100, 100));
        Assert.Equal(0, boxes[0].Area);
        Assert.Equal(10_000, boxes[1].Area, 6);
        Assert.Empty(TreemapLayout.Squarify([], new LayoutRect(0, 0, 100, 100)));
        Assert.All(TreemapLayout.Squarify([1, 2], new LayoutRect(0, 0, 0, 0)), b => Assert.Equal(0, b.Area));
    }
}

public sealed class ReportExportTests
{
    private static readonly KnowledgeBase Kb = KnowledgeBase.LoadDefault();

    private static (TempTree Tree, TempTree Out, ScanDatabase Db) ScanSample()
    {
        var tree = new TempTree();
        tree.File("Tom & Jerry, the movie.mp4", 3000);
        tree.File("=HYPERLINK(evil).txt", 10);
        string old = tree.File(@"cache\old.tmp", 5000);
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-60));
        File.SetCreationTimeUtc(old, DateTime.UtcNow.AddDays(-60)); // "created just now" would count as recent
        var output = new TempTree();
        string db = Path.Combine(output.Root, "scan.db");
        new ScanPipeline(Kb, PathTokens.ForThisMachine(), new AnalysisOptions { IncludeMsiComponents = false, Gentle = false })
            .Run(new ScanOptions { RootPath = tree.Root }, db, null, default);
        return (tree, output, ScanDatabase.Open(db));
    }

    [Fact]
    public void Html_report_has_every_section_and_escapes_names()
    {
        var (tree, output, db) = ScanSample();
        using (tree) using (output) using (db)
        {
            var report = ReportData.From(db, Kb);
            Assert.True(report.Complete);
            Assert.True(report.CanFree >= 5000);
            Assert.Contains(report.ToDelete, i => i.Name == "old.tmp" || i.Name == "cache");

            string html = ReportExporters.Html(report);
            Assert.StartsWith("<!doctype html>", html);
            foreach (string section in new[] { "can be freed safely", "Where the space goes", "What's in it", "What must be kept", "What was checked" })
                Assert.Contains(section, html);
            Assert.Contains("Tom &amp; Jerry, the movie.mp4", html);
            Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase); // static, nothing runs

            string path = Path.Combine(output.Root, "report.html");
            ReportExporters.WriteHtml(report, path);
            Assert.True(new FileInfo(path).Length > 2000);
        }
    }

    [Fact]
    public void Json_report_round_trips()
    {
        var (tree, output, db) = ScanSample();
        using (tree) using (output) using (db)
        {
            string path = Path.Combine(output.Root, "report.json");
            ReportExporters.WriteJson(ReportData.From(db, Kb), path);
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(tree.Root, doc.RootElement.GetProperty("root").GetString());
            Assert.True(doc.RootElement.GetProperty("canFree").GetInt64() >= 5000);
            Assert.True(doc.RootElement.GetProperty("verdicts").GetArrayLength() > 0);
        }
    }

    [Fact]
    public void Csv_lists_every_item_safely_quoted()
    {
        var (tree, output, db) = ScanSample();
        using (tree) using (output) using (db)
        {
            string path = Path.Combine(output.Root, "all.csv");
            long rows = ReportExporters.WriteCsv(db, Kb, path);
            var summary = db.GetSummary()!;
            Assert.Equal(summary.FileCount + summary.DirectoryCount + 1, rows);

            string[] lines = File.ReadAllLines(path);
            Assert.Equal(rows + 1, lines.Length);
            Assert.StartsWith("Path,Type,", lines[0].TrimStart('﻿'));
            Assert.Contains(lines, l => l.Contains("\"" + Path.Combine(tree.Root, "Tom & Jerry, the movie.mp4") + "\""));
            // A name that looks like a spreadsheet formula is neutralised so Excel shows it as text.
            Assert.Contains(lines, l => l.Contains("'=HYPERLINK(evil).txt") || l.Contains(Path.Combine(tree.Root, "=HYPERLINK(evil).txt")));
            Assert.Equal("'=SUM(A1)", ReportExporters.Csv("=SUM(A1)"));
            Assert.Equal("\"a, \"\"b\"\"\"", ReportExporters.Csv("a, \"b\""));
            Assert.Equal("-5", ReportExporters.Csv("-5"));
        }
    }
}
