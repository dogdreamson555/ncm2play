# Synthetic audio fixtures

These files are short, locally generated test tones. They contain no recordings or personal data and may be redistributed with this repository.

Generated with FFmpeg `2025-09-10-git-c1dc2e2b7c-full_build-www.gyan.dev` using its `lavfi` sources:

```powershell
ffmpeg -hide_banner -loglevel error -y -f lavfi -i "sine=frequency=440:sample_rate=44100:duration=0.5" -ac 2 -sample_fmt s16 -c:a flac -compression_level 8 tone-44100-stereo-s16.flac
ffmpeg -hide_banner -loglevel error -y -f lavfi -i "aevalsrc=0.2*sin(2*PI*997*t)|0.15*sin(2*PI*997*t):s=96000:d=0.5" -ac 2 -sample_fmt s32 -bits_per_raw_sample 24 -c:a flac -compression_level 8 tone-96000-stereo-s24.flac
ffmpeg -hide_banner -loglevel error -y -f lavfi -i "sine=frequency=660:sample_rate=44100:duration=0.5" -ac 2 -c:a libmp3lame -b:a 128k -write_xing 0 tone-44100-stereo-mp3-128k.mp3
```

| File | Audio format | SHA-256 |
| --- | --- | --- |
| `tone-44100-stereo-s16.flac` | FLAC, 44.1 kHz, stereo, 16-bit, 0.5 s | `95F7A581448B5AF2835679372314398705CC74DEDD1722528D3911797E9293FC` |
| `tone-96000-stereo-s24.flac` | FLAC, 96 kHz, stereo, 24-bit, 0.5 s | `5D241B0F9A259BB860C35753692BC19C5E76172591D641F6C8460F0576D19057` |
| `tone-44100-stereo-mp3-128k.mp3` | MP3, 44.1 kHz, stereo, 128 kbit/s | `7A762ED88A691AADDF297C279BE5450C733D053700CE7F4F6AC6E97250368BC9` |
| `tone-8000-stereo-mp3-64k.mp3` | MP3, 8 kHz, stereo, 64 kbit/s | `B346F373B4AE266E73B03E8B705E8A16320D56533F8E2595D49F8E7EE4D0C651` |

The 8 kHz fixture was generated with the bundled FFmpeg 9.0.2 from the 44.1 kHz FLAC tone:

```powershell
ffmpeg -hide_banner -loglevel error -i tone-44100-stereo-s16.flac -ar 8000 -c:a libmp3lame -b:a 128k -write_xing 0 tone-8000-stereo-mp3-64k.mp3
```

LAME caps MPEG-2.5 bitrates at 64 kbit/s, so this command's actual output is 64 kbit/s. The fixture verifies that selecting 128 kbit/s in the application resamples to 16 kHz and produces the requested frame bitrate. [LAME bitrate limits](https://github.com/lameproject/lame/blob/master/doc/html/detailed.html)

Decoded PCM SHA-256 reference values for FLAC recompression checks:

| Source | FFmpeg decoding format | PCM SHA-256 |
| --- | --- | --- |
| `tone-44100-stereo-s16.flac` | `pcm_s16le` | `D15CF547099F87E938FAE2DD65B4FFF14395CF8FEBB220285C06EA6DF7CA2FB5` |
| `tone-96000-stereo-s24.flac` | `pcm_s32le` | `2E2D33A60B79B0513AB64739341F21919686D1BDC0F2F71C862260108F3EB29D` |
