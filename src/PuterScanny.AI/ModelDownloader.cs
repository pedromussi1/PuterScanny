using System.Net;
using System.Security.Cryptography;

namespace PuterScanny.AI;

public enum ModelState
{
    NotDownloaded,

    /// <summary>A download was started and can be resumed.</summary>
    Partial,

    /// <summary>The file is there but hasn't been checked against its SHA-256 yet.</summary>
    NeedsCheck,

    Ready,
}

public readonly record struct DownloadProgress(string Stage, long Done, long Total, double BytesPerSecond)
{
    public double Percent => Total > 0 ? 100.0 * Done / Total : 0;
}

/// <summary>
/// Downloads a catalog model once, from its fixed address, with resume. The file is only used after its size and
/// SHA-256 match the catalog, so a damaged, incomplete or tampered file is never loaded.
/// </summary>
public static class ModelDownloader
{
    public static string FinalPath(ModelInfo m, string folder) => Path.Combine(folder, m.FileName);
    private static string PartPath(ModelInfo m, string folder) => FinalPath(m, folder) + ".part";
    private static string MarkerPath(ModelInfo m, string folder) => FinalPath(m, folder) + ".verified";

    public static ModelState GetState(ModelInfo m, string folder)
    {
        var final = new FileInfo(FinalPath(m, folder));
        if (final.Exists && final.Length == m.SizeBytes)
        {
            try
            {
                string marker = MarkerPath(m, folder);
                if (File.Exists(marker) && File.ReadAllText(marker).Trim().Equals(m.Sha256, StringComparison.OrdinalIgnoreCase))
                    return ModelState.Ready;
            }
            catch (IOException) { }
            return ModelState.NeedsCheck;
        }
        var part = new FileInfo(PartPath(m, folder));
        return part.Exists && part.Length > 0 ? ModelState.Partial : ModelState.NotDownloaded;
    }

    public static long PartialBytes(ModelInfo m, string folder)
    {
        var part = new FileInfo(PartPath(m, folder));
        return part.Exists ? part.Length : 0;
    }

    /// <summary>Downloads (or resumes) and checks the model. Returns the path of the ready file.</summary>
    public static async Task<string> DownloadAsync(ModelInfo m, string folder, IProgress<DownloadProgress>? progress, CancellationToken ct,
        HttpMessageHandler? handler = null)
    {
        Directory.CreateDirectory(folder);
        string final = FinalPath(m, folder), part = PartPath(m, folder);

        switch (GetState(m, folder))
        {
            case ModelState.Ready:
                return final;
            case ModelState.NeedsCheck:
                // Already fully there (for example copied in by hand): just check it.
                await VerifyFileAsync(m, final, progress, ct);
                File.WriteAllText(MarkerPath(m, folder), m.Sha256);
                return final;
        }

        long have = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (have > m.SizeBytes) { File.Delete(part); have = 0; }

        if (have < m.SizeBytes)
        {
            long needed = m.SizeBytes - have;
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!);
                if (drive.AvailableFreeSpace < needed + (512L << 20))
                    throw new IOException($"Not enough free space on {drive.Name.TrimEnd('\\')}: the model needs {needed / (1 << 20):N0} MB more, plus some room to spare.");
            }
            catch (ArgumentException) { }

            using var http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = true, AutomaticDecompression = DecompressionMethods.None }, disposeHandler: handler is null)
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("PuterScanny/0.1");

            using var request = new HttpRequestMessage(HttpMethod.Get, m.Url);
            if (have > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (have > 0 && response.StatusCode != HttpStatusCode.PartialContent)
                have = 0; // the server sent the whole file again: start over
            response.EnsureSuccessStatusCode();

            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var target = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
            {
                var buffer = new byte[1 << 20];
                long done = have, lastReport = 0;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                long startBytes = done;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    if (done + read > m.SizeBytes) throw new InvalidDataException("The server sent more data than expected. The download was stopped.");
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    if (clock.ElapsedMilliseconds - lastReport >= 250 || done == m.SizeBytes)
                    {
                        lastReport = clock.ElapsedMilliseconds;
                        double speed = clock.Elapsed.TotalSeconds > 0 ? (done - startBytes) / clock.Elapsed.TotalSeconds : 0;
                        progress?.Report(new DownloadProgress("Downloading", done, m.SizeBytes, speed));
                    }
                }
            }
        }

        long got = new FileInfo(part).Length;
        if (got != m.SizeBytes)
            throw new IOException($"The download stopped early ({got / (1 << 20):N0} of {m.SizeBytes / (1 << 20):N0} MB). Try again to continue where it left off.");

        try
        {
            await VerifyFileAsync(m, part, progress, ct);
        }
        catch (InvalidDataException)
        {
            File.Delete(part); // damaged: never keep or use it
            throw;
        }
        File.Move(part, final, overwrite: true);
        File.WriteAllText(MarkerPath(m, folder), m.Sha256);
        return final;
    }

    /// <summary>Throws <see cref="InvalidDataException"/> when the file's SHA-256 isn't the catalog's.</summary>
    public static async Task VerifyFileAsync(ModelInfo m, string path, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        string actual = await Sha256Async(path, m.SizeBytes, progress, ct);
        if (!actual.Equals(m.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The model file is damaged or isn't the expected file (its fingerprint doesn't match), so it won't be used. Download it again.");
    }

    public static async Task<string> Sha256Async(string path, long expectedSize, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        var buffer = new byte[4 << 20];
        long done = 0, lastReport = -1;
        int read;
        while ((read = await fs.ReadAsync(buffer, ct)) > 0)
        {
            sha.AppendData(buffer, 0, read);
            done += read;
            if (done - lastReport >= 64 << 20) { lastReport = done; progress?.Report(new DownloadProgress("Checking", done, Math.Max(expectedSize, done), 0)); }
        }
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    /// <summary>Removes a downloaded model (and any half-finished download) to free the space.</summary>
    public static void Delete(ModelInfo m, string folder)
    {
        foreach (string p in new[] { FinalPath(m, folder), PartPath(m, folder), MarkerPath(m, folder) })
            if (File.Exists(p)) File.Delete(p);
    }
}
