# Lifecycle and original-preservation fixes, 2026-11-02

Status: isolated implementation and regression verification complete; not deployed.

## Scope and base

Branch: `codex/lifecycle-fixes-20261102`, based on `0517e2e461fa81666ee0bf9dd263df63e24f031b`.
The five audited production files were identical to release v0.4.31 (`58fd2da2a83dff5d6c8cef0dbbd9fd73f4e21197`). The checkout contains unrelated later changes, so using it preserves those changes rather than resetting to an older release. During this work the original checkout advanced to documentation-only commit `6c2f46f`; this branch does not overwrite that concurrent work.

The original checkout's dirty `작업현황.md` and untracked `tests/SilentStream.Tests/TestResults/` were left alone. This document records the isolated branch's state instead of editing the user's existing status changes. No production application, startup registration, upload, original recording, installation, package, release or remote Git branch was changed.

## Confirmed failures and fixes

1. **Duplicate launch could auto-apply a pending Velopack update before the instance guard.** Real Velopack 1.2.0 with an intercepted process implementation reproduced the apply before the secondary instance was rejected. `Program` disables normal-launch automatic application; the existing guarded startup handoff remains responsible for application. No real updater was launched by tests.
2. **Encoder shutdown waited for FFmpeg while its audio input was still open.** The Windows named-pipe reproduction took 10,037 ms and forced termination before the fix. Cleanup now waits for both writer tasks, closes audio and video inputs, then waits for finalization. The repaired actual `EncoderPipeline.StopAsync` took 11–16 ms in the synthetic runs without forced termination. A writer-failure regression also proves that the other writer is awaited before closing its handle.
3. **A short or corrupt stream copy could certify an entire requested source interval.** Before the fix, a growing 12-second fragmented MP4 yielded a 3.23-second damaged clip that passed the sample-presence gate; a fake version-1 upload receipt suppressed recutting and allowed deletion after the original finished. Extraction now decodes and matches every output video frame to the requested source window, checks timestamp continuity and absolute source coverage, and rejects decoder errors. The completed-source retry succeeds. Video length cannot be substituted with audio/container length; shifted keyframe copies are rejected.
4. **Startup update application ignored explicit cancellation on the pending-package branch.** Entry and final-apply cancellation checks now propagate caller cancellation. WPF shutdown supplies the cancellation token, and cancelled startup does not continue initializing recovery/recording. Both pre-cancelled and final-boundary cases now make zero apply calls.

## Receipt and compatibility decisions

- `SourceKey` generation is unchanged. Old acknowledged receipts continue suppressing duplicate uploads.
- Version-2 evidence separately records the source's full path and recording start, verified source interval, output byte length and SHA-256. The queue checks the file and holds a read lease until the uploader finishes consuming it. Only this evidence can authorize original deletion.
- Version-0/1 receipts and receipts with missing or mismatched evidence are not upgraded automatically. `RequiresCoverageRevalidation` and a startup warning expose that state. Re-extraction metadata cannot overwrite an old upload acknowledgement or promote its validation version.
- Retention unions intervals only for the exact source path and recording start. Exact joins and overlaps can cover the original; a one-second gap, duplicated front halves, another source file or another recording generation cannot. Pending/reconciliation jobs still protect originals.
- Mid-period recording uses the intersection with the actual source interval. No fixed seconds/GOP tolerance is allowed. Rational timebases are compared at sub-tick precision, and source coverage is rounded inward to DateTime ticks. Unprovable coverage is retained, not assumed complete.

## Verification

Windows, .NET SDK 8.0.426, actual test FFmpeg `N-125119-g4bbb7d9b99-20260619`; binary SHA-256 `76D76CE3018648A2044CD8ACF2A04FDD46FC5E97616DD381813838D665A25055`.

Final related test-project runs:

- `SilentStream.Tests`: **713 passed, 0 failed, 0 skipped**.
- `SilentStream.Inspection.Tests`: **97 passed, 0 failed, 0 skipped**. Synthetic FFmpeg/named-pipe cases were enabled, not skipped.
- `SilentStream.App` build: **0 errors, 0 warnings**, no application execution.
- `git diff --check`: clean.

Coverage includes pending-update duplicate launch and cancellation, writer fault ordering, actual Windows audio EOF, short EOF, active fragmented MP4 then successful completed retry, long audio/short video, intact sample tables with corrupted video payload, shifted keyframe start, mid-period intersection, exact/overlapping coverage union, one-second gap, duplicate intervals, source identity mismatch, legacy receipt compatibility, durable receipt restoration, altered-cut rejection before upload, and stable bytes through a fake upload.

Evidence remains under the local temp directories:

- `silentstream-lifecycle-audit-20261102/audit-manifest.json`: baseline source hashes, release/base, tools and safety boundary.
- That directory's `results/lifecycle-audit.trx` and `results/growing-original-640-audit.trx`: failing baseline reproductions.
- This worktree's `artifacts/test-results/full-core.trx`, `full-inspection.trx` and `artifacts/regression/evidence/`: final test results and synthetic media/process logs.

To rerun, set `SSTREAM_AUDIT_FFMPEG` to an explicit FFmpeg test executable and optionally `SSTREAM_REGRESSION_OUTPUT` to an isolated output directory, then run `dotnet test` for the two projects above. FFmpeg cases explicitly skip if that environment variable is missing. Do not point fixtures at operational recordings or catalogs.

## Remaining validation and limits

This is not a proof that the application has no other defects. Real signed-package installation, rollback after machine power loss, restart on school PCs, and a six-hour or multi-day production soak were deliberately not executed. The two unrelated Windows test projects were not run in this change; the affected app and its platform dependencies compiled.

Full frame validation performs two video decodes and stores frame traces in memory. Performance at classroom resolution/hour-long duration during capture is not measured. Strict timestamp/coverage checks may conservatively defer valid but non-frame-aligned or irregular-timestamp clips, and legacy receipts retain originals until separately revalidated. This protects data but can increase disk use and delay VOD availability. Audio completeness is not newly certified by the video proof.

Before release: review these changes, run a disposable installed-package update/rollback test and a synthetic hour-long workload on representative hardware with CPU/memory/disk measurements. Any school-PC restart, production upload, original deletion or deployment remains a separately authorized step. No deployment artifact or push was produced here.
