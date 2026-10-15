# Initial validation — 9 October 2026

The implementation builds and runs on Sean's Windows 11 x64 PC against the installed WinGet 1.30.110-preview engine. The working build uses COM SDK 1.30.100-preview and stable .NET SDK 10.0.401.

- Locked dependency restore succeeded.
- Release build succeeded with no warnings or errors.
- All 15 tests passed. Tests cover bounded simultaneous submission, refill after out-of-order completion, failure isolation, cancellation and draining, exact selection, duplicate installed copies, command validation, overlap calculations, completion/wait separation, and SVG generation/escaping.
- Formatting verification succeeded.
- The published executable discovered and bound three real upgrades without submitting downloads or installations: lazygit 0.65.0 to 0.65.1, Notepad++ 8.9.8 to 8.9.8.1, and MediaInfo CLI 26.05 to 26.10.
- Separate user-scope and machine-scope dry runs succeeded. The adapter normalizes the API's `system` scope to the CLI's `machine` spelling.
- A selection containing a missing package and a request for the unsupported `--all` both returned exit code 2.
- The automatic SVG timeline was rendered and visually inspected using synthetic multi-package events. This is renderer validation, not a measured WinGet run.
- A second source review found no confirmed defects in the reviewed API lifetime, cancellation, version-binding, scheduling or telemetry paths.

The published executable is `artifacts/publish/winget-unshittified.exe`. Local verification outputs are in `artifacts/checks/`, and test results are in `tests/WingetUnshittified.Tests/TestResults/`. These generated directories are ignored by Git.

## Live validation

The first elevated attempt failed before submitting upgrades with `0x800706BA`. Actual NuGet binaries revealed a transport mismatch: SDK 1.29.380 used `ncalrpc`, but installed WinGet 1.30.110-preview used `ncacn_np`. Switching to SDK 1.30.100-preview and explicitly copying its native activation DLL during publish fixed elevated discovery. The original failure is retained under `artifacts/activation-failure/`; no WinGet settings or installed engine files were changed.

The authorized three-package test succeeded for lazygit, Notepad++, and MediaInfo CLI. It took 32.503 seconds end-to-end, of which 30.123 seconds were captured by the post-discovery batch timer. The run observed three simultaneous downloading API states and one installing state. Versions were verified through `winget list`.

The authorized larger batch took 34.297 seconds end-to-end (32.126 seconds on the batch timer). MKVToolNix, MediaInfo GUI, Pandoc, Oh My Posh and Topgrade succeeded. Doppler failed with `0x8A15007D`: WinGet prohibits its per-user uninstall step in an elevated process. A separate unelevated retry reused the cached installer and succeeded in about 1.5 seconds on the batch timer. All nine applications ultimately reached their target versions; no reboot was requested by their results.

Raw runs are preserved in `artifacts/runs/first-live-batch/`, `artifacts/runs/larger-live-batch/`, and `artifacts/runs/doppler-user-retry/`. Console captures and default diagnostics are in `artifacts/live-test/` and `artifacts/larger-live-test/`. Original telemetry files were not rewritten after improving the renderer and completion sampling. The live runs verify download concurrency, queue refilling and failure isolation. They do not establish a controlled speedup percentage. See [download timing analysis](live-test-notes.md).

Command used for the three-package test (now completed; these versions no longer need upgrading):

```powershell
.\artifacts\publish\winget-unshittified.exe upgrade JesseDuffield.lazygit Notepad++.Notepad++ MediaArea.MediaInfo --accept-package-agreements --output artifacts/runs/first-live-batch
```
