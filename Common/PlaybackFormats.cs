using System;

namespace Common
{
    public static class PlaybackFormats
    {
        // These formats can be decoded progressively from a sequential prefix.
        public static bool SupportsProgressivePlayback(string? filename) =>
            filename != null && (filename.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ||
                filename.EndsWith(".flac", StringComparison.OrdinalIgnoreCase));
    }
}
