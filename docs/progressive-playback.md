# Progressive MP3 playback

Enable **Enable Live Playback** in Downloads settings. Tap a single-MP3 search
result to play it. For a result containing multiple tracks, tap the MP3 in its
file list; expanded search rows also support direct playback. No additional
"Download and play" button is involved.

Download remains a separate action: touch and hold a search result to open its
file list, touch and hold tracks to select them, and use **Download selected**.
The ordinary download-only behavior remains when Live Playback is disabled.
Playback starts or attaches to the existing download; it does not create a
second transfer. Closing the player does not cancel that download.

## Growing downloads

There is no minimum file size or 10 MB threshold. The player opens an available
nonempty prefix and lets Android prepare the MP3 decoder. A 7 MB file with only
3.5 MB downloaded exposes that entire prefix immediately. Reads at the current
end wait for more bytes, then continue as the sequential download grows. Only
successful finalization produces permanent EOF. Download failure or pause does
not discard already-readable audio; playback can consume it before buffering.
The peer must supply the MP3 headers and audio frames before sound can begin.

The media source advertises unknown length while streaming, rather than the
remote file's future size. This avoids advertising missing tail metadata during
preparation. Download progress and playback position are separate; duration may
remain unknown for some unfinished MP3s.

Direct files and seekable `content:` document-provider descriptors are supported.
A provider that exposes only a pipe cannot serve random reads and reports a
playback error. The existing incomplete download is the only audio copy. New
MP3 transfers use file-backed storage while Live Playback is enabled, even if
the memory-backed download preference is set. A memory-backed transfer already
running before the toggle was enabled must finish before it has a readable file.

## Player lifecycle

Native MediaPlayer operations and storage reads run on a dedicated playback
thread. The miniplayer reads an immutable state snapshot on the UI thread.
Pause, Close, track replacement and transfer invalidation wake pending reads
before releasing the native engine. Stale callbacks cannot start an old track.
Pause saves the last polled position and releases the engine; Play prepares the
source again. Position restoration depends on the platform extractor's seeking
support for the unfinished MP3.

The application owns the session, and MainActivity displays the miniplayer
outside its tab pager. Rotation rebinds the session. Playback pauses when Seeker
enters the background or loses audio focus, and when headphones are unplugged.
There is no background playback service or process-death restoration.

The reader holds its descriptor across normal copy/delete or move finalization.
DownloadService signals completion after the final URI is ready. Transfer cleanup
invalidates the source before deleting or replacing incomplete bytes.
The existing download service remains responsible for retries and cleanup.

Debug StrictMode logging remains enabled, but its red screen-flash penalty has
been removed. Playback failures log their exception/native error under the
`SeekerPlayback` logcat tag.

## Validation

The .NET 10/API 36 Android project compiles and produces an unsigned debug APK
with zero build errors (existing project/package warnings remain). All six GrowingFileReader regression
tests pass, covering growth after temporary EOF, a 3.5 MB prefix, cancellation,
finalization, and descriptor identity across file rename/replacement.

Validation commands:

```powershell
dotnet build Seeker/Seeker.csproj -c Debug -t:Package -p:BuildingInsideVisualStudio=true -p:BuildProjectReferences=true -p:EmbedAssembliesIntoApk=true -p:AndroidPackageFormats=apk --no-restore
dotnet test UnitTestCommon/UnitTestCommon.csproj --filter FullyQualifiedName~GrowingFileReaderTests
```

The Package command deliberately omits signing and installation. Android requires
a signature to install an APK; a debug-key signature is sufficient for phone testing.

The reported startup crash has not been reproduced: no ADB device was connected
during this revision. Reader tests and compilation do not establish that an
Android device decodes a growing MP3 successfully. Device checks still required:

1. Enable Live Playback and tap a fresh 7 MB MP3. Audio must begin before download
   completion. Repeat with a larger track, CBR/VBR encodings, and large ID3 tags.
2. Limit or pause the download after roughly half arrives. Already-buffered audio
   should play, then wait at the edge. Resume the transfer and confirm continuation.
3. Pause/Play, Close during preparation/buffering, switch tracks rapidly, rotate,
   change tabs, background the app, and disconnect headphones.
4. Repeat using a normal Android document-folder download destination; verify
   completion and retry do not unexpectedly stop or mix playback sources.
5. Verify touch-and-hold selection plus Download does not start playback, and
   disabling Live Playback restores normal tap-to-select behavior.

Capture diagnostics during a reproduction (PowerShell, one line):

```powershell
adb logcat -v threadtime SeekerPlayback:V AndroidRuntime:E mono-rt:E '*:S' > playback-log.txt
```

For a crash already recorded by the phone:

```powershell
adb logcat -b crash -d > playback-crash.txt
```
