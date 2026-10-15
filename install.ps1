[CmdletBinding()]
param(
    [string] $ArchivePath,
    [string] $ChecksumPath,
    [string] $InstallDirectory,
    [switch] $NoPath
)

& {
    $ErrorActionPreference = 'Stop'

    function Get-CanonicalPath([string] $Path) {
        $fullPath = [IO.Path]::GetFullPath($Path)
        $root = [IO.Path]::GetPathRoot($fullPath)
        if ([string]::Equals($fullPath, $root, [StringComparison]::OrdinalIgnoreCase)) { return $fullPath }
        return $fullPath.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    }

    function Test-PathEntry([string] $PathValue, [string] $Candidate) {
        if ([string]::IsNullOrWhiteSpace($PathValue)) { return $false }
        foreach ($entry in $PathValue.Split(';')) {
            $trimmed = [Environment]::ExpandEnvironmentVariables($entry.Trim().Trim('"'))
            if ($trimmed) {
                try {
                    if ([string]::Equals((Get-CanonicalPath $trimmed), $Candidate, [StringComparison]::OrdinalIgnoreCase)) {
                        return $true
                    }
                } catch {
                    # Keep malformed existing entries intact and continue checking the rest.
                }
            }
        }
        return $false
    }

    function Add-UserPath([string] $Directory) {
        $processPath = [Environment]::GetEnvironmentVariable('Path', 'Process')
        if (-not (Test-PathEntry $processPath $Directory)) {
            $updatedProcessPath = if ([string]::IsNullOrWhiteSpace($processPath)) { $Directory } else { $processPath.TrimEnd(';') + ';' + $Directory }
            [Environment]::SetEnvironmentVariable('Path', $updatedProcessPath, 'Process')
        }

        $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
        if (-not (Test-PathEntry $userPath $Directory)) {
            $updatedUserPath = if ([string]::IsNullOrWhiteSpace($userPath)) { $Directory } else { $userPath.TrimEnd(';') + ';' + $Directory }
            [Environment]::SetEnvironmentVariable('Path', $updatedUserPath, 'User')
        }
    }

    function Get-LatestReleaseAssets {
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        $headers = @{ Accept = 'application/vnd.github+json'; 'User-Agent' = 'wingood-installer' }
        $release = Invoke-RestMethod -Uri 'https://api.github.com/repos/Defiect/wingood/releases/latest' -Headers $headers
        if ($release.prerelease -or -not $release.tag_name) {
            throw 'GitHub did not return a latest stable wingood release.'
        }
        if ($release.tag_name -notmatch '^v[0-9A-Za-z][0-9A-Za-z.+-]*$') {
            throw "Release tag '$($release.tag_name)' is not a supported version tag."
        }

        $archiveName = 'wingood-{0}-win-x64.zip' -f $release.tag_name
        $checksumName = $archiveName + '.sha256'
        $archiveAsset = @($release.assets | Where-Object { $_.name -ceq $archiveName })
        $checksumAsset = @($release.assets | Where-Object { $_.name -ceq $checksumName })
        if ($archiveAsset.Count -ne 1 -or $checksumAsset.Count -ne 1) {
            throw "Release $($release.tag_name) is missing its unique archive or SHA256 asset."
        }
        return @($archiveAsset[0].browser_download_url, $checksumAsset[0].browser_download_url, $archiveName)
    }

    function Assert-Payload([string] $Directory) {
        $required = @(
            'wingood.exe', 'wg.exe', 'wingood.dll', 'wingood.runtimeconfig.json',
            'Microsoft.Management.Deployment.dll', 'hostfxr.dll', 'hostpolicy.dll',
            'coreclr.dll', 'System.Private.CoreLib.dll'
        )
        foreach ($name in $required) {
            if (-not (Test-Path -LiteralPath (Join-Path $Directory $name) -PathType Leaf)) {
                throw "The archive is missing required self-contained release file '$name'."
            }
        }

        $appHash = (Get-FileHash -LiteralPath (Join-Path $Directory 'wingood.exe') -Algorithm SHA256).Hash
        $aliasHash = (Get-FileHash -LiteralPath (Join-Path $Directory 'wg.exe') -Algorithm SHA256).Hash
        if ($appHash -ne $aliasHash) { throw 'wg.exe is not an identical copy of the wingood.exe apphost.' }

        $runtime = Get-Content -LiteralPath (Join-Path $Directory 'wingood.runtimeconfig.json') -Raw | ConvertFrom-Json
        if (-not $runtime.runtimeOptions.includedFrameworks -or
            -not ($runtime.runtimeOptions.includedFrameworks | Where-Object { $_.name -eq 'Microsoft.NETCore.App' })) {
            throw 'The runtime configuration does not declare the bundled Microsoft.NETCore.App runtime.'
        }
    }

    function Get-ValidatedGeneratedDirectory([string] $Directory, [string] $ExpectedParent, [string] $NamePattern) {
        $fullPath = Get-CanonicalPath $Directory
        $actualParent = Get-CanonicalPath (Split-Path -Parent $fullPath)
        if (-not [string]::Equals($actualParent, (Get-CanonicalPath $ExpectedParent), [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove generated directory outside its expected parent: '$fullPath'."
        }
        if ((Split-Path -Leaf $fullPath) -notmatch $NamePattern) {
            throw "Refusing to remove directory without the expected generated name: '$fullPath'."
        }
        if (-not (Test-Path -LiteralPath $fullPath -PathType Container)) {
            throw "Generated directory no longer exists: '$fullPath'."
        }
        $item = Get-Item -LiteralPath $fullPath -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing to recursively remove reparse point '$fullPath'."
        }
        return $fullPath
    }

    function Assert-Checksum([string] $Archive, [string] $ChecksumFile) {
        $expectedText = (Get-Content -LiteralPath $ChecksumFile -Raw).Trim()
        if ($expectedText -notmatch '^([0-9a-fA-F]{64})(?:\s+\*?(.+?))?\s*$') {
            throw 'The SHA256 asset must contain a 64-character hexadecimal digest, optionally followed by a filename.'
        }
        $expected = $Matches[1].ToUpperInvariant()
        $namedFile = $Matches[2]
        if ($namedFile -and [IO.Path]::GetFileName($namedFile.Trim()) -cne [IO.Path]::GetFileName($Archive)) {
            throw 'The SHA256 asset names a different archive.'
        }
        $actual = (Get-FileHash -LiteralPath $Archive -Algorithm SHA256).Hash.ToUpperInvariant()
        if ($actual -cne $expected) { throw 'Archive SHA256 verification failed; the installed copy was left unchanged.' }
    }

    if (($ArchivePath -and -not $ChecksumPath) -or ($ChecksumPath -and -not $ArchivePath)) {
        throw 'ArchivePath and ChecksumPath must be supplied together.'
    }

    if (-not $InstallDirectory) {
        if (-not $env:LOCALAPPDATA) { throw 'LOCALAPPDATA is unavailable; pass InstallDirectory explicitly.' }
        $InstallDirectory = Join-Path $env:LOCALAPPDATA 'Programs\wingood'
    }
    $InstallDirectory = Get-CanonicalPath $InstallDirectory
    $installLeaf = Split-Path -Leaf $InstallDirectory
    $installParent = Split-Path -Parent $InstallDirectory
    if (-not $installParent -or $InstallDirectory -eq [IO.Path]::GetPathRoot($InstallDirectory)) {
        throw 'InstallDirectory must be a child directory, not a drive root.'
    }
    if (Test-Path -LiteralPath $InstallDirectory -PathType Leaf) { throw 'InstallDirectory exists as a file.' }
    if (Test-Path -LiteralPath $InstallDirectory -PathType Container) {
        $existingAttributes = (Get-Item -LiteralPath $InstallDirectory -Force).Attributes
        if (($existingAttributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'InstallDirectory cannot be a symbolic link or reparse point.'
        }
    }
    if (-not (Test-Path -LiteralPath $installParent -PathType Container)) {
        New-Item -ItemType Directory -Path $installParent -Force | Out-Null
    }

    $workRoot = Join-Path ([IO.Path]::GetTempPath()) ('wingood-install-' + [Guid]::NewGuid().ToString('N'))
    $payloadDirectory = Join-Path $workRoot 'payload'
    $stageDirectory = Join-Path $installParent ('.' + $installLeaf + '.stage-' + [Guid]::NewGuid().ToString('N'))
    $backupDirectory = Join-Path $installParent ('.' + $installLeaf + '.backup-' + [Guid]::NewGuid().ToString('N'))
    $oldMoved = $false
    $stageMoved = $false

    try {
        New-Item -ItemType Directory -Path $workRoot, $payloadDirectory, $stageDirectory -Force | Out-Null

        if (-not $ArchivePath) {
            $assets = Get-LatestReleaseAssets
            $ArchivePath = Join-Path $workRoot $assets[2]
            $ChecksumPath = $ArchivePath + '.sha256'
            Invoke-WebRequest -Uri $assets[0] -OutFile $ArchivePath -UseBasicParsing
            Invoke-WebRequest -Uri $assets[1] -OutFile $ChecksumPath -UseBasicParsing
        } else {
            $ArchivePath = (Resolve-Path -LiteralPath $ArchivePath).Path
            $ChecksumPath = (Resolve-Path -LiteralPath $ChecksumPath).Path
        }

        Assert-Checksum $ArchivePath $ChecksumPath
        Expand-Archive -LiteralPath $ArchivePath -DestinationPath $payloadDirectory -Force
        Assert-Payload $payloadDirectory
        Copy-Item -Path (Join-Path $payloadDirectory '*') -Destination $stageDirectory -Recurse -Force
        Assert-Payload $stageDirectory

        if (Test-Path -LiteralPath $InstallDirectory -PathType Container) {
            try {
                [IO.Directory]::Move($InstallDirectory, $backupDirectory)
                $oldMoved = $true
            } catch {
                throw "Could not stage the existing installation for update. Close any running wingood process and retry; the current copy was left unchanged. Details: $($_.Exception.Message)"
            }
        }
        try {
            [IO.Directory]::Move($stageDirectory, $InstallDirectory)
            $stageMoved = $true
        } catch {
            if ($oldMoved -and -not (Test-Path -LiteralPath $InstallDirectory)) {
                try {
                    [IO.Directory]::Move($backupDirectory, $InstallDirectory)
                    $oldMoved = $false
                } catch {
                    throw "Installation failed and the previous copy could not be restored. It remains at '$backupDirectory'. Details: $($_.Exception.Message)"
                }
            }
            throw "Could not replace '$InstallDirectory'. Close any running wingood process and retry. The previous copy was preserved. Details: $($_.Exception.Message)"
        }

        if (-not $NoPath) {
            try {
                Add-UserPath $InstallDirectory
            } catch {
                Write-Warning "Installed wingood, but could not update user PATH: $($_.Exception.Message)"
            }
        }

        if ($oldMoved) {
            try {
                $backupPattern = '^\.' + [Regex]::Escape($installLeaf) + '\.backup-[0-9a-f]{32}$'
                $validatedBackup = Get-ValidatedGeneratedDirectory $backupDirectory $installParent $backupPattern
                Remove-Item -LiteralPath $validatedBackup -Recurse -Force
                $oldMoved = $false
            } catch {
                Write-Warning "Installed wingood, but the old copy could not be removed (a file may be locked). Backup: $backupDirectory"
            }
        }

        Write-Host "wingood installed to $InstallDirectory"
        if ($NoPath) { Write-Host 'PATH was left unchanged (-NoPath).' }
        elseif (-not (Test-PathEntry ([Environment]::GetEnvironmentVariable('Path', 'Process')) $InstallDirectory)) {
            Write-Host 'Open a new terminal to use wingood from PATH.'
        }
    } finally {
        if (Test-Path -LiteralPath $stageDirectory -PathType Container) {
            try {
                $stagePattern = '^\.' + [Regex]::Escape($installLeaf) + '\.stage-[0-9a-f]{32}$'
                $validatedStage = Get-ValidatedGeneratedDirectory $stageDirectory $installParent $stagePattern
                Remove-Item -LiteralPath $validatedStage -Recurse -Force -ErrorAction SilentlyContinue
            } catch {
                Write-Warning "Could not safely remove temporary staging directory: $($_.Exception.Message)"
            }
        }
        if (Test-Path -LiteralPath $workRoot -PathType Container) {
            $tempRoot = Get-CanonicalPath ([IO.Path]::GetTempPath())
            try {
                $workPattern = '^wingood-install-[0-9a-f]{32}$'
                $validatedWorkRoot = Get-ValidatedGeneratedDirectory $workRoot $tempRoot $workPattern
                Remove-Item -LiteralPath $validatedWorkRoot -Recurse -Force -ErrorAction SilentlyContinue
            } catch {
                Write-Warning "Could not safely remove temporary download directory: $($_.Exception.Message)"
            }
        }
    }

}
