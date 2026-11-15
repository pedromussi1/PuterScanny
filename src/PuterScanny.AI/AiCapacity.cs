using System.Runtime.InteropServices;

namespace PuterScanny.AI;

/// <summary>How much the local AI can be used on this PC, right now.</summary>
public enum AiTier
{
    /// <summary>Not set up, or turned off.</summary>
    Off,

    /// <summary>Set up, but this PC can't run it well at the moment (too little free memory, or too slow).</summary>
    CantRun,

    /// <summary>Slow: only to understand questions the app can't (short, structured replies).</summary>
    Slow,

    /// <summary>Fast enough for short written summaries too.</summary>
    Fast,
}

/// <summary>What the PC has and what that allows, with the reason in plain words.</summary>
public sealed record AiCapacity(
    AiTier Tier,
    bool Summaries,
    string Reason,
    long TotalMemory,
    long FreeMemory,
    long NeededMemory,
    bool Avx2,
    int Threads,
    bool OnBattery,
    double? TokensPerSecond)
{
    /// <summary>For tests and fakes: everything allowed.</summary>
    public static AiCapacity Unlimited { get; } = new(AiTier.Fast, true, "", 0, 0, 0, true, 0, false, null);

    public static AiCapacity Unavailable(string reason) => new(AiTier.Off, false, reason, 0, 0, 0, false, 0, false, null);

    public bool CanInterpret => Tier is AiTier.Slow or AiTier.Fast;
}

/// <summary>Reads this PC's memory, processor and power state (Windows APIs, nothing leaves the PC).</summary>
public static class PcPower
{
    /// <summary>Speeds (words, roughly tokens, per second, including reading the prompt) that decide how the AI is used.</summary>
    public const double FastTokensPerSecond = 7;
    public const double SlowestUsefulTokensPerSecond = 2;

    /// <summary>Memory the model needs beyond its file size (working memory for a 4,096-token context).</summary>
    public const long WorkingMemory = 1L << 30;

    public static (long Total, long Free) Memory()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? ((long)status.TotalPhys, (long)status.AvailPhys) : (0, 0);
    }

    public static bool OnBattery()
    {
        // ACLineStatus: 0 = on battery, 1 = plugged in, 255 = unknown (desktops report 1 or 255).
        return GetSystemPowerStatus(out var s) && s.ACLineStatus == 0;
    }

    public static bool Avx2 => System.Runtime.Intrinsics.X86.Avx2.IsSupported;

    /// <summary>
    /// Decides how the AI can be used now. <paramref name="measured"/> is the speed seen in real answers on this PC
    /// (null until the first one); before that, the processor decides.
    /// </summary>
    public static AiCapacity Assess(long modelBytes, bool modelLoaded, double? measured, int threads)
    {
        var (total, free) = Memory();
        return Assess(modelBytes, modelLoaded, measured, threads, total, free, OnBattery(), Avx2);
    }

    /// <summary>The same decision, from given readings (for tests).</summary>
    public static AiCapacity Assess(long modelBytes, bool modelLoaded, double? measured, int threads, long total, long free, bool battery, bool avx2)
    {
        long needed = modelBytes + WorkingMemory;
        static string Gb(long b) => (b / (1024.0 * 1024 * 1024)).ToString("0.#", System.Globalization.CultureInfo.CurrentCulture) + " GB";

        AiTier tier;
        string reason;
        if (!modelLoaded && total > 0 && free < needed)
        {
            tier = AiTier.CantRun;
            reason = $"only {Gb(free)} of memory is free right now, and the model needs about {Gb(needed)}";
        }
        else if (measured is double tps)
        {
            tier = tps < SlowestUsefulTokensPerSecond ? AiTier.CantRun : tps < FastTokensPerSecond ? AiTier.Slow : AiTier.Fast;
            reason = tier switch
            {
                AiTier.CantRun => $"it runs too slowly on this PC (about {tps:0.#} words a second)",
                AiTier.Slow => $"it runs slowly on this PC (about {tps:0.#} words a second), so it's only used to understand questions",
                _ => $"it runs well on this PC (about {tps:0.#} words a second)",
            };
        }
        else
        {
            // Not measured yet: a modern processor with enough threads is usually fast enough.
            tier = avx2 && threads >= 4 ? AiTier.Fast : AiTier.Slow;
            reason = tier == AiTier.Fast
                ? "this PC's processor should run it well (its speed is measured on first use)"
                : "this PC's processor may run it slowly, so it's only used to understand questions until its speed is measured";
        }
        bool summaries = tier == AiTier.Fast && !battery;
        if (tier == AiTier.Fast && battery) reason += "; summaries are skipped while on battery";
        return new AiCapacity(tier, summaries, reason, total, free, needed, avx2, threads, battery, measured);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

#pragma warning disable SYSLIB1054 // plain P/Invoke keeps this project free of unsafe code
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
#pragma warning restore SYSLIB1054
}
