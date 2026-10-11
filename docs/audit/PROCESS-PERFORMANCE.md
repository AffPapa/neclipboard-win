# Process-level performance observations

Measured 2026-10-11. These are bounded process observations, not a claim that desktop E2E or final release acceptance is complete. Historical measurements below are labelled separately.

## Final source-bound candidate: repeated profile scaling

Commit `28957d4a3645b0b8c945b753ec409986c516da7c`, EXE SHA-256 `e09942e870045c4b0f3eca5906577357cb7b279acc3a525e8afa23f8ac112042`. **96/96 noninteractive isolated launches PASS**, six warmups plus 30 measured launches per profile. Direct DPAPI/JSON assertions verify exact synthetic semantics, completed current documents and absence of unfinished writes; no desktop integrations or clipboard operations.

| Profile | Input-idle p50 / p95 | Sampled peak Private Bytes p50 |
| --- | --- | --- |
| Empty | 467.44 / 555.47 ms | 37.19 MiB |
| 200 entries | 513.20 / 987.55 ms | 47.00 MiB |
| 1000 entries | 556.10 / 1576.86 ms | 108.30 MiB |

**Concurrent coordinator UI tests and uncontrolled workstation activity** make this a functional load regression check, not a controlled speed comparison to earlier results. Input-idle is not rendered readiness or hotkey latency. Sampled memory is not steady-state allocation or leak evidence. Whole-process time includes the one-second QA timer and awaited shutdown serialization. Raw ignored evidence: `artifacts/profile-scaling-f3c75ca54f3541c2aa8db3565e7bf948/{environment,samples,summary,completion,concurrency-context}.json`. Three synthetic profiles were removed after successful completion.

## Historical corrected candidate: profile scaling

Commit `7775cb310f9c85e00ae200694032e8550cbd0868`, EXE SHA-256 `7785e45c62e440ac18845cbb660c7742b761a40a1721a49f484d3e541f6240ae`. **96/96 noninteractive isolated launches PASS**: two warmups and 30 measured rotating rounds for each of three profiles. A separate nine-launch pilot passed first. Every exit checked current history presence, absence of unfinished `.tmp`/quarantine, and exact synthetic settings/history values through direct DPAPI/JSON rather than a recovering store Load. DPAPI ciphertext may change on ordinary exit; it is not a semantic equality criterion.

| Profile | Text payload | Input-idle p50 / p95 | Sampled peak Private Bytes p50 |
| --- | --- | --- | --- |
| Empty | 0 | 356.65 / 392.63 ms | 37.05 MiB |
| 200 entries | about 0.78 MiB | 403.71 / 438.96 ms | 47.05 MiB |
| 1000 entries | about 7.82 MiB | 418.47 / 475.90 ms | 108.16 MiB |

`WaitForInputIdle` is an OS observable, **not rendered palette readiness, usable input or hotkey latency**. No window was shown, no listener/tray/global clipboard/production profile was exercised. Whole-process resource observations include startup, the one-second QA timer and completed exit serialization; the timer was not subtracted to invent a startup estimate. These are sampled peaks, not steady-state allocation, GC or leak measurements. Maximum observed OS peak Working Set was 173.70 MiB in the 1000-entry case. Background workstation activity was not controlled. Raw ignored evidence: `artifacts/profile-scaling-e483ac8dd5ad4fd1b97f74807ba9d762/{environment,samples,summary,cleanup}.json`. Three synthetic profiles were removed after the successful run.

An observer of the older `ad2dbf…` desktop QA process was stopped at the coordinator's request after 120.317 seconds / 25 samples to prioritize the shutdown regression. The application was not stopped. This is **not** a completed ten-minute idle or a measurement of the corrected EXE.

## Additional 601-second observation of the UX candidate

Candidate SHA-256 `88a88dbec76e2c4c4f324bc88bdc6794568fc0b75327022fba6b90965ae9ee96`; isolated empty QA profile, not production clipboard/tray operation. Raw ignored evidence: `artifacts/idle-ux-9973cd64/{environment,idle-samples,idle-summary}.json`. 121 observations over 601.488 seconds completed, executable hash unchanged, no diagnostic file, zero profile files. Other workstation activity was not controlled.

CPU consumed 1453.125 ms: 0.2416% of one logical processor / 0.01510% across 16 processors. Private Bytes first/last 92.47/97.55 MiB, range 86.53–100.62 MiB; maximum Working Set 135.85 MiB. Handles increased 602→668 (range 596–673); threads 14→12 (range 10–16). **This observation does not prove absence of leaks**, and the handle increase needs a longer controlled/repeated-interaction investigation. It does not qualify the later commit-bound `ad2dbf...` EXE's production idle or loaded-history behavior.

## Environment and files

- Windows 11 Pro x64, build 26200 (`10.0.26200`).
- AMD Ryzen 7 9700X, 8 physical cores / 16 logical processors; approximately 32 GB installed RAM (31.11 GiB visible to Windows); NVMe SSD.
- Release configuration, no debugger attached by the benchmark. System protection and power settings were not changed. Other work on the computer was not suspended, so scheduling/AV/build noise remains possible.
- Baseline: initial 0.1.5 source snapshot, rebuilt self-contained with SDK 10.0.401 targeting .NET/Windows Desktop **8.0.31**. This is not a previously published release download.
- Candidate: 0.2.0 intermediate QA EXE, .NET/Windows Desktop **10.0.12**. Later UI/privacy fixes and the final release freeze are separate.

| Build | EXE bytes | SHA-256 |
| --- | ---: | --- |
| Baseline 0.1.5 | 161,741,860 | `218e2ea513d16f46833095d00db957e826adbb29ff6658816241dd9e07a3557e` |
| Candidate 0.2.0 | 173,089,849 | `4270ce7b830f8459f33af3836877ee9ebb89ce87ad3b75fa60ea7ddf1f6f9366` |

The candidate is approximately 7.0% larger. This includes a different bundled desktop runtime and product changes; it cannot be attributed to one application function from this comparison.

## Method

`scripts/release-benchmark-process.ps1` starts its own EXE copies with `--qa-isolated <unique temporary profile> --qa-interactive --qa-theme dark`. The two executable paths are separate from the coordinator's UI-test instance. The profile is empty; capture, clipboard listener, global hotkey, tray and autostart integrations are disabled. All data is synthetic. The script only observes process state; it does not use UI Automation, send input or inspect another application's contents.

Processes are requested hidden with `Start-Process`. WPF can still create/show its own QA window. The script observes `MainWindowHandle` and `WaitForInputIdle`, polling at about 10 ms. A window handle means a top-level window was observable; input-idle means the process entered an idle message-loop state. Neither observation proves a rendered frame, useful keyboard input, hotkey latency or clipboard readiness.

There are two initial paired runs, then **30 paired sequential runs per version**. The first observation is kept separate. No disk-cache flushing or single-file extraction-cache reset is performed: the 30-run results are **warm repeated process starts, not cold starts**. Percentiles use nearest rank. Each startup process is terminated after observation because it is an isolated throwaway test, so the measurement excludes graceful production shutdown.

After startup runs, two new isolated processes run concurrently. Following a five-second settling period, resource counters are sampled every five seconds for approximately five minutes. CPU time is normalized both to one logical processor and to all 16 logical processors. Working Set and Private Bytes are reported separately; they are not interchangeable. GPU allocation, shared driver cost, managed allocations and GC pauses are not measured by these counters.

Raw nonpersonal measurements are kept under ignored `artifacts/process-benchmark/`: `environment.json`, `startup-samples.json`, `startup-summary.json`, `idle-samples.json` and `idle-summary.json`. These data files deliberately omit usernames, serial numbers, hostnames and absolute EXE/profile paths.

## Repeated startup results

| Observable | Baseline | Candidate |
| --- | ---: | ---: |
| Main window handle p50 | 355.5 ms | 445.4 ms |
| Main window handle p95 | 471.1 ms | 569.7 ms |
| Input-idle p50 | 382.6 ms | 450.6 ms |
| Input-idle p95 | 507.4 ms | 571.8 ms |
| Window-handle samples | 30/30 | 30/30 |
| Mean Private Bytes soon after readiness | 97.0 MiB | 99.2 MiB |
| Mean Working Set soon after readiness | 114.6 MiB | 117.9 MiB |

The first observed input-idle times were 908.8 ms and 562.4 ms respectively; cache/extraction state was not equivalent, so those two values do **not** establish a cold-start improvement.

The candidate has a measurable repeated-start cost: about **90 ms median window-handle time** and **68 ms median input-idle time** in this run. The input-idle p95 increase is approximately 12.7%; this exceeds the initial 10% regression guide and must be acknowledged, not described as a universal speedup. Both versions were well below the provisional two-second startup guide on this machine, but useful-input and production-mode launch latency require separate acceptance. Reasons may include runtime and additional initialization/controls; no causal profile has isolated them.

## Idle results

The run completed; its saved samples and summary were rechecked after the interrupted audit resumed. Each version has **61 observations over 300.95 seconds** following the five-second settling period.

| Counter over the interval | Baseline | Candidate |
| --- | ---: | ---: |
| CPU time consumed | 15.625 ms | 31.250 ms |
| Average CPU, one logical processor | 0.005192% | 0.010384% |
| Average CPU, all 16 logical processors | 0.000324% | 0.000649% |
| Private Bytes, first → last | 88.85 → 82.93 MiB | 91.46 → 85.52 MiB |
| Working Set, first → last | 114.67 → 110.29 MiB | 118.23 → 113.90 MiB |
| Handles, first → last | 575 → 595 | 608 → 626 |
| Threads, first → last | 14 → 12 | 14 → 12 |

CPU remained very low and the observed memory counters decreased over this short, empty-profile interval. Handles increased by 20 and 18 respectively; these data alone do not explain their lifetime or establish a leak. This is a completed **five-minute isolated measurement**, not acceptance of the required ten-minute production idle or long-run resource-stability scenarios. The candidate binary predates subsequent privacy/startup changes, so the final EXE still needs proportionate resource rechecking.

## Boundaries and remaining work

This empty isolated profile does not represent a production profile with 200/1000 entries, large images, active clipboard traffic or restore. It does not replace the full-content model/search benchmarks, UI responsiveness tests, ten-minute production idle target, a 30–60 minute workload, or approximately 1000 repeated palette open/close cycles. Five-minute stable counters would be evidence for this particular interval and configuration, not proof that no leaks exist.

Repeat affected measurements after substantive startup/resource changes. Do not rebuild the accepted final EXE simply to attach measurement metadata; its exact checksum remains part of release acceptance.
