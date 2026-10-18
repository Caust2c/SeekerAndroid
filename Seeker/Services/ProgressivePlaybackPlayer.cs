using AudioMetadata = Common.AudioMetadata;
using Android.Content;
using Android.Media;
using Android.OS;
using AndroidX.Core.Content;
using Common;
using Seeker.Helpers;
using Soulseek;
using System;
using System.Threading.Tasks;

namespace Seeker.Services
{
    /// <summary>One playback session, kept alive by PlaybackForegroundService; DownloadService owns the download.</summary>
    public sealed class ProgressivePlaybackPlayer : Java.Lang.Object, AudioManager.IOnAudioFocusChangeListener
    {
        public static ProgressivePlaybackPlayer Instance { get; set; }
        private readonly Context context;
        private readonly Handler main = new Handler(Looper.MainLooper);
        private readonly HandlerThread thread;
        private readonly Handler handler;
        private readonly AudioManager audioManager;
        private readonly Action tick;
        private readonly NoisyReceiver noisyReceiver;
        private readonly object sourceGate = new object();
        private GrowingFileReader activeReader;
        private int generation;
        private volatile TransferItem item;
        // Only the playback thread calls the native player. UI reads a snapshot.
        private MediaPlayer player;
        private GrowingAudioDataSource source;
        private bool prepared, wantsPlay, hasFocus, visible, playbackServiceReady, seeking;
        private string filename;
        private int resumePosition, duration;
        private AudioMetadata metadata = new AudioMetadata();
        private bool metadataLoading;
        private int metadataSession;
        private long metadataBytes = -1;
        private string metadataUri;

        private int statusResource = Resource.String.playback_waiting;
        private sealed record State(bool Visible, string Filename, bool WantsPlay, int Position,
            int Duration, int DownloadProgress, int StatusResource, AudioMetadata Metadata, bool CanRetry);
        private volatile State state = new(false, null, false, 0, 0, 0, Resource.String.playback_waiting, new AudioMetadata(), false);
        public event EventHandler Changed;
        public bool Visible => state.Visible;
        public string Filename => state.Filename;
        public bool WantsPlay => state.WantsPlay;
        public int Position => state.Position;
        public int Duration => state.Duration;
        public bool CanSeek => state.Visible && state.Duration > 0;
        public int DownloadProgress => state.DownloadProgress;
        public int StatusResource => state.StatusResource;
        public string Title => string.IsNullOrWhiteSpace(state.Metadata.Title)
            ? System.IO.Path.GetFileNameWithoutExtension(state.Filename ?? "") : state.Metadata.Title;
        public string Artist => state.Metadata.Artist;
        public string Album => state.Metadata.Album;
        public string Quality => state.Metadata.Quality ?? (state.Filename?.EndsWith(".flac", StringComparison.OrdinalIgnoreCase) == true ? "FLAC" : "MP3");
        public byte[] Artwork => state.Metadata.Artwork;
        public bool CanRetry => state.CanRetry;
        public void RetryDownload() => Post(() =>
        {
            if (item == null || !item.Failed || item.InProcessing) return;
            DownloadService.Instance.RetryDownloadItem(item);
            Refresh();
        });

        public ProgressivePlaybackPlayer(Context context)
        {
            this.context = context.ApplicationContext;
            audioManager = (AudioManager)this.context.GetSystemService(Context.AudioService);
            thread = new HandlerThread("SeekerPlayback");
            thread.Start();
            handler = new Handler(thread.Looper);
            tick = () => Guard(Refresh);
            DownloadService.Instance.PlaybackSourceChanged += SourceChanged;
            DownloadService.Instance.PlaybackSourceInvalidated += SourceInvalidated;
            noisyReceiver = new NoisyReceiver(this);
            ContextCompat.RegisterReceiver(this.context, noisyReceiver,
                new IntentFilter(AudioManager.ActionAudioBecomingNoisy), ContextCompat.ReceiverNotExported);
        }
        private void Post(Action action) => handler.Post(() => Guard(action));
        private void Guard(Action action)
        {
            try { action(); }
            catch (Exception ex) { Fail(ex); }
        }
        // Wake native reads before queueing release. Also invalidates stale callbacks.
        private int CancelReader()
        {
            lock (sourceGate)
            {
                generation++;
                activeReader?.Cancel();
                return generation;
            }
        }
        private bool IsCurrent(int request) { lock (sourceGate) return request == generation; }

        public async Task RequestAsync(FullFileInfo file, string username)
        {
            if (!PreferencesState.EnableLivePlayback ||
                !PlaybackFormats.SupportsProgressivePlayback(file.FullFileName)) return;
            int request = CancelReader();
            Post(() =>
            {
                if (!IsCurrent(request)) return;
                Reset(false);
                filename = SimpleHelpers.GetFileNameFromFile(file.FullFileName).ToString();
                visible = wantsPlay = true;
                statusResource = Resource.String.playback_waiting;
                Refresh();
            });
            try
            {
                if (!await PlaybackForegroundService.EnsureStarted(context))
                    throw new InvalidOperationException("Could not start the playback service.");
                if (!IsCurrent(request)) return;
                Post(() => { if (IsCurrent(request)) { playbackServiceReady = true; Refresh(); } });
                if (SessionService.Instance.CurrentlyLoggedInButDisconnectedState())
                {
                    if (!SessionService.Instance.ShowMessageAndCreateReconnectTask(false, out Task reconnect))
                    {
                        Post(() => { if (IsCurrent(request)) Reset(); });
                        return;
                    }
                    await reconnect;
                }
                if (!IsCurrent(request)) return;
                var transfer = await DownloadService.Instance.EnqueueFileForPlaybackAsync(file, username);
                Post(() =>
                {
                    if (!IsCurrent(request)) return;
                    if (transfer == null) { Reset(); return; }
                    item = transfer;
                    Android.Util.Log.Info("SeekerPlayback", $"Attached: state={item.State}, bytes={item.BytesTransferred}/{item.Size}, finalized={Finalized(item)}");
                    Refresh();
                });
            }
            catch (Exception ex) { Post(() => { if (IsCurrent(request)) Fail(ex); }); }
        }
        public void RequestTransfer(TransferItem transfer)
        {
            if (transfer == null || !PreferencesState.EnableLivePlayback ||
                !PlaybackFormats.SupportsProgressivePlayback(transfer.FullFilename)) return;
            int request = CancelReader();
            Post(() =>
            {
                if (!IsCurrent(request)) return;
                Reset(false);
                item = transfer;
                filename = transfer.Filename;
                visible = wantsPlay = true;
                statusResource = Resource.String.playback_waiting;
                PublishState();
            });
            _ = ResumeWithServiceAsync(request);
        }
        public void Play()
        {
            int request;
            lock (sourceGate) request = generation;
            _ = ResumeWithServiceAsync(request);
        }
        private async Task ResumeWithServiceAsync(int request)
        {
            Post(() =>
            {
                if (!IsCurrent(request) || !visible || !PreferencesState.EnableLivePlayback) return;
                playbackServiceReady = false;
                wantsPlay = true;
                statusResource = Resource.String.playback_waiting;
                PublishState();
            });
            try
            {
                bool ready = await PlaybackForegroundService.EnsureStarted(context);
                Post(() =>
                {
                    if (!IsCurrent(request) || !visible || !PreferencesState.EnableLivePlayback) return;
                    if (!ready) { Fail(new InvalidOperationException("Could not start the playback service.")); return; }
                    playbackServiceReady = true;
                    Refresh();
                });
            }
            catch (Exception ex) { Post(() => { if (IsCurrent(request)) Fail(ex); }); }
        }
        public void Pause()
        {
            CancelReader();
            Post(() =>
            {
                wantsPlay = false;
                // Resume from the last poll, without a UI-thread native query.
                ReleaseEngine();
                statusResource = Resource.String.playback_paused;
                PublishState();
            });
        }
        public void SeekTo(long milliseconds)
        {
            var snapshot = state;
            if (!snapshot.Visible || snapshot.Duration <= 0) return;
            int position = (int)Math.Clamp(milliseconds, 0, snapshot.Duration - 1L);
            // A decoder may be blocked reading a future byte. Wake it before queuing
            // release, so another seek, Pause or Close can interrupt a pending seek.
            int request = CancelReader();
            Post(() =>
            {
                if (!IsCurrent(request) || !visible) return;
                ReleaseEngine();
                resumePosition = position;
                seeking = true;
                statusResource = wantsPlay ? Resource.String.playback_seeking : Resource.String.playback_paused;
                PublishState();
                if (wantsPlay) _ = ResumeWithServiceAsync(request);
            });
        }
        public void Close() { CancelReader(); Post(() => Reset()); }
        private void Reset(bool publish = true)
        {
            wantsPlay = visible = playbackServiceReady = false;
            ReleaseEngine();
            item = null;
            metadataSession++;
            metadata = new AudioMetadata();
            metadataLoading = false;
            metadataUri = null;
            metadataBytes = -1;
            resumePosition = duration = 0;
            seeking = false;
            handler.RemoveCallbacks(tick);
            if (publish) PublishState();
        }
        private static bool Finalized(TransferItem transfer) => transfer != null && !transfer.InProcessing &&
            transfer.State.HasFlag(TransferStates.Succeeded) && !string.IsNullOrEmpty(transfer.FinalUri);
        private void SourceChanged(object sender, TransferItem changed)
        {
            if (!ReferenceEquals(item, changed)) return;
            // Completion wakes the reader even if a native command is waiting on it.
            if (Finalized(changed)) { lock (sourceGate) activeReader?.Complete(); }
            Post(() => { if (ReferenceEquals(item, changed)) Refresh(); });
        }
        private void SourceInvalidated(object sender, TransferItem changed)
        {
            if (!ReferenceEquals(item, changed)) return;
            bool cleared = changed.CancelAndClearFlag || !changed.InProcessing;
            CancelReader();
            Post(() =>
            {
                if (!ReferenceEquals(item, changed)) return;
                ReleaseEngine();
                resumePosition = duration = 0;
                statusResource = Resource.String.playback_waiting;
                if (cleared) Reset(); else Refresh();
            });
        }
        private void Refresh()
        {
            handler.RemoveCallbacks(tick);
            if (!visible) return;
            if (item?.CancelAndClearFlag == true) { Reset(); return; }
            bool finalized = Finalized(item);
            ReadMetadata(finalized);
            if (finalized) activeReader?.Complete();
            if (wantsPlay && playbackServiceReady && item != null && player == null)
            {
                int request;
                lock (sourceGate) request = generation;
                string uri = finalized ? item.FinalUri : item.IncompleteUri;
                if (!string.IsNullOrEmpty(uri))
                {
                    GrowingFileReader reader = null;
                    try
                    {
                        if (!finalized)
                        {
                            var stream = FileSystemService.Instance.OpenProgressiveRead(uri);
                            if (stream == null) throw new NotSupportedException("Storage does not support progressive reads.");
                            try { reader = new GrowingFileReader(stream); }
                            catch { stream.Dispose(); throw; }
                            if (reader.Length == 0) { reader.Dispose(); reader = null; }
                        }
                        if (finalized || reader != null)
                        {
                            lock (sourceGate)
                            {
                                if (request != generation) { reader?.Dispose(); return; }
                                activeReader = reader;
                            }
                            Prepare(uri, reader, request);
                        }
                    }
                    catch (System.IO.FileNotFoundException)
                    {
                        reader?.Dispose();
                        // Source notification can precede creation or race finalization.
                        if (finalized) throw;
                    }
                    catch { reader?.Dispose(); throw; }
                }
            }
            if (prepared)
            {
                if (!seeking) resumePosition = Math.Max(0, player.CurrentPosition);
                duration = Math.Max(0, player.Duration);
            }
            PublishState();
            if (visible) handler.PostDelayed(tick, 500);
        }
        private void ReadMetadata(bool finalized)
        {
            string uri = finalized ? item?.FinalUri : item?.IncompleteUri;
            if (string.IsNullOrEmpty(uri) || metadataLoading ||
                (uri == metadataUri && (metadata.Complete || item.BytesTransferred == metadataBytes))) return;
            metadataLoading = true;
            metadataUri = uri;
            metadataBytes = item.BytesTransferred;
            int session = metadataSession;
            _ = Task.Run(() =>
            {
                AudioMetadata result = null;
                try
                {
                    using var stream = FileSystemService.Instance.OpenProgressiveRead(uri);
                    if (stream != null) result = AudioMetadataReader.Read(stream);
                }
                catch (Exception ex) { Android.Util.Log.Warn("SeekerPlayback", "Metadata unavailable: " + ex.GetType().Name); }
                Post(() =>
                {
                    if (session != metadataSession) return;
                    metadataLoading = false;
                    if (result != null) metadata = result;
                    PublishState();
                });
            });
        }
        private void Prepare(string uri, GrowingFileReader reader, int request)
        {
            var engine = new MediaPlayer();
            player = engine;
            engine.SetWakeMode(context, WakeLockFlags.Partial);
            long seekLength = (seeking || resumePosition > 0) &&
                filename?.EndsWith(".flac", StringComparison.OrdinalIgnoreCase) == true ? item.Size : -1;
            source = reader == null ? null : new GrowingAudioDataSource(reader, seekLength);
            engine.SetAudioAttributes(new AudioAttributes.Builder()
                .SetUsage(AudioUsageKind.Media).SetContentType(AudioContentType.Music).Build());
            engine.Prepared += (s, e) => Post(() =>
            {
                if (!ReferenceEquals(player, engine) || !IsCurrent(request)) return;
                prepared = true;
                Android.Util.Log.Info("SeekerPlayback", "Prepared native decoder; progressive=" + (reader != null));
                if (resumePosition > 0 || seeking)
                {
                    seeking = true;
                    statusResource = Resource.String.playback_seeking;
                    engine.SeekTo(resumePosition);
                }
                // Let the native pipeline run its asynchronous seek/preroll;
                // keep displaying the target until SeekComplete confirms it.
                if (wantsPlay) Start();
                Refresh();
            });
            engine.SeekComplete += (s, e) => Post(() =>
            {
                if (!ReferenceEquals(player, engine) || !IsCurrent(request)) return;
                seeking = false;
                Refresh();
            });
            engine.Completion += (s, e) => Post(() =>
            {
                if (!ReferenceEquals(player, engine) || !IsCurrent(request)) return;
                wantsPlay = false;
                resumePosition = 0;
                ReleaseEngine();
                statusResource = Resource.String.playback_finished;
                PublishState();
            });
            engine.Error += (s, e) =>
            {
                e.Handled = true;
                var message = $"MediaPlayer error: {e.What}, extra: {e.Extra}";
                Post(() =>
                {
                    if (ReferenceEquals(player, engine) && IsCurrent(request)) Fail(new InvalidOperationException(message));
                });
            };
            statusResource = Resource.String.playback_buffering;
            PublishState();
            if (source != null) engine.SetDataSource(source);
            else engine.SetDataSource(context, Android.Net.Uri.Parse(uri));
            if (IsCurrent(request)) engine.PrepareAsync();
        }
        private void Start()
        {
#pragma warning disable CS0618
            if (!hasFocus && audioManager.RequestAudioFocus(this, Android.Media.Stream.Music,
                AudioFocus.Gain) != AudioFocusRequest.Granted)
#pragma warning restore CS0618
            {
                Pause();
                return;
            }
            hasFocus = true;
            player.Start();
            statusResource = Resource.String.playback_playing;
        }
        private void ReleaseEngine()
        {
            GrowingFileReader reader;
            lock (sourceGate)
            {
                reader = activeReader;
                activeReader = null;
                reader?.Cancel();
            }
            var old = player;
            var oldSource = source;
            player = null;
            source = null;
            prepared = false;
            try { old?.Release(); }
            finally
            {
                old?.Dispose();
                // Release can return before Android's final read/close callbacks.
                // Close the file now, but let the Java/managed bridge reclaim the
                // peer once Java no longer references it. Disposing the peer here
                // makes a late native callback try to resurrect an unusable source.
                oldSource?.Close();
                reader?.Dispose();
                if (hasFocus)
                {
#pragma warning disable CS0618
                    audioManager.AbandonAudioFocus(this);
#pragma warning restore CS0618
                    hasFocus = false;
                }
            }
        }
        private void Fail(Exception ex)
        {
            Android.Util.Log.Error("SeekerPlayback", ex.ToString());
            wantsPlay = false;
            try { ReleaseEngine(); }
            catch (Exception releaseError) { Android.Util.Log.Error("SeekerPlayback", releaseError.ToString()); }
            statusResource = Resource.String.playback_failed;
            PublishState();
        }
        private void PublishState()
        {
            int progress = item?.State.HasFlag(TransferStates.Succeeded) == true ? 100
                : item?.Size > 0 ? (int)Math.Clamp(item.BytesTransferred * 100.0 / item.Size, 0, 100) : 0;
            int status = wantsPlay && seeking ? Resource.String.playback_seeking
                : activeReader?.IsWaiting == true && wantsPlay ? Resource.String.playback_buffering : statusResource;
            bool failed = item?.Failed == true && !item.InProcessing;
            if (failed && (!prepared || activeReader?.IsWaiting == true))
                status = item.State.HasFlag(TransferStates.TimedOut) ? Resource.String.playback_peer_timeout : Resource.String.playback_download_failed;
            else if (wantsPlay && player == null && item?.State.HasFlag(TransferStates.Queued) == true)
                status = Resource.String.playback_queued;
            state = new State(visible, filename, wantsPlay, resumePosition,
                duration > 0 ? duration : metadata.DurationMilliseconds, progress, status, metadata, failed);
            main.Post(() => Changed?.Invoke(this, EventArgs.Empty));
        }
        public void OnAudioFocusChange(AudioFocus focusChange)
        {
            if (focusChange != AudioFocus.Gain) Pause();
        }
        private sealed class NoisyReceiver : BroadcastReceiver
        {
            private readonly ProgressivePlaybackPlayer owner;
            public NoisyReceiver(ProgressivePlaybackPlayer owner) { this.owner = owner; }
            public override void OnReceive(Context context, Intent intent) => owner.Pause();
        }
    }
}
