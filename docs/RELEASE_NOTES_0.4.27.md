# Media Capture Helper 0.4.27

- Rejects tiny legacy clips and files without video-track samples before contacting YouTube. Audio-only samples cannot satisfy the class-video upload check, including in fragmented MP4 files. The check and upload share the same read-only file handle.
- Invalid local media fails once, remains available for inspection, and no longer delays other recordings with repeated network retries. Existing completed video IDs and source receipts remain preserved.
- Includes 0.4.26's recording-safe background updates: download during operation; apply only at normal startup before recording begins. Includes 0.4.25's delayed upload recovery and seven-day history.
- Adds real synthetic FFmpeg fixtures for normal/fragmented video, audio-only MP4 and empty EOF cuts, plus regression checks for tiny clips, track association and queue continuation. Local validation does not replace actual YouTube playback verification.
