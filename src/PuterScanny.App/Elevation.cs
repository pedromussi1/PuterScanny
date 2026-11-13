using System.ComponentModel;
using System.Diagnostics;

namespace PuterScanny.App;

/// <summary>Restarting PuterScanny as administrator for a Deep scan, and reading the scan request it was started with.</summary>
public static class Elevation
{
    public enum Result { Started, Declined, Failed }

    public sealed record Request(string Path, bool Deep, bool LookInside, bool Gentle);

    /// <summary>Asks Windows (the usual permission prompt) to start PuterScanny as administrator, continuing with this scan.</summary>
    public static Result TryRelaunchElevated(string path, bool lookInside, bool gentle)
    {
        string? exe = Environment.ProcessPath;
        if (exe is null) return Result.Failed;
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            Verb = "runas",
            Arguments = $"--scan \"{path.TrimEnd('\\')}\" --deep --look-inside {(lookInside ? 1 : 0)} --gentle {(gentle ? 1 : 0)}",
        };
        try
        {
            Process.Start(info);
            return Result.Started;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // the user said No to the permission prompt
        {
            return Result.Declined;
        }
        catch (Win32Exception)
        {
            return Result.Failed;
        }
    }

    /// <summary>"--scan &lt;path&gt; [--deep] [--look-inside 0|1] [--gentle 0|1]"</summary>
    public static Request? Parse(IReadOnlyList<string> args)
    {
        string? path = null;
        bool deep = false, look = true, gentle = true;
        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--scan" when i + 1 < args.Count: path = args[++i]; break;
                case "--deep": deep = true; break;
                case "--look-inside" when i + 1 < args.Count: look = args[++i] != "0"; break;
                case "--gentle" when i + 1 < args.Count: gentle = args[++i] != "0"; break;
            }
        }
        if (path is null) return null;
        if (path.Length == 2 && path[1] == ':') path += "\\";
        return new Request(path, deep, look, gentle);
    }
}
