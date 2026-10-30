namespace SilentStream.Core.YouTube;

/// <summary>The request may have committed remotely. Starting another upload could duplicate it.</summary>
public sealed class UploadOutcomeUnknownException(string message, Exception? inner = null) : Exception(message, inner);
