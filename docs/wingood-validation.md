# Wingood implementation validation

Checked October 14, 2026. The working repository moved to `D:\Programming\wingood`; generated artifacts, raw logs, and the known-good prototype executable were preserved locally. Public source is `Defiect/wingood`, licensed MIT.

## Automated checks

- Locked restore, Release build with warnings treated as errors, and format verification pass.
- 39 tests pass across the core and CLI projects. Coverage includes normal/short entry points, default-yes/EOF semantics, name matching, ambiguous copies, exclusions, source/version binding, ordinary vs explicit-only tables, empty/unknown layouts, whole-batch submission, bounded submission/refill, individual failures, cancellation/draining, dry-run submission prevention, retry staying within recorded failures, interrupted/truncated-run recovery, source-error reporting, retention, and literal metadata in wide/narrow/plain terminal output.
- A self-contained Windows x64 publish includes both apphosts and the exact build's native WinGet bridge. Runtime configuration declares includedFrameworks and the ZIP contains hostfxr, hostpolicy, coreclr, and System.Private.CoreLib.
- The installer test runs under PowerShell 7 and Windows PowerShell 5.1, in isolated temporary directories without modifying PATH. It executes both aliases, installs twice, verifies the full previous installation survives checksum failure and an exclusive executable file lock, and checks process/user PATH remain unchanged.

## Read-only checks on this PC

The installed engine is WinGet `1.30.110-preview`, App Installer `1.30.109.0`; SDK `10.0.401`, COM SDK `1.30.100-preview`.

- Full `upgrade --dry-run` selected 40 applicable matching updates from 45 native ordinary rows. MSYS2's explicit-targeting entry was excluded. The API found no applicable update for five other apps, which were skipped and reported, with exit code 1 for incomplete processing. No downloads or installations were submitted. A separate read-only probe confirmed their IDs/sources/latest versions match the CLI listing; `IsUpdateAvailable` is false, and `DefaultInstallVersion` falls back to the newest available version. The native listing itself does not perform final installer applicability selection.
- `up Fastfetch-cli.Fastfetch KDE.KDEConnect --exclude KDE.KDEConnect --dry-run` selected only fastfetch, returned 0, and submitted nothing.
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

GitHub checks and the public release-download installer path are verified after the first release is published. Their final results will be recorded here.
