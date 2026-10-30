# Media Capture Helper 0.4.30

- Persists a daily YouTube API quota deadline for playlist preparation, video membership and live archive maintenance. A quota failure stops the remaining batch and further API calls across dates, including after an app restart. Existing video IDs and membership receipts remain intact.
- Retries after the next Pacific-time midnight with a one-second margin, including daylight-saving transitions. A reset deadline permits another check; it does not guarantee that YouTube will accept the request. Channel upload limits, temporary rate limits, authorization errors and server errors are not treated as this daily API quota.
- Includes the 0.4.29 local-recording, cancellation, queue, original-file and exit-aware restart protections. The bundled signed inspection helper remains 0.4.22. Updating the separately released inspection helper 0.4.28 still requires a verified app-closed maintenance window for its first installation.

The additional regression suite reproduces ten failures in the previous code and verifies four unrelated error cases. Device installation, recording continuity, remaining uploads and playback require separate field verification. This release does not increase YouTube limits or clear the upload queue's channel-limit pause.

Quota timing follows the [official YouTube Data API quota documentation](https://developers.google.com/youtube/data/determine_quota_cost).
