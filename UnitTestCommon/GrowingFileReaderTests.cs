using Common;
using NUnit.Framework;
using System;
using System.IO;
using System.Threading.Tasks;

namespace UnitTestCommon
{
    [TestFixture]
    public class GrowingFileReaderTests
    {
        [Test]
        public async Task TemporaryEofWaitsForNewBytesAndCompletionProducesEof()
        {
            string path = Path.GetTempFileName();
            try
            {
                using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                using var reader = new GrowingFileReader(new FileStream(path, FileMode.Open,
                    FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
                byte[] bytes = new byte[4];
                var pending = Task.Run(() => reader.ReadAt(0, bytes, 0, bytes.Length));
                Assert.That(System.Threading.SpinWait.SpinUntil(() => reader.IsWaiting, 2000), Is.True);
                Assert.That(pending.IsCompleted, Is.False);
                writer.Write(new byte[] { 1, 2, 3, 4 }, 0, 4);
                writer.Flush();
                Assert.That(await pending.WaitAsync(TimeSpan.FromSeconds(2)), Is.EqualTo(4));
                Assert.That(bytes, Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
                reader.Complete();
                Assert.That(reader.ReadAt(4, bytes, 0, 4), Is.EqualTo(-1));
            }
            finally { File.Delete(path); }
        }

        [Test]
        public async Task CloseWakesPendingRead()
        {
            using var reader = new GrowingFileReader(new MemoryStream());
            var pending = Task.Run(() => reader.ReadAt(0, new byte[1], 0, 1));
            Assert.That(System.Threading.SpinWait.SpinUntil(() => reader.IsWaiting, 2000), Is.True);
            reader.Dispose();
            Assert.That(await pending.WaitAsync(TimeSpan.FromSeconds(2)), Is.EqualTo(-1));
        }

        [Test]
        public void SmallAndUnknownFilesWaitForFinalization()
        {
            long threshold = GrowingFileReader.StartThresholdBytes;
            Assert.That(GrowingFileReader.CanStart(threshold - 1, threshold, false), Is.False);
            Assert.That(GrowingFileReader.CanStart(threshold, threshold, false), Is.False);
            Assert.That(GrowingFileReader.CanStart(-1, threshold, false), Is.False);
            Assert.That(GrowingFileReader.CanStart(threshold + 1, threshold - 1, false), Is.False);
            Assert.That(GrowingFileReader.CanStart(threshold + 1, threshold, false), Is.True);
            Assert.That(GrowingFileReader.CanStart(1, 1, true), Is.True);
        }

        [Test]
        public async Task FinalizationWakesReadWaitingBeyondThePrefix()
        {
            using var reader = new GrowingFileReader(new MemoryStream(new byte[] { 7 }));
            var pending = Task.Run(() => reader.ReadAt(1, new byte[1], 0, 1));
            Assert.That(System.Threading.SpinWait.SpinUntil(() => reader.IsWaiting, 2000), Is.True);
            reader.Complete();
            Assert.That(await pending.WaitAsync(TimeSpan.FromSeconds(2)), Is.EqualTo(-1));
        }

        [Test]
        public void OpenReaderSurvivesRenameAndDoesNotReadReplacementFile()
        {
            string path = Path.GetTempFileName();
            string moved = path + ".complete";
            try
            {
                File.WriteAllBytes(path, new byte[] { 7, 8 });
                using var reader = new GrowingFileReader(new FileStream(path, FileMode.Open,
                    FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
                File.Move(path, moved);
                File.WriteAllBytes(path, new byte[] { 9, 10 });
                reader.Complete();
                var bytes = new byte[2];
                Assert.That(reader.ReadAt(0, bytes, 0, 2), Is.EqualTo(2));
                Assert.That(bytes, Is.EqualTo(new byte[] { 7, 8 }));
            }
            finally
            {
                File.Delete(path);
                File.Delete(moved);
            }
        }
    }
}
