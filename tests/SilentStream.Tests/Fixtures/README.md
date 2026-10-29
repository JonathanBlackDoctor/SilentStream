# MP4 validation fixtures

These small synthetic fixtures contain only generated black frames and silence; no user recording is included. They were generated with the pinned production FFmpeg sidecar.

- `valid-av.mp4`: two seconds, 160×90 H.264 at 25 fps and silent stereo AAC, normal fast-start MP4.
- `valid-fragmented.mp4`: the same generated content using `+frag_keyframe+empty_moov`.
- `audio-only.mp4`: five seconds of silent AAC with no video track.
- `empty-eof.mp4`: a stream-copy cut starting after EOF, with the additional output `-ss 0` used by the fixed cutter.

The fixtures exercise real container layouts. Upload validation remains a conservative local gate; completed YouTube playback must be checked separately.
