# Media Capture Helper 0.4.26

- Fixes live streaming and recording stopping after an update downloads during normal operation. Background checks now download and stage the package without launching the updater. Velopack's exit-wait API has a 60-second limit and must only run when the app is actually preparing to exit.
- Keeps installation of pending/new updates at normal startup, before recording begins. Existing recordings, upload jobs, completed video receipts and the signed inspection helper remain unchanged.
- Adds lifecycle regression coverage for background staging without exit/restart, failed/cancelled downloads, and startup application with restart. Includes the 0.4.25 delayed upload recovery and seven-day diagnostic history improvements.
