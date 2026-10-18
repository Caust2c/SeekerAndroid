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
        public async Task ReadBeyondDownloadedPrefixWaitsForTheRequestedBytes()
        {
            string path = Path.GetTempFileName();
            try
            {
                using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                writer.Write(new byte[] { 0, 1, 2, 3 });
                writer.Flush();
                using var reader = new GrowingFileReader(new FileStream(path, FileMode.Open,
                    FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
                var actual = new byte[2];
                var pending = Task.Run(() => reader.ReadFullyAt(8, actual, 0, 2));
                Assert.That(System.Threading.SpinWait.SpinUntil(() => reader.IsWaiting, 2000), Is.True);
                writer.Write(new byte[] { 4, 5, 6, 7 });
                writer.Flush();
                Assert.That(pending.IsCompleted, Is.False, "Growth before the requested offset cannot satisfy the seek.");
                writer.Write(new byte[] { 8, 9, 10, 11 });
                writer.Flush();
                Assert.That(await pending.WaitAsync(TimeSpan.FromSeconds(2)), Is.EqualTo(2));
                Assert.That(actual, Is.EqualTo(new byte[] { 8, 9 }));
                Assert.That(reader.Length, Is.EqualTo(12), "A future read must not require download completion.");
            }
            finally { File.Delete(path); }
        }

        [Test]
        public async Task NativeFrameReadWaitsForMissingRemainderInsteadOfReturningShortEof()
        {
            string path = Path.GetTempFileName();
            try
            {
                using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                writer.Write(new byte[] { 1, 2 });
                writer.Flush();
                using var reader = new GrowingFileReader(new FileStream(path, FileMode.Open,
                    FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
                var bytes = new byte[4];
                var pending = Task.Run(() => reader.ReadFullyAt(0, bytes, 0, 4));
                Assert.That(System.Threading.SpinWait.SpinUntil(() => reader.IsWaiting, 2000), Is.True);
                Assert.That(pending.IsCompleted, Is.False);
                writer.Write(new byte[] { 3, 4 });
                writer.Flush();
                Assert.That(await pending.WaitAsync(TimeSpan.FromSeconds(2)), Is.EqualTo(4));
                Assert.That(bytes, Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
                reader.Complete();
                Assert.That(reader.ReadFullyAt(2, bytes, 0, 4), Is.EqualTo(2));
                Assert.That(reader.ReadFullyAt(4, bytes, 0, 4), Is.EqualTo(-1));
            }
            finally { File.Delete(path); }
        }

        [Test]
        public async Task HalfOfSevenMegabytesIsReadableBeforeCompletionThenWaitsForGrowth()
        {
            string path = Path.GetTempFileName();
            try
            {
                using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                byte[] prefix = new byte[7 * 1024 * 1024 / 2];
                new Random(42).NextBytes(prefix);
                writer.Write(prefix);
                writer.Flush();
                using var reader = new GrowingFileReader(new FileStream(path, FileMode.Open,
                    FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
                byte[] actual = new byte[prefix.Length + 128];
                // Returns the entire available prefix immediately, with no file-size gate.
                Assert.That(reader.ReadAt(0, actual, 0, actual.Length), Is.EqualTo(prefix.Length));
                Assert.That(actual.AsSpan(0, prefix.Length).ToArray(), Is.EqualTo(prefix));
                var pending = Task.Run(() => reader.ReadAt(prefix.Length, actual, 0, 128));
                Assert.That(System.Threading.SpinWait.SpinUntil(() => reader.IsWaiting, 2000), Is.True);
                Assert.That(pending.IsCompleted, Is.False);
                writer.Write(new byte[] { 42, 43 });
                writer.Flush();
                Assert.That(await pending.WaitAsync(TimeSpan.FromSeconds(2)), Is.EqualTo(2));
                Assert.That(actual[0], Is.EqualTo(42));
                reader.Complete();
                Assert.That(reader.ReadAt(prefix.Length + 2, actual, 0, 128), Is.EqualTo(-1));
            }
            finally { File.Delete(path); }
        }

        [Test]
        public async Task CancelWakesReadWithoutDisposingStreamAndCanBeRepeated()
        {
            using var stream = new MemoryStream(new byte[] { 1 });
            using var reader = new GrowingFileReader(stream);
            var pending = Task.Run(() => reader.ReadAt(1, new byte[1], 0, 1));
            Assert.That(System.Threading.SpinWait.SpinUntil(() => reader.IsWaiting, 2000), Is.True);
            reader.Cancel();
            reader.Cancel();
            Assert.That(await pending.WaitAsync(TimeSpan.FromSeconds(2)), Is.EqualTo(-1));
            Assert.That(stream.CanRead, Is.True);
            reader.Dispose();
            reader.Dispose();
            Assert.That(stream.CanRead, Is.False);
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
