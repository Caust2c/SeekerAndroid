using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;
using Google.Android.Material.BottomSheet;
using Seeker.Services;
using System;
using System.Threading.Tasks;

namespace Seeker
{
    public sealed class ExpandedPlayerDialog : BottomSheetDialogFragment
    {
        private PlayerArtworkBinding artwork;
        public override View OnCreateView(LayoutInflater inflater, ViewGroup container, Bundle savedInstanceState) =>
            inflater.Inflate(Resource.Layout.player_expanded, container, false);
        public override void OnViewCreated(View view, Bundle savedInstanceState)
        {
            base.OnViewCreated(view, savedInstanceState);
            artwork = new PlayerArtworkBinding(view.FindViewById<ImageView>(Resource.Id.playerArtwork));
            view.FindViewById(Resource.Id.playerCollapse).Click += (s, e) => Dismiss();
            view.FindViewById(Resource.Id.playerClose).Click += (s, e) => { ProgressivePlaybackPlayer.Instance.Close(); Dismiss(); };
            view.FindViewById(Resource.Id.playerPlayPause).Click += (s, e) => PlayerUi.Toggle();
            view.FindViewById(Resource.Id.playerRetry).Click += (s, e) => ProgressivePlaybackPlayer.Instance.RetryDownload();
        }
        public override void OnStart()
        {
            base.OnStart();
            var sheet = ((BottomSheetDialog)Dialog).FindViewById<View>(Resource.Id.design_bottom_sheet);
            if (sheet != null)
            {
                sheet.SetBackgroundColor(Color.Transparent);
                var behavior = BottomSheetBehavior.From(sheet);
                behavior.SkipCollapsed = true;
                behavior.State = BottomSheetBehavior.StateExpanded;
            }
            ProgressivePlaybackPlayer.Instance.Changed += Update;
            Update(this, EventArgs.Empty);
        }
        public override void OnStop()
        {
            ProgressivePlaybackPlayer.Instance.Changed -= Update;
            base.OnStop();
        }
        public override void OnDestroyView()
        {
            artwork?.Dispose(); artwork = null;
            base.OnDestroyView();
        }
        private void Update(object sender, EventArgs e)
        {
            if (View == null) return;
            var p = ProgressivePlaybackPlayer.Instance;
            if (!p.Visible) { DismissAllowingStateLoss(); return; }
            View.FindViewById<TextView>(Resource.Id.playerTitle).Text = p.Title;
            View.FindViewById<TextView>(Resource.Id.playerArtist).Text = p.Artist ?? GetString(Resource.String.player_artist_unknown);
            var album = View.FindViewById<TextView>(Resource.Id.playerAlbum);
            album.Text = p.Album;
            album.Visibility = string.IsNullOrWhiteSpace(p.Album) ? ViewStates.Gone : ViewStates.Visible;
            View.FindViewById<TextView>(Resource.Id.playerQuality).Text = p.Quality;
            View.FindViewById<TextView>(Resource.Id.playerStatus).SetText(p.StatusResource);
            View.FindViewById<TextView>(Resource.Id.playerElapsed).Text = PlayerUi.Time(p.Position);
            View.FindViewById<TextView>(Resource.Id.playerDuration).Text = p.Duration > 0 ? PlayerUi.Time(p.Duration) : "—";
            PlayerUi.Progress(View.FindViewById<ProgressBar>(Resource.Id.playerProgress));
            PlayerUi.PlayButton(View.FindViewById<ImageButton>(Resource.Id.playerPlayPause));
            View.FindViewById<TextView>(Resource.Id.playerDownloadLabel).Text = string.Format(GetString(Resource.String.miniplayer_download_percent), p.DownloadProgress);
            View.FindViewById<ProgressBar>(Resource.Id.playerDownloadProgress).Progress = p.DownloadProgress;
            View.FindViewById(Resource.Id.playerRetry).Visibility = p.CanRetry ? ViewStates.Visible : ViewStates.Gone;
            artwork?.Set(p.Artwork);
        }
    }
    internal static class PlayerUi
    {
        public static void Toggle()
        {
            var p = ProgressivePlaybackPlayer.Instance;
            if (p.WantsPlay) p.Pause(); else p.Play();
        }
        public static void PlayButton(ImageButton button)
        {
            bool playing = ProgressivePlaybackPlayer.Instance.WantsPlay;
            button.SetImageResource(playing ? Resource.Drawable.player_pause : Resource.Drawable.player_play);
            button.ContentDescription = button.Context.GetString(playing ? Resource.String.miniplayer_pause : Resource.String.miniplayer_play);
        }
        public static void Progress(ProgressBar bar)
        {
            var p = ProgressivePlaybackPlayer.Instance;
            bar.Indeterminate = false;
            bar.Max = Math.Max(1, p.Duration);
            bar.Progress = p.Position;
        }
        public static string Time(int milliseconds)
        {
            var t = TimeSpan.FromMilliseconds(milliseconds);
            return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
        }
    }
    // Decode untrusted embedded images off the UI thread with a bounded bitmap size.
    internal sealed class PlayerArtworkBinding : IDisposable
    {
        private readonly ImageView view;
        private byte[] current;
        private Bitmap bitmap;
        private int version;
        private bool disposed;
        public PlayerArtworkBinding(ImageView view) { this.view = view; }
        public void Set(byte[] bytes)
        {
            if (disposed || ReferenceEquals(current, bytes)) return;
            current = bytes;
            int request = ++version;
            view.SetImageResource(Resource.Drawable.player_cover);
            bitmap?.Dispose(); bitmap = null;
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
                    while (options.OutWidth / sample > 1024 || options.OutHeight / sample > 1024) sample *= 2;
                    options.InJustDecodeBounds = false;
                    options.InSampleSize = sample;
                    decoded = BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length, options);
                }
                catch (Exception ex) { Android.Util.Log.Warn("SeekerPlayback", "Artwork unavailable: " + ex.GetType().Name); }
                var image = decoded;
                new Handler(Looper.MainLooper).Post(() =>
                {
                    if (disposed || request != version) { image?.Dispose(); return; }
                    bitmap = image;
                    if (image != null) view.SetImageBitmap(image);
                });
            });
        }
        public void Dispose()
        {
            disposed = true; version++;
            view.SetImageResource(Resource.Drawable.player_cover);
            bitmap?.Dispose(); bitmap = null; current = null;
        }
    }
}
