using SilentStream.Core.Contracts;
using SilentStream.Core.Media;
using SilentStream.Core.Models;

namespace SilentStream.Tests;

// Extraction argument tests isolate the process invocation from decoder verification.
// Decoder failures/coverage are exercised separately by VodMediaValidatorTests and real FFmpeg tests.
internal sealed class CompleteVodValidationStub : IVodMediaValidator
{
    public Task<VerifiedRecordingCoverage?> VerifyAsync(RecordingSession source, DateTime start,
        DateTime end, string path, CancellationToken ct) => Task.FromResult<VerifiedRecordingCoverage?>(
        new(Path.GetFullPath(source.FilePath), source.StartLocal, start, end,
            new FileInfo(path).Length, new string('a',64)));
    public Task<bool> VerifyJoinedAsync(string path, TimeSpan duration, CancellationToken ct) => Task.FromResult(true);
}
