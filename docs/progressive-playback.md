# Progressive MP3 playback

Enable **Enable Live Playback** in Downloads settings, select one MP3 in the
search download dialog, and choose **Download and play selected MP3**. The normal
download continues independently. Closing the player, changing tracks, losing
audio focus, or leaving Seeker in the background does not cancel it.

The player is application-owned; MainActivity binds a miniplayer outside its tab
pager. Activity recreation rebinds the same session. Playback does not restore
after process death and does not continue in the background. The overlay appears
in MainActivity, not in separate activities such as Settings.

## Storage and buffering

For direct `file:` downloads larger than 10 MiB, the source opens after at least
10 MiB is readable. The platform MP3 decoder determines when preparation can
finish. Files at or below that threshold, unknown sizes, memory-backed downloads,
and `content:` document providers wait for download finalization. No HTTP server,
streaming protocol, extra media dependency, or second download is involved.

The enabled file writer permits readers. GrowingFileReader checks actual file
length rather than assuming network progress implies visibility. A positional
read beyond the current prefix waits for growth and wakes on Close or finalization.
Pause releases the media engine and saves its playback position; Play prepares
the source again and seeks to that position. This also makes Pause responsive
while the platform is preparing or buffering.

DownloadService returns the actual transfer identity for the selected file and
publishes source changes after incomplete-location setup and after its save
continuation. Soulseek library success alone is not final-file readiness.
TransferCleanup invalidates playback before removing/replacing incomplete bytes.
Size-mismatch replacement resets playback position. Normal completion keeps an
already-open local reader alive across copy/delete finalization; a session that
has not opened a reader uses FinalUri instead.

Download progress is byte-based, separate from playback time. It is not an
estimate of playable duration for variable-bitrate MP3s.

## Cache scope

This implementation reads the existing download and owns no disposable audio
copies. An audio cache and the proposed 100/250/500 MB/1 GB limit are deferred
until a separate cache is justified. Completed music and resumable partial files
must never be pruned to meet a playback-cache budget. Existing transfer cleanup
and persistence remain authoritative.

## Validation and remaining device checks

The development machine has .NET 9 and Android API 35 tooling; the repository
requires SDK/workload 10.0.401 and targets Android API 36. A full unsigned app
build was attempted and blocked by the missing SDK. The project targets have not
been changed and no app was installed or signed.

The actual platform-independent Common project (including the download service
changes and Soulseek.NET reference) compiled successfully with the installed
SDK, with its existing nullable/style warnings. Workload resolution was disabled
for that build process only; no SDK configuration was edited.

GrowingFileReaderTests run in an isolated .NET 9 harness using the repository
source: temporary EOF, growth, Close, finalization, threshold boundaries, and
reader identity across rename/replacement. The new media backend also compiles
in an isolated harness against installed Android API 35 references and project
contract stubs. These checks are not a full project build or device playback test.

Before upstream submission, run the normal build and repository tests with the
required tools, then validate on API 23 and a current Android device:

- CBR and VBR MP3s over/under the threshold, including large ID3 tags. A platform
  extractor may request unavailable tail data and delay preparation until the
  download finishes; early start is not yet verified on-device.
- Catch-up buffering, a stalled peer, download pause/resume, failure/retry, and
  size-mismatch replacement.
- Copy/delete completion with an open reader, source deletion, auto-clear of
  completed transfer rows, and restored completed transfer records.
- Close and Pause during preparation/buffering; rapidly select another track.
- Tab navigation, rotation, Settings navigation, true backgrounding, headphone
  unplugging, audio-focus loss, and process death.
- Completion-only fallback for SAF document providers and memory-backed mode;
  unreadable/revoked final URIs must show playback failure without altering the
  transfer.

The Android MediaPlayer/MediaDataSource growing-file behavior remains a device
validation requirement. Do not treat successful managed-reader tests as proof
that every Android MP3 extractor begins playback at the threshold.
