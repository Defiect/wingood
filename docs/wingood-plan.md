# wingood CLI plan

Status: approved by Sean on October 14, 2026, including the routine implementation defaults below. Migration, implementation, and publication have not begun.

## Confirmed requirements

- Name the tool and repository `wingood`.
- Design for Sean's personal use, with possible limited use by others.
- Provide a simple Windows CLI for updating apps.
- Support a hands-off bulk-update command. Requiring manual selection of every package is unacceptable.
- Preserve concurrent downloads while WinGet handles installations.
- Provide a high-quality terminal experience with terminal-aware layout, colors, and plain language.
- Keep reports in the terminal. Browser, HTML, and automatically opened graphical reports are outside the first-release scope.
- Begin execution by migrating the existing repository to `D:\Programming\wingood` and establishing GitHub syncing.
- Make `Defiect/wingood` public.
- License the project under MIT.
- Provide a single-command PowerShell installer that downloads a prebuilt GitHub release. Users must not need Git or a compiler. Build releases in GitHub Actions on Windows.

## Agreed command behavior

| Invocation | Agreed behavior |
| --- | --- |
| `wingood upgrade` | Discover and display available updates with enhanced terminal formatting, then ask whether to update the whole selection. Prompt is yes/no with yes as the default. |
| `wingood update`, `wingood up` | Aliases for `wingood upgrade`. |
| `wingood upgrade --all` | Proceed with the bulk update without the wingood confirmation prompt. |
| `wingood upgrade -y`, `wingood upgrade --yes` | Also imply proceeding with the full upgrade without the wingood confirmation prompt. |
| `wingood upgrade <app>` | Display the explicitly selected app(s) and proceed without an additional wingood confirmation. |
| `wg` | Short entry point that automatically updates all eligible apps, as hands-off as possible. |
| `wingood` | Same as `wingood upgrade`: display available updates and prompt `[Y/n]`. |
| `wg <command>` | Behave like the corresponding `wingood <command>`; only bare `wg` has the automatic-upgrade default. |
| `wingood show <app>` | Display rich package information comparable to `winget show <app>`, using wingood's own terminal formatting. |
| `wingood show` | Display available updates without offering to install them. |
| `--dry-run` | Inspect the selection without downloading or installing. |
| `--exclude <app>` | Exclude an app from this run; use existing WinGet pins for permanent exclusions. |
| `wingood retry` | Retry the failed apps from the last attempted batch, after refreshing their update availability. Proposed default: display the retry selection and proceed, as with explicitly selected apps. |

Bare `wingood`, named commands through `wg`, explicit-app behavior, and no-argument `show` follow the table above. Accept exact IDs and names identifying one app; ambiguous matches produce a list of candidates and require a more specific command. Proposed matching order: exact ID, exact name, then unique case-insensitive name/ID substring. Resolve explicit selections and exclusions before submitting any update.

Proceeding with an update means accepting the needed package and source agreements. This applies to a yes answer, an explicit app selection, and automatic forms (`--all`, `-y`, `--yes`, bare `wg`). After that decision, wingood should require no further user input wherever possible. Windows administrator approval and vendor GUI installer prompts can still occur; automating vendor GUI interactions is outside scope.

Without interactive input, an invocation that normally asks for confirmation must display its selection and explain that `--yes` or bare `wg` is required. EOF is not a yes answer. An interactive Enter on the `[Y/n]` prompt is yes. Read-only and dry-run commands do not grant permission to upgrade.

## Agreed batch and display behavior

- Wingood must not proactively close apps. Sean accepts that a vendor installer may independently close or restart apps. Defer a generic opt-in close-apps flag because it is not straightforward to implement consistently.
- Continue with the other updates after an individual failure, and clearly report failures at the end.
- If one source fails, continue with sources whose selections were successfully checked, clearly report the failed source, and return a partial-failure result. Skip uncertain work rather than guessing at app identity or bulk eligibility.
- Submit the full selected batch immediately by default so later downloads can begin as early as WinGet permits. WinGet controls the active download/installation worker counts. Retain an optional advanced outstanding-operation limit for users who want to reduce lookahead.
- Provide a command to retry failed updates without manually reconstructing an ID list.
- Show a live table with downloading, waiting, installing, done, and failed states, followed by a short summary.
- First-release scope is on-demand updating and supporting inspection/retry commands, including package details through `show`.
- Include Microsoft Store updates alongside the normal WinGet catalog wherever supported, with flags to restrict the source. Do not imply that WinGet discovers every update the Store app itself can find.
- Scheduled updates, general installation/uninstallation features, and self-update functionality are not first-release requirements currently.

## Package details presentation

Sean's reference command is `winget show JanDeDobbeleer.OhMyPosh`. It prints name/ID, version, publisher/author, description, publisher/support/homepage links, license and license URL, release-notes URL, tags, and installer details including type, locale, URL, SHA256, release date, and offline distribution support.

The desired information is comparable, but the raw WinGet formatting is explicitly disliked. Use wingood's own terminal-aware formatting. Prefer structured fields from the public API where available; direct `winget show` passthrough would not meet the display requirement. Wrapping the command is an acceptable fallback only if the output can be reformatted reliably.

The pinned SDK's actual projection was inspected: it exposes most package metadata plus basic installer type/architecture/scope/locale, but omits installer URL, SHA256, release date, dependencies, and other fields printed by the CLI. Consequently, the practical first-release direction is to capture `winget show` output and format it in wingood, matching Sean's explicitly accepted wrapper approach. Preserve unfamiliar read-only fields rather than silently losing information. Do not rely on `--output-locale`: it exists in the separately inspected newer source tree but is not supported by this installed CLI.

Design direction: visually emphasize name/version/description, group links and installer details, align labels, wrap tags compactly rather than using a line per tag, and adapt wrapping to terminal width. Sean explicitly welcomes generous use of color for legibility: use distinct visual treatment for headings, current/available versions, links, metadata, and update states. Combine color with spacing and grouping. Preserve useful information and provide readable plain output when terminal features are unavailable.

## Proposed implementation defaults

- Keep the working C# update core and COM integration. Sean approved Spectre.Console for styled output, progress, tables, prompts, and terminal capability handling, plus Spectre.Console.Cli for commands, aliases, flags, and help. The current prototype has no terminal framework and uses plain Console output.
- Preserve metadata as text when rendering it; escape library markup/control sequences from package data. Width-aware wrapping must preserve useful values and hyperlinks where supported. Redirected output uses stable plain lines, with no cursor rewriting. Honor standard color controls and expose an explicit override.
- Keep normal user context for the main process. Let WinGet/vendor installers request administrator approval when necessary. Do not elevate the entire updater by default, because some per-user updates fail in that context. Do not grant automatic reboot permission.
- Use WinGet's combined installed-app correlation and selected source/version. Bulk selection must honor pins, unknown-version exclusions, and updates requiring explicit targeting. A named app may bypass the bulk-only explicit-selection exclusion, but does not silently override user pins.
- Treat parsing WinGet's human-readable output as a narrow compatibility layer. An unrecognized or truncated eligibility table must not become permission to upgrade a guessed selection. Tests must cover output containing only excluded apps, secondary explicit-only tables, unknown versions, and app identities appearing in multiple sources. The printed total is not an eligibility count.
- Preserve exact user order for explicit selections; use the native combined listing's order for a bulk selection. Apply no speculative size-based sorting. Submit all selected requests by default; actual start/completion order remains controlled by WinGet.
- Store recent plan/result/progress records under a user-owned application-data directory, independent of the repository/current working directory. Proposed retention: the latest 30 runs. Preserve failed IDs and source identities for retry, refresh their versions before retrying, and omit apps that are already current. No HTML/SVG report generation in normal product runs.
- Keep exit codes consistent: success/no eligible updates is 0, failed updates or incomplete source processing is 1, invalid/ambiguous requests or missing required confirmation is 2, cancellation is 130. Ctrl+C requests cancellation of queued/download work, waits for already-running operations to settle, and records their actual outcomes.

## Prebuilt releases and installer

Confirmed delivery: a single command in PowerShell downloads and runs a public installer script. Proposed canonical command after publication:

```powershell
irm https://raw.githubusercontent.com/Defiect/wingood/main/install.ps1 | iex
```

The URL is a plan, not a published installer yet.

Proposed release defaults:

- Target Windows 11 x64 first, matching the existing project's platform baseline. Require a working supported WinGet/App Installer installation; report an unavailable/incompatible engine clearly rather than changing it automatically.
- Publish an untrimmed, self-contained `win-x64` build so users do not need to install .NET. Distribute the full publish folder as a versioned ZIP; retain the native WinGet bridge and all required sidecar files. Single-command installation does not require single-file executable packaging.
- The installer downloads the latest non-prerelease GitHub release and its checksum, verifies the archive, extracts into a temporary staging directory, and installs into `%LOCALAPPDATA%\Programs\wingood`. Supply both `wingood` and `wg`, preserving their agreed default behavior.
- Add the installation directory to the current process and user PATH idempotently, preserving existing entries. The installer should work from Windows PowerShell 5.1 and current PowerShell. Installing the tool itself requires no administrator rights.
- Rerunning the installer updates wingood. Stage and validate the new payload before replacing the installed version; retain the previous copy until installation succeeds, and report locked files without corrupting the existing installation.
- GitHub Actions runs locked restore, Release build, tests, and publish on `windows-2025`, with `actions/setup-dotnet` reading `global.json`. Build/test normal pushes and PRs; publish versioned ZIP/checksum assets to GitHub Releases from `v*` version tags after checks pass. Proposed first release: `v0.1.0`.
- Validate installation from the prebuilt ZIP without invoking Git or a compiler. Verify from the runtime configuration and an execution smoke test that the app uses its bundled runtime. Check both entry points, PATH setup, rerun/update behavior, and a failed-download/checksum case preserving the existing install.
- Keep raw machine logs, installer downloads, and local test artifacts out of Git. Commit source, lockfiles, workflows, installer script, MIT license, documentation, and selected illustrative fixtures.

## Execution step 0: migration and GitHub

Source: `D:\Programming\winget-unshittified`. Destination: `D:\Programming\wingood`.

Verified on 11 October 2026: the destination exists and is empty; the source has an initialized Git repository with no commits and no remote. `gh repo view Defiect/wingood` did not find an accessible repository with that name.

At execution, verify both resolved paths and the destination contents, preserve source files and local experiment evidence during migration, rename the executable/projects and relevant current documentation, validate the renamed build, establish an initial commit on `main` and public `Defiect/wingood` GitHub remote, and push. Commit and push at completed, tested milestones. Keep machine-specific raw logs and generated installers out of the public repository. Historical measurements must retain their original meaning after the rename.

The ignored `artifacts/` tree contains about 30.5 MB, including the original baseline, live runs, diagnostic logs, analysis scripts, and known-good published executable. Preserve it locally through migration; regenerate the renamed build before retiring the old published files.

## Remaining execution sequence

1. **Command surface and terminal UI:** implement the agreed aliases/defaults, confirmation rules, app matching, source filters, exclusions, live progress, terminal summary, and reformatted `show`. Use `--source winget` or `--source store`, accepting `msstore` as the native-name alias. The filter applies consistently to preview, execution, exclusions, and details.
2. **Automatic selection and scheduling:** integrate validated native bulk eligibility with COM discovery, use combined source correlation, preserve version/source binding, support partial-source failures, and submit the whole batch with optional bounded lookahead.
3. **Local history and retry:** record results outside the repo, support retrying failed apps, preserve cancellation/draining and meaningful errors, and remove automatic graphical-report output from the user-facing command flow.
4. **Distribution:** add self-contained publishing, Windows GitHub Actions checks/releases, the download-and-install PowerShell script, both executable names, and public usage documentation.
5. **Verification and first release:** complete the acceptance checks below, inspect the assembled release archive, test installation of that archive, then create the version tag/release and verify the public installer URL and assets. Reaching this step does not justify publishing a failed build.

## Acceptance checks

- Table-driven command tests establish the differences between bare `wingood`, bare `wg`, named subcommands, explicit apps, confirmation flags, dry runs, EOF, and an interactive default-yes answer. A dry run never submits operations.
- Selection fixtures verify pins/explicit-only exclusions, secondary/empty tables, malformed/truncated data, ambiguous names/copies, source filtering, and duplicate apps across catalogs. A bad explicit selection/exclusion fails before submitting its batch.
- Controlled backend tests verify whole-batch submission, an optional cap, continued work after a failure, cancellation/draining, source-specific partial failures, and retry staying within recorded failed apps.
- Inspect actual colored output in a wide and narrow terminal plus redirected/plain output. Verify long names, descriptions, tags, URLs, errors, and literal markup characters remain legible.
- Run read-only live `show`, `upgrade --dry-run`, and source-filtered discovery against this PC. Record which engine versions and Store paths were actually checked. Existing nine successful app updates remain evidence for the core execution path; do not claim new paths were live-tested without doing so.
- Verify both release entry points from an installed prebuilt archive; the runtime must be bundled and native activation files must match the published build. Verify installer repeatability and failure preservation.
- Review the public file set, run the required build/test/format checks, and ensure GitHub checks pass before tagging the first release.

Sean approved this complete plan on October 14, 2026: "Skimmed the plan, it looks fine to me." No further approval or interview round is required. Execution starts with step 0 above after the requested context-compaction checkpoint.

## Current technical evidence

The existing C# prototype completed nine real app updates across two batches and a retry. It has 15 passing tests recorded in [validation](validation.md). The first version accepts exact IDs, submits at most four whole updates at once, and lets the installed WinGet engine run up to three download workers and one installation worker. These are existing implementation facts, not decisions about the final CLI interface.

The proposed bulk-selection approach is to read the main table from a read-only `winget upgrade` invocation, match its IDs to the programming interface's eligible candidates, and use the existing concurrent updater. WinGet prints apps requiring explicit selection in a separate table. A parser must distinguish these tables and handle unknown output without guessing. See [bulk update research](bulk-update-research.md). Implementation has not begun.

Store discovery was verified read-only on this PC: `winget upgrade --source msstore --disable-interactivity` found Docker Desktop 4.84.0 to 4.94.0. Separate winget-only discovery also finds that installed app. A combined native query lists it once, using the Store entry. Use the combined catalog's installed-app correlation and bind the selected source/version; do not concatenate two independent update lists and submit both entries. WinGet can only discover Store updates for which its catalog provides enough version information, so coverage does not equal the Microsoft Store app's complete update scan. See [Store research](wingood-store-research.md).

`wg` currently has no command/alias collision in the checked PowerShell session. The existing publish-folder deployment is already validated and is a practical first-release installation method. Starting wingood in the normal user context allows WinGet/vendor installers to request elevation as needed; elevating the entire helper can prevent some per-user updates, as observed with Doppler. Exact prompt behavior varies with installer type and metadata.

Prebuilt release feasibility was checked against [setup-dotnet](https://github.com/actions/setup-dotnet), the [Windows 2025 runner image](https://github.com/actions/runner-images/blob/main/images/windows/Windows2025-Readme.md), and [.NET deployment documentation](https://learn.microsoft.com/en-us/dotnet/core/deploying/). Test COM activation/catalog discovery in the actual execution context; a normal-context success does not verify elevated compatibility. The observed [SDK/engine transport mismatch](https://github.com/microsoft/winget-cli/issues/6560) cannot be predicted reliably from numeric version ordering alone.

The COM options do not offer a generic close-running-apps switch or a reliable mapping from every package ID to its running processes. Vendor installers can close apps independently: [Inno Setup's CloseApplications setting](https://jrsoftware.org/ishelp/topic_setup_closeapplications.htm) defaults to yes and can close/restart affected apps in silent mode; its `/NOCLOSEAPPLICATIONS` override is specific to that installer family. Sean accepted that wingood's no-close behavior covers wingood's own actions, while vendor behavior remains outside its guarantee. The generic opt-in flag is deferred.

The shared glossary is [CONTEXT.md](../CONTEXT.md). Add an ADR only for a settled decision with a substantial tradeoff and meaningful cost to reverse; ordinary CLI options belong in this plan.
