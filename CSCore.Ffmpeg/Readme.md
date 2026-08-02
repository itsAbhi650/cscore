# Changelog

### FFmpeg 8.x Upgrade &mdash; <sub>2026-08-02</sub>

Upgraded the FFmpeg integration from **3.x to 8.x** (`avcodec-62`, `avformat-62`, `avutil-60`, `swresample-6`) and added new capabilities. CSCore's internal DSP is untouched &mdash; every FFmpeg feature below is opt-in.

**Interop**
- Replaced the vendored FFmpeg 3.x interop with the latest FFmpeg.AutoGen 8.x sources (Dynamically Linked / `DllImport`).
- Removed old generated files (`FFmpeg.avcodec/avformat/avutil/swresample.g.cs`) and swapped the shipped native DLLs to the 8.x set.

**API migration**
- Decode via `codecpar` + a self-allocated `AVCodecContext` (replaces the removed `AVStream.codec`).
- `avcodec_decode_audio4` &rarr; `avcodec_send_packet` / `avcodec_receive_frame`.
- Heap-allocated packets (`av_packet_alloc`/`free`); channel handling via `AVChannelLayout`.
- `av_muxer_iterate` / `av_demuxer_iterate`, `av_dict_iterate` metadata, new log-callback signature.

**Fixes**
- Flush the decoder (and reset the resampler) on every seek to avoid stale samples.
- `CanSeek` is now derived from the source + `ctx_flags` instead of the unreliable `pb->seekable`.
- Config section read is resilient on modern .NET (optional `<ffmpeg>` section no longer breaks init).

**New features**
- Selectable resampling: `FfmpegProcessingOptions` / `AudioProcessor` let callers pick CSCore or FFmpeg (libswresample) for rate/format/channel conversion, with per-decoder or global defaults.
- `FfmpegEncoder`: encode to any FFmpeg audio codec/container (MP3, AAC, FLAC, &hellip;) with `EncodeWholeSource`/`CreateMP3Encoder` helpers.
- `FfmpegUtils.GetAudioEncoderNames()` to discover valid encoder names.

---

## Using the Project ##
Make sure that the directory with the native libraries is placed correctly within the folder with your assembly.

### Linux & Mono ###
Make sure to start your assembly with LD_LIBRARY_PATH=./ mono MyApp.exe
To debug your assembly using MonoDevelop follow these steps:
1. Open the "Project Options" of your project
2. Navigate to Run > General
3. Add a new variable with the name "LD_LIBRARY_PATH" and use your output directory (which contains your compiled assembly) as the variables value

## Important: ##

The CSCore.Ffmpeg project is licensed under the **[LGPL2.1](https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html)**

CSCore.Ffmpeg uses parts of https://github.com/Ruslan-B/FFmpeg.AutoGen
The author "Ruslan Balanukhin" gave the explicit permission to use its source code
within CSCore.Ffmpeg under the MS-PL or the LPGL.
> I do not mind if you would like to reuse some results of my work in case you will keep reference to my original project.

This software uses libraries from the FFmpeg project under the LGPLv2.1

This software uses code of [FFmpeg](http://ffmpeg.org) licensed under the 
[LGPLv2.1](http://www.gnu.org/licenses/old-licenses/lgpl-2.1.html>LGPLv2.1) and 
its source can be downloaded [here](https://github.com/filoe/cscore) or [here](https://github.com/filoe/cscore/tree/ffmpeg).