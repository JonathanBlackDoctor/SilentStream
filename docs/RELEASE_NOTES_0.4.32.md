# Media Capture Helper 0.4.32

This update hardens recording shutdown, startup updates, and proof of complete VOD coverage.

- Disable implicit Velopack application before the single-instance guard; use the explicit pre-capture startup path.
- Respect startup cancellation on entry and immediately before applying an update.
- Drain encoder writers and close both video and audio inputs before waiting for FFmpeg to exit.
- Verify decoded video frames, timestamps, and requested source coverage before accepting a clip. Reject truncated or damaged output and retry once the source is ready.
- Preserve AAC timing and keyframe preroll for ordinary nonaligned period boundaries. Proof covers only the requested, verified source interval.
- Keep legacy receipts for duplicate-upload prevention without treating them as permission to delete originals. Deletion requires complete verified coverage of the same recording.

Based on the released v0.4.31 source (58fd2da), with only the reviewed lifecycle fixes from af054701, c38e357, and 472a67f. Existing consent wording, automation behavior, and signed inspection helper 0.4.31 are preserved. The helper remains for explicit app-closed maintenance and is not imported automatically.

Validation before release preparation: 815 local tests passed (717 Core and 98 Inspection), including synthetic FFmpeg and Windows named-pipe cases. Independent review reran 36 focused cases successfully, including AAC and 0.1-4.1 second nonaligned extraction. Release CI repeats all established suites and the actual bundled-FFmpeg regressions.

Existing device preservation holds remain in force. Publishing this package does not establish installation on any classroom PC. Long-duration performance, real installation/rollback, and classroom soak verification remain separate checks.