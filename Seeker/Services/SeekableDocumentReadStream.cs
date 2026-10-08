using Android.OS;
using System;
using System.IO;

namespace Seeker.Services
{
    /// <summary>Keep a document descriptor open across download finalization.</summary>
    internal sealed class SeekableDocumentReadStream : Stream
    {
        private readonly ParcelFileDescriptor.AutoCloseInputStream input;
        private readonly Java.Nio.Channels.FileChannel channel;
        private bool disposed;
        public SeekableDocumentReadStream(ParcelFileDescriptor descriptor)
        {
            input = new ParcelFileDescriptor.AutoCloseInputStream(descriptor);
            channel = input.Channel;
            try
            {
                channel.Position(0);
                _ = channel.Size();
            }
            catch (Exception ex)
            {
                input.Close();
                channel.Dispose();
                input.Dispose();
                throw new NotSupportedException("This document provider does not support seekable playback.", ex);
            }
        }
        public override bool CanRead => !disposed;
        public override bool CanSeek => !disposed;
        public override bool CanWrite => false;
        public override long Length => channel.Size();
        public override long Position { get => channel.Position(); set => channel.Position(value); }
        public override int Read(byte[] buffer, int offset, int count) => Math.Max(0, input.Read(buffer, offset, count));
        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => Position + offset,
                SeekOrigin.End => Length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            return Position;
        }
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing && !disposed)
            {
                disposed = true;
                try { input.Close(); }
                finally { channel.Dispose(); input.Dispose(); }
            }
            base.Dispose(disposing);
        }
    }
}
