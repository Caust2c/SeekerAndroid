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
    /// <summary>One foreground playback session. Downloads remain owned by DownloadService.</summary>
    public sealed class ProgressivePlaybackPlayer : Java.Lang.Object, AudioManager.IOnAudioFocusChangeListener
    {
        public static ProgressivePlaybackPlayer Instance { get; set; }
        private readonly Context context;
        private readonly Handler handler = new Handler(Looper.MainLooper);
        private readonly AudioManager audioManager;
        private readonly Action tick;
        private readonly NoisyReceiver noisyReceiver;
        private TransferItem item;
        private MediaPlayer player;
        private GrowingMp3DataSource source;
        private bool prepared;
        private bool preparing;
        private bool wantsPlay;
        private bool hasFocus;
        private int generation;
        private int resumePosition;
        private int statusResource = Resource.String.playback_waiting;

        public event EventHandler Changed;
        public bool Visible { get; private set; }
        public string Filename { get; private set; }
        public bool WantsPlay => wantsPlay;
        public int Position => prepared ? player.CurrentPosition : resumePosition;
        public int Duration => prepared ? Math.Max(0, player.Duration) : 0;
        public int DownloadProgress => item?.State.HasFlag(TransferStates.Succeeded) == true ? 100
            : item?.Size > 0 ? (int)Math.Clamp(item.BytesTransferred * 100.0 / item.Size, 0, 100) : 0;
        public int StatusResource => source?.Reader.IsWaiting == true && wantsPlay
            ? Resource.String.playback_buffering : statusResource;

        public ProgressivePlaybackPlayer(Context context)
        {
            this.context = context.ApplicationContext;
            audioManager = (AudioManager)this.context.GetSystemService(Context.AudioService);
            tick = Refresh;
            DownloadService.Instance.PlaybackSourceChanged += SourceChanged;
            DownloadService.Instance.PlaybackSourceInvalidated += SourceInvalidated;
            noisyReceiver = new NoisyReceiver(this);
            ContextCompat.RegisterReceiver(this.context, noisyReceiver,
                new IntentFilter(AudioManager.ActionAudioBecomingNoisy), ContextCompat.ReceiverNotExported);
        }

        public async Task RequestAsync(FullFileInfo file, string username)
        {
            if (!PreferencesState.EnableLivePlayback ||
                !file.FullFileName.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)) return;
            Close();
            int request = generation;
            Filename = SimpleHelpers.GetFileNameFromFile(file.FullFileName).ToString();
            Visible = true;
            wantsPlay = true;
            statusResource = Resource.String.playback_waiting;
            Refresh();
            try
            {
                var transfer = await DownloadService.Instance.EnqueueFileForPlaybackAsync(file, username);
                if (request != generation) return;
                if (transfer == null) { Close(); return; }
                item = transfer;
                Refresh();
            }
            catch (Exception ex)
            {
                if (request == generation) Fail(ex);
            }
        }

        public void Play()
        {
            if (!Visible || !PreferencesState.EnableLivePlayback) return;
            wantsPlay = true;
            if (prepared) Start();
            else
            {
                statusResource = Resource.String.playback_waiting;
                Refresh();
            }
        }

        public void Pause()
        {
            wantsPlay = false;
            if (prepared) resumePosition = player.CurrentPosition;
            // Close the data source before release, so a pending read cannot hold release up.
            ReleaseEngine();
            statusResource = Resource.String.playback_paused;
            PublishState();
        }

        public void Close()
        {
            generation++;
            preparing = false;
            wantsPlay = false;
            ReleaseEngine();
            item = null;
            resumePosition = 0;
            Visible = false;
            handler.RemoveCallbacks(tick);
            PublishState();
        }

        private void SourceChanged(object sender, TransferItem changed) => handler.Post(() =>
        {
            if (ReferenceEquals(item, changed)) Refresh();
        });

        private void SourceInvalidated(object sender, TransferItem changed)
        {
            // Wake the native reader synchronously before the downloader deletes/replaces bytes.
            if (!ReferenceEquals(item, changed)) return;
            source?.Close();
            handler.Post(() =>
            {
                if (!ReferenceEquals(item, changed)) return;
                generation++;
                preparing = false;
                ReleaseEngine();
                resumePosition = 0;
                statusResource = Resource.String.playback_waiting;
                if (changed.CancelAndClearFlag || !changed.InProcessing) Close();
                else Refresh();
            });
        }

        private async void Refresh()
        {
            handler.RemoveCallbacks(tick);
            if (!Visible) return;
            if (item?.CancelAndClearFlag == true) { Close(); return; }
            bool finalized = item != null && !item.InProcessing &&
                item.State.HasFlag(TransferStates.Succeeded) && !string.IsNullOrEmpty(item.FinalUri);
            if (finalized) source?.Reader.Complete();
            if (item?.Failed == true && !item.InProcessing)
            {
                Pause();
                statusResource = Resource.String.playback_download_failed;
            }
            if (wantsPlay && item != null && player == null && !preparing)
            {
                preparing = true;
                int request = generation;
                var selected = item;
                long selectedSize = selected.Size;
                string uri = finalized ? selected.FinalUri : selected.IncompleteUri;
                try
                {
                    // SAF providers are completion-only. Never probe storage on the UI thread.
                    var reader = await Task.Run(() =>
                    {
                        if (finalized || selectedSize <= GrowingFileReader.StartThresholdBytes ||
                            string.IsNullOrEmpty(uri)) return null;
                        var stream = FileSystemService.Instance.OpenProgressiveRead(uri);
                        if (stream == null) return null;
                        try
                        {
                            var growing = new GrowingFileReader(stream);
                            if (GrowingFileReader.CanStart(selectedSize, growing.Length, false)) return growing;
                            growing.Dispose();
                            return null;
                        }
                        catch { stream.Dispose(); throw; }
                    });
                    if (request != generation || !wantsPlay)
                    {
                        reader?.Dispose();
                        return;
                    }
                    if (finalized || reader != null)
                        Prepare(uri, reader, selectedSize);
                }
                catch (System.IO.IOException ex)
                {
                    // The writer may not have opened yet, or completion may have moved the file.
                    // The next source notification/tick rechecks the current URI.
                    if (request == generation && (finalized || player != null)) Fail(ex);
                }
                catch (Exception ex) { if (request == generation) Fail(ex); }
                finally { if (request == generation) preparing = false; }
            }
            PublishState();
            if (Visible) handler.PostDelayed(tick, 500);
        }

        private void Prepare(string uri, GrowingFileReader reader, long size)
        {
            var engine = new MediaPlayer();
            player = engine;
            source = reader == null ? null : new GrowingMp3DataSource(reader, size);
            engine.SetAudioAttributes(new AudioAttributes.Builder()
                .SetUsage(AudioUsageKind.Media).SetContentType(AudioContentType.Music).Build());
            engine.Prepared += (s, e) => handler.Post(() =>
            {
                if (!ReferenceEquals(player, engine)) return;
                prepared = true;
                if (resumePosition > 0) engine.SeekTo(resumePosition);
                if (wantsPlay) Start();
                PublishState();
            });
            engine.Completion += (s, e) => handler.Post(() =>
            {
                if (!ReferenceEquals(player, engine)) return;
                wantsPlay = false;
                resumePosition = 0;
                ReleaseEngine();
                statusResource = Resource.String.playback_finished;
                PublishState();
            });
            engine.Error += (s, e) =>
            {
                e.Handled = true;
                handler.Post(() =>
                {
                    if (ReferenceEquals(player, engine)) Fail(new InvalidOperationException("MediaPlayer error: " + e.What));
                });
            };
            if (source != null) engine.SetDataSource(source);
            else engine.SetDataSource(context, Android.Net.Uri.Parse(uri));
            statusResource = Resource.String.playback_buffering;
            engine.PrepareAsync();
        }

        private void Start()
        {
            // Legacy focus API covers the project's API 23 minimum without a new dependency.
#pragma warning disable CS0618
            if (!hasFocus && audioManager.RequestAudioFocus(this, Android.Media.Stream.Music,
                AudioFocus.Gain) != AudioFocusRequest.Granted)
#pragma warning restore CS0618
            {
                Pause();
                return;
            }
            hasFocus = true;
            try
            {
                player.Start();
                statusResource = Resource.String.playback_playing;
            }
            catch (Exception ex) { Fail(ex); }
        }

        private void ReleaseEngine()
        {
            source?.Close();
            var old = player;
            player = null;
            prepared = false;
            old?.Release();
            old?.Dispose();
            source?.Dispose();
            source = null;
            if (hasFocus)
            {
#pragma warning disable CS0618
                audioManager.AbandonAudioFocus(this);
#pragma warning restore CS0618
                hasFocus = false;
            }
        }

        private void Fail(Exception ex)
        {
            Logger.Debug("Live playback failed: " + ex);
            wantsPlay = false;
            ReleaseEngine();
            statusResource = Resource.String.playback_failed;
            PublishState();
        }

        private void PublishState() => Changed?.Invoke(this, EventArgs.Empty);
        public void OnAudioFocusChange(AudioFocus focusChange)
        {
            if (focusChange != AudioFocus.Gain) handler.Post(Pause);
        }

        private sealed class NoisyReceiver : BroadcastReceiver
        {
            private readonly ProgressivePlaybackPlayer owner;
            public NoisyReceiver(ProgressivePlaybackPlayer owner) { this.owner = owner; }
            public override void OnReceive(Context context, Intent intent) => owner.Pause();
        }
    }
}
