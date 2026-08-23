using System.Security.Cryptography;
using System.Text.Json;
using SilentStream.Inspection;

namespace SilentStream.Inspection.Coordinator;

public static class InspectionApiPolicy
{
    public const string DestructiveAcknowledgementPrefix = "destructive-";

    public static bool IsSafeExternalAcknowledgementChannel(string channel) =>
        channel.Length is >= 1 and <= 40
        && !channel.StartsWith(DestructiveAcknowledgementPrefix, StringComparison.OrdinalIgnoreCase)
        && channel.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public static bool IsMatchingRunningCheckpoint(StoredBundle bundle, IReadOnlyDictionary<string, StoredStep> states, string channel) =>
        bundle.Manifest.Plan.Steps.Any(step => step.Type == InspectionStepType.Checkpoint
            && states.TryGetValue(step.Id, out var state)
            && state.Status == InspectionRunStatus.Running
            && step.Input.ValueKind == JsonValueKind.Object
            && step.Input.TryGetProperty("ack", out var ack)
            && string.Equals(ack.GetString(), channel, StringComparison.Ordinal));

    public static void EnsureBundleReplacementAllowed(StoredBundle? existing, VerifiedInspectionBundle candidate)
    {
        if (existing is null) return;
        if (!Version.TryParse(existing.Manifest.Version, out var existingVersion)
            || !Version.TryParse(candidate.Manifest.Version, out var candidateVersion))
            throw new InvalidDataException("Stored or candidate bundle version is invalid.");
        var comparison = candidateVersion.CompareTo(existingVersion);
        if (comparison < 0)
            throw new InvalidDataException($"Bundle downgrade is not allowed ({existing.Manifest.Version} -> {candidate.Manifest.Version}).");
        if (comparison > 0) return;

        var existingHash = SHA256.HashData(InspectionBundleVerifier.SignaturePayload(existing.Manifest));
        var candidateHash = SHA256.HashData(InspectionBundleVerifier.SignaturePayload(candidate.Manifest));
        if (!CryptographicOperations.FixedTimeEquals(existingHash, candidateHash))
            throw new InvalidDataException($"Bundle version {candidate.Manifest.Version} is already registered with different signed content.");
    }
}
