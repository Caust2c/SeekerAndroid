using Android.Media;
using Common;

namespace Seeker.Services
{
    internal sealed class GrowingAudioDataSource : MediaDataSource
    {
        public GrowingFileReader Reader { get; }
        private readonly long length;
        internal long LastReadPosition { get; private set; }
        public GrowingAudioDataSource(GrowingFileReader reader, long length = -1)
        {
            Reader = reader;
            this.length = length > 0 ? length : -1;
        }

        // An unfinished download is an unknown-length source. Advertising the final
        // size lets extractors probe missing tail metadata during preparation.
        // A seek session can supply the peer's final size: libFLAC requires it for
        // timestamp seeks. Missing bytes still block in Reader, never return EOF.
        public override long Size => length;
        public override int ReadAt(long position, byte[] buffer, int offset, int count)
        {
            try
            {
                LastReadPosition = position;
                if (length > 0)
                {
                    if (position >= length) return -1;
                    count = (int)System.Math.Min(count, length - position);
                }
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
