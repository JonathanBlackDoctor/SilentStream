# Media Capture Helper 0.4.21

Installation and updates use the authenticated SilentStream distribution server. The application
keeps a Windows DPAPI-protected update identity; its public key is bound to the enrolled classroom
device through the existing coordinator. Requests are signed, time limited and protected against replay.
GitHub credentials remain on the server and are never included in a PC package.

The app checks for updates at startup and every six hours. Recording is not forcibly restarted for an
update. During the first migration startup, registration is retried briefly after the local app starts;
an unavailable update server leaves the currently installed version running.

Helper downloads use the same authenticated server, retaining size and SHA-256 verification and
generating a fresh request proof on each retry. New classroom installation retains the existing
Google approval and one-time device enrollment flow.

Only the M112 replay archive remains public. Download guidance, classroom scripts, manifests, icons
and control APIs require authentication. Repository visibility changes only after the enrolled PCs
are confirmed on this update path.
