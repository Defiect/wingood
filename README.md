# wingood

Download upcoming updates while WinGet installs. Wingood submits the selected batch early, letting WinGet overlap downloads with installation, and shows progress in a colorful terminal view.

## Install

Windows 11 24H2 or later, x64, with a working WinGet/App Installer installation:

```powershell
irm https://raw.githubusercontent.com/Defiect/wingood/main/install.ps1 | iex
```

The script downloads a prebuilt release, verifies its SHA256 checksum, installs under `%LOCALAPPDATA%\Programs\wingood`, and adds `wingood` and `wg` to your user PATH. It bundles .NET, so no Git, SDK, or separate runtime installation is required. Rerun the same command to install the latest release. Close wingood before updating wingood itself.

## Use

```powershell
wingood upgrade                         # Preview all eligible updates, then ask [Y/n].
wingood upgrade --all                    # Update without the confirmation prompt.
wg                                      # Short form: automatically update everything eligible.
wingood up                              # Alias for upgrade. update works too.
wingood upgrade Fastfetch-cli.Fastfetch  # Update one explicitly selected app.
wingood upgrade "KDE Connect"            # An unambiguous app name works too.
wingood upgrade --dry-run                # Inspect without downloading or installing.
wingood show                            # List updates without offering to install them.
wg show JanDeDobbeleer.OhMyPosh           # Show formatted package details.
wingood retry                           # Retry failures from the last run.
```

Bare `wingood` behaves like `wingood upgrade`. Only bare `wg` automatically proceeds; `wg show`, `wg upgrade`, and other named commands behave like their `wingood` counterparts. A named app proceeds after displaying the selection. Ambiguous names require a more specific name or ID. You can name several apps in one command.

Proceeding accepts package and source agreements and requests silent installation. Wingood runs in your normal user context and lets individual installers request administrator rights. Windows UAC and vendor dialogs may still require input. Wingood does not deliberately close apps, although an installer can do that independently. It does not authorize a reboot.

## Options

| Option | Behavior |
| --- | --- |
| `--all`, `-y`, `--yes` | Proceed without wingood's confirmation. Use one for unattended bulk upgrades. |
| `--dry-run` | Never submit download or installation operations. |
| `--source winget` | Limit discovery and execution to the WinGet catalog. |
| `--source store` | Limit to supported Microsoft Store updates. `msstore` also works. |
| `--scope user` or `--scope machine` | Limit installed copies to that scope. |
| `--exclude <app>` | Exclude an app for this run. Repeat for more exclusions. Use WinGet pins for permanent exclusions. |
| `--in-flight N` | Advanced: cap outstanding operations. The default submits the complete selected batch. This does not change WinGet's worker counts. |
| `--color auto`, `always`, `never` | Control colors. Auto honors `NO_COLOR`, `TERM=dumb`, and redirected output. |

Default discovery includes WinGet and supported Store entries. Wingood uses combined installed-app correlation to avoid updating one installed app through two sources. Store coverage depends on WinGet's available version data; it does not replace the Store app's complete update scan.

Ctrl+C requests cancellation of queued/download work and waits for submitted operations to settle. An installer already running may finish. Individual failures do not stop the rest of the batch. `retry` refreshes versions for the last run's failed apps and does not add unrelated updates.

Exit codes are `0` for success or no updates, `1` for failed operations or incomplete processing, `2` for invalid/ambiguous requests or missing confirmation, and `130` for cancellation. When input is redirected, ordinary bulk upgrade requires `--yes`. End-of-input never counts as confirmation.

Read-only commands do not accept new source agreements. If a source needs first-use consent before it can be listed, an automatic upgrade such as `wingood upgrade --yes` or bare `wg` accepts it as part of proceeding. Ordinary upgrade checks failed sources again after you answer yes. Sources already accepted through WinGet work with previews normally.

## How it works

Update execution uses the installed WinGet COM API. The current engine provides up to three download workers and one installation worker; wingood supplies the whole batch rather than waiting for one app to finish before submitting the next. WinGet retains control of installer choice, hashes, dependencies, user pins, and installation order.

The API omits the CLI's explicit-targeting exclusions. Wingood reads the ordinary table from a read-only `winget upgrade`, then requires the app ID, source, installed version, and available version to match API discovery. It excludes the separate explicit-targeting table. Unfamiliar layouts and ambiguous identities are skipped or rejected with an explanation, rather than submitted as guessed updates.

The API can find no applicable update for an app whose update appears in the CLI's listing. Wingood reports that difference and continues with the verified apps. Package details come from captured `winget show` output, reformatted locally because the API omits several useful fields. This compatibility layer currently recognizes English WinGet output.

Recent run records live under `%LOCALAPPDATA%\wingood\runs`, with the latest 30 retained. They include the selected versions, timestamped API progress, results, error codes, and state metrics used for retry and diagnosis. No browser reports are generated. API download states can include waiting after bytes finish; installation states can include preparation. These metrics are not a native installer profiler.

## Development

```powershell
dotnet restore --locked-mode
dotnet build -c Release --no-restore
dotnet test -c Release --no-build
dotnet format --verify-no-changes --no-restore
dotnet run --project src/Wingood -- upgrade --dry-run
```

The SDK version is pinned in `global.json`. GitHub Actions builds and tests on `windows-2025`, checks archive installation in PowerShell 7 and Windows PowerShell 5.1, and publishes self-contained ZIP/checksum assets for `v*` tags after checks pass.

The COM SDK is currently `1.30.100-preview`, tested on WinGet `1.30.110-preview` / App Installer `1.30.109.0`. Other engine versions have not been live-tested here. Activation transport compatibility matters; see [API notes](docs/winget-api-notes.md) and [Microsoft issue #6560](https://github.com/microsoft/winget-cli/issues/6560). Wingood reports incompatible activation without replacing your engine.

The [implementation validation](docs/wingood-validation.md) records the current checks. The original prototype's nine successful app updates and baseline measurements remain in [historical validation](docs/validation.md) and [live-test notes](docs/live-test-notes.md). They demonstrate working concurrent submissions, not a controlled speedup percentage.

MIT license. Personal use is the design target.
