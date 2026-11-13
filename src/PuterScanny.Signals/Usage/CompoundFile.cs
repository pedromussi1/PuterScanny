using System.Buffers.Binary;
using System.Text;

namespace PuterScanny.Signals.Usage;

/// <summary>
/// Minimal read-only reader for OLE compound files (the format of jump lists, old Office files, MSI…).
/// Loads the whole file into memory, so it is meant for small files.
/// </summary>
public sealed class CompoundFile
{
    private const uint EndOfChain = 0xFFFFFFFE;
    private const int MaxChain = 1_000_000;

    private readonly byte[] _data;
    private readonly int _sectorSize;
    private readonly int _miniSectorSize;
    private readonly uint _miniCutoff;
    private readonly uint[] _fat;
    private readonly uint[] _miniFat;
    private readonly byte[] _miniStream;
    private readonly List<(string Name, int Type, uint Start, long Size)> _entries = [];

    private CompoundFile(byte[] data)
    {
        _data = data;
        var h = data.AsSpan();
        _sectorSize = 1 << BinaryPrimitives.ReadUInt16LittleEndian(h[0x1E..]);
        _miniSectorSize = 1 << BinaryPrimitives.ReadUInt16LittleEndian(h[0x20..]);
        uint firstDir = BinaryPrimitives.ReadUInt32LittleEndian(h[0x30..]);
        _miniCutoff = BinaryPrimitives.ReadUInt32LittleEndian(h[0x38..]);
        uint firstMiniFat = BinaryPrimitives.ReadUInt32LittleEndian(h[0x3C..]);
        uint firstDifat = BinaryPrimitives.ReadUInt32LittleEndian(h[0x44..]);
        int difatCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(h[0x48..]);

        // DIFAT: the first 109 FAT sector numbers are in the header, the rest in a chain of DIFAT sectors.
        var fatSectors = new List<uint>();
        for (int i = 0; i < 109; i++)
        {
            uint s = BinaryPrimitives.ReadUInt32LittleEndian(h[(0x4C + i * 4)..]);
            if (s < 0xFFFFFFFA) fatSectors.Add(s);
        }
        uint difat = firstDifat;
        for (int n = 0; n < difatCount && difat < 0xFFFFFFFA; n++)
        {
            var sec = Sector(difat);
            for (int i = 0; i < _sectorSize / 4 - 1; i++)
            {
                uint s = BinaryPrimitives.ReadUInt32LittleEndian(sec[(i * 4)..]);
                if (s < 0xFFFFFFFA) fatSectors.Add(s);
            }
            difat = BinaryPrimitives.ReadUInt32LittleEndian(sec[(_sectorSize - 4)..]);
        }

        var fat = new List<uint>();
        foreach (uint s in fatSectors)
        {
            var sec = Sector(s);
            for (int i = 0; i < _sectorSize / 4; i++) fat.Add(BinaryPrimitives.ReadUInt32LittleEndian(sec[(i * 4)..]));
        }
        _fat = [.. fat];

        byte[] dir = ReadChain(firstDir, long.MaxValue, mini: false);
        for (int i = 0; i + 128 <= dir.Length; i += 128)
        {
            int nameLen = BinaryPrimitives.ReadUInt16LittleEndian(dir.AsSpan(i + 64));
            int type = dir[i + 66];
            if (type == 0 || nameLen < 2 || nameLen > 64) continue;
            string name = Encoding.Unicode.GetString(dir, i, nameLen - 2);
            uint start = BinaryPrimitives.ReadUInt32LittleEndian(dir.AsSpan(i + 116));
            long size = (long)BinaryPrimitives.ReadUInt64LittleEndian(dir.AsSpan(i + 120));
            if (_sectorSize == 512) size &= 0xFFFFFFFF; // version 3 files only use the low 32 bits
            _entries.Add((name, type, start, size));
        }

        var root = _entries.FirstOrDefault(e => e.Type == 5);
        _miniStream = root.Name is null ? [] : ReadChain(root.Start, root.Size, mini: false);
        _miniFat = BytesToUInts(firstMiniFat < 0xFFFFFFFA ? ReadChain(firstMiniFat, long.MaxValue, mini: false) : []);
    }

    public static CompoundFile? TryOpen(byte[] data)
    {
        if (data.Length < 512 || BinaryPrimitives.ReadUInt64LittleEndian(data) != 0xE11AB1A1E011CFD0) return null;
        try { return new CompoundFile(data); }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or InvalidDataException) { return null; }
    }

    public IEnumerable<string> StreamNames => _entries.Where(e => e.Type == 2).Select(e => e.Name);

    public byte[]? ReadStream(string name)
    {
        var e = _entries.FirstOrDefault(x => x.Type == 2 && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (e.Name is null) return null;
        try { return ReadChain(e.Start, e.Size, mini: e.Size < _miniCutoff); }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or InvalidDataException) { return null; }
    }

    private ReadOnlySpan<byte> Sector(uint n)
    {
        long offset = (n + 1L) * _sectorSize;
        if (offset >= _data.Length) throw new InvalidDataException("Sector outside file.");
        if (offset + _sectorSize <= _data.Length) return _data.AsSpan((int)offset, _sectorSize);

        // Windows trims the file after the last stream's data, so the final sector may be short: pad it.
        var padded = new byte[_sectorSize];
        _data.AsSpan((int)offset).CopyTo(padded);
        return padded;
    }

    private byte[] ReadChain(uint start, long size, bool mini)
    {
        var table = mini ? _miniFat : _fat;
        int unit = mini ? _miniSectorSize : _sectorSize;
        using var ms = new MemoryStream();
        uint s = start;
        for (int n = 0; s < 0xFFFFFFFA && s != EndOfChain && n < MaxChain && ms.Length < size; n++)
        {
            if (mini)
            {
                long off = (long)s * unit;
                if (off + unit > _miniStream.Length) throw new InvalidDataException("Mini sector outside stream.");
                ms.Write(_miniStream, (int)off, unit);
            }
            else
            {
                ms.Write(Sector(s));
            }
            if (s >= table.Length) break;
            s = table[s];
        }
        var bytes = ms.ToArray();
        return size < bytes.Length ? bytes[..(int)size] : bytes;
    }

    private static uint[] BytesToUInts(byte[] b)
    {
        var r = new uint[b.Length / 4];
        for (int i = 0; i < r.Length; i++) r[i] = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(i * 4));
        return r;
    }
}
