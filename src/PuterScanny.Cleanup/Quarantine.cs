namespace PuterScanny.Cleanup;

/// <summary>
/// PuterScanny's safe-keeping area. Items are moved (not copied) into a folder on the same drive, so it's instant and
/// nothing is lost; they can be put back at any time until the quarantine is emptied.
/// </summary>
public sealed class Quarantine
{
    public const string FolderName = "PuterScanny Quarantine";

    private readonly string _localRoot;

    /// <param name="localRoot">Quarantine for the drive holding the user's profile (defaults to %LOCALAPPDATA%\PuterScanny\Quarantine).</param>
    public Quarantine(string? localRoot = null)
    {
        _localRoot = localRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PuterScanny", "Quarantine");
    }

    /// <summary>The quarantine folder for the drive <paramref name="path"/> is on (a move within one drive never copies data).</summary>
    public string RootFor(string path)
    {
        string? drive = Path.GetPathRoot(path);
        string? localDrive = Path.GetPathRoot(_localRoot);
        if (drive is null || string.Equals(drive, localDrive, StringComparison.OrdinalIgnoreCase)) return _localRoot;
        return Path.Combine(drive, FolderName);
    }

    /// <summary>All quarantine roots that exist (one per drive used).</summary>
    public IEnumerable<string> Roots()
    {
        if (Directory.Exists(_localRoot)) yield return _localRoot;
        foreach (var d in DriveInfo.GetDrives())
        {
            if (!d.IsReady) continue;
            string root = Path.Combine(d.Name, FolderName);
            if (Directory.Exists(root)) yield return root;
        }
    }

    /// <summary>Moves an item into quarantine; returns where it went.</summary>
    public string Move(string path, string batchId, int index)
    {
        string root = RootFor(path);
        bool created = !Directory.Exists(root);
        string slot = Path.Combine(root, batchId, index.ToString("D5"));
        Directory.CreateDirectory(slot);
        if (created) WriteReadme(root);

        string target = Path.Combine(slot, Path.GetFileName(path.TrimEnd('\\')));
        if (Directory.Exists(path)) Directory.Move(path, target);
        else File.Move(path, target);
        return target;
    }

    /// <summary>Puts an item back. Never overwrites: if something new now has the name, it's restored next to it.</summary>
    public (bool Ok, string Message) Restore(string storedAt, string originalPath)
    {
        if (!File.Exists(storedAt) && !Directory.Exists(storedAt))
            return (false, "It's no longer in quarantine (it may have been emptied or restored already).");
        string target = RecycleBin.FreeTarget(originalPath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (Directory.Exists(storedAt)) Directory.Move(storedAt, target);
        else File.Move(storedAt, target);
        TryRemoveEmptyParents(Path.GetDirectoryName(storedAt));
        return (true, target == originalPath ? "Restored." : $"Restored as \"{Path.GetFileName(target)}\" because a new item now has the old name.");
    }

    /// <summary>Deletes a quarantined item for good (frees its space). Only ever touches paths inside a quarantine folder.</summary>
    public void Purge(string storedAt)
    {
        if (!IsInsideQuarantine(storedAt)) throw new InvalidOperationException($"Refusing to delete \"{storedAt}\": it is not inside a PuterScanny quarantine folder.");
        if (Directory.Exists(storedAt))
        {
            foreach (var f in new DirectoryInfo(storedAt).EnumerateFiles("*", SearchOption.AllDirectories))
                if (f.IsReadOnly) f.IsReadOnly = false;
            Directory.Delete(storedAt, recursive: true);
        }
        else if (File.Exists(storedAt))
        {
            File.SetAttributes(storedAt, FileAttributes.Normal);
            File.Delete(storedAt);
        }
        TryRemoveEmptyParents(Path.GetDirectoryName(storedAt));
    }

    public bool IsInsideQuarantine(string path)
    {
        string full = Path.GetFullPath(path);
        string local = Path.GetFullPath(_localRoot).TrimEnd('\\') + "\\";
        if (full.StartsWith(local, StringComparison.OrdinalIgnoreCase)) return true;
        string? drive = Path.GetPathRoot(full);
        return drive is not null && full.StartsWith(Path.Combine(drive, FolderName) + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private void TryRemoveEmptyParents(string? dir)
    {
        // Remove the now-empty slot and batch folders, never the quarantine root itself.
        for (int i = 0; i < 2 && dir is not null; i++)
        {
            try
            {
                if (!IsInsideQuarantine(dir) || Directory.EnumerateFileSystemEntries(dir).Any()) return;
                Directory.Delete(dir);
            }
            catch (IOException) { return; }
            catch (UnauthorizedAccessException) { return; }
            dir = Path.GetDirectoryName(dir);
        }
    }

    private static void WriteReadme(string root)
    {
        try
        {
            File.WriteAllText(Path.Combine(root, "README.txt"),
                "This folder is PuterScanny's quarantine: items you cleaned up are kept here so you can undo the clean-up.\r\n" +
                "Restore or permanently delete them from PuterScanny (Undo & quarantine tab). Please don't change files here by hand.\r\n");
        }
        catch (IOException) { }
    }
}
