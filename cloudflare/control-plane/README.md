# SilentStream Cloudflare control plane

This Worker replaces the always-on ASP.NET host with Cloudflare Workers, one SQLite-backed Durable
Object, Workers Static Assets, and release artifacts served through the authenticated Worker. Classroom devices open outbound WebSockets
to `agent.silentstream.win`; the human hostnames remain available even while every classroom PC is
offline.

Production deployment order:

1. Publish the signed package and bootstrap installer at the HTTPS URLs named in `wrangler.jsonc`. A
   private R2 binding named `ARTIFACTS` can be added later; the deployment uses GitHub Releases when
   that binding is absent.
2. Deploy the Worker and its Custom Domains, including the apex and `www` hostnames. Both redirect
   without content to the protected admin portal.
3. Create a Google OAuth Web client with `https://admin.silentstream.win/auth/callback` as its redirect,
   set `GOOGLE_CLIENT_ID`, and store `GOOGLE_CLIENT_SECRET` with `wrangler secret put`. Cloudflare
   Access remains an optional alternative through `ACCESS_TEAM_DOMAIN` and `ACCESS_AUD`.
4. Set `BOOTSTRAP_OWNER_EMAIL` to the first administrator's exact Google email, then redeploy.
5. Open `https://admin.silentstream.win/control/`. The first successfully authenticated Access user
   becomes the non-removable owner and can assign admin or room-manager access in the portal.

`/health` and `/ready` are public only on the device-only hostname for machine probes. On every
human-facing hostname they require the same login as the portal. All protected responses are marked
`no-store`, unauthenticated APIs return a bodyless `401`, and unauthenticated pages redirect directly
to login. The device API uses a one-time enrollment code followed by a random per-device secret; it
never trusts Access user headers and is unavailable on the human hostnames.

## Room-manager PWAs

Each configured room hostname is an independent installable PWA:

- `https://m111.silentstream.win/` — `M111`
- `https://m112.silentstream.win/` — `M112`
- `https://m113.silentstream.win/` — `M113`
- `https://baek202.silentstream.win/` — `백202`

The central portal exposes a room-app link only for rooms in the signed-in user's session. With Google
OAuth, opening that link uses a two-minute, single-use handoff stored in the Durable Object. The room
origin then receives its own host-only `__Host-SilentStreamSession` cookie bound to that room. Do not
replace this with a parent-domain cookie. Every page load and command still checks the current room
assignment, and a room origin refuses commands addressed to a different room.

The PWA service worker never caches authenticated HTML, API responses, device snapshots, or commands.
When offline it renders a standalone safety page with all controls hidden. Android/desktop Chromium
shows the in-app install button when `beforeinstallprompt` is available; iPhone users are shown the
Safari **공유 → 홈 화면에 추가 → 웹 앱으로 열기** instructions.

Room icons are deterministic project assets based on the existing SilentStream radio mark. Regenerate
all 192, 512, maskable, and Apple touch PNGs on Windows with:

```powershell
./scripts/Generate-RoomPwaIcons.ps1
```

After deployment, install at least two room PWAs on the same Android device and iPhone, confirm their
labels/icons remain distinct, and verify each launches only its own room. Also verify expired login,
revoked room access, offline launch, and logout on both platforms.
