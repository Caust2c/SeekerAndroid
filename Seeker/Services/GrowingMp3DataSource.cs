using Android.Media;
using Common;

namespace Seeker.Services
{
    internal sealed class GrowingMp3DataSource : MediaDataSource
    {
        public GrowingFileReader Reader { get; }
        private readonly long size;

        public GrowingMp3DataSource(GrowingFileReader reader, long size)
        {
            Reader = reader;
            this.size = size;
        }

        public override long Size => size;
        public override int ReadAt(long position, byte[] buffer, int offset, int count) =>
            Reader.ReadAt(position, buffer, offset, count);
        public override void Close() => Reader.Dispose();
    }
}
