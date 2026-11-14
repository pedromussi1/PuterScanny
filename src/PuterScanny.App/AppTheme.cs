using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;

namespace PuterScanny.App;

/// <summary>
/// Light or dark look. Until the user picks one with the toggle, PuterScanny follows Windows' own setting.
/// The choice is saved in %LOCALAPPDATA%\PuterScanny\app-settings.json.
/// </summary>
public static class AppTheme
{
#pragma warning disable WPF0001 // the built-in Fluent theme (ThemeMode) is still marked experimental
    private sealed record Saved(string? Theme);

    private static string SettingsPath =>
        Path.Combine(Environment.GetEnvironmentVariable("PUTERSCANNY_SETTINGS_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PuterScanny"), "app-settings.json");

    /// <summary>"light", "dark", or null to follow Windows.</summary>
    public static string? Choice { get; private set; }

    public static event EventHandler? Changed;

    public static bool IsDark => Choice switch
    {
        "dark" => true,
        "light" => false,
        _ => WindowsUsesDarkApps(),
    };

    /// <summary>Applies the saved choice (at startup).</summary>
    public static void Load()
    {
        try
        {
            if (File.Exists(SettingsPath) && JsonSerializer.Deserialize<Saved>(File.ReadAllText(SettingsPath)) is { Theme: "light" or "dark" } saved)
                Choice = saved.Theme;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Choice = null;
        }
        Apply();
    }

    /// <summary>Switches to dark (true) or light (false) and remembers it.</summary>
    public static void Set(bool dark)
    {
        Choice = dark ? "dark" : "light";
        Apply();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new Saved(Choice)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not saved: it still applies until PuterScanny closes.
        }
    }

    private static void Apply()
    {
        if (Application.Current is not { } app) return;
        app.ThemeMode = Choice switch
        {
            "dark" => ThemeMode.Dark,
            "light" => ThemeMode.Light,
            _ => ThemeMode.System,
        };
        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static bool WindowsUsesDarkApps()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
#pragma warning restore WPF0001
}
