using Android.Media;
using Common;

namespace Seeker.Services
{
    internal sealed class GrowingMp3DataSource : MediaDataSource
    {
        public GrowingFileReader Reader { get; }
        public GrowingMp3DataSource(GrowingFileReader reader)
        {
            Reader = reader;
        }

        // An unfinished download is an unknown-length source. Advertising the final
        // size lets extractors probe missing tail metadata during preparation.
        public override long Size => -1;
        public override int ReadAt(long position, byte[] buffer, int offset, int count)
        {
            try { return Reader.ReadAt(position, buffer, offset, count); }
            catch (System.Exception ex)
            {
                Android.Util.Log.Error("SeekerPlayback", ex.ToString());
                // Translate managed I/O failures at the Java callback boundary.
                throw new Java.IO.IOException("Unable to read playback source", new Java.Lang.Exception(ex.Message));
            }
        }
        public override void Close() => Reader.Dispose();
    }
}
