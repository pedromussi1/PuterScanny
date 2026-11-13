using System.Buffers.Binary;
using System.Text;

namespace PuterScanny.Scanning.Ntfs;

/// <summary>One name of a file (a file with hard links has several).</summary>
public readonly record struct MftName(ulong ParentRecord, string Name, byte Namespace);

/// <summary>A named data stream (alternate data stream) of a file.</summary>
public readonly record struct MftStream(string Name, long Size, long Allocated);

/// <summary>What one FILE record (or an extension record of it) says about a file or folder.</summary>
public sealed class MftRecordInfo
{
    public ulong RecordNumber;
    public ulong BaseRecord;       // 0 = this is the base record; otherwise attributes belong to that record
    public bool InUse;
    public bool IsDirectory;
    public uint Attributes;
    public long Created, Modified, Accessed; // FILETIME ticks
    public bool HasStandardInfo;
    public List<MftName> Names = [];
    public long Size = -1;         // unnamed $DATA real size (-1 = not in this record)
    public long Allocated = -1;
    public List<MftStream> Streams = [];
    public uint ReparseTag;
    public List<(long Lcn, long Clusters)>? DataRuns; // unnamed $DATA runs (used to read $MFT itself)

    /// <summary>Clusters really stored on disk for the unnamed data in this record (sparse holes excluded).</summary>
    public long DataClusters;

    /// <summary>Clusters really stored on disk per named stream in this record.</summary>
    public Dictionary<string, long>? StreamClusters;
}

/// <summary>Parses NTFS master file table records (FILE records), including the multi-sector fix-ups.</summary>
public static class MftRecordParser
{
    public const uint AttrStandardInformation = 0x10;
    public const uint AttrFileName = 0x30;
    public const uint AttrData = 0x80;
    public const uint AttrReparsePoint = 0xC0;
    public const uint AttrEnd = 0xFFFFFFFF;

    /// <summary>
    /// Applies the update-sequence fix-ups in place (NTFS replaces the last two bytes of every sector with a check
    /// value on disk). Returns false when the record is torn or not a FILE record.
    /// </summary>
    public static bool ApplyFixups(Span<byte> record, int bytesPerSector)
    {
        if (record.Length < 48 || record[0] != (byte)'F' || record[1] != (byte)'I' || record[2] != (byte)'L' || record[3] != (byte)'E') return false;
        int usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
        int usaCount = BinaryPrimitives.ReadUInt16LittleEndian(record[6..]);
        if (usaCount < 2 || usaOffset + usaCount * 2 > record.Length) return false;
        ushort usn = BinaryPrimitives.ReadUInt16LittleEndian(record[usaOffset..]);
        for (int i = 1; i < usaCount; i++)
        {
            int end = i * bytesPerSector - 2;
            if (end + 2 > record.Length) break;
            if (BinaryPrimitives.ReadUInt16LittleEndian(record[end..]) != usn) return false; // torn write
            record.Slice(usaOffset + i * 2, 2).CopyTo(record.Slice(end, 2));
        }
        return true;
    }

    /// <summary>Parses a fixed-up FILE record. Returns null for records that aren't valid FILE records.</summary>
    public static MftRecordInfo? Parse(ReadOnlySpan<byte> r, ulong recordNumber)
    {
        if (r.Length < 48 || r[0] != (byte)'F' || r[1] != (byte)'I' || r[2] != (byte)'L' || r[3] != (byte)'E') return null;
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(r[0x16..]);
        var info = new MftRecordInfo
        {
            RecordNumber = recordNumber,
            InUse = (flags & 0x01) != 0,
            IsDirectory = (flags & 0x02) != 0,
            BaseRecord = BinaryPrimitives.ReadUInt64LittleEndian(r[0x20..]) & 0x0000FFFFFFFFFFFF,
        };
        if (!info.InUse) return info;

        int pos = BinaryPrimitives.ReadUInt16LittleEndian(r[0x14..]);
        int used = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(r[0x18..]), (uint)r.Length);
        int guard = 0;
        while (pos + 16 <= used && guard++ < 512)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(r[pos..]);
            if (type == AttrEnd) break;
            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(r[(pos + 4)..]);
            if (length < 16 || pos + length > used) break;
            var a = r.Slice(pos, length);
            bool nonResident = a[8] != 0;
            int nameLength = a[9];
            int nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(a[10..]);
            ushort attrFlags = BinaryPrimitives.ReadUInt16LittleEndian(a[12..]);
            string attrName = nameLength > 0 && nameOffset + nameLength * 2 <= length
                ? Encoding.Unicode.GetString(a.Slice(nameOffset, nameLength * 2)) : "";

            if (!nonResident)
            {
                int valueLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(a[16..]);
                int valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(a[20..]);
                if (valueOffset + valueLength > length) { pos += length; continue; }
                var v = a.Slice(valueOffset, valueLength);
                switch (type)
                {
                    case AttrStandardInformation when v.Length >= 36:
                        info.Created = BinaryPrimitives.ReadInt64LittleEndian(v);
                        info.Modified = BinaryPrimitives.ReadInt64LittleEndian(v[8..]);
                        info.Accessed = BinaryPrimitives.ReadInt64LittleEndian(v[24..]);
                        info.Attributes = BinaryPrimitives.ReadUInt32LittleEndian(v[32..]);
                        info.HasStandardInfo = true;
                        break;
                    case AttrFileName when v.Length >= 66:
                        int chars = v[64];
                        if (66 + chars * 2 <= v.Length)
                            info.Names.Add(new MftName(BinaryPrimitives.ReadUInt64LittleEndian(v) & 0x0000FFFFFFFFFFFF,
                                Encoding.Unicode.GetString(v.Slice(66, chars * 2)), v[65]));
                        break;
                    case AttrData:
                        if (attrName.Length == 0) { info.Size = valueLength; info.Allocated = 0; } // resident: stored inside the record
                        else info.Streams.Add(new MftStream(attrName, valueLength, 0));
                        break;
                    case AttrReparsePoint when v.Length >= 4:
                        info.ReparseTag = BinaryPrimitives.ReadUInt32LittleEndian(v);
                        break;
                }
            }
            else if (type == AttrData && length >= 64)
            {
                long lowestVcn = BinaryPrimitives.ReadInt64LittleEndian(a[16..]);
                int runsOffset = BinaryPrimitives.ReadUInt16LittleEndian(a[32..]);
                long allocated = BinaryPrimitives.ReadInt64LittleEndian(a[40..]);
                long real = BinaryPrimitives.ReadInt64LittleEndian(a[48..]);
                // Compressed or sparse data: the space really used is in "total allocated".
                if ((attrFlags & 0x8001) != 0 && length >= 72) allocated = BinaryPrimitives.ReadInt64LittleEndian(a[64..]);

                var runs = runsOffset < length ? DecodeRuns(a[runsOffset..]) : [];
                // Real space used = clusters actually stored. "Allocated" counts sparse holes too, which is how
                // NTFS's $BadClus can claim to be as big as the whole drive while using nothing.
                long stored = 0;
                foreach (var (lcnStart, clusters) in runs) if (lcnStart >= 0) stored += clusters;
                if (attrName.Length == 0) info.DataClusters += stored;
                else
                {
                    info.StreamClusters ??= new Dictionary<string, long>(StringComparer.Ordinal);
                    info.StreamClusters[attrName] = info.StreamClusters.GetValueOrDefault(attrName) + stored;
                }

                if (lowestVcn == 0)
                {
                    if (attrName.Length == 0)
                    {
                        info.Size = real;
                        info.Allocated = allocated;
                        info.DataRuns = runs;
                    }
                    else info.Streams.Add(new MftStream(attrName, real, allocated));
                }
                else if (attrName.Length == 0)
                {
                    // A later piece of a fragmented file's data (only needed for the run list of $MFT itself).
                    (info.DataRuns ??= []).AddRange(runs);
                }
            }
            pos += length;
        }
        return info;
    }

    /// <summary>Decodes an NTFS run list into absolute (cluster, length) extents. Sparse runs get Lcn = -1.</summary>
    public static List<(long Lcn, long Clusters)> DecodeRuns(ReadOnlySpan<byte> runs)
    {
        var result = new List<(long, long)>();
        long lcn = 0;
        int i = 0;
        while (i < runs.Length && runs[i] != 0)
        {
            int lenBytes = runs[i] & 0x0F, offBytes = runs[i] >> 4;
            i++;
            if (lenBytes == 0 || i + lenBytes + offBytes > runs.Length) break;
            long length = 0;
            for (int b = 0; b < lenBytes; b++) length |= (long)runs[i + b] << (8 * b);
            i += lenBytes;
            if (offBytes == 0)
            {
                result.Add((-1, length));
                continue;
            }
            long delta = 0;
            for (int b = 0; b < offBytes; b++) delta |= (long)runs[i + b] << (8 * b);
            if ((runs[i + offBytes - 1] & 0x80) != 0) delta |= -1L << (8 * offBytes); // sign-extend
            i += offBytes;
            lcn += delta;
            result.Add((lcn, length));
        }
        return result;
    }
}
