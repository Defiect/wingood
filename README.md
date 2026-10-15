# wingood

A Windows app updater that downloads upcoming updates while WinGet installs. Wingood keeps WinGet's installer selection, verification, dependencies, and installation handling, and supplies the batch early so downloads can overlap installation.

The working prototype has moved here from `winget-unshittified`. The complete CLI and prebuilt installer are in development under the [approved plan](docs/wingood-plan.md). The prototype's nine successful app updates and original measurements are documented in [validation](docs/validation.md) and [live-test notes](docs/live-test-notes.md). Generated logs and installer downloads remain local.

## Development

Requires Windows 11 x64, WinGet/App Installer, and the .NET SDK specified in `global.json`.

```powershell
 dotnet restore --locked-mode
 dotnet build -c Release --no-restore
 dotnet test -c Release --no-build
 dotnet run --project src/Wingood -- list
```

Use a normal terminal. Let individual installers request administrator rights. The current COM SDK is `1.30.100-preview`, validated against this PC's preview engine. SDK activation transport compatibility matters; see [API notes](docs/winget-api-notes.md) and [Microsoft issue #6560](https://github.com/microsoft/winget-cli/issues/6560).

Wingood is licensed under MIT.
