using System;
using System.IO;
using System.Threading;

namespace Common
{
    /// <summary>
    /// Positional reads from an append-only file. Temporary exhaustion waits for growth;
    /// only finalization produces EOF. Close wakes pending reads before releasing the file.
    /// </summary>
    public sealed class GrowingFileReader : IDisposable
    {
        public const long StartThresholdBytes = 10L * 1024 * 1024;
        private readonly Stream stream;
        private readonly object gate = new object();
        private bool complete;
        private bool closed;
        private bool waiting;

        public GrowingFileReader(Stream stream)
        {
            if (!stream.CanRead || !stream.CanSeek)
                throw new ArgumentException("Playback requires a readable, seekable file.", nameof(stream));
            this.stream = stream;
        }

        public static bool CanStart(long size, long readableBytes, bool finalized) =>
            finalized || (size > StartThresholdBytes && readableBytes >= StartThresholdBytes);

        public bool IsWaiting { get { lock (gate) return waiting; } }
        public long Length { get { lock (gate) return closed ? 0 : stream.Length; } }

        public void Complete()
        {
            lock (gate)
            {
                complete = true;
                Monitor.PulseAll(gate);
            }
        }

        public int ReadAt(long position, byte[] buffer, int offset, int count)
        {
            if (position < 0) throw new ArgumentOutOfRangeException(nameof(position));
            if (offset < 0 || count < 0 || offset > buffer.Length - count)
                throw new ArgumentOutOfRangeException(nameof(count));
            if (count == 0) return 0;
            lock (gate)
            {
                try
                {
                    while (!closed)
                    {
                        long available = stream.Length - position;
                        if (available > 0)
                        {
                            stream.Position = position;
                            int read = stream.Read(buffer, offset, (int)Math.Min(count, available));
                            if (read > 0) return read;
                        }
                        if (complete) return -1;
                        waiting = true;
                        // Progress is not a flush guarantee: recheck the actual readable file.
                        Monitor.Wait(gate, 100);
                    }
                    return -1;
                }
                finally { waiting = false; }
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (closed) return;
                closed = true;
                Monitor.PulseAll(gate);
                stream.Dispose();
            }
        }
    }
}
