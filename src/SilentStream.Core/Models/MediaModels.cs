namespace SilentStream.Core.Models;

/// <summary>
/// A single captured video frame. Phase 0 holds an opaque buffer reference only;
/// the real DXGI implementation (Phase 2) fills in surface details.
/// </summary>
/// <param name="Width">Frame width in pixels.</param>
/// <param name="Height">Frame height in pixels.</param>
/// <param name="TimestampUtc">Capture timestamp.</param>
/// <param name="Data">Raw pixel buffer (BGRA). May be empty in Phase-0 stubs.</param>
public sealed record VideoFrame(
    int Width,
    int Height,
    DateTime TimestampUtc,
    ReadOnlyMemory<byte> Data);

/// <summary>
/// A mixed PCM audio buffer (system loopback + microphone). See plan §3.4.
/// </summary>
/// <param name="SampleRate">Samples per second (e.g. 48000).</param>
/// <param name="Channels">Channel count (e.g. 2 for stereo).</param>
/// <param name="TimestampUtc">Buffer timestamp.</param>
/// <param name="Pcm">Interleaved 16-bit PCM samples. May be empty in Phase-0 stubs.</param>
public sealed record AudioBuffer(
    int SampleRate,
    int Channels,
    DateTime TimestampUtc,
    ReadOnlyMemory<byte> Pcm);

/// <summary>
/// A selectable microphone input device for the control UI dropdown (plan §3.4).
/// </summary>
/// <param name="Id">Stable device identifier persisted in config.</param>
/// <param name="Name">Human-readable device name shown in the UI.</param>
public sealed record AudioDeviceInfo(string Id, string Name);

/// <summary>
/// Options handed to the encoder pipeline when starting a session. The pipeline
/// fans the encoded stream out to RTMP (onfail=ignore) and the local mp4 via FFmpeg tee.
/// See plan §3.6.
/// </summary>
/// <param name="RtmpUrl">Full RTMP ingest URL including the stream key (empty = recording only).</param>
/// <param name="RecordingFilePath">Absolute path of the session mp4 file (empty = streaming only).</param>
/// <param name="VideoBitrateKbps">Target video bitrate (kbps).</param>
/// <param name="Width">Capture width in pixels.</param>
/// <param name="Height">Capture height in pixels.</param>
/// <param name="Fps">Capture frame rate.</param>
/// <param name="AudioBitrateKbps">Target audio bitrate (kbps).</param>
public sealed record EncoderStartOptions(
    string RtmpUrl,
    string RecordingFilePath,
    int VideoBitrateKbps,
    int Width = 1920,
    int Height = 1080,
    double Fps = 30,
    int AudioBitrateKbps = 160);

/// <summary>
/// A created YouTube live session: the broadcast plus the RTMP ingest details. See plan §3.7.
/// </summary>
/// <param name="BroadcastId">YouTube broadcast id (used for the complete transition).</param>
/// <param name="StreamKey">Ingest stream key.</param>
/// <param name="RtmpUrl">Base RTMP ingest URL (without the key).</param>
/// <param name="WatchUrl">Public watch URL of the broadcast.</param>
public sealed record LiveSession(
    string BroadcastId,
    string StreamKey,
    string RtmpUrl,
    string WatchUrl);

/// <summary>
/// Current recording status surfaced in the control UI recording panel (plan §3.8).
/// </summary>
/// <param name="CurrentFilePath">Active session file, or null when not recording.</param>
/// <param name="TotalUsedBytes">Total bytes used by recordings in the target folder.</param>
/// <param name="FreeDiskBytes">Free space on the recording volume.</param>
public sealed record RecordingStatus(
    string? CurrentFilePath,
    long TotalUsedBytes,
    long FreeDiskBytes)
{
    public static RecordingStatus Empty { get; } = new(null, 0, 0);
}
