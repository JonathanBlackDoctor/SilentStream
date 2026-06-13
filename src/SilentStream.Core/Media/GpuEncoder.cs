namespace SilentStream.Core.Media;

/// <summary>
/// Hardware encoder families in detection-priority order (plan §3.5):
/// NVENC → AMF → QSV → x264 (CPU fallback).
/// </summary>
public enum GpuEncoder
{
    Nvenc,
    Amf,
    Qsv,
    X264
}

public static class GpuEncoderExtensions
{
    /// <summary>FFmpeg encoder name for this family.</summary>
    public static string ToFfmpegName(this GpuEncoder encoder) => encoder switch
    {
        GpuEncoder.Nvenc => "h264_nvenc",
        GpuEncoder.Amf => "h264_amf",
        GpuEncoder.Qsv => "h264_qsv",
        GpuEncoder.X264 => "libx264",
        _ => throw new ArgumentOutOfRangeException(nameof(encoder))
    };

    /// <summary>Parses the config value ("auto"/"nvenc"/"amf"/"qsv"/"x264").</summary>
    public static GpuEncoder? FromConfigValue(string value) => value.ToLowerInvariant() switch
    {
        "nvenc" => GpuEncoder.Nvenc,
        "amf" => GpuEncoder.Amf,
        "qsv" => GpuEncoder.Qsv,
        "x264" => GpuEncoder.X264,
        _ => null // "auto" or unknown → run detection
    };
}
