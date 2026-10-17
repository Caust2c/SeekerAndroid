# Progressive MP3 and FLAC playback

The first phone-confirmed MP3 implementation is preserved at commit `4bc81504`
on `codex/mp3-playback-base`. Further work is on `codex/playback-stability-flac`.

Enable **Enable Live Playback** in Downloads settings. Tap a single-track MP3 or FLAC search
result to play it. For a result containing multiple tracks, tap the MP3 or FLAC in its
file list; expanded search rows also support direct playback. No additional
"Download and play" button is involved.

Download remains a separate action: touch and hold a search result to open its
file list, touch and hold tracks to select them, and use **Download selected**.
The ordinary download-only behavior remains when Live Playback is disabled.
Playback starts or attaches to the existing download; it does not create a
second transfer. Closing the player does not cancel that download.

## Growing downloads

There is no minimum file size or 10 MB threshold. The player opens an available
nonempty prefix and lets Android prepare the audio decoder. A 7 MB file with only
3.5 MB downloaded exposes that entire prefix immediately. Reads at the current
end wait for more bytes, then continue as the sequential download grows. Only
successful finalization produces permanent EOF. Download failure or pause does
not discard already-readable audio; playback can consume it before buffering.
The peer must supply the format headers and audio frames before sound can begin.
Android also performs its own extractor prefetch. A very short prefix can remain
in preparation until more bytes arrive, even though it already contains some
audio frames; the application imposes no fixed startup byte threshold.

The media source advertises unknown length while streaming, rather than the
remote file's future size. This avoids advertising missing tail metadata during
preparation. Download progress and playback position are separate; duration may
remain unknown for some unfinished files.

Direct files and seekable `content:` document-provider descriptors are supported.
A provider that exposes only a pipe cannot serve random reads and reports a
playback error. The existing incomplete download is the only audio copy. New
MP3 and FLAC transfers use file-backed storage while Live Playback is enabled, even if
the memory-backed download preference is set. A memory-backed transfer already
running before the toggle was enabled must finish before it has a readable file.

## Player lifecycle

Native MediaPlayer operations and storage reads run on a dedicated playback
thread. The miniplayer reads an immutable state snapshot on the UI thread.
Pause, Close, track replacement and transfer invalidation wake pending reads
before releasing the native engine. Stale callbacks cannot start an old track.
The source closes its file during release but leaves Java peer disposal to the
runtime bridge: Android may deliver a final native callback after Release returns.
Pause saves the last polled position and releases the engine; Play prepares the
source again. Position restoration depends on the platform extractor's seeking
support for the unfinished file.

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

Normal debug builds now leave the hardware volume buttons to Android. The
baseline inherited test shortcuts: Volume Down called `ConnectAsync` on an
already-connected Soulseek client and threw on the UI thread; Volume Up enqueued
test uploads. These shortcuts are now restricted to explicit mock builds, and
the hard-coded reconnect shortcut has been removed.

The Android adapter uses full frame reads across appends, so a partial frame
at the current download edge does not appear as permanent EOF to the extractor.

## Validation

The .NET 10/API 36 Android project builds and a debug-signed update was installed
on the connected CPH2707 phone. Both hardware volume keys were exercised without
terminating the app. The captured baseline crash stack identified
`MainActivity.DispatchKeyEvent` calling `SoulseekClient.ConnectAsync` with an
already-connected client.

All 15 reader/format tests pass, including reading a 3.5 MB prefix, growing reads,
full frame reads across appends, cancellation, finalization, and descriptor
identity across file rename/replacement.

Validation commands:

```powershell
dotnet build Seeker/Seeker.csproj -c Debug -t:Package -p:BuildingInsideVisualStudio=true -p:BuildProjectReferences=true -p:EmbedAssembliesIntoApk=true -p:AndroidPackageFormats=apk --no-restore
dotnet test UnitTestCommon/UnitTestCommon.csproj --filter "FullyQualifiedName~GrowingFileReaderTests|FullyQualifiedName~PlaybackFormatsTests"
```

The Package command deliberately omits signing and installation. Android requires
a signature to install an APK; a debug-key signature is sufficient for phone testing.

The Android test fixture exercises actual MediaPlayer decoding from a file that
initially contains only half its bytes, using the production audio source and
document-descriptor wrapper. Fixtures cover stereo 16-bit/44.1 kHz FLAC, mono
24-bit/96 kHz FLAC, MP3, and closing during a blocked native read. They are muted
and use synthetic audio; see `InstrumentationTests/Assets/README.md`.
All four checks passed on the CPH2707 phone on 2026-10-17 with no native crash:
both FLAC variants started from a half-file, buffered at its edge, resumed after
an append, and completed. MP3 started from its half-file. The cancellation check
released a player with a blocked native read and allowed late callbacks safely.

Further device checks across different phones/providers and music files:

1. Enable Live Playback and tap a fresh 7 MB MP3. Audio must begin before download
   completion. Repeat with FLAC, CBR/VBR encodings, and large embedded artwork.
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
