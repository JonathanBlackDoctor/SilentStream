using System.Buffers.Binary;
using System.Text;

namespace SilentStream.Core.Media;

/// <summary>Reads MP4 sample counts and data-box headers without loading a large recording.</summary>
public static class Mp4MediaSamples
{
    public static bool HasSamples(string path) => Read(path, requireVideo: false);

    /// <summary>
    /// Conservative upload gate, separate from diagnostic sample presence. Tiny legacy clips
    /// need manual inspection; audio samples alone cannot make a class video uploadable.
    /// This does not certify decoder compatibility or YouTube playback.
    /// </summary>
    public static bool HasUploadableVideo(string path) => Read(path, requireVideo: true);
    public static bool HasUploadableVideo(Stream stream) => Read(stream, requireVideo: true);

    private static bool Read(string path, bool requireVideo)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Read(stream, requireVideo);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static bool Read(Stream stream, bool requireVideo)
    {
        if (!stream.CanRead || !stream.CanSeek) return false;
        var originalPosition = stream.Position;
        try
        {
            stream.Position = 0;
            if (requireVideo && stream.Length < 2048) return false;
            var state = new ScanState(requireVideo);
            var remaining = 100_000;
            if (!Scan(stream, stream.Length, 0, state, null, null, ref remaining) || !state.Data)
                return false;
            if (!requireVideo) return state.Samples;
            var videoIds = state.Tracks.Where(t => t.Video && t.Id is not null).Select(t => t.Id!.Value).ToHashSet();
            return state.Tracks.Any(t => t.Video && t.Samples) ||
                state.Fragments.Any(f => f.Samples && f.Id is { } id && videoIds.Contains(id));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
        finally { stream.Position = originalPosition; }
    }

    private sealed class ScanState(bool requireVideo)
    {
        public bool RequireVideo { get; } = requireVideo;
        public bool Samples, Data;
        public List<Track> Tracks { get; } = [];
        public List<Track> Fragments { get; } = [];
    }

    private sealed class Track
    {
        public uint? Id;
        public bool Video, Samples;
    }

    private static bool Scan(Stream stream, long end, int depth, ScanState state, Track? track,
        Track? fragment, ref int remaining)
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
            if (type == "mdat" && payload > 0) state.Data = true;
            if (type is "stsz" or "stz2" or "trun")
            {
                var countOffset = type == "trun" ? 4 : 8;
                if (payload < countOffset + 4) return false;
                stream.Position += countOffset;
                stream.ReadExactly(header[..4]);
                var hasSamples = BinaryPrimitives.ReadUInt32BigEndian(header[..4]) > 0;
                state.Samples |= hasSamples;
                if (type == "trun" && fragment is not null) fragment.Samples |= hasSamples;
                else if (type != "trun" && track is not null) track.Samples |= hasSamples;
            }
            else if (state.RequireVideo && type == "hdlr" && track is not null)
            {
                if (payload < 12) return false;
                stream.Position += 8;
                stream.ReadExactly(header[..4]);
                track.Video = header[..4].SequenceEqual("vide"u8);
            }
            else if (state.RequireVideo && type == "tkhd" && track is not null)
            {
                if (payload < 1) return false;
                var version = stream.ReadByte();
                var offset = version == 0 ? 12 : version == 1 ? 20 : -1;
                if (offset < 0 || payload < offset + 4) return false;
                stream.Position += offset - 1;
                stream.ReadExactly(header[..4]);
                track.Id = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
            }
            else if (state.RequireVideo && type == "tfhd" && fragment is not null)
            {
                if (payload < 8) return false;
                stream.Position += 4;
                stream.ReadExactly(header[..4]);
                fragment.Id = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
            }
            else if (type is "moov" or "trak" or "mdia" or "minf" or "stbl" or "moof" or "traf")
            {
                var childTrack = track;
                var childFragment = fragment;
                if (state.RequireVideo && type == "trak")
                { childTrack = new Track(); state.Tracks.Add(childTrack); }
                if (state.RequireVideo && type == "traf")
                { childFragment = new Track(); state.Fragments.Add(childFragment); }
                if (depth >= 8 || !Scan(stream, boxEnd, depth + 1, state, childTrack, childFragment, ref remaining))
                    return false;
            }
            stream.Position = boxEnd;
        }
        return stream.Position == end;
    }
}
