using System.Buffers.Binary;
using System.Text;
using SilentStream.Core.Media;
using SilentStream.Core.Implementations;
using Xunit;

namespace SilentStream.Tests;

public sealed class Mp4VideoValidationTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("sstream-video-").FullName;

    [Theory]
    [InlineData("valid-av.mp4", true)]
    [InlineData("valid-fragmented.mp4", true)]
    [InlineData("audio-only.mp4", false)]
    [InlineData("empty-eof.mp4", false)]
    public void RealFfmpegOutputsNeedVideoSamples(string file, bool valid) =>
        Assert.Equal(valid, Mp4MediaSamples.HasUploadableVideo(Path.Combine(AppContext.BaseDirectory, "Fixtures", file)));

    [Fact]
    public void ValidationRestoresTheUploadHandlePosition()
    {
        using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "valid-av.mp4"));
        Assert.True(Mp4MediaSamples.HasUploadableVideo(stream));
        Assert.Equal(0, stream.Position);
        stream.Position = 5;
        Assert.True(Mp4MediaSamples.HasUploadableVideo(stream));
        Assert.Equal(5, stream.Position);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AudioSamplesCannotMakeAnEmptyVideoUploadable(bool fragmented)
    {
        var bytes = fragmented
            ? Join(Box("moov", Join(Track(1, "vide", 0), Track(2, "soun", 0))), Fragment(2, 5), Data())
            : Join(Box("moov", Join(Track(1, "vide", 0), Track(2, "soun", 5))), Data());
        Assert.False(Check(bytes));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VideoSamplesWithDataAreAcceptedRegardlessOfTrackOrder(bool fragmented)
    {
        var bytes = fragmented
            ? Join(Box("moov", Join(Track(2, "soun", 0), Track(7, "vide", 0))), Fragment(7, 5), Data())
            : Join(Box("moov", Join(Track(2, "soun", 8), Track(7, "vide", 5))), Data());
        Assert.True(Check(bytes));
    }

    [Fact]
    public void TinyHeaderOnlyLegacyClipIsRejectedEvenIfItClaimsSamples()
    {
        var headers = Box("moov", Track(1, "vide", 1));
        var bytes = Join(headers, Box("mdat", new byte[1217 - headers.Length - 8]));
        Assert.Equal(1217, bytes.Length);
        Assert.False(Check(bytes));
    }

    [Fact]
    public void UnknownFragmentTrackAndMissingHandlerCannotClaimVideoSamples()
    {
        Assert.False(Check(Join(Box("moov", Track(1, "vide", 0)), Fragment(99, 5), Data())));
        Assert.False(Check(Join(Box("moov", Track(1, "", 5)), Data())));
    }

    [Fact]
    public async Task UploadRejectsAudioOnlyBeforeUsingCredentialsOrCallingYouTube()
    {
        var path = Path.Combine(_directory, "audio-only.mp4");
        File.WriteAllBytes(path, Join(Box("moov", Track(1, "soun", 5)), Data()));
        Assert.True(Mp4MediaSamples.HasSamples(path)); // Diagnostic meaning stays unchanged.
        var service = new YouTubeUploadService(null!, null!, null!, null!);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.UploadAsync(path, "test", "unlisted", CancellationToken.None));
    }

    private bool Check(byte[] bytes)
    {
        var path = Path.Combine(_directory, Guid.NewGuid() + ".mp4");
        File.WriteAllBytes(path, bytes);
        return Mp4MediaSamples.HasUploadableVideo(path);
    }

    internal static byte[] Track(uint id, string handler, uint samples)
    {
        var tkhd = new byte[20];
        BinaryPrimitives.WriteUInt32BigEndian(tkhd.AsSpan(12), id);
        var hdlr = new byte[12];
        Encoding.ASCII.GetBytes(handler).CopyTo(hdlr, 8);
        var stsz = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(stsz.AsSpan(8), samples);
        return Box("trak", Join(Box("tkhd", tkhd), Box("mdia", Join(Box("hdlr", hdlr),
            Box("minf", Box("stbl", Box("stsz", stsz)))))));
    }

    private static byte[] Fragment(uint track, uint samples)
    {
        var tfhd = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(tfhd.AsSpan(4), track);
        var trun = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(trun.AsSpan(4), samples);
        return Box("moof", Box("traf", Join(Box("tfhd", tfhd), Box("trun", trun))));
    }

    internal static byte[] Data() => Box("mdat", new byte[4096]);
    internal static byte[] Join(params byte[][] parts) => parts.SelectMany(x => x).ToArray();
    internal static byte[] Box(string name, byte[] body)
    {
        var result = new byte[body.Length + 8];
        BinaryPrimitives.WriteInt32BigEndian(result, result.Length);
        Encoding.ASCII.GetBytes(name).CopyTo(result, 4);
        body.CopyTo(result, 8);
        return result;
    }

    public void Dispose() => Directory.Delete(_directory, true);
}
