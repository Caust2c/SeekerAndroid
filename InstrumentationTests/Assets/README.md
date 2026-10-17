# Progressive audio fixtures

These are synthetic pink-noise samples generated for the tests (30-second stereo
standard-resolution FLAC, 20-second mono high-resolution FLAC, 90-second MP3);
they contain no music or third-party recordings. Device tests mute playback.

Regenerate from the repository root with FFmpeg:

```powershell
ffmpeg -f lavfi -i "anoisesrc=color=pink:seed=42:duration=30:sample_rate=44100:amplitude=0.1[a];anoisesrc=color=pink:seed=43:duration=30:sample_rate=44100:amplitude=0.1[b];[a][b]amerge=inputs=2" -ac 2 -c:a flac -sample_fmt s16 -y InstrumentationTests/Assets/playback-16bit-44100.flac
ffmpeg -f lavfi -i "anoisesrc=color=pink:seed=42:duration=20:sample_rate=96000:amplitude=0.1" -ac 1 -c:a flac -sample_fmt s32 -bits_per_raw_sample 24 -y InstrumentationTests/Assets/playback-24bit-96000.flac
ffmpeg -f lavfi -i "anoisesrc=color=pink:seed=42:duration=90:sample_rate=44100:amplitude=0.1" -ac 1 -c:a libmp3lame -b:a 320k -y InstrumentationTests/Assets/playback.mp3
```

The tests initially write only the first half of each encoded file. FLAC tests
verify that Android starts decoding before the remaining bytes exist, waits at
the available edge, then completes after the rest of the file is appended. The
MP3 regression verifies startup from its partial file and then cancels playback.
Android's native extractor can prefetch beyond very short/low-data prefixes during
preparation. These fixtures deliberately contain enough encoded data before the
midpoint to exercise actual playback and catch-up buffering, rather than only a
preparation wait. There is no application-imposed byte threshold.

Run this fixture only (avoids unrelated write benchmarks):

```powershell
adb shell am instrument -w -e fixture ProgressivePlaybackTests com.seeker.instrumentationtests/instrumentationtests.TestInstrumentation
```
