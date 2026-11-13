using System.IO;
using PuterScanny.AI;
using PuterScanny.Core.Models;

namespace PuterScanny.App.ViewModels;

/// <summary>
/// Turns what the scan found about one item into the plain facts the local AI is given. The keep/delete verdict is
/// deliberately left out: the AI describes what something is, it doesn't judge it.
/// </summary>
internal static class AiFactsBuilder
{
    public static ExplainFacts Build(FileSystemEntry e, ResultsContext ctx)
    {
        var facts = new List<(string, string?)>();
        var a = e.Analysis;
        var kb = ctx.Kb;

        string? parent = Path.GetDirectoryName(e.Path.TrimEnd('\\'));
        facts.Add(("Location", parent is null ? null : ExplanationPrompt.MaskUserName(parent)));

        if (e.IsDirectory)
        {
            facts.Add(("Contains", $"{Display.Count(e.FileCount, "file", "files")} in {Display.Count(e.DirectoryCount, "folder", "folders")}, {Display.Size(e.TotalSize)} in total"));
            var children = ctx.Db.GetChildren(e.Id, 10);
            if (children.Count > 0)
                facts.Add(("Biggest items inside", string.Join(", ", children.Select(c => $"{c.Name}{(c.IsDirectory ? "\\" : "")} ({Display.Size(c.TotalSize)}{(c.IsDirectory ? "" : TypeSuffix(c))})"))));
            if (a?.ContainedLastModifiedUtc is { } changed) facts.Add(("Last change inside", Display.Ago(changed)));
            if (a?.ContainedLastUsedUtc is { } used) facts.Add(("Last time anything inside was opened", Display.Ago(used)));
        }
        else
        {
            facts.Add(("Type", a?.TypeLabel ?? (e.Extension is { } x ? $"{x.TrimStart('.').ToUpperInvariant()} file" : "File without an extension")));
            if (a?.ExtensionMismatch == true) facts.Add(("Note", "the content doesn't match the file name's extension"));
            if (e.Extension is { } ext && ctx.Db.GetExtensionStat(ext) is { } stat)
            {
                if (stat.WindowsTypeName is { } wn && !string.Equals(wn, a?.TypeLabel, StringComparison.OrdinalIgnoreCase)) facts.Add(("Windows calls this type", wn));
                if (stat.OpensWith is { } app) facts.Add(("Opens with", app));
                if (stat.Files > 1) facts.Add(("Files of this type on the drive", $"{stat.Files:N0}"));
            }
            facts.Add(("Size", Display.Size(e.Size)));
            if (a is not null && ctx.Db.GetPeInfo(e.Id) is { } pe)
            {
                facts.Add(("Program description", pe.FileDescription));
                facts.Add(("Product", pe.ProductName));
                facts.Add(("Company", pe.CompanyName));
                facts.Add(("Version", pe.FileVersion ?? pe.ProductVersion));
                if (pe.OriginalFilename is { } orig && !string.Equals(orig, e.Name, StringComparison.OrdinalIgnoreCase)) facts.Add(("Original file name", orig));
                facts.Add(("Digital signature", pe.SignedBy is { } by ? $"signed by {by} ({pe.SignatureState})" : pe.SignatureState.ToString()));
            }
            if (a?.LastUsedUtc is { } used) facts.Add(("Last opened", Display.Ago(used)));
            if (a?.UseCount > 0) facts.Add(("Times opened", a.UseCount.ToString("N0")));
        }

        facts.Add(("Created", Display.Ago(e.CreatedUtc)));
        facts.Add(("Last modified", Display.Ago(e.ModifiedUtc)));
        string attrs = AttributeWords(e.Attributes);
        if (attrs.Length > 0) facts.Add(("Attributes", attrs));

        if (a?.OwnerAppName is { } owner) facts.Add(("Belongs to installed program", owner));
        if (kb.GetRule(a?.LocationRuleId) is { } rule) facts.Add(("Known location", $"{rule.Name}: {rule.Description}"));
        if (a?.Category is { } cat && cat != "other" && kb.GetCategory(cat) is { } category) facts.Add(("Kind of file", category.Name));

        var refs = ctx.Db.GetReferences(e.Id);
        if (refs.Count > 0) facts.Add(("Used by Windows as", string.Join("; ", refs.Take(4).Select(r => $"{Display.ReferenceKindName(r.Kind)}: {r.Detail}"))));

        if (e.ParentId is long parentId && parentId > 0)
        {
            var siblings = ctx.Db.GetChildren(parentId, 9).Where(s => s.Id != e.Id).Take(8).ToList();
            if (siblings.Count > 0) facts.Add(("Other items in the same folder", string.Join(", ", siblings.Select(s => s.Name + (s.IsDirectory ? "\\" : "")))));
        }

        return ExplainFacts.Create(e.Depth == 0 ? e.Path : e.Name, e.IsDirectory, facts);
    }

    private static string TypeSuffix(FileSystemEntry c) => c.Analysis?.TypeLabel is { } t ? ", " + t : "";

    private static string AttributeWords(FileAttributes a)
    {
        var words = new List<string>();
        if (a.HasFlag(FileAttributes.Hidden)) words.Add("hidden");
        if (a.HasFlag(FileAttributes.System)) words.Add("system");
        if (a.HasFlag(FileAttributes.ReadOnly)) words.Add("read-only");
        if (a.HasFlag(FileAttributes.Temporary)) words.Add("temporary");
        if (a.HasFlag(FileAttributes.Encrypted)) words.Add("encrypted");
        return string.Join(", ", words);
    }
}
