# Lifecycle and original-preservation fixes, 2026-11-02

Status: isolated implementation and regression verification complete; not deployed.

Independent re-review of `c38e357` passed the three AAC acceptance windows and 36 focused tests, including preservation of the real 21-ms prefix gap. It also identified a normal 0.1–4.1-second window between keyframes that still failed extraction. The additional keyframe-preroll correction below is implemented and awaiting independent review; release remains held.

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

- `SilentStream.Tests`: **717 passed, 0 failed, 0 skipped**.
- `SilentStream.Inspection.Tests`: **98 passed, 0 failed, 0 skipped**. Synthetic FFmpeg/named-pipe cases were enabled, not skipped.
- `SilentStream.App` build: **0 errors, 0 warnings**, no application execution.
- `git diff --check`: clean.

Coverage includes pending-update duplicate launch and cancellation, writer fault ordering, actual Windows audio EOF, short EOF, active fragmented MP4 then successful completed retry, long audio/short video, intact sample tables with corrupted video payload, shifted keyframe start, mid-period intersection, exact/overlapping coverage union, one-second gap, duplicate intervals, source identity mismatch, legacy receipt compatibility, durable receipt restoration, altered-cut rejection before upload, and stable bytes through a fake upload.

Evidence remains under the local temp directories:

- `silentstream-lifecycle-audit-20261102/audit-manifest.json`: baseline source hashes, release/base, tools and safety boundary.
- That directory's `results/lifecycle-audit.trx` and `results/growing-original-640-audit.trx`: failing baseline reproductions.
- This worktree's `artifacts/test-results/full-core.trx`, `full-inspection.trx` and `artifacts/regression/evidence/`: initial fix test results and synthetic media/process logs.
- `artifacts/test-results/aac-before.trx`: the independent review's healthy-AAC regression reproduced against the initial fix. `aac-after-focused.trx` has all 12 lifecycle regressions passing. `aac-full-core.trx` and `aac-full-inspection.trx` are the final **815 passing** tests after the AAC correction. `artifacts/aac-regression-after/evidence/` contains the new synthetic run evidence.

## Independent-review AAC correction

Review of `af054701` found a release-blocking regression: normal H.264/AAC 48-kHz fragmented MP4 cuts were rejected by whole-trace frame-count equality. The initial synthetic acceptance tests had no ordinary AAC soundtrack, so they missed this case. A new test first reproduced failure against `af054701`, then passed after correction for 0–4, 0–8 and 2–6 seconds of a completed 8-second recording.

The validator now preserves the demuxer's timebase instead of rounding timestamps to 1/fps. Every selected source frame must still appear in order with equal decoded hash, size, relative timestamp and duration. Additional decoded frames are permitted only outside the requested window after mapping to source time; they cannot replace a missing requested frame or extend short-source evidence. Decoder errors and internal timestamp gaps still reject the cut.

AAC timing can put the first selected frame slightly after a requested seek boundary. A prefix smaller than the measured first-frame duration can yield an accepted clip, but that prefix is excluded from its receipt: the 2–6 second fixture records **2.0213542–6 seconds**, not 2–6. One full missing frame is still rejected. This is a narrower verified interval, not a waived duration deficit. Persistence and retention tests confirm that partial receipts do not release the full original, whereas the verified 0–8 second receipt does. Short video with long audio, corrupted payload, shifted keyframes, completed-source retry and all previous retention safeguards pass again. Independent re-review is still pending; no deployment was performed.

To rerun, set `SSTREAM_AUDIT_FFMPEG` to an explicit FFmpeg test executable and optionally `SSTREAM_REGRESSION_OUTPUT` to an isolated output directory, then run `dotnet test` for the two projects above. FFmpeg cases explicitly skip if that environment variable is missing. Do not point fixtures at operational recordings or catalogs.

## Remaining validation and limits

This is not a proof that the application has no other defects. Real signed-package installation, rollback after machine power loss, restart on school PCs, and a six-hour or multi-day production soak were deliberately not executed. The two unrelated Windows test projects were not run in this change; the affected app and its platform dependencies compiled.

Full frame validation performs two video decodes and stores frame traces in memory. Performance at classroom resolution/hour-long duration during capture is not measured. Strict timestamp/coverage checks may conservatively defer irregular-timestamp clips, and legacy receipts retain originals until separately revalidated. This protects data but can increase disk use and delay VOD availability. Audio completeness is not newly certified by the video proof.

## Non-keyframe availability correction

The same normal synthetic AAC original was passed through the actual base `0517e2e` service DLL and the `c38e357` service DLL using an isolated comparison harness. For 0.1–4.1 seconds, the base returned one clip while `c38e357` returned none. Frame hashes show that the base clip's first frame comes from source time **2.0213541667 seconds**, omitting **1.9213541667 seconds** of the requested prefix. The old clip has 63 video frames; the source window including its necessary keyframe preroll has 123. Thus the strict validator exposed an existing incomplete-copy defect while also making ordinary VOD requests unavailable.

This is a normal code path, not a rare alignment promise: `FfmpegArgumentsBuilder` sets GOP to `EffectiveFps * 2`; `StreamOrchestrator` stamps recording/restart times with `DateTime.Now`; `PeriodScheduler` builds boundaries independently from the timetable. There is no quantization of recording starts to period/GOP boundaries. Field incidence was not measured, but ordinary fractional-second recording starts and subsequent encoder restarts can leave every period request off the GOP grid.

The correction removes the output-side `-ss 0` that discarded the preceding keyframe and dependent frames. The video remains stream-copied, retaining the small early prefix already allowed by the cutter's documented D2 behavior. Validation uses `-noaccurate_seek` on the source trace to decode the identical preceding keyframe and compare all of its frames, including the frame straddling the requested start. There is no relaxed missing-frame/duration allowance and no re-encoding. Only the requested interval is stored as verified coverage, after the actual preserved frames prove it.

On the identical comparison original, the corrected service returns one verified clip with the same SourceKey as the base and coverage exactly 0.1–4.1 seconds (at DateTime tick precision). Synthetic AAC regressions cover 0–4, 0–8, 2–6, 0.1–4.1, 1.9–5.9, 2.1–6.1 and 7.9–8 seconds; video-only nonaligned extraction also succeeds. The old incomplete `-ss 0` cut is explicitly rejected by the validator, and an active-session request entirely beyond the real EOF remains rejected. Existing short-video/long-audio, corruption, completed-source retry and receipt-retention cases remain included.

The formerly missing 21-ms prefix of the 2–6-second AAC case is now physically present in the retained preroll, so that new clip can prove 2–6 seconds. Existing receipts are not promoted. Cost is the extra preceding GOP in the copied file and frame trace, normally up to about two seconds for this encoder; the VOD can visibly begin that much before the period. There is no full-video encoding cost. The previously noted full-decode validation cost still needs representative long-duration measurement.

Comparison evidence is in sibling temp directory `silentstream-unaligned-comparison-20261102`: `base/result.json`, `current/result.json`, `preroll-fixed/result.json`, `baseline-comparison.json` and decoded frame traces. New regression results use `artifacts/test-results/unaligned-*.trx` and `artifacts/unaligned-regression/evidence/` in this worktree. No operational media or processes were used.

Before release: review these changes, run a disposable installed-package update/rollback test and a synthetic hour-long workload on representative hardware with CPU/memory/disk measurements. Any school-PC restart, production upload, original deletion or deployment remains a separately authorized step. No deployment artifact or push was produced here.
