using Android.Views;
using Android.Widget;
using Seeker.Services;
using System;

namespace Seeker
{
    public partial class MainActivity
    {
        private PlayerArtworkBinding miniArtwork;
        protected override void OnStop()
        {
            ProgressivePlaybackPlayer.Instance.Changed -= PlaybackChanged;
            miniArtwork?.Dispose();
            miniArtwork = null;
            base.OnStop();
        }
        private void InitializeMiniplayer()
        {
            FindViewById<ImageButton>(Resource.Id.livePlayPause).Click += (s, e) => PlayerUi.Toggle();
            FindViewById<ImageButton>(Resource.Id.liveClose).Click += (s, e) => ProgressivePlaybackPlayer.Instance.Close();
            FindViewById(Resource.Id.liveExpand).Click += (s, e) =>
            {
                if (!SupportFragmentManager.IsStateSaved && SupportFragmentManager.FindFragmentByTag("expanded-player") == null)
                    new ExpandedPlayerDialog().Show(SupportFragmentManager, "expanded-player");
            };
        }
        private void PlaybackChanged(object sender, EventArgs e) => UpdateMiniplayer();
        private void UpdateMiniplayer()
        {
            var playback = ProgressivePlaybackPlayer.Instance;
            FindViewById(Resource.Id.livePlayer).Visibility = playback.Visible ? ViewStates.Visible : ViewStates.Gone;
            if (!playback.Visible) { miniArtwork?.Dispose(); miniArtwork = null; return; }
            FindViewById<TextView>(Resource.Id.liveFilename).Text = playback.Title;
            FindViewById<TextView>(Resource.Id.liveStatus).Text = GetString(playback.StatusResource) +
                (string.IsNullOrEmpty(playback.Artist) ? "" : " · " + playback.Artist);
            PlayerUi.PlayButton(FindViewById<ImageButton>(Resource.Id.livePlayPause));
            PlayerUi.Progress(FindViewById<ProgressBar>(Resource.Id.livePlaybackProgress));
            miniArtwork ??= new PlayerArtworkBinding(FindViewById<ImageView>(Resource.Id.liveArtwork));
            miniArtwork.Set(playback.Artwork);
        }
    }
}
