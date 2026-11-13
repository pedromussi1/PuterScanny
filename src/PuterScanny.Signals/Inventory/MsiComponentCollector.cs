using static PuterScanny.Signals.Native.Msi;

namespace PuterScanny.Signals.Inventory;

/// <summary>
/// Asks Windows Installer which exact files each MSI-installed product registered as its own. This catches
/// files a program placed outside its main folder (System32, Common Files, …).
/// </summary>
public static class MsiComponentCollector
{
    /// <summary>Returns file path → every product code (GUID with braces, upper case) that registered it.</summary>
    public static Dictionary<string, List<string>> Collect(CancellationToken ct, IProgress<int>? componentsDone = null)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        char[] component = new char[39];
        char[] product = new char[39];
        char[] path = new char[1024];

        for (uint ci = 0; ; ci++)
        {
            if ((ci & 1023) == 0)
            {
                ct.ThrowIfCancellationRequested();
                componentsDone?.Report((int)ci);
            }

            int rc;
            try { rc = MsiEnumComponents(ci, component); }
            catch (DllNotFoundException) { return result; }
            if (rc != ERROR_SUCCESS) break;
            string componentId = new string(component).TrimEnd('\0');

            for (uint pi = 0; ; pi++)
            {
                if (MsiEnumClients(componentId, pi, product) != ERROR_SUCCESS) break;
                string productCode = new string(product).TrimEnd('\0');

                uint len = (uint)path.Length;
                int state = MsiGetComponentPath(productCode, componentId, path, ref len);
                if (state is not (INSTALLSTATE_LOCAL or INSTALLSTATE_SOURCE or INSTALLSTATE_DEFAULT)) continue;
                if (len == 0 || len >= path.Length) continue;

                string keyPath = new string(path, 0, (int)len);
                // Registry key paths look like "02:\SOFTWARE\..."; only files and folders matter here.
                if (keyPath.Length < 4 || keyPath[1] != ':' || keyPath[2] != '\\' || !char.IsLetter(keyPath[0])) continue;
                keyPath = keyPath.TrimEnd('\\');
                if (!result.TryGetValue(keyPath, out var products)) result[keyPath] = products = [];
                string code = productCode.ToUpperInvariant();
                if (!products.Contains(code)) products.Add(code);
            }
        }
        return result;
    }
}
