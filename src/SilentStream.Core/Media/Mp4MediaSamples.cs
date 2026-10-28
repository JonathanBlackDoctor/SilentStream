using System.Buffers.Binary;
using System.Text;

namespace SilentStream.Core.Media;

/// <summary>Reads MP4 sample counts and data-box headers without loading a large recording.</summary>
public static class Mp4MediaSamples
{
    public static bool HasSamples(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var samples = false;
            var data = false;
            var remaining = 100_000;
            return Scan(stream, stream.Length, 0, ref samples, ref data, ref remaining) && samples && data;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static bool Scan(Stream stream, long end, int depth, ref bool samples, ref bool data,
        ref int remaining)
    {
        Span<byte> header = stackalloc byte[8];
        while (stream.Position < end)
        {
            var start = stream.Position;
            if (--remaining < 0 || end - start < 8) return false;
            stream.ReadExactly(header);
            var size = (long)BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = Encoding.ASCII.GetString(header[4..]);
            var headerSize = 8;
            if (size == 1)
            {
                if (end - stream.Position < 8) return false;
                stream.ReadExactly(header);
                var extended = BinaryPrimitives.ReadUInt64BigEndian(header);
                if (extended > long.MaxValue) return false;
                size = (long)extended;
                headerSize = 16;
            }
            else if (size == 0) size = end - start;
            if (size < headerSize || size > end - start) return false;
            var boxEnd = start + size;
            var payload = size - headerSize;
            if (type == "mdat" && payload > 0) data = true;
            if (type is "stsz" or "stz2" or "trun")
            {
                var countOffset = type == "trun" ? 4 : 8;
                if (payload < countOffset + 4) return false;
                stream.Position += countOffset;
                stream.ReadExactly(header[..4]);
                samples |= BinaryPrimitives.ReadUInt32BigEndian(header[..4]) > 0;
            }
            else if (type is "moov" or "trak" or "mdia" or "minf" or "stbl" or "moof" or "traf")
            {
                if (depth >= 8 || !Scan(stream, boxEnd, depth + 1, ref samples, ref data, ref remaining))
                    return false;
            }
            stream.Position = boxEnd;
        }
        return stream.Position == end;
    }
}
