using Android.App;
using Android.Media;
using Android.OS;
using Common;
using NUnit.Framework;
using Seeker.Services;
using System;
using System.IO;
using System.Threading.Tasks;

namespace InstrumentationTests
{
    [TestFixture]
    public class ProgressivePlaybackTests
    {
        [Test]
        public Task Flac16Bit44100PlaysBeforeCompletionAndContinuesAfterGrowth() =>
            PlayGrowingFile("playback-16bit-44100.flac");

        [Test]
        public Task Flac24Bit96000PlaysBeforeCompletionAndContinuesAfterGrowth() =>
            PlayGrowingFile("playback-24bit-96000.flac");

        [Test]
        public Task Mp3StillStartsFromPartialFile() =>
            PlayGrowingFile("playback.mp3", startOnly: true);

        [Test]
        public Task CloseWhileBufferingKeepsLateNativeCallbacksSafe() =>
            PlayGrowingFile("playback-24bit-96000.flac", closeAtEdge: true);

        internal static async Task PlayGrowingFile(string asset, bool closeAtEdge = false, bool startOnly = false, bool seek = false)
        {
            Android.Util.Log.Warn("SeekerTests", "Starting progressive decode: " + asset);
            using var encoded = new MemoryStream();
            using (var input = Application.Context.Assets.Open(asset)) input.CopyTo(encoded);
            byte[] bytes = encoded.ToArray();
            string path = Path.Combine(Application.Context.CacheDir.AbsolutePath, Guid.NewGuid() + ".partial");
            var thread = new HandlerThread("GrowingAudioTest");
            thread.Start();
            using var handler = new Handler(thread.Looper);
            MediaPlayer player = null;
            GrowingAudioDataSource source = null;
            GrowingFileReader reader = null;
            var prepared = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            string stage = "opening prefix";

            Task OnPlayerThread(Action action)
            {
                var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                handler.Post(() =>
                {
                    try { action(); done.TrySetResult(true); }
                    catch (Exception ex) { done.TrySetException(ex); }
                });
                return done.Task.WaitAsync(TimeSpan.FromSeconds(15));
            }

            void CreatePlayer(long length = -1)
            {
                source = new GrowingAudioDataSource(reader, length);
                player = new MediaPlayer();
                player.SetVolume(0, 0);
                player.Prepared += (s, e) => prepared.TrySetResult(true);
                player.Completion += (s, e) => completed.TrySetResult(true);
                player.Error += (s, e) =>
                {
                    e.Handled = true;
                    var error = new InvalidOperationException($"{asset}: MediaPlayer {e.What}/{e.Extra}");
                    prepared.TrySetException(error);
                    completed.TrySetException(error);
                };
                player.SetDataSource(source);
                player.PrepareAsync();
            }

            try
            {
                using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                int prefixLength = bytes.Length / 2;
                writer.Write(bytes, 0, prefixLength);
                writer.Flush();
                // Exercise the same descriptor/channel wrapper used for document providers.
                using var javaFile = new Java.IO.File(path);
                using var uri = Android.Net.Uri.FromFile(javaFile);
                var descriptor = Application.Context.ContentResolver.OpenFileDescriptor(uri, "r");
                reader = new GrowingFileReader(new SeekableDocumentReadStream(descriptor));
                Assert.That(reader.Length, Is.EqualTo(prefixLength));
                stage = "setting data source";
                await OnPlayerThread(() => CreatePlayer());
                stage = "waiting for preparation";
                await prepared.Task.WaitAsync(TimeSpan.FromSeconds(15));
                Android.Util.Log.Warn("SeekerTests", "Prepared from partial source: " + asset);
                await OnPlayerThread(() => player.Start());
                stage = "waiting for audible progression";
                int position = 0;
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (position < 500 && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(100);
                    await OnPlayerThread(() => position = player.CurrentPosition);
                }
                Assert.That(position, Is.GreaterThanOrEqualTo(500), "Audio must advance while half the file is missing.");
                Assert.That(reader.Length, Is.LessThan(bytes.Length));
                if (seek)
                {
                    int target = asset.EndsWith(".mp3") ? 55000 : asset.Contains("24bit") ? 12000 : 18000;
                    // Match the application's interruptible seek: replace the engine,
                    // then advertise final length only for FLAC's seek session.
                    reader.Cancel();
                    await OnPlayerThread(() => { player.Release(); player.Dispose(); source.Close(); });
                    reader = new GrowingFileReader(new SeekableDocumentReadStream(
                        Application.Context.ContentResolver.OpenFileDescriptor(uri, "r")));
                    prepared = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    await OnPlayerThread(() => CreatePlayer(asset.EndsWith(".flac") ? bytes.Length : -1));
                    stage = "preparing seek session";
                    await prepared.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    var sought = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    await OnPlayerThread(() =>
                    {
                        player.SeekComplete += (s, e) => sought.TrySetResult(true);
                        player.SeekTo(target);
                        player.Start();
                    });
                    await Task.Delay(1000);
                    stage = "seek waiting for missing data";
                    Assert.That(reader.IsWaiting, Is.True, "Seek must read and wait for a position beyond the prefix.");
                    Assert.That(completed.Task.IsCompleted, Is.False);
                    // Allow native decoder read-ahead after the selected frame.
                    // At 85%, the 20-second fixture ends exactly five seconds
                    // beyond its seek target, splitting the final preroll read.
                    int extendedLength = bytes.Length * 90 / 100;
                    writer.Write(bytes, prefixLength, extendedLength - prefixLength);
                    writer.Flush();
                    stage = "waiting for seek completion after growth";
                    await sought.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    stage = "waiting for audio at the selected timestamp";
                    deadline = DateTime.UtcNow.AddSeconds(10);
                    do
                    {
                        await Task.Delay(100);
                        await OnPlayerThread(() => position = player.CurrentPosition);
                    } while (position < target + 500 && DateTime.UtcNow < deadline);
                    Assert.That(position, Is.InRange(target + 500, target + 3000), "Playback must advance at the requested timestamp while the tail is still missing.");
                    Assert.That(reader.Length, Is.EqualTo(extendedLength));
                    Assert.That(completed.Task.IsCompleted, Is.False);
                    stage = "seeking back into downloaded data";
                    sought = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    await OnPlayerThread(() => { player.Pause(); player.SeekTo(1000); });
                    await sought.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    await OnPlayerThread(() => position = player.CurrentPosition);
                    Assert.That(position, Is.InRange(500, 2000));
                    await Task.Delay(500);
                    await OnPlayerThread(() => position = player.CurrentPosition);
                    Assert.That(position, Is.InRange(500, 2000), "A paused seek must stay paused.");
                    return;
                }
                if (startOnly) return; // MP3 regression: actual playback starts before the remainder exists.
                // Prefix duration is approximate for compressed variable-rate files.
                stage = "checking catch-up buffering";
                int prefixMilliseconds = asset == "playback-24bit-96000.flac" ? 10000 : 15000;
                await Task.Delay(prefixMilliseconds + 500);
                Assert.That(reader.IsWaiting, Is.True, "The native reader must wait for the missing tail.");
                Assert.That(completed.Task.IsCompleted, Is.False, "Temporary EOF must not complete playback.");
                int stalled = 0, stillStalled = 0;
                await OnPlayerThread(() => stalled = player.CurrentPosition);
                await Task.Delay(600);
                await OnPlayerThread(() => stillStalled = player.CurrentPosition);
                Assert.That(Math.Abs(stillStalled - stalled), Is.LessThan(400), "Playback must buffer at the available edge.");

                if (closeAtEdge) return; // finally cancels the pending native read and releases the player.

                stage = "resuming appended tail";
                writer.Write(bytes, prefixLength, bytes.Length - prefixLength);
                writer.Flush();
                reader.Complete();
                await completed.Task.WaitAsync(TimeSpan.FromSeconds(25));
                Android.Util.Log.Warn("SeekerTests", $"PASS {asset}: decoded partial prefix, buffered, resumed and completed");
            }
            catch (Exception ex)
            {
                var detail = $"{asset}: {stage}; readOffset={source?.LastReadPosition}; prefix={reader?.Length}; waiting={reader?.IsWaiting}; {ex}\n";
                File.AppendAllText(Path.Combine(Application.Context.FilesDir.AbsolutePath, "decoder-failures.txt"), detail);
                throw new InvalidOperationException(detail, ex);
            }
            finally
            {
                reader?.Cancel();
                try
                {
                    await OnPlayerThread(() => { player?.Release(); player?.Dispose(); source?.Close(); });
                }
                finally
                {
                    reader?.Dispose();
                    thread.QuitSafely();
                    thread.Join(2000);
                    thread.Dispose();
                    File.Delete(path);
                }
                // Give Android's deferred close callback time to run after Release.
                GC.Collect();
                GC.WaitForPendingFinalizers();
                await Task.Delay(500);
            }
        }
    }

    [TestFixture]
    public class ProgressiveSeekingTests
    {
        [Test]
        public Task Flac16BitSeeksForwardWhileDownloadingAndBackWhilePaused() =>
            ProgressivePlaybackTests.PlayGrowingFile("playback-16bit-44100.flac", seek: true);
        [Test]
        public Task Flac24BitSeeksForwardWhileDownloadingAndBackWhilePaused() =>
            ProgressivePlaybackTests.PlayGrowingFile("playback-24bit-96000.flac", seek: true);
        [Test]
        public Task Mp3SeeksForwardWhileDownloadingAndBackWhilePaused() =>
            ProgressivePlaybackTests.PlayGrowingFile("playback.mp3", seek: true);
    }
}
