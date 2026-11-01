# Media Capture Helper 0.4.31

Recording continues independently of upload limits. Application recovery now preserves the operator's last mode and original files.

- Persist stopped, recording-only and live intent across same-boot relaunches. A separate process can request one restart after unexpected process exit; manual stop, planned update/power/removal, stale state and original-preservation holds suppress recovery. Recovery is limited to three attempts per 30 minutes and a launch receipt does not certify recording health.
- Wait for the same encoder finalization task across concurrent stop/restart/shutdown requests. Serialize maintenance ownership and cancel a power request during finalization without interrupting the file flush or starting a new broadcast.
- Warn when the current recording file stops growing for 20 seconds, escalate at 60 seconds, and clear only after real growth or an explicit stop. Notification delivery depends on the configured notification channels.
- Preserve original recordings unless validated upload receipts cover the entire original time interval, including breaks and after-class tails. A local original-preservation marker blocks app startup, update application, cleanup and new uploads; a completed upload still preserves its cut while held.
- Coordinate app/inspection maintenance with an execution lease. A future inspection update cannot stop the session agent while the app is running or launch the app in the middle of maintenance. The first upgrade from an older inspection helper still requires an app-closed maintenance window after originals are copied.
- Use a shared VOD admission budget across devices, prioritize current-day recordings, and durably share quota pauses. This does not increase or bypass YouTube limits.
- The control service can hold all update feed/artifact access for a verified device before proxy/cache access. Policy release requires an authenticated administrator, the current revision and a preservation-manifest hash. The server records that hash; actual copy/hash verification must be performed separately. A server hold cannot cancel an updater that was already downloaded or started by an older app.

For incident recovery, copy and hash the complete original/part/period files and logs before updating or starting the app. Publication alone does not prove installation, continuous recording, upload completion or playback on a field device.
