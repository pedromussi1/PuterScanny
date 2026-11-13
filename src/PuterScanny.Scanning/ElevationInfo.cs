using System.Security.Principal;

namespace PuterScanny.Scanning;

public static class ElevationInfo
{
    /// <summary>True when this process is running with administrator rights.</summary>
    public static bool IsProcessElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
