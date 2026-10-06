using Android.Views;
using Android.Widget;
using Seeker.Services;
using System;

namespace Seeker
{
    public partial class MainActivity
    {
        protected override void OnStop()
        {
            ProgressivePlaybackPlayer.Instance.Changed -= PlaybackChanged;
            base.OnStop();
        }

        private void InitializeMiniplayer()
        {
            FindViewById<Button>(Resource.Id.livePlayPause).Click += (s, e) =>
            {
                var playback = ProgressivePlaybackPlayer.Instance;
                if (playback.WantsPlay) playback.Pause();
                else playback.Play();
            };
            FindViewById<Button>(Resource.Id.liveClose).Click += (s, e) => ProgressivePlaybackPlayer.Instance.Close();
        }

        private void PlaybackChanged(object sender, EventArgs e) => UpdateMiniplayer();

        private void UpdateMiniplayer()
        {
            var playback = ProgressivePlaybackPlayer.Instance;
            FindViewById(Resource.Id.livePlayer).Visibility = playback.Visible ? ViewStates.Visible : ViewStates.Gone;
            if (!playback.Visible) return;
            FindViewById<TextView>(Resource.Id.liveFilename).Text = playback.Filename;
            FindViewById<TextView>(Resource.Id.liveStatus).SetText(playback.StatusResource);
            FindViewById<Button>(Resource.Id.livePlayPause).SetText(playback.WantsPlay
                ? Resource.String.miniplayer_pause : Resource.String.miniplayer_play);
            int duration = playback.Duration;
            int position = playback.Position;
            var bar = FindViewById<ProgressBar>(Resource.Id.livePlaybackProgress);
            bar.Indeterminate = duration <= 0;
            bar.Max = Math.Max(1, duration);
            bar.Progress = position;
            FindViewById<TextView>(Resource.Id.liveElapsed).Text = duration > 0
                ? FormatTime(position) + " / " + FormatTime(duration) : FormatTime(position);
            FindViewById<ProgressBar>(Resource.Id.liveDownloadProgress).Progress = playback.DownloadProgress;
            FindViewById<TextView>(Resource.Id.liveDownloadLabel).Text =
                string.Format(GetString(Resource.String.miniplayer_download_percent), playback.DownloadProgress);
        }

        private static string FormatTime(int milliseconds) =>
            TimeSpan.FromMilliseconds(milliseconds).ToString(@"hh\:mm\:ss");
    }
}
