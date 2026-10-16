# Media Capture Helper 0.4.5

- Include up to 400 persisted room/date playlist records in the authenticated app status. Existing coordinators can forward these records without requiring a coordinator upgrade.
- Cloud control accepts playlist history from app status when the dedicated inspection query is unavailable, retains the last successful history through outages, and rejects mismatched room history.
- Room history opens at five calendar days before today in Asia/Seoul and searches immediately when the date changes.
- An offline PC must reconnect and apply the app update before previously unsynchronized records can appear. This release does not recreate or publicly expose playlists.

Validation: isolated Windows app Release build (0 warnings/errors), core history unit tests, legacy-coordinator snapshot/retention/room-isolation tests, and room history behavior tests.
