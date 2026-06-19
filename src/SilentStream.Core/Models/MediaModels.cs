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

/// <summary>Kind of an audio source in the mixer.</summary>
public enum AudioSourceKind
{
    /// <summary>System/desktop audio via WASAPI loopback (one per mixer).</summary>
    System,

    /// <summary>A microphone / line-in capture device.</summary>
    Microphone
}

/// <summary>
/// Runtime configuration for one audio source fed into the mixer. The mixer captures
/// the system loopback plus zero or more microphones, applies per-source gain
/// (>1.0 amplifies), mute, and an optional noise gate, then mixes them down (OBS 대비
/// 다중 오디오 채널 + 증폭/필터 기능).
/// </summary>
/// <param name="Id">Stable mixer key: "system" or "mic:{deviceId}" (UI/remote address it by this).</param>
/// <param name="Kind">System loopback or microphone.</param>
/// <param name="DeviceId">Capture device id; null = system default (loopback/communications mic).</param>
/// <param name="Name">Human-readable label for the UI.</param>
/// <param name="Gain">Linear gain multiplier. 0 = silence, 1 = unity, >1 amplifies (UI caps ~4 ≈ +12 dB).</param>
/// <param name="Muted">When true the source contributes nothing to the mix.</param>
/// <param name="GateEnabled">When true a noise gate silences the source below the threshold.</param>
/// <param name="GateThresholdDb">Gate open threshold in dBFS (e.g. -45). Below it the source is attenuated.</param>
public sealed record AudioSourceSettings(
    string Id,
    AudioSourceKind Kind,
    string? DeviceId,
    string Name,
    double Gain = 1.0,
    bool Muted = false,
    bool GateEnabled = false,
    double GateThresholdDb = -45);

/// <summary>Latest peak/RMS level for one mixer source (dBFS; -100 ≈ silence).</summary>
/// <param name="Id">Matches <see cref="AudioSourceSettings.Id"/>.</param>
/// <param name="PeakDb">Peak level since the previous tick, in dBFS (0 = full scale).</param>
/// <param name="RmsDb">RMS level since the previous tick, in dBFS.</param>
public sealed record AudioSourceLevel(string Id, double PeakDb, double RmsDb);

/// <summary>
/// A point-in-time snapshot of every source's level plus the mixed master level, pushed to
/// the control window meters (~15 Hz) and phone remote (~10 Hz) so the user can confirm a
/// microphone is actually being picked up in real time (OBS 대비 실시간 레벨 미터).
/// </summary>
public sealed record AudioLevels(
    IReadOnlyList<AudioSourceLevel> Sources,
    double MasterPeakDb,
    double MasterRmsDb,
    DateTime TimestampUtc)
{
    /// <summary>The dBFS floor used to represent silence throughout the level pipeline.</summary>
    public const double SilenceFloorDb = -100;

    public static AudioLevels Empty { get; } =
        new(Array.Empty<AudioSourceLevel>(), SilenceFloorDb, SilenceFloorDb, DateTime.UnixEpoch);
}

/// <summary>
/// Whether a present microphone is currently delivering audible signal. Flips to false after
/// the mic stays below the silence threshold for a grace period while capturing, so the UI and
/// phone can warn that the mic may be muted/unplugged (무음·마이크 끊김 감지 → 폰 알림).
/// </summary>
/// <param name="SourceId">The mic source this status refers to.</param>
/// <param name="SignalPresent">True when audible signal is detected; false when silent too long.</param>
/// <param name="TimestampUtc">When the state changed.</param>
public sealed record MicSignalStatus(string SourceId, bool SignalPresent, DateTime TimestampUtc);

/// <summary>
/// A selectable capture monitor for the control UI (OBS 대비 모니터/영역 선택). Index pairs
/// the DXGI adapter+output so the capture source can re-resolve it.
/// </summary>
/// <param name="Index">0-based ordinal across all outputs (matches config capture.monitorIndex).</param>
/// <param name="Name">Device/display name shown in the UI.</param>
/// <param name="Width">Monitor width in pixels.</param>
/// <param name="Height">Monitor height in pixels.</param>
/// <param name="IsPrimary">True for the primary monitor.</param>
public sealed record MonitorInfo(int Index, string Name, int Width, int Height, bool IsPrimary)
{
    /// <summary>Friendly label for the control-UI picker, e.g. "★ \\.\DISPLAY1 (1920×1080)".</summary>
    public string Display => $"{(IsPrimary ? "★ " : string.Empty)}{Name} ({Width}×{Height})";
}

/// <summary>
/// Optional crop region within the captured monitor, in pixels. A zero <paramref name="Width"/>
/// or <paramref name="Height"/> means "no crop — use the whole monitor".
/// </summary>
public sealed record CaptureRegion(int X, int Y, int Width, int Height)
{
    public static CaptureRegion None { get; } = new(0, 0, 0, 0);

    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>
/// Master-bus audio post-processing applied by FFmpeg at encode time (afftdn/acompressor/
/// alimiter/volume). Like the resource limit these take effect on the next session — the
/// realtime per-source gain/mute/gate live in the mixer instead. See plan §3.4.
/// </summary>
/// <param name="NoiseSuppressionEnabled">Apply broadband spectral denoise (FFmpeg afftdn).</param>
/// <param name="NoiseSuppressionDb">afftdn reduction amount in dB (1-97; ~12 is gentle, ~24 aggressive).</param>
/// <param name="CompressorEnabled">Even out loud/quiet speech (FFmpeg acompressor).</param>
/// <param name="LimiterEnabled">Brick-wall peak limiter to prevent clipping (FFmpeg alimiter).</param>
/// <param name="MasterGainDb">Master make-up gain in dB applied after the filters (0 = unity).</param>
public sealed record AudioFilterSettings(
    bool NoiseSuppressionEnabled = false,
    int NoiseSuppressionDb = 12,
    bool CompressorEnabled = false,
    bool LimiterEnabled = false,
    double MasterGainDb = 0)
{
    public static AudioFilterSettings None { get; } = new();

    /// <summary>True when at least one master filter would alter the audio (drives whether -af is emitted).</summary>
    public bool HasAny =>
        NoiseSuppressionEnabled || CompressorEnabled || LimiterEnabled || Math.Abs(MasterGainDb) > 0.01;
}

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
/// <param name="AudioFilters">Master-bus audio post-processing (denoise/compressor/limiter/gain); null = none.</param>
/// <param name="OutputWidth">Encode/scale target width; 0 = same as <paramref name="Width"/> (no scale).</param>
/// <param name="OutputHeight">Encode/scale target height; 0 = same as <paramref name="Height"/>.</param>
/// <param name="OutputFps">Output frame rate; 0 = same as <paramref name="Fps"/> (no frame drop).</param>
public sealed record EncoderStartOptions(
    string RtmpUrl,
    string RecordingFilePath,
    int VideoBitrateKbps,
    int Width = 1920,
    int Height = 1080,
    double Fps = 30,
    int AudioBitrateKbps = 160,
    AudioFilterSettings? AudioFilters = null,
    int OutputWidth = 0,
    int OutputHeight = 0,
    double OutputFps = 0);

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
