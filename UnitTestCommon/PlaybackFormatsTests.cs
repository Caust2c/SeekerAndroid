using Common;
using NUnit.Framework;

namespace UnitTestCommon
{
    [TestFixture]
    public class PlaybackFormatsTests
    {
        [TestCase("music\\track.mp3", true)]
        [TestCase("music\\track.FLAC", true)]
        [TestCase("track.Mp3", true)]
        [TestCase("track.flac.txt", false)]
        [TestCase("track.mp4", false)]
        [TestCase("track.ogg", false)]
        [TestCase("", false)]
        [TestCase(null, false)]
        public void OnlySupportedAudioFilesEnterProgressivePlayback(string? filename, bool supported)
        {
            Assert.That(PlaybackFormats.SupportsProgressivePlayback(filename), Is.EqualTo(supported));
        }
    }
}
