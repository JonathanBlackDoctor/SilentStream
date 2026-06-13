namespace SilentStream.Core.Media;

/// <summary>
/// Maps capture resolution/FPS to the YouTube-recommended video bitrate (plan §3.5).
/// </summary>
public static class BitrateMapper
{
    /// <summary>Returns the recommended video bitrate in kbps.</summary>
    public static int GetVideoBitrateKbps(int height, double fps)
    {
        if (height >= 1080)
        {
            return fps >= 48 ? 9000 : 6000;
        }
        if (height >= 720)
        {
            return 3500;
        }
        // Below 720p the plan table ends; use a conservative SD value.
        return 2500;
    }
}
