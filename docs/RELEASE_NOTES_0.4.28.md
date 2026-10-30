# Media Capture Helper 0.4.28

- Recovers from transient capture initialization failures, repeated streaming connection failures, and a stalled recording-only encoder. A late cancellation from an old session cannot stop a newly started session.
- Keeps an acknowledged upload completed if playlist association or local persistence fails. Repairs the saved state without re-uploading the video. An interrupted upload with an unknown outcome requires reconciliation before another transfer.
- Preserves pending, failed and uncertain source recordings during retention cleanup. Cleanup requires matching durable receipts with local video validation for every scheduled part. Older unverified originals remain protected; low-space warnings still require attention.
- Preserves successful video IDs and source identities across metadata updates, duplicate queue submissions and history pruning. Corrupt queue/receipt files are retained for recovery and cannot silently become an empty upload queue. Queue recovery failures do not prevent local recording startup.
- Recovers the last valid room configuration after a damaged configuration file and bounds historical recording intervals by the previous file's last write. Audio recovery publishes a terminal result after temporary-file cleanup.
- Includes the 0.4.27 video validation and 0.4.26 recording-safe updater. This app package continues to bundle the separately verified signed helper 0.4.22. New helper lifecycle changes require a separate signed helper release.

Local media validation does not establish YouTube playback success. This release does not change YouTube limits or erase existing quota pauses, successful video receipts, or unresolved uploads.
