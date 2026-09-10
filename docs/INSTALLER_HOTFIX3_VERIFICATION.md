# Installer recovery hardening — 2026-09-10

## Scope

Production bootstrap version 0.4.1.3, release artifact suffix `hotfix3`. Keeps the shipped v0.4.1
application, coordinator, broker and signed startup bundle unchanged. Only the prepared package's
installation script changes. Full entry-by-entry SHA-256 comparison confirmed that boundary.

Changes:

- Keep the earlier browser-login redirect and browser copy-suffix filename fixes.
- Prevent simultaneous installer instances. Check 3 GB free on the temporary drive before enrollment.
- Retry idempotent package downloads at most three times, with a 60-second idle read deadline and
  15-minute attempt deadline. Reject redirects, auth failures, certificate failures and hash corruption;
  never disable TLS validation. Validate size while streaming and SHA-256 before extraction.
- Ignore unrelated/stale loopback callbacks, retry port binding, and constrain the enrollment endpoint.
- Wait for the user's actual first-run `.consent` marker, not merely creation of `config.json`.
- Register/change services using the typed Windows CIM API, preserving quoted executable paths and
  avoiding native `sc create` parsing or delete/recreate races. Check native return codes.
- Use language-independent well-known SIDs for SYSTEM/Administrators and check ACL command failures.
- Preserve previous binaries/policy/config before replacement. On script failure restore these and
  previous service definitions/task; retain administrator-only recovery material on failure.
- Stop only exact owned executable paths, not every process named cloudflared.
- Store connection instructions outside the disposable extraction folder. Record stages and redacted
  failures in `%LOCALAPPDATA%\MediaCaptureHelper\InstallerLogs`.
- Distinguish installed-but-central-connection-pending from installation failure; do not advise a
  destructive whole-install retry merely because the first report takes more than two minutes.

Windows service API reference: [Microsoft Create](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/create-method-in-class-win32-service)
and [Change](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/change-method-in-class-win32-service).

## Verified

- Core tests: 463 passed; inspection/download/filename tests: 48 passed.
- Worker tests: 18 passed.
- Windows PowerShell 5.1: production service helper tested against mocked CIM for quoted Korean/space
  paths, LocalService, create, repeated change and nonzero error propagation.
- Windows PowerShell 5.1: production transaction with real temporary files and mocked OS integration;
  injected copy, ACL and registration failures all restore original app config, binaries, policy and
  coordinator config. These are not actual SCM installation tests.
- Local Worker/Durable Object/R2/WebSocket smoke: health, artifact readiness, owner/room permissions,
  one-time enrollment, package transfer, self status before/after first report and command round trip.
- Published artifact sizes and GitHub-reported SHA-256 match local files.
- Production Worker version `75c4c87a-57c2-4a4d-8e16-8aab48240ff1` deployed. Unauthenticated
  installation navigation for all four rooms returns login redirects; device `/ready` returns
  `{"status":"ready","missing":[]}` after deployment.

Published identities:

| Artifact | Bytes | SHA-256 |
| --- | ---: | --- |
| Installer hotfix3 | 161714473 | `496963496d21f179c4a568c44b8d2bcd602f8fa7a8c69091b217b458981149b7` |
| Prepared package hotfix3 | 514388119 | `78508203ab751f22b394082fd381e5927430e575cfc8f06b3bb7005c01d4d198` |

## Reproduce local server integration checks

Run in `cloudflare/control-plane`. The smoke config is local-only test configuration; **never deploy it**.
It has no production routes and uses dummy identities, not Google sign-in.

```powershell
./node_modules/.bin/wrangler.cmd r2 object put silentstream-smoke-fixtures/package.bin --local --file test/fixtures/package.bin --config test/wrangler-smoke.jsonc --persist-to .deploy/smoke-test
./node_modules/.bin/wrangler.cmd r2 object put silentstream-smoke-fixtures/installer.bin --local --file test/fixtures/installer.bin --config test/wrangler-smoke.jsonc --persist-to .deploy/smoke-test
./node_modules/.bin/wrangler.cmd dev --local --ip 127.0.0.1 --port 8799 --config test/wrangler-smoke.jsonc --persist-to .deploy/smoke-test
# In another terminal:
npm run smoke
```

Run `inspection/deployment/Test-InstallerRecovery.ps1` and `Test-ServiceRegistration.ps1` explicitly
under Windows PowerShell 5.1 as well as modern PowerShell. Neither installs real services.

## Limits and remaining field acceptance

The agent process is not a Windows administrator even outside its sandbox. Actual service creation,
startup under LocalService, UAC account switching, task launch, and M112 hardware/network/Google login
have not been exercised on the classroom PC. A local simulated report is not a real M112 report.

The rollback covers the inspection service script, not the upstream application's own installer,
power loss, or central credential revocation after a replacement device has connected. SSL trust
failures caused by the classroom clock, certificate store or network interception require evidence
from that PC; weakening certificate validation is not an acceptable fix. Google approval exchange
remains single-use and is deliberately not blindly retried.

Field acceptance: download fresh from the M112 installation page, approve UAC and Google access,
complete first-use consent if shown, then confirm real M112 central connection and first report.
If that fails, collect the new diagnostic log and retained recovery directory; do not ask the user to
run blind cleanup commands or delete their settings/YouTube credentials.
