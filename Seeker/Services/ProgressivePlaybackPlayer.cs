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
    /// <summary>One foreground session; DownloadService owns the download.</summary>
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
        private GrowingMp3DataSource source;
        private bool prepared, wantsPlay, hasFocus, visible;
        private string filename;
        private int resumePosition, duration;
        private int statusResource = Resource.String.playback_waiting;
        private sealed record State(bool Visible, string Filename, bool WantsPlay, int Position,
            int Duration, int DownloadProgress, int StatusResource);
        private volatile State state = new(false, null, false, 0, 0, 0, Resource.String.playback_waiting);
        public event EventHandler Changed;
        public bool Visible => state.Visible;
        public string Filename => state.Filename;
        public bool WantsPlay => state.WantsPlay;
        public int Position => state.Position;
        public int Duration => state.Duration;
        public int DownloadProgress => state.DownloadProgress;
        public int StatusResource => state.StatusResource;

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
                !file.FullFileName.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)) return;
            int request = CancelReader();
            Post(() =>
            {
                if (!IsCurrent(request)) return;
                Reset();
                filename = SimpleHelpers.GetFileNameFromFile(file.FullFileName).ToString();
                visible = wantsPlay = true;
                statusResource = Resource.String.playback_waiting;
                Refresh();
            });
            try
            {
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
                    Refresh();
                });
            }
            catch (Exception ex) { Post(() => { if (IsCurrent(request)) Fail(ex); }); }
        }
        public void Play() => Post(() =>
        {
            if (!visible || !PreferencesState.EnableLivePlayback) return;
            wantsPlay = true;
            statusResource = Resource.String.playback_waiting;
            Refresh();
        });
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
        public void Close() { CancelReader(); Post(Reset); }
        private void Reset()
        {
            wantsPlay = visible = false;
            ReleaseEngine();
            item = null;
            resumePosition = duration = 0;
            handler.RemoveCallbacks(tick);
            PublishState();
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
            if (finalized) activeReader?.Complete();
            if (wantsPlay && item != null && player == null)
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
                resumePosition = Math.Max(0, player.CurrentPosition);
                duration = Math.Max(0, player.Duration);
            }
            PublishState();
            if (visible) handler.PostDelayed(tick, 500);
        }
        private void Prepare(string uri, GrowingFileReader reader, int request)
        {
            var engine = new MediaPlayer();
            player = engine;
            source = reader == null ? null : new GrowingMp3DataSource(reader);
            engine.SetAudioAttributes(new AudioAttributes.Builder()
                .SetUsage(AudioUsageKind.Media).SetContentType(AudioContentType.Music).Build());
            engine.Prepared += (s, e) => Post(() =>
            {
                if (!ReferenceEquals(player, engine) || !IsCurrent(request)) return;
                prepared = true;
                if (resumePosition > 0) engine.SeekTo(resumePosition);
                if (wantsPlay) Start();
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
                oldSource?.Dispose();
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
            int status = activeReader?.IsWaiting == true && wantsPlay ? Resource.String.playback_buffering : statusResource;
            state = new State(visible, filename, wantsPlay, resumePosition, duration, progress, status);
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
