using Common;
using NUnit.Framework;
using System;
using System.IO;
using System.Text;

namespace UnitTestCommon
{
    public class AudioMetadataTests
    {
        [Test]
        public void ReadsFlacTitleQualityDurationAndCoverBeforeAudioArrives()
        {
            using var file = Flac();
            var tags = AudioMetadataReader.Read(file);
            Assert.Multiple(() =>
            {
                Assert.That(tags.Title, Is.EqualTo("Track"));
                Assert.That(tags.Artist, Is.EqualTo("Artist"));
                Assert.That(tags.Album, Is.EqualTo("Album"));
                Assert.That(tags.DurationMilliseconds, Is.EqualTo(10000));
                Assert.That(tags.Quality, Does.Contain("24-bit"));
                Assert.That(tags.Artwork, Is.EqualTo(new byte[] { 1, 2, 3 }));
                Assert.That(tags.Complete, Is.True);
            });
        }
        [Test]
        public void TruncatedFlacKeepsAvailableTagsAndNeverWaitsForMissingCover()
        {
            using var full = Flac();
            using var partial = new MemoryStream(full.ToArray()[..^2]);
            var tags = AudioMetadataReader.Read(partial);
            Assert.That(tags.Title, Is.EqualTo("Track"));
            Assert.That(tags.Artwork, Is.Null);
            Assert.That(tags.Complete, Is.False);
        }
        [TestCase(3)]
        [TestCase(4)]
        public void ReadsId3Utf8TextAndCover(int version)
        {
            using var frames = new MemoryStream();
            void Frame(string id, byte[] data)
            {
                frames.Write(Encoding.ASCII.GetBytes(id));
                frames.Write(version == 3 ? Big(data.Length) : Safe(data.Length));
                frames.Write(new byte[2]); frames.Write(data);
            }
            Frame("TIT2", new byte[] { 3, 72, 101, 108, 108, 111 });
            Frame("APIC", new byte[] { 0, 105, 109, 97, 103, 101, 47, 106, 112, 101, 103, 0, 3, 0, 1, 2, 3 });
            using var file = new MemoryStream();
            file.Write(new byte[] { 73, 68, 51, (byte)version, 0, 0 }); file.Write(Safe((int)frames.Length)); frames.Position = 0; frames.CopyTo(file);
            var tags = AudioMetadataReader.Read(file);
            Assert.That(tags.Title, Is.EqualTo("Hello"));
            Assert.That(tags.Artwork, Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(tags.Complete, Is.True);
        }
        [Test]
        public void InvalidPictureLengthDoesNotAllocateOrThrow()
        {
            using var file = Flac();
            var bytes = file.ToArray();
            // Corrupt the final picture payload length while preserving its block bounds.
            for (int i = bytes.Length - 7; i < bytes.Length - 3; i++) bytes[i] = 255;
            var tags = AudioMetadataReader.Read(new MemoryStream(bytes));
            Assert.That(tags.Artwork, Is.Null);
            Assert.That(tags.Title, Is.EqualTo("Track"));
        }
        [Test]
        public void TinyPrefixIsIncomplete()
        {
            Assert.That(AudioMetadataReader.Read(new MemoryStream(new byte[4])).Complete, Is.False);
        }
        private static MemoryStream Flac()
        {
            var file = new MemoryStream(); file.Write(Encoding.ASCII.GetBytes("fLaC"));
            void Block(byte type, byte[] data)
            {
                file.WriteByte(type); file.Write(Big(data.Length).AsSpan(1)); file.Write(data);
            }
            var info = new byte[34];
            ulong packed = (48000UL << 44) | (1UL << 41) | (23UL << 36) | 480000;
            for (int i = 17; i >= 10; i--) { info[i] = (byte)packed; packed >>= 8; }
            Block(0, info);
            using var comments = new MemoryStream();
            using (var writer = new BinaryWriter(comments, Encoding.UTF8, true))
            {
                writer.Write(0); writer.Write(3);
                foreach (var text in new[] { "TITLE=Track", "ARTIST=Artist", "ALBUM=Album" })
                { var data = Encoding.UTF8.GetBytes(text); writer.Write(data.Length); writer.Write(data); }
            }
            Block(4, comments.ToArray());
            using var picture = new MemoryStream();
            picture.Write(Big(3)); picture.Write(Big(10)); picture.Write(Encoding.ASCII.GetBytes("image/jpeg"));
            picture.Write(Big(0)); picture.Write(new byte[16]); picture.Write(Big(3)); picture.Write(new byte[] { 1, 2, 3 });
            Block(134, picture.ToArray()); return file;
        }
        private static byte[] Big(int n) => new byte[] { (byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n };
        private static byte[] Safe(int n) => new byte[] { (byte)((n >> 21) & 127), (byte)((n >> 14) & 127), (byte)((n >> 7) & 127), (byte)(n & 127) };
    }
}
