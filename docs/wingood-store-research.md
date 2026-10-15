# Microsoft Store update research

## Live behavior on this PC

On 2026-10-11, the installed WinGet CLI reported `v1.30.110-preview`. Running the read-only listing command

```text
winget upgrade --source msstore --disable-interactivity
```

completed with exit code 0 and listed one update:

```text
Name           Id             Version Available
Docker Desktop XP8CBJ40XLBWKX 4.84.0  4.94.0
1 upgrades available.
```

This confirms that this installed CLI and this PC's configured Store source can discover at least one Store update. The command had no package query and no `--all`, so it only listed candidates; it did not request an upgrade. This result does not establish coverage for every Store app.

The separate winget-only listing had also shown the same Docker Desktop installation and version transition as `Docker.DockerDesktop` from `winget`. To check for duplicate output, the combined read-only command `winget upgrade --disable-interactivity` was run on the same CLI. It exited 0 and emitted exactly one Docker row, under the Store ID `XP8CBJ40XLBWKX`, with source `msstore` and `4.84.0` → `4.94.0`; no second `Docker.DockerDesktop` row appeared. This demonstrates that this installed CLI's combined listing coalesced these matching results into one candidate and selected the Store catalog for display. It does not prove the same source choice for other overlapping packages or WinGet/API versions.

## API and source limitations

The WinGet COM implementation computes `CatalogPackage.IsUpdateAvailable` from its latest applicable catalog version, and `DefaultInstallVersion` returns that selected version (or can fall back to an available version when there is no applicable default). It is catalog/version based, rather than a live Store update check. For Microsoft Store installer types, the normal upgrade execution path dispatches to the Store update handler, which asks Windows `AppInstallManager.SearchForUpdatesAsync` for the product. These are separate stages: source discovery can miss an update even though the Store execution handler supports updating it.

Unknown versions remain a coverage caveat. The current project discovery skips a locally installed package whose reported installed version is `Unknown`. WinGet version ordering also treats an unknown candidate version as lower than a known version, so it cannot prove that candidate is newer. `InstallOptions.AllowUpgradeToUnknownVersion` defaults to false; setting it only relaxes the execution check for an unknown selected manifest version, and does not make discovery find an update. WinGet maintainers reported in October 2025 that the Store REST source had version data for unpackaged Win32 apps but not packaged UWP apps; this is an upstream Store-source limitation and can change independently of the CLI/API client.

When both `winget` and `msstore` are searched, WinGet correlates installed records with available sources. Its version-selection code filters to the tracked source when that source is known and contains available packages. For installations without usable source tracking, the original source may be unknown, so correlation across sources cannot guarantee that the selected update comes from the installation's original source. Bind execution to the selected catalog/version identity and deduplicate each installed app; do not imply that source preservation is always knowable. A WinGet issue documents a cross-source ambiguity that caused an upgrade from a competing source.

Store installation/update is performed by the Windows Store service and can fail due to Store policy, account/entitlement, network, or package conditions. WinGet's Store entitlement check is best effort. Silent/no-toast operation is not a guarantee that all Store-side requirements are satisfied. Keep the operation on the upgrade path; `Force` controls forced app restart behavior in the Store handler and is not needed to turn an upgrade into a reinstall.

## Version correspondence

The live result above came from installed WinGet `1.30.110-preview`. Source-code observations above came from the separate local `microsoft/winget-cli` clone at commit `3d9083c`. The preview engine and source snapshot may differ; the live result is evidence for this PC and command only, while the code claims describe that snapshot.

## Sources

- [WinGet `upgrade` command documentation](https://learn.microsoft.com/en-us/windows/package-manager/winget/upgrade) — `--source` restricts the search to the named source; the no-argument command lists available upgrades.
- [WinGet source documentation](https://learn.microsoft.com/en-us/windows/package-manager/winget/source) — default `msstore` source and source-agreement behavior.
- [WinGet CLI issue #2374](https://github.com/microsoft/winget-cli/issues/2374) — Store version-data limitation; the latest maintainer update in October 2025 says unpackaged Win32 versions are available, packaged UWP versions are not.
- [WinGet CLI issue #4347](https://github.com/microsoft/winget-cli/issues/4347) — reported cross-source upgrade ambiguity.
- [CatalogPackage implementation at inspected commit](https://github.com/microsoft/winget-cli/blob/3d9083c062c1e7a7086e05becb414d4e005e4ab6/src/Microsoft.Management.Deployment/CatalogPackage.cpp) and [version-selection implementation](https://github.com/microsoft/winget-cli/blob/3d9083c062c1e7a7086e05becb414d4e005e4ab6/src/AppInstallerRepositoryCore/PackageVersionSelection.cpp).
- [Upgrade execution and Store dispatch at inspected commit](https://github.com/microsoft/winget-cli/blob/3d9083c062c1e7a7086e05becb414d4e005e4ab6/src/AppInstallerCLICore/Workflows/InstallFlow.cpp), [Store update handler](https://github.com/microsoft/winget-cli/blob/3d9083c062c1e7a7086e05becb414d4e005e4ab6/src/AppInstallerCLICore/Workflows/MSStoreInstallerHandler.cpp), and [COM upgrade version validation](https://github.com/microsoft/winget-cli/blob/3d9083c062c1e7a7086e05becb414d4e005e4ab6/src/Microsoft.Management.Deployment/PackageManager.cpp).
