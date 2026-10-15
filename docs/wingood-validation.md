# Wingood implementation validation

Checked October 14, 2026. The working repository moved to `D:\Programming\wingood`; generated artifacts, raw logs, and the known-good prototype executable were preserved locally. Public source is `Defiect/wingood`, licensed MIT.

## Automated checks

- Locked restore, Release build with warnings treated as errors, and format verification pass.
- 42 tests pass across the core and CLI projects. Coverage includes normal/short entry points, default-yes/EOF semantics, name matching, ambiguous copies, exclusions, source/version binding, ordinary vs explicit-only tables, empty/unknown layouts, whole-batch submission, bounded submission/refill, individual failures, cancellation/draining, dry-run submission prevention, retry staying within recorded failures, interrupted/truncated-run recovery, source-error reporting, known applicability exclusions, retention, literal metadata in wide/narrow/plain terminal output, CI color overrides, and late progress after completion.
- A self-contained Windows x64 publish includes both apphosts and the exact build's native WinGet bridge. Runtime configuration declares includedFrameworks and the ZIP contains hostfxr, hostpolicy, coreclr, and System.Private.CoreLib.
- The installer test runs under PowerShell 7 and Windows PowerShell 5.1, in isolated temporary directories without modifying PATH. It executes both aliases, installs twice, verifies the full previous installation survives checksum failure and an exclusive executable file lock, and checks process/user PATH remain unchanged.

## Read-only checks on this PC

The installed engine is WinGet `1.30.110-preview`, App Installer `1.30.109.0`; SDK `10.0.401`, COM SDK `1.30.100-preview`.

- The final full `upgrade --dry-run` selected 40 applicable matching updates from 45 native ordinary rows and returned 0. MSYS2's explicit-targeting entry was excluded. The API found no applicable update for five other apps, which were reported as normal informational exclusions. No downloads or installations were submitted. A separate read-only probe confirmed their IDs/sources/latest versions match the CLI listing; `IsUpdateAvailable` is false, and `DefaultInstallVersion` falls back to the newest available version. The native listing itself does not perform final installer applicability selection.
- `up Fastfetch-cli.Fastfetch KDE.KDEConnect --exclude KDE.KDEConnect --dry-run` selected only fastfetch and submitted nothing. This check preceded the final source-agreement changes; named selections still use the same tested exclusion path.
- `upgrade --source store --dry-run` returned 0 with no eligible updates. This checks discovery/empty handling, not Store installation.
- `show JanDeDobbeleer.OhMyPosh` captured and formatted name/version, description, metadata, links, tags, installer type/locale/hash/release date/offline information. Forced-color output contains ANSI styles; plain output retains complete links and hashes without terminal-width wrapping.

The new CLI has not performed additional real app upgrades during this implementation. The original nine successful upgrades remain historical evidence for the reused COM execution path. Store installation, alternate engine versions, and a controlled same-app speed comparison are not claimed as live-tested.

The five API exclusions were investigated with a separate read-only COM probe and source/installer-filtered `winget show` commands:

| App | Recorded installed type/scope | Available target | Why the default update is not applicable |
| --- | --- | --- | --- |
| LLVM | exe / machine | Wix | No compatible exe installer for the target. |
| PowerShell Preview | msi / machine | msix | No applicable MSI/Wix target. |
| PowerShell | exe / machine | msix | No compatible exe installer for the target. |
| Qobuz | portable / user | exe | No portable target. |
| Vulkan SDK | exe / user | exe / machine | The target does not support the recorded user scope. |

WinGet's own bulk execution performs final installer applicability checks after printing its version list. The ordinary printed table is useful for the explicit-targeting exclusion, but is not proof that an installer is applicable. No forced installer-type or scope migration was attempted.

## Release checks

- [Final source checks](https://github.com/Defiect/wingood/actions/runs/38660985141) and the [v0.1.0 release workflow](https://github.com/Defiect/wingood/actions/runs/38661196236) passed, including archive installation under both PowerShell versions. The tagged code is commit `c56005177af52f181f38a63f7085e4bf6c544eb9`.
- [v0.1.0](https://github.com/Defiect/wingood/releases/tag/v0.1.0) is public with the full self-contained ZIP and SHA256 sidecar. The ZIP is 45,145,265 bytes; SHA256 `531b18af30906e8c162037c01b6cc028ddb984f8eb62b803d3fa79c18171f29f`.
- The canonical public `irm .../install.ps1 | iex` command installed wingood under `C:\Users\Sean\AppData\Local\Programs\wingood`. Both apphosts return `0.1.0`. User PATH contains exactly one entry for the installation folder; caller error preferences are preserved.
- Native host tracing confirms `hostfxr.dll` and `coreclr.dll` load from that installation directory and the app executes as self-contained.
- The installed prebuilt `wg upgrade --dry-run --source winget` activated the WinGet API successfully, selected 41 updates, reported the five known applicability exclusions, and returned 0 without submitting updates. The source-only count differs from combined discovery because the native combined catalog chooses one correlated source for an installed app.
- Installed `wg show JanDeDobbeleer.OhMyPosh --color never` returned the expected formatted details.
- The public installer was then rerun through Windows PowerShell 5.1 against the real installed folder. It downloaded/verified the release, reinstalled successfully, and preserved the user PATH byte for byte.
