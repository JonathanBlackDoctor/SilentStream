# Media Capture Helper 0.4.29

- Starts local backup recording before waiting for YouTube authentication or broadcast creation. A separate guard repairs a stopped or stalled recording while the connection is pending, without starting additional YouTube requests. File growth prevents false stall recovery.
- Serializes encoder transitions and retention sweeps. Cancellation and late API results belong to their original run; they cannot stop a newer recording, replace its broadcast, or erase its broadcast timer. Broadcast completion has a real bounded wait even when a provider ignores cancellation.
- Replaces the fixed two-second application restart delay with a bundled helper that waits for the exact previous process to exit. A timeout or reused process ID does not force another instance. A launch receipt does not certify that the new app started recording.
- Preserves source identity and successful upload receipts across a short startup backup part and the following live recording. Empty parts do not block the usable part or authorize an empty upload.
- Includes the 0.4.28 queue, configuration and original-recording protection, 0.4.27 video validation, and 0.4.26 recording-safe updater. The separately signed inspection helper bundled with this app remains 0.4.22; the new 0.4.28 inspection helper requires installation during a verified app-closed maintenance window.

This release does not change YouTube limits or erase quota pauses, successful videos, or uncertain uploads. Whole-app crash recovery, actual device installation, ongoing recording, and YouTube playback are separate checks; code and package validation alone do not establish field recovery.
