using Android.Media;
using Common;

namespace Seeker.Services
{
    internal sealed class GrowingAudioDataSource : MediaDataSource
    {
        public GrowingFileReader Reader { get; }
        internal long LastReadPosition { get; private set; }
        public GrowingAudioDataSource(GrowingFileReader reader)
        {
            Reader = reader;
        }

        // An unfinished download is an unknown-length source. Advertising the final
        // size lets extractors probe missing tail metadata during preparation.
        public override long Size => -1;
        public override int ReadAt(long position, byte[] buffer, int offset, int count)
        {
            try
            {
                LastReadPosition = position;
#if DEBUG
                long available = Reader.Length;
                if (available > 0 && count > 0 && (position >= available || count > available - position))
                    Android.Util.Log.Debug("SeekerPlayback", $"Reader waiting: offset={position}, requested={count}, available={available}");
#endif
                return Reader.ReadFullyAt(position, buffer, offset, count);
            }
            catch (System.Exception ex)
            {
                Android.Util.Log.Error("SeekerPlayback", ex.ToString());
                // Translate managed I/O failures at the Java callback boundary.
                throw new Java.IO.IOException("Unable to read playback source", new Java.Lang.Exception(ex.Message));
            }
        }
        public override void Close()
        {
            // Native release may call this on a binder thread after the UI session
            // has ended. Cleanup errors must not escape that Java callback.
            try { Reader.Dispose(); }
            catch (System.Exception ex) { Android.Util.Log.Error("SeekerPlayback", ex.ToString()); }
        }
    }
}
