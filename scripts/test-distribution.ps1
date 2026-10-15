[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ArchivePath,
    [Parameter(Mandatory = $true)]
    [string] $ChecksumPath
)

$ErrorActionPreference = 'Stop'
$installer = Join-Path (Split-Path -Parent $PSScriptRoot) 'install.ps1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('wingood-distribution-test-' + [Guid]::NewGuid().ToString('N'))
$installDirectory = Join-Path $testRoot 'Programs\wingood'
$badChecksum = Join-Path $testRoot 'bad.sha256'

function Assert-True([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}

function Get-DirectoryFingerprint([string] $Directory) {
    $parts = @(
        Get-ChildItem -LiteralPath $Directory -File -Recurse |
            Sort-Object -Property FullName |
            ForEach-Object {
                $relative = $_.FullName.Substring($Directory.Length).TrimStart([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
                $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
                "$relative=$hash"
            }
    )
    return [string]::Join("`n", $parts)
}

try {
    New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
    $archive = (Resolve-Path -LiteralPath $ArchivePath).Path
    $checksum = (Resolve-Path -LiteralPath $ChecksumPath).Path
    $processPathBefore = [Environment]::GetEnvironmentVariable('Path', 'Process')
    $userPathBefore = [Environment]::GetEnvironmentVariable('Path', 'User')

    & $installer -ArchivePath $archive -ChecksumPath $checksum -InstallDirectory $installDirectory -NoPath
    $firstExeHash = (Get-FileHash -LiteralPath (Join-Path $installDirectory 'wingood.exe') -Algorithm SHA256).Hash

    foreach ($name in @('wingood.exe', 'wg.exe')) {
        Assert-True (Test-Path -LiteralPath (Join-Path $installDirectory $name) -PathType Leaf) "Installed entry point '$name' is missing."
    }
    Assert-True ((Get-FileHash -LiteralPath (Join-Path $installDirectory 'wingood.exe') -Algorithm SHA256).Hash -eq
        (Get-FileHash -LiteralPath (Join-Path $installDirectory 'wg.exe') -Algorithm SHA256).Hash) 'The two apphosts differ.'

    foreach ($name in @('hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll', 'System.Private.CoreLib.dll', 'Microsoft.Management.Deployment.dll')) {
        Assert-True (Test-Path -LiteralPath (Join-Path $installDirectory $name) -PathType Leaf) "Bundled runtime or native bridge file '$name' is missing."
    }
    $runtime = Get-Content -LiteralPath (Join-Path $installDirectory 'wingood.runtimeconfig.json') -Raw | ConvertFrom-Json
    $hasBundledRuntime = [bool]($runtime.runtimeOptions.includedFrameworks | Where-Object { $_.name -eq 'Microsoft.NETCore.App' })
    Assert-True $hasBundledRuntime 'Runtime config does not declare a bundled .NET runtime.'

    foreach ($name in @('wingood.exe', 'wg.exe')) {
        $output = & (Join-Path $installDirectory $name) --help 2>&1
        if ($LASTEXITCODE -ne 0) { throw "'$name --help' exited with code $LASTEXITCODE. Output: $($output -join "`n")" }
    }

    & $installer -ArchivePath $archive -ChecksumPath $checksum -InstallDirectory $installDirectory -NoPath
    Assert-True ((Get-FileHash -LiteralPath (Join-Path $installDirectory 'wingood.exe') -Algorithm SHA256).Hash -eq $firstExeHash) 'Reinstall changed the installed executable unexpectedly.'

    $lockedFingerprint = Get-DirectoryFingerprint $installDirectory
    $lockedStream = $null
    $lockedUpdateFailed = $false
    try {
        $lockedStream = [IO.File]::Open((Join-Path $installDirectory 'wingood.exe'), [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
        try {
            & $installer -ArchivePath $archive -ChecksumPath $checksum -InstallDirectory $installDirectory -NoPath
        } catch {
            $lockedUpdateFailed = $true
        }
    } finally {
        if ($lockedStream) { $lockedStream.Dispose() }
    }
    Assert-True $lockedUpdateFailed 'The installer updated a directory while one of its files denied delete sharing.'
    Assert-True ((Get-DirectoryFingerprint $installDirectory) -ceq $lockedFingerprint) 'A locked-file update failure changed the existing installation.'

    Set-Content -LiteralPath $badChecksum -Value ('0' * 64) -Encoding ASCII
    $installFingerprint = Get-DirectoryFingerprint $installDirectory
    $failedAsExpected = $false
    try {
        & $installer -ArchivePath $archive -ChecksumPath $badChecksum -InstallDirectory $installDirectory -NoPath
    } catch {
        $failedAsExpected = $true
    }
    Assert-True $failedAsExpected 'The installer accepted an invalid checksum.'
    Assert-True ((Get-DirectoryFingerprint $installDirectory) -ceq $installFingerprint) 'A checksum failure changed the existing installation.'
    Assert-True ([Environment]::GetEnvironmentVariable('Path', 'Process') -ceq $processPathBefore) '-NoPath changed the process PATH.'
    Assert-True ([Environment]::GetEnvironmentVariable('Path', 'User') -ceq $userPathBefore) '-NoPath changed the user PATH.'

    Write-Host 'Distribution installation checks passed: repeat install, locked-file rollback, checksum failure preservation, aliases, native bridge, and bundled runtime.'
} finally {
    try {
        $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
        $actualRoot = [IO.Path]::GetFullPath($testRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
        $namePattern = '^wingood-distribution-test-[0-9a-f]{32}$'
        if (-not [string]::Equals((Split-Path -Parent $actualRoot), $tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
            (Split-Path -Leaf $actualRoot) -notmatch $namePattern) {
            throw "Refusing to remove unvalidated test directory '$actualRoot'."
        }
        $attributes = (Get-Item -LiteralPath $actualRoot -Force).Attributes
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing to recursively remove test reparse point '$actualRoot'."
        }
        Remove-Item -LiteralPath $actualRoot -Recurse -Force -ErrorAction SilentlyContinue
    } catch {
        Write-Warning "Could not safely remove distribution test directory: $($_.Exception.Message)"
    }
}
