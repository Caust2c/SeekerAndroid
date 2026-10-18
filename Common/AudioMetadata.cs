using System;
using System.IO;
using System.Text;

namespace Common
{
    public sealed class AudioMetadata
    {
        public string? Title { get; internal set; }
        public string? Artist { get; internal set; }
        public string? Album { get; internal set; }
        public byte[]? Artwork { get; internal set; }
        public string? Quality { get; internal set; }
        public int DurationMilliseconds { get; internal set; }
        public bool Complete { get; internal set; }
    }

    /// <summary>Reads only available leading tags; never waits for a growing file.</summary>
    public static class AudioMetadataReader
    {
        private const int MaxArtwork = 8 * 1024 * 1024;

        public static AudioMetadata Read(Stream stream)
        {
            var result = new AudioMetadata();
            long available = stream.Length;
            var signature = ReadAt(stream, 0, 10, available);
            if (signature == null) return result;
            if (Encoding.ASCII.GetString(signature, 0, 4) == "fLaC")
            {
                long position = 4;
                while (true)
                {
                    var header = ReadAt(stream, position, 4, available);
                    if (header == null) return result;
                    int length = (header[1] << 16) | (header[2] << 8) | header[3];
                    long end = position + 4 + length;
                    if (end > available) return result;
                    int type = header[0] & 127;
                    if ((type == 0 && length == 34) || (type == 4 && length <= 1024 * 1024) ||
                        (type == 6 && length <= MaxArtwork))
                    {
                        var block = ReadAt(stream, position + 4, length, available)!;
                        if (type == 0) ReadStreamInfo(block, result);
                        else if (type == 4) ReadComments(block, result);
                        else ReadPicture(block, result);
                    }
                    if ((header[0] & 128) != 0) { result.Complete = true; return result; }
                    position = end;
                }
            }
            if (Encoding.ASCII.GetString(signature, 0, 3) != "ID3")
            {
                result.Complete = true;
                return result;
            }
            int version = signature[3];
            // Extended/compressed/unsynchronised tags are left to the filename fallback.
            if ((version != 3 && version != 4) || signature[5] != 0) { result.Complete = true; return result; }
            long tagEnd = 10L + SynchSafe(signature, 6);
            long offset = 10;
            while (offset + 10 <= tagEnd)
            {
                var header = ReadAt(stream, offset, 10, available);
                if (header == null) return result;
                if (header[0] == 0) { result.Complete = true; return result; }
                string id = Encoding.ASCII.GetString(header, 0, 4);
                int size = version == 4 ? SynchSafe(header, 4) : BigEndian(header, 4);
                if (size < 0 || offset + 10L + size > tagEnd) { result.Complete = true; return result; }
                if (offset + 10L + size > available) return result;
                if (header[8] == 0 && header[9] == 0 && size > 0 &&
                    ((id == "APIC" && size <= MaxArtwork) ||
                    ((id == "TIT2" || id == "TPE1" || id == "TALB") && size <= 65536)))
                {
                    var data = ReadAt(stream, offset + 10, size, available)!;
                    if (id == "APIC") ReadId3Picture(data, result);
                    else
                    {
                        string text = DecodeText(data);
                        if (id == "TIT2") result.Title = text;
                        else if (id == "TPE1") result.Artist = text;
                        else result.Album = text;
                    }
                }
                offset += 10L + size;
            }
            result.Complete = available >= tagEnd;
            return result;
        }

        private static byte[]? ReadAt(Stream stream, long offset, int count, long available)
        {
            if (offset < 0 || count < 0 || offset > available - count) return null;
            var data = new byte[count];
            stream.Position = offset;
            int total = 0;
            while (total < count)
            {
                int read = stream.Read(data, total, count - total);
                if (read == 0) return null;
                total += read;
            }
            return data;
        }

        private static int BigEndian(byte[] data, int p) =>
            (data[p] << 24) | (data[p + 1] << 16) | (data[p + 2] << 8) | data[p + 3];
        private static int SynchSafe(byte[] data, int p) =>
            ((data[p] & 127) << 21) | ((data[p + 1] & 127) << 14) | ((data[p + 2] & 127) << 7) | (data[p + 3] & 127);

        private static void ReadStreamInfo(byte[] data, AudioMetadata result)
        {
            ulong packed = 0;
            for (int i = 10; i < 18; i++) packed = (packed << 8) | data[i];
            int rate = (int)(packed >> 44);
            int bits = (int)((packed >> 36) & 31) + 1;
            ulong samples = packed & 0xfffffffffUL;
            if (rate == 0) return;
            result.Quality = $"FLAC · {bits}-bit · {rate / 1000.0:0.#} kHz";
            result.DurationMilliseconds = (int)Math.Min(int.MaxValue, samples * 1000UL / (ulong)rate);
        }

        private static void ReadComments(byte[] data, AudioMetadata result)
        {
            using var input = new BinaryReader(new MemoryStream(data), Encoding.UTF8);
            try
            {
                uint vendor = input.ReadUInt32();
                if (vendor > input.BaseStream.Length - input.BaseStream.Position) return;
                input.BaseStream.Position += vendor;
                uint count = input.ReadUInt32();
                for (uint i = 0; i < count && input.BaseStream.Length - input.BaseStream.Position >= 4; i++)
                {
                    uint size = input.ReadUInt32();
                    if (size > input.BaseStream.Length - input.BaseStream.Position) return;
                    string text = Encoding.UTF8.GetString(input.ReadBytes((int)size));
                    int separator = text.IndexOf('=');
                    if (separator < 0) continue;
                    string key = text.Substring(0, separator).ToUpperInvariant();
                    string value = text.Substring(separator + 1);
                    if (key == "TITLE") result.Title = value;
                    else if (key == "ARTIST") result.Artist = value;
                    else if (key == "ALBUM") result.Album = value;
                }
            }
            catch (EndOfStreamException) { }
        }

        private static void ReadPicture(byte[] data, AudioMetadata result)
        {
            int p = 0;
            bool Take(out int value)
            {
                value = 0;
                if (p > data.Length - 4) return false;
                value = BigEndian(data, p); p += 4;
                return value >= 0;
            }
            if (!Take(out int type) || !Take(out int mimeLength) || mimeLength > data.Length - p) return;
            string mime = Encoding.ASCII.GetString(data, p, mimeLength); p += mimeLength;
            if (mime == "-->" || !Take(out int descriptionLength) || descriptionLength > data.Length - p) return;
            p += descriptionLength;
            // Width, height, depth and palette count.
            if (p > data.Length - 16) return;
            p += 16;
            if (!Take(out int size) || size > MaxArtwork || size > data.Length - p || size == 0) return;
            if (result.Artwork != null && type != 3) return;
            result.Artwork = new byte[size];
            Buffer.BlockCopy(data, p, result.Artwork, 0, size);
        }

        private static string DecodeText(byte[] data)
        {
            Encoding encoding = data[0] switch
            {
                0 => Encoding.GetEncoding(28591),
                1 => data.Length > 2 && data[1] == 254 && data[2] == 255 ? Encoding.BigEndianUnicode : Encoding.Unicode,
                2 => Encoding.BigEndianUnicode,
                _ => Encoding.UTF8
            };
            return encoding.GetString(data, 1, data.Length - 1).Trim('\0', '\uFEFF', ' ');
        }

        private static void ReadId3Picture(byte[] data, AudioMetadata result)
        {
            if (data.Length < 4) return;
            int mimeEnd = Array.IndexOf(data, (byte)0, 1);
            if (mimeEnd < 0 || mimeEnd + 2 >= data.Length) return;
            if (Encoding.ASCII.GetString(data, 1, mimeEnd - 1) == "-->") return;
            int type = data[mimeEnd + 1];
            int p = mimeEnd + 2;
            int step = data[0] == 1 || data[0] == 2 ? 2 : 1;
            while (p + step <= data.Length)
            {
                bool end = data[p] == 0 && (step == 1 || data[p + 1] == 0);
                p += step;
                if (end) break;
            }
            int size = data.Length - p;
            if (size <= 0 || size > MaxArtwork || (result.Artwork != null && type != 3)) return;
            result.Artwork = new byte[size];
            Buffer.BlockCopy(data, p, result.Artwork, 0, size);
        }
    }
}
