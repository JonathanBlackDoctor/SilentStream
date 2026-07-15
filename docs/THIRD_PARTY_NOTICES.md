# Third-party notices

Media Capture Helper release packages include third-party software used only for the features
described below. Copyrights and licenses remain with their respective owners.

## yt-dlp 2026.06.09

- Project: https://github.com/yt-dlp/yt-dlp
- Purpose: recover an audio-only source from a public or unlisted YouTube video previously
  uploaded by this application when its local M4A cache is missing.
- The yt-dlp project is primarily offered under the Unlicense. Its Windows executable also
  contains components under ISC, MIT, and other licenses.
- The release package includes `yt-dlp/THIRD_PARTY_LICENSES.txt` from the pinned upstream tag with
  the detailed component notices.

The application does not pass arbitrary user-provided URLs, browser cookies, or OAuth credentials
to yt-dlp. Private or sign-in-required videos are outside the supported recovery scope.

## Deno 2.8.1

- Project: https://github.com/denoland/deno
- Purpose: execute yt-dlp's bundled YouTube JavaScript challenge solver with restricted runtime
  permissions.
- License: MIT.
- The release package includes `yt-dlp/DENO_LICENSE.md` from the pinned upstream tag.
