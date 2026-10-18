using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Media;
using Android.Media.Session;
using Android.OS;
using Seeker.Helpers;
using System;
using System.Threading.Tasks;

namespace Seeker.Services
{
    [Service(Name = "com.companyname.andriodapp1.PlaybackService", Exported = false,
        ForegroundServiceType = ForegroundService.TypeMediaPlayback)]
    public sealed class PlaybackForegroundService : Service
    {
        private const string ChannelId = "seeker_music_playback";
        private const int NotificationId = 131;
        private const string EnsureAction = "seeker.playback.ENSURE";
        public const string PlayAction = "seeker.playback.PLAY";
        public const string PauseAction = "seeker.playback.PAUSE";
        public const string StopAction = "seeker.playback.STOP";
        private static readonly object StartupGate = new object();
        private static TaskCompletionSource<bool> startup;
        private static PlaybackForegroundService instance;
        private readonly Handler main = new Handler(Looper.MainLooper);
        private MediaSession session;
        private SessionCallback callback;
        private NotificationManager notifications;
        private volatile bool foreground;
        private bool destroyed, stopping, hasSeenSession, demotionScheduled;
        private Action demote, idleStop;
        private string notificationKey, metadataKey;
        private byte[] artworkBytes;
        private Bitmap artwork;
        private int artworkVersion;
        private long lastDiagnostic;

        // Called from a user-initiated play request, before native preparation/focus.
        public static Task<bool> EnsureStarted(Context context)
        {
            Task<bool> result;
            lock (StartupGate)
            {
                if (instance?.foreground == true) return Task.FromResult(true);
                if (startup != null) return startup.Task;
                startup = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                result = startup.Task;
            }
            try
            {
                var intent = new Intent(context, typeof(PlaybackForegroundService)).SetAction(EnsureAction);
                if (OperatingSystem.IsAndroidVersionAtLeast(26)) context.StartForegroundService(intent);
                else context.StartService(intent);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Error("SeekerPlayback", "Playback service start failed: " + ex);
                CompleteStartup(false);
            }
            return result;
        }
        private static void CompleteStartup(bool ready)
        {
            TaskCompletionSource<bool> completion;
            lock (StartupGate) { completion = startup; startup = null; }
            completion?.TrySetResult(ready);
        }
        public override IBinder OnBind(Intent intent) => null;
        public override void OnCreate()
        {
            base.OnCreate();
            lock (StartupGate) instance = this;
            notifications = (NotificationManager)GetSystemService(NotificationService);
            CommonHelpers.CreateNotificationChannel(this, ChannelId, GetString(Resource.String.playback_notification_channel));
            session = new MediaSession(this, "SeekerPlayback");
            callback = new SessionCallback();
            session.SetCallback(callback, main);
#pragma warning disable CS0618
            session.SetFlags(MediaSessionFlags.HandlesMediaButtons | MediaSessionFlags.HandlesTransportControls);
#pragma warning restore CS0618
            session.SetSessionActivity(OpenPlayer());
            session.Active = true;
            demote = () =>
            {
                demotionScheduled = false;
                if (!destroyed && !ProgressivePlaybackPlayer.Instance.WantsPlay && foreground)
                {
                    if (OperatingSystem.IsAndroidVersionAtLeast(24)) StopForeground(StopForegroundFlags.Detach);
                    else StopForeground(false);
                    foreground = false;
                    Android.Util.Log.Info("SeekerPlayback", "Playback service left foreground while paused");
                }
            };
            idleStop = () => { if (!destroyed && !ProgressivePlaybackPlayer.Instance.Visible) StopPlaybackService(); };
            ProgressivePlaybackPlayer.Instance.Changed += PlayerChanged;
        }
        public override StartCommandResult OnStartCommand(Intent intent, StartCommandFlags flags, int startId)
        {
            string action = intent?.Action;
            if (action == StopAction)
            {
                ProgressivePlaybackPlayer.Instance.Close();
                StopPlaybackService();
                return StartCommandResult.NotSticky;
            }
            if (action == PauseAction)
            {
                ProgressivePlaybackPlayer.Instance.Pause();
                return StartCommandResult.NotSticky;
            }
            try
            {
                // Promote before allowing background audio-focus requests (API 35+).
                var notification = BuildNotification();
                if (OperatingSystem.IsAndroidVersionAtLeast(29))
                    StartForeground(NotificationId, notification, ForegroundService.TypeMediaPlayback);
                else StartForeground(NotificationId, notification);
                foreground = true;
                main.RemoveCallbacks(demote);
                demotionScheduled = false;
                CompleteStartup(true);
                Android.Util.Log.Info("SeekerPlayback", "Playback service foreground: mediaPlayback");
                if (action == PlayAction) ProgressivePlaybackPlayer.Instance.Play();
                UpdateSession();
            }
            catch (Exception ex)
            {
                Android.Util.Log.Error("SeekerPlayback", "Playback service promotion failed: " + ex);
                CompleteStartup(false);
                ProgressivePlaybackPlayer.Instance.Pause();
                StopPlaybackService();
            }
            return StartCommandResult.NotSticky;
        }
        private void PlayerChanged(object sender, EventArgs e)
        {
            try { UpdateSession(); }
            catch (Exception ex) { Android.Util.Log.Error("SeekerPlayback", "Media controls update failed: " + ex); }
        }
        private void UpdateSession()
        {
            if (destroyed || stopping) return;
            var p = ProgressivePlaybackPlayer.Instance;
            if (!p.Visible)
            {
                if (hasSeenSession) StopPlaybackService();
                else { main.RemoveCallbacks(idleStop); main.PostDelayed(idleStop, 2000); }
                return;
            }
            hasSeenSession = true;
            main.RemoveCallbacks(idleStop);
            if (p.WantsPlay) { main.RemoveCallbacks(demote); demotionScheduled = false; }
            else if (foreground && !demotionScheduled)
            {
                // Brief pause/resume grace; do not retain foreground priority indefinitely.
                demotionScheduled = true;
                main.PostDelayed(demote, 30000);
            }
            UpdateArtwork(p.Artwork);
            string currentMetadata = $"{p.Title}\n{p.Artist}\n{p.Album}\n{p.Duration}";
            if (metadataKey != currentMetadata)
            {
                metadataKey = currentMetadata;
                using var builder = new MediaMetadata.Builder();
                builder.PutString(MediaMetadata.MetadataKeyTitle, p.Title);
                builder.PutString(MediaMetadata.MetadataKeyArtist, p.Artist ?? "");
                builder.PutString(MediaMetadata.MetadataKeyAlbum, p.Album ?? "");
                builder.PutLong(MediaMetadata.MetadataKeyDuration, p.Duration);
                if (artwork != null) builder.PutBitmap(MediaMetadata.MetadataKeyAlbumArt, artwork);
                using var metadata = builder.Build();
                session.SetMetadata(metadata);
            }
            bool playing = p.WantsPlay && p.StatusResource == Resource.String.playback_playing;
            var code = playing ? PlaybackStateCode.Playing : p.WantsPlay ? PlaybackStateCode.Buffering : PlaybackStateCode.Paused;
            using (var builder = new PlaybackState.Builder())
            {
                builder.SetActions(PlaybackState.ActionPlay | PlaybackState.ActionPause | PlaybackState.ActionPlayPause | PlaybackState.ActionStop |
                    (p.CanSeek ? PlaybackState.ActionSeekTo : 0));
                builder.SetState(code, p.Position, playing ? 1f : 0f, SystemClock.ElapsedRealtime());
                using var state = builder.Build();
                session.SetPlaybackState(state);
            }
            string currentNotification = $"{currentMetadata}\n{p.WantsPlay}\n{p.StatusResource}";
            if (notificationKey != currentNotification)
            {
                notificationKey = currentNotification;
                notifications.Notify(NotificationId, BuildNotification());
            }
#if DEBUG
            if (SystemClock.ElapsedRealtime() - lastDiagnostic >= 5000)
            {
                lastDiagnostic = SystemClock.ElapsedRealtime();
                Android.Util.Log.Debug("SeekerPlayback", $"Media session: {code}, position={p.Position}, foreground={foreground}");
            }
#endif
        }
        private PendingIntent OpenPlayer() => PendingIntent.GetActivity(this, 21000,
            new Intent(this, typeof(MainActivity)).AddFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop),
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
        private PendingIntent Control(string action, int request, bool startForeground = false)
        {
            var intent = new Intent(this, typeof(PlaybackForegroundService)).SetAction(action);
            var flags = PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable;
            return startForeground && OperatingSystem.IsAndroidVersionAtLeast(26)
                ? PendingIntent.GetForegroundService(this, request, intent, flags)
                : PendingIntent.GetService(this, request, intent, flags);
        }
        private Notification BuildNotification()
        {
            var p = ProgressivePlaybackPlayer.Instance;
            using var builder = OperatingSystem.IsAndroidVersionAtLeast(26)
                ? new Notification.Builder(this, ChannelId) : new Notification.Builder(this);
            builder.SetSmallIcon(Resource.Drawable.ic_stat_soulseekicontransparent)
                .SetContentTitle(p.Visible ? p.Title : GetString(Resource.String.app_name))
                .SetContentText(p.Artist ?? GetString(p.StatusResource))
                .SetContentIntent(OpenPlayer()).SetOnlyAlertOnce(true)
                .SetCategory(Notification.CategoryTransport).SetVisibility(NotificationVisibility.Public)
                .SetOngoing(p.WantsPlay).SetDeleteIntent(Control(StopAction, 21003));
            if (artwork != null) builder.SetLargeIcon(artwork);
            if (OperatingSystem.IsAndroidVersionAtLeast(31)) builder.SetForegroundServiceBehavior((int)NotificationForegroundService.Immediate);
            builder.AddAction(p.WantsPlay ? Resource.Drawable.paused : Resource.Drawable.player_play,
                GetString(p.WantsPlay ? Resource.String.miniplayer_pause : Resource.String.miniplayer_play),
                Control(p.WantsPlay ? PauseAction : PlayAction, p.WantsPlay ? 21001 : 21002, !p.WantsPlay));
            builder.AddAction(Resource.Drawable.close_20dp, GetString(Resource.String.miniplayer_close), Control(StopAction, 21003));
            using var style = new Notification.MediaStyle();
            style.SetMediaSession(session.SessionToken).SetShowActionsInCompactView(0, 1);
            builder.SetStyle(style);
            return builder.Build();
        }
        private void UpdateArtwork(byte[] bytes)
        {
            if (ReferenceEquals(bytes, artworkBytes)) return;
            artworkBytes = bytes;
            int version = ++artworkVersion;
            metadataKey = notificationKey = null;
            var previous = artwork; artwork = null;
            previous?.Dispose();
            if (bytes == null) return;
            _ = Task.Run(() =>
            {
                Bitmap decoded = null;
                try
                {
                    using var options = new BitmapFactory.Options { InJustDecodeBounds = true };
                    BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length, options);
                    if (options.OutWidth <= 0 || options.OutHeight <= 0) return;
                    int sample = 1;
                    while (options.OutWidth / sample > 256 || options.OutHeight / sample > 256) sample *= 2;
                    options.InJustDecodeBounds = false; options.InSampleSize = sample;
                    decoded = BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length, options);
                }
                catch (Exception ex) { Android.Util.Log.Warn("SeekerPlayback", "Notification artwork unavailable: " + ex.GetType().Name); }
                var image = decoded;
                main.Post(() =>
                {
                    if (destroyed || version != artworkVersion) { image?.Dispose(); return; }
                    artwork = image;
                    metadataKey = notificationKey = null;
                    PlayerChanged(this, EventArgs.Empty);
                });
            });
        }
        private void StopPlaybackService()
        {
            if (stopping) return;
            stopping = true;
            StopForeground(true);
            foreground = false;
            notifications.Cancel(NotificationId);
            StopSelf();
        }
        public override void OnTaskRemoved(Intent rootIntent)
        {
            if (!ProgressivePlaybackPlayer.Instance.WantsPlay)
            {
                ProgressivePlaybackPlayer.Instance.Close();
                StopPlaybackService();
            }
            base.OnTaskRemoved(rootIntent);
        }
        public override void OnDestroy()
        {
            destroyed = true;
            lock (StartupGate) { if (ReferenceEquals(instance, this)) instance = null; }
            CompleteStartup(false);
            ProgressivePlaybackPlayer.Instance.Changed -= PlayerChanged;
            main.RemoveCallbacks(demote); main.RemoveCallbacks(idleStop);
            session.Active = false;
            session.SetCallback(null);
            session.Release(); session.Dispose(); callback = null;
            artworkVersion++; artwork?.Dispose(); artwork = null;
            StopForeground(true); notifications.Cancel(NotificationId);
            if (!stopping) ProgressivePlaybackPlayer.Instance.Close();
            base.OnDestroy();
        }
        private sealed class SessionCallback : MediaSession.Callback
        {
            public override void OnPlay() => ProgressivePlaybackPlayer.Instance.Play();
            public override void OnSeekTo(long pos) => ProgressivePlaybackPlayer.Instance.SeekTo(pos);
            public override void OnPause() => ProgressivePlaybackPlayer.Instance.Pause();
            public override void OnStop() => ProgressivePlaybackPlayer.Instance.Close();
        }
    }
}
