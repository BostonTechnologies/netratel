using System.Buffers.Binary;
using System.IO.Compression;

namespace NetRatel.Web.Services.FileSystem;

/// <summary>Verify raw ZIP expansion before a browser parser sees the original bytes.</summary>
internal static class RemoteArchiveBudget
{
    public static void Validate(Stream source, CancellationToken cancellationToken)
    {
        if (source.Length < 22 || source.Length > RemotePreviewPolicy.MaximumExpandedBytes)
            throw new InvalidDataException("Archive compressed input exceeds the local preview budget or is incomplete.");
        var tail = new byte[(int)Math.Min(source.Length, 65557)];
        ReadAt(source.Length - tail.Length, tail);
        var end = -1;
        for (var offset = tail.Length - 22; offset >= 0; offset--)
            if (U32(tail, offset) == 0x06054b50 && offset + 22 + U16(tail, offset + 20) == tail.Length) { end = offset; break; }
        if (end < 0) throw new InvalidDataException("Archive end directory is missing.");
        var count = U16(tail, end + 10);
        var centralLength = U32(tail, end + 12);
        long centralStart = U32(tail, end + 16);
        if (U16(tail, end + 4) != 0 || U16(tail, end + 6) != 0 || U16(tail, end + 8) != count ||
            count > RemotePreviewPolicy.MaximumArchiveEntries || centralStart + centralLength > source.Length - tail.Length + end)
            throw new InvalidDataException("Archive supports at most 2,048 classic ZIP entries on one disk; ZIP64 requires Download.");
        long next = centralStart, declared = 0, observed = 0, compressedTotal = 0;
        var central = new byte[46];
        var local = new byte[30];
        var output = new byte[16 * 1024];
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadAt(next, central);
            if (U32(central, 0) != 0x02014b50) throw new InvalidDataException("Archive central directory is malformed.");
            var flags = U16(central, 8);
            var method = U16(central, 10);
            long compressedLength = U32(central, 20);
            long expandedLength = U32(central, 24);
            var localOffset = U32(central, 42);
            next += 46 + U16(central, 28) + U16(central, 30) + U16(central, 32);
            declared += expandedLength;
            compressedTotal += compressedLength;
            if ((flags & 1) != 0 || method is not (0 or 8) || U16(central, 34) != 0 || next > centralStart + centralLength)
                throw new InvalidDataException("Encrypted, split or unsupported ZIP compression requires Download.");
            if (declared > RemotePreviewPolicy.MaximumExpandedBytes || compressedTotal > source.Length)
                throw new InvalidDataException("Archive expanded-size limit exceeded (32 MiB) or compressed entries overlap.");
            ReadAt(localOffset, local);
            if (U32(local, 0) != 0x04034b50 || U16(local, 8) != method || (U16(local, 6) & 1) != 0)
                throw new InvalidDataException("Archive local entry is malformed.");
            long dataStart = localOffset + 30L + U16(local, 26) + U16(local, 28);
            if (dataStart + compressedLength > centralStart) throw new InvalidDataException("Archive entry overlaps its directory.");
            // ZipArchive.Open can stop at the declared expanded length. Read the
            // raw bounded compressed payload instead, so a forged small length
            // cannot hide a bomb from this check and then reach JSZip.
            var compressed = new byte[checked((int)compressedLength)];
            ReadAt(dataStart, compressed);
            using var input = new MemoryStream(compressed, writable: false);
            using Stream decoded = method == 8 ? new DeflateStream(input, CompressionMode.Decompress, leaveOpen: true) : input;
            long entryBytes = 0;
            int read;
            while ((read = decoded.Read(output)) != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                entryBytes += read;
                observed += read;
                if (entryBytes > expandedLength || observed > RemotePreviewPolicy.MaximumExpandedBytes)
                    throw new InvalidDataException("Archive observed expanded-size exceeds its metadata or the 32 MiB limit.");
            }
            if (entryBytes != expandedLength) throw new InvalidDataException("Archive expanded length does not match its metadata.");
        }
        if (next != centralStart + centralLength) throw new InvalidDataException("Archive directory entry count does not match its byte length.");
        source.Position = 0;

        void ReadAt(long position, byte[] bytes)
        {
            if (position < 0 || position > source.Length - bytes.Length) throw new InvalidDataException("Archive entry lies outside the file.");
            source.Position = position;
            source.ReadExactly(bytes);
        }
    }
    private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
}
