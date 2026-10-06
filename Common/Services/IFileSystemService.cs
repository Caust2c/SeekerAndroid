using System;
using System.IO;

namespace Seeker.Services
{
    public interface IFileSystemService
    {
        void GetOrCreateIncompleteLocation(string username, string fullfilename, int depth,
            out string incompleteUri, out string parentUri, out long partialLength);

        Stream OpenIncompleteStream(string incompleteUri, long partialLength);

        // Null means this storage provider cannot safely expose a growing file.
        Stream? OpenProgressiveRead(string uri);

        string SaveToFile(string fullfilename, string username, ArraySegment<byte> bytes,
            string uriOfIncomplete, string parentUriOfIncomplete,
            bool memoryMode, int depth, bool noSubFolder, out string finalUri);

        void SaveFileToMediaStore(string path);
    }
}
