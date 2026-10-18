# Changes from the starting fork

Compared with the starting `master` commit `94a49dd4` (Seeker 3.11.0, version
code 142). This describes the final implementation, including the revisions
made after testing it on the phone. Work is on `codex/player-artwork-flac-diagnostics`.

## Playback behavior

- Added **Enable Live Playback** under Downloads settings. It is saved between
  launches and defaults to off. Turning it off closes the player.
- When enabled, tapping an MP3 or FLAC search result starts playback while the
  file downloads. Multi-track results support tapping a track in the file list
  or expanded result. The earlier extra "Download and play" action was removed.
- Download remains a separate action through long-press selection and Download.
  With Live Playback disabled, the existing download-only interaction remains.
- A playback request starts or attaches to the existing transfer. It does not
  download a second copy for the player. Completed MP3/FLAC downloads can also
  open in this player through their Play action when the setting is enabled.
- Removed the initial MP3-only/10 MB restrictions. There is no app-imposed file
  size or startup-byte threshold. Android still needs enough headers/audio data
  for its native extractor to prepare.

## How unfinished playback works

- Added a seekable reader over the actual incomplete file, connected to Android
  MediaPlayer through MediaDataSource. New supported transfers use file-backed
  storage while Live Playback is enabled, including when memory-backed downloads
  are otherwise selected.
- The player can consume the available prefix, wait at its current end, and
  continue when more bytes arrive. Temporary EOF is distinct from successful
  finalization. A failed download does not discard its playable prefix.
- Added support for ordinary files and seekable Android document-provider
  descriptors. Completion, source replacement and cleanup notify the player so
  it can retain or release the correct file safely.

## Player and appearance

- Added a persistent compact player above the bottom navigation with artwork,
  track title/status, Play/Pause, Close and playback progress. It stays available
  across tabs; rotating the activity rebinds the same session.
- Tapping its cover/title expands a player sheet with title, artist, album,
  artwork, elapsed time, duration, download progress and FLAC bit depth/sample rate
  when those details are available. Collapse keeps the session; Close stops
  playback while leaving the download running.
- Embedded FLAC and ordinary ID3v2.3/v2.4 metadata/artwork are read off the UI
  thread. No artwork lookup service is used. Missing artwork gets a themed
  record illustration. Image payloads and decoded dimensions are bounded.
- Both sizes follow Seeker's selected theme and existing text/accent colors.
  Close, expand/collapse and pause reuse existing app icons. The custom navy/teal
  styling was replaced. The compact bar now has square edges and a 4 dp gap
  between its 52 dp Play/Pause and 48 dp Close touch targets.

## Stability and error handling

- Moved native playback operations to a dedicated worker, with UI snapshots and
  guards against callbacks from replaced tracks. Pause, Close and replacement
  wake blocked reads before releasing the player.
- Fixed late native read/close callbacks by allowing the Java runtime bridge to
  reclaim the media source after release rather than prematurely disposing its
  peer. Full-frame reads also span download appends safely.
- Removed the ordinary debug build's volume-key test shortcuts. Volume Down
  could reconnect an already-connected Soulseek client and crash; volume keys
  now perform their normal Android behavior outside explicit mock builds.
- Removed StrictMode's red screen-flash penalty while retaining debug logging.
- Distinguished peer waiting/timeouts from decoder buffering. Failed transfers
  offer Retry Download in the expanded player through the existing retry path.
  The captured FLAC timeout was an unresponsive peer, not a native codec error.
- Playback pauses on backgrounding, audio-focus loss or headphone unplugging.

## Verification and current boundaries

- 21 focused unit tests passed for supported formats, growing reads, cancellation,
  finalization and metadata/artwork parsing.
- Five phone checks passed: two FLAC variants buffering/resuming from partial
  files, MP3 partial startup, closing during a blocked native read, and a real
  FLAC starting with only half its bytes present. Production FLAC playback also
  advanced before the transfer finished.
- Built and installed debug-signed APK updates through ADB, preserving app data.
  Subsequent visual-only revisions were checked through builds and the phone UI.
- The project still targets the fork's .NET 10 / Android API 36 configuration;
  this work did not upgrade that target. Android requires an APK signature;
  the debug key is sufficient for these phone installations.
- Current playback formats are MP3 and FLAC. Playback is foreground-only, with
  no background service, queue/playlist, process-death session restoration or
  interactive seek control. Android may prefetch before startup, and an
  unresponsive peer cannot provide audio. Pipe-only document providers do not
  support the random reads required by this implementation.

The first phone-confirmed MP3 base remains at `4bc81504` on
`codex/mp3-playback-base`. Implementation details are in
[progressive-playback.md](progressive-playback.md).
