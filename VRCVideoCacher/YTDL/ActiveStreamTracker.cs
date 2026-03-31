namespace VRCVideoCacher.YTDL;

/// <summary>
/// Tracks active video streams being served to VRChat.
/// Downloads are deferred until all known streams have likely finished
/// (based on video duration) plus the configured idle buffer.
/// </summary>
public static class ActiveStreamTracker
{
    /// <summary>
    /// Fired on the thread pool whenever a new streaming URL is served.
    /// VideoDownloader subscribes to this to pause active downloads immediately.
    /// </summary>
    public static event Action? OnStreamingActivity;

    private static readonly object Lock = new();

    /// <summary>
    /// Tracks the expected end time of each active stream by video ID.
    /// If no duration is known, the entry stores just the start time
    /// and the idle buffer alone governs the delay.
    /// </summary>
    private static readonly Dictionary<string, DateTime> _expectedEndTimes = new();

    /// <summary>
    /// Fallback: the last time any activity was recorded, used when
    /// duration is unknown.
    /// </summary>
    private static DateTime _lastActivityAt = DateTime.MinValue;
    private static bool _hasActivity;

    /// <summary>
    /// Record that a video URL was just served to VRChat.
    /// </summary>
    /// <param name="videoId">The video ID being streamed.</param>
    /// <param name="durationSeconds">
    /// Known duration of the video in seconds, or null if unknown.
    /// </param>
    public static void RecordActivity(string? videoId = null, double? durationSeconds = null)
    {
        lock (Lock)
        {
            _lastActivityAt = DateTime.UtcNow;
            _hasActivity = true;

            if (!string.IsNullOrEmpty(videoId))
            {
                var expectedEnd = durationSeconds > 0
                    ? DateTime.UtcNow.AddSeconds(durationSeconds.Value)
                    : DateTime.UtcNow;

                // Keep the latest (longest) expected end time for this video
                if (!_expectedEndTimes.TryGetValue(videoId, out var existing) || expectedEnd > existing)
                    _expectedEndTimes[videoId] = expectedEnd;
            }

            // Clean up entries that have long since passed (> 1 hour stale)
            var cutoff = DateTime.UtcNow.AddHours(-1);
            var stale = _expectedEndTimes.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList();
            foreach (var key in stale)
                _expectedEndTimes.Remove(key);
        }
        Task.Run(() => OnStreamingActivity?.Invoke());
    }

    /// <summary>
    /// Returns true if all known streams have likely finished playing
    /// and the idle buffer has elapsed.
    /// </summary>
    public static bool IsIdle(int idleSeconds)
    {
        if (idleSeconds <= 0) return true;
        lock (Lock)
        {
            if (!_hasActivity) return true;

            var now = DateTime.UtcNow;

            // Find the latest expected end time across all active streams
            var latestEnd = _lastActivityAt;
            foreach (var endTime in _expectedEndTimes.Values)
            {
                if (endTime > latestEnd)
                    latestEnd = endTime;
            }

            // Idle = we're past the longest video's expected end + the buffer
            return (now - latestEnd).TotalSeconds >= idleSeconds;
        }
    }
}
