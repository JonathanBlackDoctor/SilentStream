using System.Security.Cryptography;

namespace SilentStream.Core.Models;

/// <summary>Versioned evidence from decoding and matching a lossless cut to source video frames.
/// Upload acknowledgements without this evidence still suppress duplicates, but cannot delete originals.</summary>
public sealed record VerifiedRecordingCoverage(string SourceFilePath, DateTime SourceStartLocal,
    DateTime StartLocal, DateTime EndLocal, long FileSizeBytes, string FileSha256, int Version = 2)
{
    public bool IsValid => Version == 2 && !string.IsNullOrWhiteSpace(SourceFilePath) && Path.IsPathFullyQualified(SourceFilePath) &&
        StartLocal >= SourceStartLocal && EndLocal > StartLocal && FileSizeBytes >= 2048 &&
        FileSha256 is { Length: 64 } && FileSha256.All(Uri.IsHexDigit);

    public bool MatchesFile(string path)
    {
        if (!IsValid) return false;
        try
        {
            using var stream = File.OpenRead(path);
            return stream.Length == FileSizeBytes && Convert.ToHexString(SHA256.HashData(stream))
                .Equals(FileSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
