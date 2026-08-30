[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$DeploymentRoot,
    [string]$RimWorldManagedDir,
    [string]$RimBridgeSdkPath,
    [string]$ChangedSince,
    [Alias('ChangedFiles', 'Path')]
    [string[]]$ChangedFile,
    [switch]$CompleteRuntime,
    [switch]$BootstrapRuntime,
    [switch]$DryRun,
    [switch]$Json
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Set-Location -LiteralPath $repoRoot
$planScript = Join-Path $repoRoot 'scripts\dev-plan.ps1'
$publishCompanionScript = Join-Path $repoRoot 'Publish-DevBridge.ps1'
$coordinatorProject = Join-Path $repoRoot 'Source\Coordinator\DevBridge.Coordinator.csproj'
$bridgeToolsProject = Join-Path $repoRoot 'Source\BridgeTools\DevBridge2.BridgeTools.csproj'
$modProject = Join-Path $repoRoot 'Source\Mod\DevBridge2.csproj'

if ([string]::IsNullOrWhiteSpace($DeploymentRoot)) {
    $DeploymentRoot = $repoRoot
}
$DeploymentRoot = [System.IO.Path]::GetFullPath($DeploymentRoot)

function Invoke-Required {
    param(
        [Parameter(Mandatory = $true)][string]$Command,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$Description
    )

    if (-not $Json) {
        Write-Host "`n== $Description =="
        & $Command @Arguments
    }
    else {
        $null = & $Command @Arguments 2>&1
    }
    if ($LASTEXITCODE -ne 0) {
        throw "$Command $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Get-FileSha256 {
    param([Parameter(Mandatory = $true)][string]$Path)

    try {
        if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
            return $null
        }
        return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
    }
    catch {
        throw ('DEVBRIDGE_ARTIFACT_IDENTITY_UNAVAILABLE: ' + $Path + ': ' + $_.Exception.Message)
    }
}

function Get-GitValue {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)
    $value = & git @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
    return ([string]$value).Trim()
}

function Get-AuthoritativeProductVersion {
    $propsPath = Join-Path $repoRoot 'Source\Directory.Build.props'
    $text = Get-Content -LiteralPath $propsPath -Raw
    $match = [regex]::Match($text,
        '<DevBridgeProductVersion>\s*([^<\s]+)\s*</DevBridgeProductVersion>',
        [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
    if (-not $match.Success -or $match.Groups[1].Value -notmatch '^\d+\.\d+\.\d+$') {
        throw 'Source/Directory.Build.props does not contain a valid authoritative product version.'
    }
    return $match.Groups[1].Value
}

function Get-BuildProperties {
    $revision = Get-GitValue @('rev-parse', 'HEAD')
    $status = @(git status --porcelain=v1 --untracked-files=all)
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not inspect source tree status.'
    }
    $dirty = $status.Count -gt 0
    $sourceRevision = $revision + $(if ($dirty) { '.dirty' } else { '' })
    return @(
        '-p:ContinuousIntegrationBuild=true'
        ('-p:SourceRevisionId=' + $sourceRevision)
        ('-p:DevBridgeBuildDirty=' + ($(if ($dirty) { 'true' } else { 'false' })))
    )
}

function Test-PathEqual {
    param([Parameter(Mandatory = $true)][string]$Left,
        [Parameter(Mandatory = $true)][string]$Right)
    return [string]::Equals(
        [System.IO.Path]::GetFullPath($Left).TrimEnd('\'),
        [System.IO.Path]::GetFullPath($Right).TrimEnd('\'),
        [StringComparison]::OrdinalIgnoreCase)
}

function Get-CanonicalRimWorldProcesses {
    param([Parameter(Mandatory = $true)][string]$RimWorldExecutable)

    $expected = [IO.Path]::GetFullPath($RimWorldExecutable)
    $all = @(Get-CimInstance Win32_Process -Filter "Name = 'RimWorldWin64.exe'" |
        ForEach-Object {
            $path = [string]$_.ExecutablePath
            if ([string]::IsNullOrWhiteSpace($path)) {
                throw 'DEVBRIDGE_RIMWORLD_PROCESS_AMBIGUOUS: a RimWorld process has no readable executable path.'
            }
            $process = Get-Process -Id ([int]$_.ProcessId) -ErrorAction Stop
            [pscustomobject]@{
                Pid = [int]$_.ProcessId
                StartIdentity = [long]$process.StartTime.ToUniversalTime().Ticks
                Path = [IO.Path]::GetFullPath($path)
            }
        })
    $foreign = @($all | Where-Object {
        -not [string]::Equals($_.Path, $expected, [StringComparison]::OrdinalIgnoreCase)
    })
    if ($foreign.Count -gt 0) {
        throw 'DEVBRIDGE_RIMWORLD_PROCESS_AMBIGUOUS: an unrelated RimWorld executable is running.'
    }
    return @($all)
}

function Invoke-DevBridgeJson {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    $wrapper = Join-Path $Root 'DevBridge.cmd'
    if (-not (Test-Path -LiteralPath $wrapper -PathType Leaf)) {
        throw 'DEVBRIDGE_RUNTIME_WRAPPER_MISSING: the installed DevBridge wrapper is unavailable.'
    }
    if ([string]::IsNullOrWhiteSpace($env:DEVBRIDGE_AGENT)) {
        $env:DEVBRIDGE_AGENT = 'devbridge-publish-' + $PID
    }
    $raw = @(& $wrapper @Arguments 2>&1 | ForEach-Object { [string]$_ })
    $exitCode = $LASTEXITCODE
    $text = $raw -join "`n"
    try {
        $json = $text | ConvertFrom-Json -Depth 20
    }
    catch {
        throw ('DEVBRIDGE_NO_STRUCTURED_RESPONSE: lifecycle command returned non-JSON output: ' + $text)
    }
    if ($exitCode -ne 0 -or $null -eq $json -or $json.success -eq $false) {
        $code = if ($null -ne $json.errorCode) { [string]$json.errorCode } else { 'DEVBRIDGE_LIFECYCLE_FAILED' }
        $message = if ($null -ne $json.error) { [string]$json.error } else { $text }
        throw ($code + ': ' + $message)
    }
    return $json
}

function Get-CurrentRimWorldProcess {
    param([Parameter(Mandatory = $true)][string]$RimWorldExecutable)

    $processes = @(Get-CanonicalRimWorldProcesses $RimWorldExecutable)
    if ($processes.Count -gt 1) {
        throw 'DEVBRIDGE_RIMWORLD_PROCESS_AMBIGUOUS: multiple canonical RimWorld processes are running.'
    }
    if ($processes.Count -eq 1) { return $processes[0] }
    return $null
}

function Assert-CompleteRuntimeTarget {
    if (-not ($CompleteRuntime -or $BootstrapRuntime)) { return }
    if (-not (Test-Path -LiteralPath $DeploymentRoot -PathType Container)) {
        throw 'DEVBRIDGE_RUNTIME_TARGET_MISSING: complete runtime target does not exist.'
    }
    $aboutPath = Join-Path $DeploymentRoot 'About\About.xml'
    if (-not (Test-Path -LiteralPath $aboutPath -PathType Leaf)) {
        throw 'DEVBRIDGE_RUNTIME_TARGET_INVALID: target is missing About/About.xml.'
    }
    try {
        [xml]$about = Get-Content -LiteralPath $aboutPath -Raw
        $packageId = [string]$about.ModMetaData.packageId
    }
    catch {
        throw 'DEVBRIDGE_RUNTIME_TARGET_INVALID: target About/About.xml is unreadable.'
    }
    if (-not [string]::Equals($packageId.Trim(), 'lan.devbridge2',
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'DEVBRIDGE_RUNTIME_TARGET_INVALID: target packageId is not lan.devbridge2.'
    }
    $modsRoot = [System.IO.Directory]::GetParent($DeploymentRoot)
    $rimWorldRoot = if ($null -eq $modsRoot) { $null } else {
        [System.IO.Directory]::GetParent($modsRoot.FullName)
    }
    if ($null -eq $modsRoot -or $null -eq $rimWorldRoot -or
        -not [string]::Equals($modsRoot.Name, 'Mods', [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(([System.IO.Path]::GetFileName($DeploymentRoot)), 'DevBridge2',
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'DEVBRIDGE_RUNTIME_TARGET_INVALID: target must be RimWorld\Mods\DevBridge2.'
    }
    $rimWorldExecutable = Join-Path $rimWorldRoot.FullName 'RimWorldWin64.exe'
    if (-not (Test-Path -LiteralPath $rimWorldExecutable -PathType Leaf)) {
        throw 'RIMWORLD_EXECUTABLE_MISSING: canonical RimWorld executable is absent.'
    }
    $script:CanonicalRimWorldExecutable = $rimWorldExecutable
    $script:CurrentRimWorldProcess = Get-CurrentRimWorldProcess $rimWorldExecutable
}

function Acquire-DeploymentLock {
    if (-not ($CompleteRuntime -or $BootstrapRuntime)) { return }
    $hashBytes = [Text.Encoding]::UTF8.GetBytes(
        ([System.IO.Path]::GetFullPath($DeploymentRoot)).ToLowerInvariant())
    $hash = ([Security.Cryptography.SHA256]::Create().ComputeHash($hashBytes) |
        ForEach-Object { $_.ToString('x2') }) -join ''
    $script:DeploymentMutex = [Threading.Mutex]::new(
        $false, 'Global\DevBridge2-RuntimeDeploy-' + $hash)
    if (-not $script:DeploymentMutex.WaitOne(0)) {
        $script:DeploymentMutex.Dispose()
        $script:DeploymentMutex = $null
        throw 'DEVBRIDGE_DEPLOYMENT_CONTENTION: another runtime deployment is active.'
    }
}


function Release-DeploymentLock {
    if ($null -ne $script:DeploymentMutex) {
        try { $script:DeploymentMutex.ReleaseMutex() } catch { }
        try { $script:DeploymentMutex.Dispose() } catch { }
        $script:DeploymentMutex = $null
    }
}

function Get-RuntimePackageHash {
    param([Parameter(Mandatory = $true)][object[]]$Artifacts)
    $lines = @($Artifacts | Sort-Object Destination | ForEach-Object {
        $relative = [System.IO.Path]::GetRelativePath(
            $DeploymentRoot, [string]$_.Destination).Replace('\', '/').ToLowerInvariant()
        $relative + "`0" + [string]$_.DestinationHash
    })
    $bytes = [Text.Encoding]::UTF8.GetBytes(($lines -join "`n"))
    return ([Security.Cryptography.SHA256]::Create().ComputeHash($bytes) |
        ForEach-Object { $_.ToString('x2') }) -join ''
}

function Write-RuntimeManifest {
    param([Parameter(Mandatory = $true)][object[]]$Artifacts,
        [Parameter(Mandatory = $true)][string]$PackageHash,
        [Parameter(Mandatory = $true)][string]$SourceCommit,
        [Parameter(Mandatory = $true)][bool]$SourceDirty)
    $manifestPath = Join-Path $DeploymentRoot '.devbridge-runtime-manifest.json'
    $manifest = [ordered]@{
        schemaVersion = 'devbridge-runtime-manifest/v1'
        ownerProduct = 'RimLiaison'
        componentRole = 'runtime'
        productionEligible = $false
        project = 'DevBridge2'
        packageId = 'lan.devbridge2'
        sourceRoot = $repoRoot
        sourceCommit = $SourceCommit
        sourceDirty = $SourceDirty
        productVersion = $productVersion
        packageSha256 = $PackageHash
        files = @($Artifacts | Sort-Object Destination | ForEach-Object {
            [ordered]@{
                path = [System.IO.Path]::GetRelativePath(
                    $DeploymentRoot, [string]$_.Destination).Replace('\', '/')
                sha256 = [string]$_.DestinationHash
                source = [string]$_.Source
            }
        })
    }
    $temporary = $manifestPath + '.' + $PID + '.tmp'
    [IO.File]::WriteAllText(
        $temporary,
        ($manifest | ConvertTo-Json -Depth 12),
        [Text.UTF8Encoding]::new($false))
    try {
        [IO.File]::Move($temporary, $manifestPath, $true)
    }
    finally {
        if (Test-Path -LiteralPath $temporary -PathType Leaf) {
            Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
        }
    }
    $manifestHash = Get-FileSha256 $manifestPath
    if ([string]::IsNullOrWhiteSpace($manifestHash)) {
        throw 'DEVBRIDGE_RUNTIME_MANIFEST_IDENTITY_UNPROVEN: runtime manifest could not be hashed.'
    }
    return [pscustomobject]@{
        Path = $manifestPath
        PackageHash = $PackageHash
        ManifestSha256 = $manifestHash
    }
}
function Get-RuntimeManifestPath {
    return (Join-Path $DeploymentRoot '.devbridge-runtime-manifest.json')
}

function Get-RuntimePendingPath {
    return (Join-Path $DeploymentRoot '.devbridge-runtime-manifest.pending')
}

function Write-RuntimePending {
    param(
        [Parameter(Mandatory = $true)][object[]]$Artifacts,
        [Parameter(Mandatory = $true)][string]$PackageHash,
        [Parameter(Mandatory = $true)][string]$Reason
    )

    $pendingPath = Get-RuntimePendingPath
    $pending = [ordered]@{
        schemaVersion = 'devbridge-runtime-pending/v1'
        operation = 'complete-runtime-reconciliation'
        reason = $Reason
        expectedPackageSha256 = $PackageHash
        files = @($Artifacts | Sort-Object Destination | ForEach-Object {
            [ordered]@{
                path = [IO.Path]::GetRelativePath(
                    $DeploymentRoot, [string]$_.Destination).Replace('\', '/')
                sha256 = [string]$_.DestinationHash
            }
        })
    }
    $temporary = $pendingPath + '.' + $PID + '.tmp'
    [IO.File]::WriteAllText(
        $temporary,
        ($pending | ConvertTo-Json -Depth 12),
        [Text.UTF8Encoding]::new($false))
    try {
        [IO.File]::Move($temporary, $pendingPath, $true)
    }
    finally {
        if (Test-Path -LiteralPath $temporary -PathType Leaf) {
            Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
        }
    }
    $script:RuntimePendingPath = $pendingPath
}

function Clear-RuntimePending {
    $pendingPath = Get-RuntimePendingPath
    if (Test-Path -LiteralPath $pendingPath -PathType Leaf) {
        Remove-Item -LiteralPath $pendingPath -Force
    }
    $script:RuntimePendingPath = $null
}

function Get-CompleteRuntimeHealth {
    param(
        [Parameter(Mandatory = $true)][object[]]$ExpectedArtifacts,
        [AllowNull()][object]$Manifest,
        [switch]$IgnorePending
    )

    $expected = @($ExpectedArtifacts | Sort-Object Destination)
    $missing = [System.Collections.Generic.List[string]]::new()
    $mismatched = [System.Collections.Generic.List[string]]::new()
    $actualArtifacts = [System.Collections.Generic.List[object]]::new()
    foreach ($artifact in $expected) {
        $destination = [string]$artifact.Destination
        if (-not (Test-Path -LiteralPath $destination -PathType Leaf)) {
            [void]$missing.Add([string]$artifact.RelativePath)
            continue
        }
        $actualHash = Get-FileSha256 $destination
        [void]$actualArtifacts.Add([pscustomobject]@{
            Destination = $destination
            DestinationHash = $actualHash
        })
        if (-not [string]::Equals($actualHash, [string]$artifact.DestinationHash,
                [StringComparison]::OrdinalIgnoreCase)) {
            [void]$mismatched.Add([string]$artifact.RelativePath)
        }
    }

    $manifestEntries = @{}
    $manifestValid = $null -ne $Manifest
    if ($manifestValid) {
        foreach ($entry in @($Manifest.files)) {
            $manifestEntries[[string]$entry.path] = [string]$entry.sha256
        }
    }
    $manifestMismatched = [System.Collections.Generic.List[string]]::new()
    foreach ($artifact in $expected) {
        $relative = [string]$artifact.RelativePath
        $actual = @($actualArtifacts | Where-Object {
            [string]::Equals([string]$_.Destination, [string]$artifact.Destination,
                [StringComparison]::OrdinalIgnoreCase)
        })[0]
        if (-not $manifestEntries.ContainsKey($relative) -or
            $null -eq $actual -or
            -not [string]::Equals($manifestEntries[$relative], [string]$actual.DestinationHash,
                [StringComparison]::OrdinalIgnoreCase)) {
            [void]$manifestMismatched.Add($relative)
        }
    }
    $staleManaged = [System.Collections.Generic.List[string]]::new()
    $ambiguousManaged = [System.Collections.Generic.List[string]]::new()
    if ($manifestValid) {
        foreach ($entry in @($Manifest.files)) {
            $relative = [string]$entry.path
            if ($expected.RelativePath -contains $relative) { continue }
            $path = Join-Path $DeploymentRoot ($relative -replace '/', '\')
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
            $actual = Get-FileSha256 $path
            if ([string]::Equals($actual, [string]$entry.sha256,
                    [StringComparison]::OrdinalIgnoreCase)) {
                [void]$staleManaged.Add($relative)
            } else {
                [void]$ambiguousManaged.Add($relative)
            }
        }
    }
    $installedPackageHash = if ($missing.Count -eq 0) {
        Get-RuntimePackageHash @($actualArtifacts)
    } else { $null }
    $expectedPackageHash = Get-RuntimePackageHash $expected
    $manifestPackageMatches = $manifestValid -and
        [string]::Equals([string]$Manifest.packageSha256, $installedPackageHash,
            [StringComparison]::OrdinalIgnoreCase)
    $pending = (Test-Path -LiteralPath (Get-RuntimePendingPath) -PathType Leaf) -and
        -not $IgnorePending
    $state = if ($ambiguousManaged.Count -gt 0) { 'UNKNOWN/AMBIGUOUS' }
        elseif ($missing.Count -gt 0) { 'RUNTIME_INCOMPLETE' }
        elseif ($mismatched.Count -gt 0) { 'RUNTIME_STALE' }
        elseif ($staleManaged.Count -gt 0) { 'MANAGED_STALE_FILES' }
        elseif ($pending -or -not $manifestValid -or $manifestMismatched.Count -gt 0 -or
            -not $manifestPackageMatches) { 'MANIFEST_STALE' }
        else { 'HEALTHY' }
    [pscustomobject]@{
        State = $state
        ExpectedPackageHash = $expectedPackageHash
        InstalledPackageHash = $installedPackageHash
        ManifestPackageHash = if ($Manifest) { [string]$Manifest.packageSha256 } else { $null }
        MissingFiles = @($missing)
        MismatchedFiles = @($mismatched)
        ManifestMismatchedFiles = @($manifestMismatched)
        StaleManagedFiles = @($staleManaged)
        AmbiguousManagedFiles = @($ambiguousManaged)
        ManifestPresent = $manifestValid
        Pending = $pending
        RepairRequired = $state -ne 'HEALTHY'
    }
}


function Read-RuntimeManifest {
    if (-not $CompleteRuntime) { return $null }
    $path = Join-Path $DeploymentRoot '.devbridge-runtime-manifest.json'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $null }
    try {
        $manifest = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -Depth 12
        if ($manifest.schemaVersion -ne 'devbridge-runtime-manifest/v1' -or
            $manifest.packageId -ne 'lan.devbridge2') { return $null }
        return $manifest
    }
    catch {
        throw 'DEVBRIDGE_RUNTIME_MANIFEST_INVALID: installed runtime manifest is unreadable.'
    }
}

function Remove-Proven-StaleRuntimeArtifacts {
    param([Parameter(Mandatory = $true)][AllowNull()]$PreviousManifest,
        [Parameter(Mandatory = $true)][string[]]$ExpectedPaths)
    if ($null -eq $PreviousManifest) { return }
    foreach ($entry in @($PreviousManifest.files)) {
        $relative = [string]$entry.path
        if ([string]::IsNullOrWhiteSpace($relative) -or
            [IO.Path]::IsPathRooted($relative) -or $relative.Contains(':') -or
            $relative.Replace('\', '/').StartsWith('../', [StringComparison]::Ordinal)) {
            throw 'DEVBRIDGE_RUNTIME_MANIFEST_INVALID: managed path is unsafe.'
        }
        $normalized = $relative.Replace('\', '/')
        if ($normalized -eq '.devbridge-runtime-manifest.json' -or
            $normalized.StartsWith('Runtime/', [StringComparison]::OrdinalIgnoreCase) -or
            $normalized -in $ExpectedPaths) { continue }
        $destination = Join-Path $DeploymentRoot ($relative -replace '/', '\')
        if (-not (Test-Path -LiteralPath $destination -PathType Leaf)) { continue }
        $actual = Get-FileSha256 $destination
        if (-not [string]::Equals($actual, [string]$entry.sha256,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw ('DEVBRIDGE_DEPLOYMENT_OWNERSHIP_AMBIGUOUS: managed static file was changed outside DevBridge2: ' + $relative)
        }
        Remove-Item -LiteralPath $destination -Force
    }
}

function Get-Plan {
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $planScript, '-Json')
    if (-not [string]::IsNullOrWhiteSpace($ChangedSince)) {
        $arguments += @('-ChangedSince', $ChangedSince)
    }
    if ($null -ne $ChangedFile -and $ChangedFile.Count -gt 0) {
        $arguments += @('-ChangedFile', ($ChangedFile -join ','))
    }
    $jsonOutput = & pwsh @arguments
    if ($LASTEXITCODE -ne 0) {
        throw 'scripts/dev-plan.ps1 failed.'
    }
    try {
        return ($jsonOutput -join "`n" | ConvertFrom-Json)
    } catch {
        throw 'scripts/dev-plan.ps1 did not return valid JSON: ' + $_.Exception.Message
    }
}

function Remove-StaleDeploymentTemps {
    param([Parameter(Mandatory = $true)][string]$Destination)

    $directory = Split-Path -Parent $Destination
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        return
    }
    $leaf = Split-Path -Leaf $Destination
    $prefix = '.' + $leaf + '.'
    try {
        $staleFiles = @(Get-ChildItem -LiteralPath $directory -File -Force -ErrorAction Stop |
            Where-Object {
                $_.Name.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -and
                $_.Name.EndsWith('.tmp', [StringComparison]::OrdinalIgnoreCase)
            })
        foreach ($staleFile in $staleFiles) {
            Remove-Item -LiteralPath $staleFile.FullName -Force -ErrorAction Stop
        }
    }
    catch {
        throw ('DEVBRIDGE_DEPLOYMENT_TEMP_CLEANUP_FAILED: ' + $Destination + ': ' + $_.Exception.Message)
    }
}

function Wait-ArtifactWritable {
    param([Parameter(Mandatory = $true)][string]$Destination)

    if (-not [System.IO.File]::Exists($Destination)) {
        return
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while ($true) {
        $stream = $null
        try {
            $stream = [System.IO.File]::Open(
                $Destination,
                [System.IO.FileMode]::Open,
                [System.IO.FileAccess]::Read,
                ([System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete))
            return
        }
        catch [System.UnauthorizedAccessException] {
            throw ('DEVBRIDGE_DEPLOYMENT_DESTINATION_UNAVAILABLE: ' + $Destination + ': ' + $_.Exception.Message)
        }
        catch [System.IO.IOException] {
            if ([DateTime]::UtcNow -ge $deadline) {
                throw ('DEVBRIDGE_DEPLOYMENT_DESTINATION_LOCKED: ' + $Destination + ': remained locked for 15 seconds')
            }
            Start-Sleep -Milliseconds 100
        }
        finally {
            if ($null -ne $stream) {
                $stream.Dispose()
            }
        }
    }
}

function Get-DestinationSha256 {
    param([Parameter(Mandatory = $true)][string]$Destination)

    Wait-ArtifactWritable $Destination
    return (Get-FileSha256 $Destination)
}

function Copy-Atomic {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination,
        [Parameter(Mandatory = $true)][string]$ExpectedHash
    )

    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) {
        throw ('DEVBRIDGE_SOURCE_ARTIFACT_MISSING: ' + $Source)
    }
    $sourceHash = Get-FileSha256 $Source
    if (-not [string]::Equals($sourceHash, $ExpectedHash, [StringComparison]::OrdinalIgnoreCase)) {
        throw ('DEVBRIDGE_SOURCE_ARTIFACT_IDENTITY_MISMATCH: ' + $Source +
            ' expected ' + $ExpectedHash + ' but found ' + $sourceHash)
    }

    $directory = Split-Path -Parent $Destination
    try {
        New-Item -ItemType Directory -Force -Path $directory -ErrorAction Stop | Out-Null
    }
    catch {
        throw ('DEVBRIDGE_DEPLOYMENT_DESTINATION_UNAVAILABLE: ' + $Destination + ': ' + $_.Exception.Message)
    }

    Remove-StaleDeploymentTemps $Destination
    $leaf = Split-Path -Leaf $Destination
    $temporary = Join-Path $directory ('.' + $leaf + '.' + $PID + '.' + [guid]::NewGuid().ToString('N') + '.tmp')
    $backup = Join-Path $directory ('.' + $leaf + '.' + $PID + '.' + [guid]::NewGuid().ToString('N') + '.bak')
    $hadDestination = [System.IO.File]::Exists($Destination)
    if ($hadDestination) {
        Wait-ArtifactWritable $Destination
    }
    $previousHash = if ($hadDestination) { Get-FileSha256 $Destination } else { $null }
    $replacementCommitted = $false
    $action = if ($hadDestination) { 'replaced' } else { 'created' }
    try {
        try {
            [System.IO.File]::Copy($Source, $temporary, $true)
        }
        catch {
            throw ('DEVBRIDGE_DEPLOYMENT_STAGE_FAILED: ' + $Destination + ': ' + $_.Exception.Message)
        }

        $temporaryHash = Get-FileSha256 $temporary
        if (-not [string]::Equals($temporaryHash, $ExpectedHash, [StringComparison]::OrdinalIgnoreCase)) {
            throw ('DEVBRIDGE_DEPLOYMENT_STAGE_IDENTITY_MISMATCH: ' + $Destination +
                ' expected ' + $ExpectedHash + ' but found ' + $temporaryHash)
        }

        if ($hadDestination) {
            Wait-ArtifactWritable $Destination
            try {
                [System.IO.File]::Replace($temporary, $Destination, $backup, $true)
                $replacementCommitted = $true
            }
            catch [System.UnauthorizedAccessException] {
                throw ('DEVBRIDGE_DEPLOYMENT_DESTINATION_UNAVAILABLE: ' + $Destination + ': ' + $_.Exception.Message)
            }
            catch [System.IO.IOException] {
                throw ('DEVBRIDGE_DEPLOYMENT_DESTINATION_LOCKED: ' + $Destination + ': ' + $_.Exception.Message)
            }
        }
        else {
            try {
                [System.IO.File]::Move($temporary, $Destination)
                $replacementCommitted = $true
            }
            catch [System.UnauthorizedAccessException] {
                throw ('DEVBRIDGE_DEPLOYMENT_DESTINATION_UNAVAILABLE: ' + $Destination + ': ' + $_.Exception.Message)
            }
            catch [System.IO.IOException] {
                throw ('DEVBRIDGE_DEPLOYMENT_DESTINATION_RACE: ' + $Destination + ': ' + $_.Exception.Message)
            }
        }

        $deployedHash = Get-FileSha256 $Destination
        if (-not [string]::Equals($deployedHash, $ExpectedHash, [StringComparison]::OrdinalIgnoreCase)) {
            throw ('DEVBRIDGE_DEPLOYMENT_IDENTITY_MISMATCH: ' + $Destination +
                ' expected ' + $ExpectedHash + ' but found ' + $deployedHash)
        }

        if ($hadDestination -and [System.IO.File]::Exists($backup)) {
            try {
                Remove-Item -LiteralPath $backup -Force -ErrorAction Stop
            }
            catch {
                $action = 'replaced-backup-retained'
            }
        }
        return [pscustomobject]@{
            action = $action
            sourceSha256 = $sourceHash
            destinationSha256 = $deployedHash
            previousDestinationSha256 = $previousHash
            identityVerified = $true
        }
    }
    catch {
        $failureMessage = [string]$_.Exception.Message
        try {
            if ($replacementCommitted) {
                if ($hadDestination) {
                    if (-not [System.IO.File]::Exists($backup)) {
                        throw 'the replacement backup is missing'
                    }
                    [System.IO.File]::Replace($backup, $Destination, $null, $true)
                    $restoredHash = Get-FileSha256 $Destination
                    if (-not [string]::Equals($restoredHash, $previousHash, [StringComparison]::OrdinalIgnoreCase)) {
                        throw ('restored hash ' + $restoredHash + ' does not match ' + $previousHash)
                    }
                }
                elseif ([System.IO.File]::Exists($Destination)) {
                    Remove-Item -LiteralPath $Destination -Force -ErrorAction Stop
                }
            }
        }
        catch {
            throw ('DEVBRIDGE_DEPLOYMENT_ROLLBACK_FAILED: ' + $Destination +
                ': original=' + $failureMessage + '; rollback=' + $_.Exception.Message)
        }
        throw $failureMessage
    }
    finally {
        if ([System.IO.File]::Exists($temporary)) {
            Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
        }
        if (-not $replacementCommitted -and [System.IO.File]::Exists($backup)) {
            Remove-Item -LiteralPath $backup -Force -ErrorAction SilentlyContinue
        }
    }
}

function Get-CanonicalBridgeToolsPath {
    param([Parameter(Mandatory = $true)][string]$ModRoot)

    $aboutPath = Join-Path $ModRoot 'About\About.xml'
    if (-not (Test-Path -LiteralPath $aboutPath -PathType Leaf)) {
        throw "BridgeTools deployment target is not a DevBridge2 mod root; missing $aboutPath"
    }
    [xml]$about = Get-Content -LiteralPath $aboutPath -Raw
    $packageId = [string]$about.ModMetaData.packageId
    if (-not [string]::Equals($packageId.Trim(), 'lan.devbridge2', [StringComparison]::OrdinalIgnoreCase)) {
        throw "BridgeTools deployment target is not lan.devbridge2: $ModRoot"
    }
    $modsRoot = Split-Path -Parent $ModRoot
    $rimWorldRoot = Split-Path -Parent $modsRoot
    if (-not [string]::Equals((Split-Path -Leaf $modsRoot), 'Mods', [StringComparison]::OrdinalIgnoreCase)) {
        throw "BridgeTools deployment target must be directly under a RimWorld Mods directory: $ModRoot"
    }
    $destinationDirectory = Join-Path (Join-Path $rimWorldRoot 'BridgeTools') (Split-Path -Leaf $ModRoot)
    return Join-Path $destinationDirectory 'DevBridge2.BridgeTools.dll'
}

function Get-ManagedDirectory {
    $candidate = $RimWorldManagedDir
    if ([string]::IsNullOrWhiteSpace($candidate)) {
        $candidate = [Environment]::GetEnvironmentVariable('DEVBRIDGE_RIMWORLD_MANAGED_DIR')
    }
    if ([string]::IsNullOrWhiteSpace($candidate)) {
        $candidate = Join-Path $repoRoot '..\..\RimWorldWin64_Data\Managed'
    }
    return [System.IO.Path]::GetFullPath($candidate)
}

function Wait-RimWorldStopped {
    param([Parameter(Mandatory = $true)][string]$RimWorldExecutable)

    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        $current = Get-CurrentRimWorldProcess $RimWorldExecutable
        if ($null -eq $current) { return }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'DEVBRIDGE_RIMWORLD_SHUTDOWN_TIMEOUT: owned RimWorld did not exit within 20 seconds.'
}
function Enter-ManagedRimWorldMaintenance {
    if (-not $CompleteRuntime) {
        return
    }
    $status = Invoke-DevBridgeJson $DeploymentRoot @('status', '--json')
    if ($null -ne $script:CurrentRimWorldProcess) {
        $expected = $status.identity.expectedRimWorldProcess
        if ($null -eq $expected -or [int]$expected.pid -ne [int]$script:CurrentRimWorldProcess.Pid -or
            [long]$expected.startIdentity -ne [long]$script:CurrentRimWorldProcess.StartIdentity) {
            throw 'DEVBRIDGE_RIMWORLD_PROCESS_AMBIGUOUS: coordinator ownership does not match the exact RimWorld process.'
        }
    }
    $leases = @($status.leases | Where-Object { $null -ne $_ })
    $activeTestCount = [int]$status.activeTests
    if ($leases.Count -gt 0 -or $activeTestCount -gt 0) {
        throw 'DEVBRIDGE_MAINTENANCE_CONTENTION: another lease or lifecycle transaction is active.'
    }
    $script:MaintenanceGenerationBefore = [int]$status.generation
    $begin = Invoke-DevBridgeJson $DeploymentRoot @('test', 'begin', '--json')
    $lease = [string]$begin.leaseId
    if ($lease -notmatch '^lease-[0-9A-Fa-f]{32}$') {
        throw 'DEVBRIDGE_MAINTENANCE_LEASE_INVALID: lifecycle owner did not return a complete lease capability.'
    }
    $script:MaintenanceLeaseId = $lease
    $stop = Invoke-DevBridgeJson $DeploymentRoot @('stop', $lease, '--json')
    if (-not [bool]$stop.maintenanceReady -or [string]$stop.gameState -ne 'STOPPED') {
        throw 'DEVBRIDGE_MAINTENANCE_NOT_CONFIRMED: lifecycle owner did not confirm STOPPED and maintenanceReady.'
    }
    Wait-RimWorldStopped $CanonicalRimWorldExecutable
    $script:MaintenanceEstablished = $true
}

function Invoke-ManagedRimWorldReady {
    if (-not $CompleteRuntime -or [string]::IsNullOrWhiteSpace($script:MaintenanceLeaseId)) {
        return $null
    }
    $ready = Invoke-DevBridgeJson $DeploymentRoot @(
        'ensure-ready', $script:MaintenanceLeaseId, '--json')
    if ([string]$ready.state -ne 'READY') {
        throw 'DEVBRIDGE_QUICKTEST_NOT_READY: managed ensure-ready did not return READY.'
    }
    $after = [int]$ready.generation
    if ($after -le [int]$script:MaintenanceGenerationBefore) {
        throw 'DEVBRIDGE_GENERATION_NOT_ADVANCED: managed restart did not establish a new generation.'
    }
    $script:MaintenanceGenerationAfter = $after
    $script:MaintenanceEstablished = $false
    return $ready
}

function Release-ManagedRimWorldMaintenance {
    if ([string]::IsNullOrWhiteSpace($script:MaintenanceLeaseId)) { return }
    if (-not $script:MaintenanceEstablished) {
        $null = Invoke-DevBridgeJson $DeploymentRoot @(
            'test', 'end', $script:MaintenanceLeaseId, '--json')
    }
    $script:MaintenanceLeaseId = $null
}

function Start-InstalledCoordinator {
    $coordinatorExe = Join-Path $DeploymentRoot 'Coordinator\DevBridge.Coordinator.exe'
    $controlPath = Join-Path $DeploymentRoot 'Runtime\coordinator-control.json'
    $slot = $null
    if (Test-Path -LiteralPath $controlPath -PathType Leaf) {
        try { $slot = [string](Get-Content -LiteralPath $controlPath -Raw | ConvertFrom-Json).RuntimeSlotId }
        catch { $slot = $null }
    }
    $arguments = @('--server', '--root', $DeploymentRoot)
    if (-not [string]::IsNullOrWhiteSpace($slot)) {
        $arguments += @('--runtime-slot', $slot)
    }
    Start-Process -FilePath $coordinatorExe -ArgumentList $arguments `
        -WorkingDirectory (Split-Path -Parent $coordinatorExe) | Out-Null
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 200
        try {
            $probe = Invoke-DevBridgeJson $DeploymentRoot @('coordinator', 'probe', '--json')
            if ([bool]$probe.success -and [bool]$probe.healthPipeAvailable) { return }
        } catch { }
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'DEVBRIDGE_COORDINATOR_RESTART_TIMEOUT: repaired coordinator did not reach its health boundary.'
}

function Wait-ExactCoordinatorExit {
    param(
        [Parameter(Mandatory = $true)][string]$Executable,
        [Parameter(Mandatory = $true)][int]$ProcessId,
        [Parameter(Mandatory = $true)][long]$StartIdentity
    )

    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $process = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
        if ($null -eq $process) { return }
        if (-not [string]::Equals([IO.Path]::GetFullPath($process.Path),
                [IO.Path]::GetFullPath($Executable), [StringComparison]::OrdinalIgnoreCase) -or
            [long]$process.StartTime.ToUniversalTime().Ticks -ne $StartIdentity) {
            throw 'DEVBRIDGE_COORDINATOR_PROCESS_AMBIGUOUS: coordinator PID identity changed during shutdown.'
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'DEVBRIDGE_COORDINATOR_SHUTDOWN_TIMEOUT: owned coordinator did not exit within 15 seconds.'
}

function Wait-CoordinatorRootStopped {
    param([Parameter(Mandatory = $true)][string]$Root)

    $expectedExe = [IO.Path]::GetFullPath(
        (Join-Path $Root 'Coordinator\DevBridge.Coordinator.exe'))
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $matches = @(Get-CimInstance Win32_Process | Where-Object {
            $path = [string]$_.ExecutablePath
            -not [string]::IsNullOrWhiteSpace($path) -and
            [string]::Equals([IO.Path]::GetFullPath($path),
                $expectedExe, [StringComparison]::OrdinalIgnoreCase) -and
            [string]$_.CommandLine -match '--server' -and
            [string]$_.CommandLine -match [regex]::Escape($Root)
        })
        if ($matches.Count -eq 0) { return }
        if ($matches.Count -gt 1) {
            throw 'DEVBRIDGE_COORDINATOR_PROCESS_AMBIGUOUS: multiple exact coordinator servers remain.'
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'DEVBRIDGE_COORDINATOR_SHUTDOWN_TIMEOUT: exact coordinator server did not exit within 15 seconds.'
}

function Invoke-CoordinatorShutdown {
    param([Parameter(Mandatory = $true)][string]$Root)

    $coordinatorExe = Join-Path $Root 'Coordinator\DevBridge.Coordinator.exe'
    if (-not (Test-Path -LiteralPath $coordinatorExe -PathType Leaf)) {
        return $false
    }
    $expectedExe = [IO.Path]::GetFullPath($coordinatorExe)
    $live = @(Get-CimInstance Win32_Process | Where-Object {
        $path = [string]$_.ExecutablePath
        -not [string]::IsNullOrWhiteSpace($path) -and
        [string]::Equals([IO.Path]::GetFullPath($path),
            $expectedExe, [StringComparison]::OrdinalIgnoreCase) -and
        [string]$_.CommandLine -match '--server' -and
        [string]$_.CommandLine -match [regex]::Escape($Root)
    })
    if ($live.Count -gt 1) {
        throw 'DEVBRIDGE_COORDINATOR_PROCESS_AMBIGUOUS: multiple exact coordinator servers are running.'
    }
    if ($live.Count -eq 0) {
        $script:CoordinatorWasRunning = $false
        return $true
    }
    $script:CoordinatorWasRunning = $true
    $coordinatorPid = [int]$live[0].ProcessId
    $running = Get-Process -Id $coordinatorPid
    $startIdentity = [long]$running.StartTime.ToUniversalTime().Ticks
    $null = & $coordinatorExe '--root' $Root 'coordinator' 'shutdown' '--json' 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Graceful coordinator shutdown failed for $Root with exit code $LASTEXITCODE."
    }
    if ($coordinatorPid -gt 0 -and $startIdentity -gt 0) {
        Wait-ExactCoordinatorExit $coordinatorExe $coordinatorPid $startIdentity
    } else {
        Wait-CoordinatorRootStopped $Root
    }
    return $true
}

function Add-ArtifactRecord {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][System.Collections.Generic.List[object]]$Records,
        [Parameter(Mandatory = $true)][string]$Component,
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination,
        [Parameter(Mandatory = $true)][string]$LoadedStatus,
        [switch]$DidDeploy,
        [switch]$DidBuild,
        [string]$SourceHash,
        [string]$DestinationHash,
        [string]$PreviousDestinationHash,
        [string]$ReconciliationAction = 'not-attempted',
        [bool]$IdentityVerified = $false,
        [bool]$DeployRequired = $false
    )
    [void]$Records.Add([ordered]@{
        component = $Component
        source = $Source
        destination = $Destination
        built = [bool]$DidBuild
        sourceSha256 = $SourceHash
        deployedSha256 = $DestinationHash
        destinationHash = $DestinationHash
        previousDestinationSha256 = $PreviousDestinationHash
        deployRequired = $DeployRequired
        deployPerformed = [bool]$DidDeploy
        reconciliationAction = $ReconciliationAction
        identityVerified = $IdentityVerified
        loadedStatus = $LoadedStatus
    })
}

$plan = Get-Plan
if ($CompleteRuntime) {
    $plan.build = @($plan.build + @('coordinator', 'rimworld-mod') | Sort-Object -Unique)
    $plan.deploy = @($plan.deploy + @('coordinator', 'rimworld-mod') | Sort-Object -Unique)
    $plan.changeClass = 'complete-runtime'
    $plan.changeClasses = @($plan.changeClasses + 'complete-runtime' | Sort-Object -Unique)
}
elseif ($BootstrapRuntime) {
    $plan.build = @($plan.build + 'coordinator' | Sort-Object -Unique)
    $plan.deploy = @($plan.deploy + 'coordinator' | Sort-Object -Unique)
    $plan.changeClass = 'runtime-bootstrap'
    $plan.changeClasses = @($plan.changeClasses + 'runtime-bootstrap' | Sort-Object -Unique)
}
$records = [System.Collections.Generic.List[object]]::new()
$built = [System.Collections.Generic.List[string]]::new()
$deployed = [System.Collections.Generic.List[string]]::new()
$coordinatorShutdown = $false
$script:MaintenanceLeaseId = $null
$script:MaintenanceGenerationBefore = $null
$script:MaintenanceGenerationAfter = $null
$script:MaintenanceEstablished = $false
$script:CurrentRimWorldProcess = $null
$script:CanonicalRimWorldExecutable = $null
$script:RuntimePendingPath = $null
$script:CoordinatorWasRunning = $false
$stagingRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    'DevBridge2-dev-publish-' + $PID + '-' + [guid]::NewGuid().ToString('N'))
$buildProperties = Get-BuildProperties
$productVersion = Get-AuthoritativeProductVersion
$sourceCommit = Get-GitValue @('rev-parse', 'HEAD')
$sourceDirty = @(git status --porcelain=v1 --untracked-files=all).Count -gt 0

$report = $null
$failure = $null
try {
    Assert-CompleteRuntimeTarget
    Acquire-DeploymentLock
    New-Item -ItemType Directory -Force -Path $stagingRoot | Out-Null
    $coordinatorStaging = Join-Path $stagingRoot 'Coordinator'
    $modStaging = Join-Path $stagingRoot 'Mod'
    $runtimeStaticStaging = Join-Path $stagingRoot 'RuntimeStatic'
    $runtimeStaticArtifacts = [System.Collections.Generic.List[object]]::new()
    if ($DryRun) {
        $report = [ordered]@{
            schemaVersion = 'devbridge-dev-publish/v1'
            dryRun = $true
            productVersion = $productVersion
            plan = $plan
            built = @()
            deployed = @()
            artifacts = @()
            coordinatorShutdown = $false
            deployRequired = $null
            coordinatorRefreshRequired = $plan.coordinatorRefreshRequired
            rimWorldRestartRequired = $plan.rimWorldRestartRequired
            requiredRefresh = $plan.requiredRefresh
            loadedCodeStatus = 'not-proven'
        }
    }
    else {
        if ($plan.build -contains 'coordinator') {
            Invoke-Required 'dotnet' @(
                'restore', $coordinatorProject, '-r', 'win-x64', '--locked-mode', '--nologo'
            ) 'Restore Coordinator development publish assets'
            $arguments = @(
                'publish', $coordinatorProject, '-c', $Configuration, '-r', 'win-x64',
                '--self-contained', 'false', '-o', $coordinatorStaging, '--no-restore', '--nologo'
            ) + $buildProperties
            Invoke-Required 'dotnet' $arguments 'Build Coordinator (including Coordinator.Core)'
            [void]$built.Add('coordinator')
        }

        if ($plan.build -contains 'rimworld-mod') {
            $managed = Get-ManagedDirectory
            if (-not (Test-Path -LiteralPath (Join-Path $managed 'Assembly-CSharp.dll') -PathType Leaf) -or
                -not (Test-Path -LiteralPath (Join-Path $managed 'UnityEngine.CoreModule.dll') -PathType Leaf)) {
                throw "RimWorld managed assemblies were not found at $managed. Pass -RimWorldManagedDir to build the mod assembly."
            }
            Invoke-Required 'dotnet' @(
                'restore', $modProject, '--locked-mode', '--nologo'
            ) 'Restore RimWorld mod development build assets'
            New-Item -ItemType Directory -Force -Path $modStaging | Out-Null
            $arguments = @(
                'build', $modProject, '-c', $Configuration, '--no-restore', '--nologo',
                ('-p:RimWorldManagedDir=' + [System.IO.Path]::GetFullPath($managed)),
                ('-p:OutputPath=' + ($modStaging.TrimEnd('\') + '\'))
            ) + $buildProperties
            Invoke-Required 'dotnet' $arguments 'Build RimWorld mod assembly'
            if (-not (Test-Path -LiteralPath (Join-Path $modStaging 'DevBridge2.dll') -PathType Leaf)) {
                throw 'RimWorld mod build did not produce DevBridge2.dll.'
            }
            [void]$built.Add('rimworld-mod')
        }

        if ($plan.build -contains 'bridgeTools') {
            if (-not (Test-Path -LiteralPath $publishCompanionScript -PathType Leaf)) {
                throw "Existing companion publisher was not found: $publishCompanionScript"
            }
            Invoke-Required 'dotnet' @(
                'restore', $bridgeToolsProject, '--locked-mode', '--nologo'
            ) 'Restore BridgeTools development build assets'
            $bridgeToolsDestination = Get-CanonicalBridgeToolsPath $DeploymentRoot
            $bridgeToolsBefore = Get-DestinationSha256 $bridgeToolsDestination
            $arguments = @(
                '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $publishCompanionScript,
                '-Configuration', $Configuration, '-CompanionOnly', '-DeployCompanion',
                '-SkipIfIdentical', '-DeploymentRoot', $DeploymentRoot
            )
            if (-not [string]::IsNullOrWhiteSpace($RimBridgeSdkPath)) {
                $arguments += @('-RimBridgeSdkPath', $RimBridgeSdkPath)
            }
            if (-not $Json) { Write-Host "`n== Build/deploy canonical BridgeTools companion ==" }
            $null = & pwsh @arguments 2>&1
            if ($LASTEXITCODE -ne 0) {
                throw 'Publish-DevBridge.ps1 failed while building/deploying BridgeTools.'
            }
            [void]$built.Add('bridgeTools')
            $bridgeToolsAfter = Get-DestinationSha256 $bridgeToolsDestination
            $bridgeToolsChanged = $null -eq $bridgeToolsBefore -or
                -not [string]::Equals($bridgeToolsBefore, $bridgeToolsAfter, [StringComparison]::OrdinalIgnoreCase)
            if ($bridgeToolsChanged) { [void]$deployed.Add('bridgeTools') }
            $bridgeToolsSource = Join-Path $repoRoot ('Source\BridgeTools\bin\' + $Configuration + '\DevBridge2.BridgeTools.dll')
            $bridgeToolsIdentityVerified = $null -ne $bridgeToolsAfter -and
                [string]::Equals((Get-FileSha256 $bridgeToolsSource), $bridgeToolsAfter, [StringComparison]::OrdinalIgnoreCase)
            Add-ArtifactRecord -Records $records -Component 'bridgeTools' -Source $bridgeToolsSource `
                -Destination $bridgeToolsDestination -LoadedStatus 'not-proven' -DidDeploy:$bridgeToolsChanged -DidBuild `
                -SourceHash (Get-FileSha256 $bridgeToolsSource) -DestinationHash $bridgeToolsAfter `
                -PreviousDestinationHash $bridgeToolsBefore `
                -ReconciliationAction $(if ($bridgeToolsChanged) { 'replaced-by-companion' } else { 'unchanged' }) `
                -IdentityVerified:$bridgeToolsIdentityVerified `
                -DeployRequired:$bridgeToolsChanged
        }

        if ($CompleteRuntime -or $BootstrapRuntime) {
            $staticSources = @(
                [pscustomobject]@{ RelativePath = 'DevBridge.cmd'; Source = Join-Path $repoRoot 'DevBridge.cmd' },
                [pscustomobject]@{ RelativePath = 'About/About.xml'; Source = Join-Path $repoRoot 'About\About.xml' },
                [pscustomobject]@{ RelativePath = 'LoadFolders.xml'; Source = Join-Path $repoRoot 'LoadFolders.xml' }
            )
            foreach ($static in $staticSources) {
                if (-not (Test-Path -LiteralPath $static.Source -PathType Leaf)) {
                    throw ('DEVBRIDGE_SOURCE_ARTIFACT_MISSING: ' + $static.Source)
                }
                $staged = Join-Path $runtimeStaticStaging ($static.RelativePath -replace '/', '\')
                New-Item -ItemType Directory -Force -Path (Split-Path -Parent $staged) | Out-Null
                Copy-Item -LiteralPath $static.Source -Destination $staged -Force
                [void]$runtimeStaticArtifacts.Add([pscustomobject]@{
                    relativePath = $static.RelativePath
                    source = $static.Source
                    staged = $staged
                    destination = Join-Path $DeploymentRoot ($static.RelativePath -replace '/', '\')
                })
            }
        }

        if ($CompleteRuntime) {
            $expectedRuntimePaths = @(
                'DevBridge.cmd',
                'About/About.xml',
                'LoadFolders.xml',
                'Coordinator/DevBridge.Coordinator.exe',
                'Coordinator/DevBridge.Coordinator.dll',
                'Coordinator/DevBridge.Coordinator.Core.dll',
                'Coordinator/DevBridge.Coordinator.deps.json',
                'Coordinator/DevBridge.Coordinator.runtimeconfig.json',
                '1.6/Assemblies/DevBridge2.dll'
            )
            $previousRuntimeManifest = Read-RuntimeManifest
        }

        $coordinatorDeployRequired = $false
        $coordinatorArtifacts = [System.Collections.Generic.List[object]]::new()
        if ($plan.build -contains 'coordinator') {
            foreach ($file in @('DevBridge.Coordinator.exe', 'DevBridge.Coordinator.dll',
                    'DevBridge.Coordinator.Core.dll', 'DevBridge.Coordinator.deps.json',
                    'DevBridge.Coordinator.runtimeconfig.json')) {
                $source = Join-Path $coordinatorStaging $file
                $destination = Join-Path $DeploymentRoot ('Coordinator\' + $file)
                $sourceHash = Get-FileSha256 $source
                if ($null -eq $sourceHash) {
                    throw ('DEVBRIDGE_SOURCE_ARTIFACT_MISSING: ' + $source)
                }
                $destinationHash = Get-FileSha256 $destination
                $different = $null -eq $destinationHash -or
                    -not [string]::Equals($sourceHash, $destinationHash,
                        [StringComparison]::OrdinalIgnoreCase)
                if ($different) { $coordinatorDeployRequired = $true }
                [void]$coordinatorArtifacts.Add([ordered]@{
                    file = $file
                    relativePath = 'Coordinator/' + $file
                    source = $source
                    destination = $destination
                    sourceHash = $sourceHash
                    destinationHash = $destinationHash
                    different = $different
                })
            }
        }

        $runtimeExpectedArtifacts = [System.Collections.Generic.List[object]]::new()
        if ($CompleteRuntime) {
            foreach ($artifact in $coordinatorArtifacts) {
                [void]$runtimeExpectedArtifacts.Add([pscustomobject]@{
                    RelativePath = [string]$artifact.relativePath
                    Source = [string]$artifact.source
                    Destination = [string]$artifact.destination
                    DestinationHash = [string]$artifact.sourceHash
                })
            }
            $modSource = Join-Path $modStaging 'DevBridge2.dll'
            $modDestination = Join-Path $DeploymentRoot '1.6\Assemblies\DevBridge2.dll'
            $modSourceHash = Get-FileSha256 $modSource
            if ($null -eq $modSourceHash) {
                throw ('DEVBRIDGE_SOURCE_ARTIFACT_MISSING: ' + $modSource)
            }
            [void]$runtimeExpectedArtifacts.Add([pscustomobject]@{
                RelativePath = '1.6/Assemblies/DevBridge2.dll'
                Source = $modSource
                Destination = $modDestination
                DestinationHash = $modSourceHash
            })
            foreach ($static in $runtimeStaticArtifacts) {
                [void]$runtimeExpectedArtifacts.Add([pscustomobject]@{
                    RelativePath = [string]$static.relativePath
                    Source = [string]$static.source
                    Destination = [string]$static.destination
                    DestinationHash = Get-FileSha256 $static.staged
                })
            }
            $runtimeHealth = Get-CompleteRuntimeHealth $runtimeExpectedArtifacts `
                $previousRuntimeManifest
            $repairRequired = [bool]$runtimeHealth.RepairRequired -or
                ($plan.deploy -contains 'rimworld-content')
            if ($repairRequired) {
                Write-RuntimePending $runtimeExpectedArtifacts `
                    $runtimeHealth.ExpectedPackageHash 'runtime-or-manifest-inconsistency'
                Enter-ManagedRimWorldMaintenance
                Remove-Proven-StaleRuntimeArtifacts $previousRuntimeManifest $expectedRuntimePaths
            }
        } elseif ($BootstrapRuntime -and $coordinatorDeployRequired) {
            $bootstrapPending = @($coordinatorArtifacts | ForEach-Object {
                [pscustomobject]@{
                    RelativePath = [string]$_.relativePath
                    Source = [string]$_.source
                    Destination = [string]$_.destination
                    DestinationHash = [string]$_.sourceHash
                }
            })
            Write-RuntimePending $bootstrapPending 'coordinator-only-update-invalidates-complete-runtime'
        }

        if ($coordinatorDeployRequired) {
            $coordinatorShutdown = Invoke-CoordinatorShutdown $DeploymentRoot
            foreach ($artifact in $coordinatorArtifacts | Where-Object { $_.different }) {
                Wait-ArtifactWritable $artifact.destination
            }
        }
        foreach ($artifact in $coordinatorArtifacts) {
            $reconciliation = $null
            if ($artifact.different) {
                $reconciliation = Copy-Atomic -Source $artifact.source -Destination $artifact.destination `
                    -ExpectedHash $artifact.sourceHash
                [void]$deployed.Add('coordinator')
            }
            else {
                $reconciliation = [pscustomobject]@{
                    action = 'unchanged'
                    sourceSha256 = $artifact.sourceHash
                    destinationSha256 = $artifact.destinationHash
                    previousDestinationSha256 = $artifact.destinationHash
                    identityVerified = $true
                }
            }
            Add-ArtifactRecord -Records $records -Component 'coordinator' -Source $artifact.source -Destination $artifact.destination `
                -LoadedStatus 'not-proven' -DidDeploy:$artifact.different -DidBuild -SourceHash $reconciliation.sourceSha256 `
                -DestinationHash $reconciliation.destinationSha256 `
                -PreviousDestinationHash $reconciliation.previousDestinationSha256 `
                -ReconciliationAction $reconciliation.action -IdentityVerified:$reconciliation.identityVerified `
                -DeployRequired:$artifact.different
        }

        if ($plan.build -contains 'rimworld-mod') {
            $source = Join-Path $modStaging 'DevBridge2.dll'
            $destination = Join-Path $DeploymentRoot '1.6\Assemblies\DevBridge2.dll'
            $sourceHash = Get-FileSha256 $source
            $destinationBefore = Get-DestinationSha256 $destination
            $different = $null -eq $destinationBefore -or
                -not [string]::Equals($sourceHash, $destinationBefore, [StringComparison]::OrdinalIgnoreCase)
            $reconciliation = $null
            if ($different) {
                $reconciliation = Copy-Atomic -Source $source -Destination $destination -ExpectedHash $sourceHash
                [void]$deployed.Add('rimworld-mod')
            }
            else {
                $reconciliation = [pscustomobject]@{
                    action = 'unchanged'
                    sourceSha256 = $sourceHash
                    destinationSha256 = $destinationBefore
                    previousDestinationSha256 = $destinationBefore
                    identityVerified = $true
                }
            }
            Add-ArtifactRecord -Records $records -Component 'rimworld-mod' -Source $source -Destination $destination `
                -LoadedStatus 'not-proven' -DidDeploy:$different -DidBuild -SourceHash $reconciliation.sourceSha256 `
                -DestinationHash $reconciliation.destinationSha256 `
                -PreviousDestinationHash $reconciliation.previousDestinationSha256 `
                -ReconciliationAction $reconciliation.action -IdentityVerified:$reconciliation.identityVerified `
                -DeployRequired:$different
        }
        if ($CompleteRuntime -or $BootstrapRuntime) {
            foreach ($static in $runtimeStaticArtifacts) {
                $destinationBefore = Get-DestinationSha256 $static.destination
                $sourceHash = Get-FileSha256 $static.staged
                $different = $null -eq $destinationBefore -or
                    -not [string]::Equals($sourceHash, $destinationBefore,
                        [StringComparison]::OrdinalIgnoreCase)
                if ($different) {
                    $reconciliation = Copy-Atomic -Source $static.staged `
                        -Destination $static.destination -ExpectedHash $sourceHash
                    [void]$deployed.Add('runtime-static')
                }
                else {
                    $reconciliation = [pscustomobject]@{
                        action = 'unchanged'
                        sourceSha256 = $sourceHash
                        destinationSha256 = $destinationBefore
                        previousDestinationSha256 = $destinationBefore
                        identityVerified = $true
                    }
                }
                Add-ArtifactRecord -Records $records -Component 'runtime-static' `
                    -Source $static.source -Destination $static.destination `
                    -LoadedStatus 'not-applicable' -DidDeploy:$different `
                    -DidBuild:$false -SourceHash $reconciliation.sourceSha256 `
                    -DestinationHash $reconciliation.destinationSha256 `
                    -PreviousDestinationHash $reconciliation.previousDestinationSha256 `
                    -ReconciliationAction $reconciliation.action `
                    -IdentityVerified:$reconciliation.identityVerified `
                    -DeployRequired:$different
            }
        }

        if ($plan.deploy -contains 'rimworld-content') {
            $contentFiles = @($plan.fileClassifications |
                Where-Object { $_.changeClass -eq 'RimWorld-content/xml' } |
                ForEach-Object { $_.path })
            foreach ($file in $contentFiles) {
                $source = Join-Path $repoRoot ($file -replace '/', '\')
                if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
                    throw "Changed RimWorld content is not present for publishing: $source"
                }
                $destination = Join-Path $DeploymentRoot ($file -replace '/', '\')
                $sourceHash = Get-FileSha256 $source
                $destinationBefore = Get-DestinationSha256 $destination
                $different = $null -eq $destinationBefore -or
                    -not [string]::Equals($sourceHash, $destinationBefore, [StringComparison]::OrdinalIgnoreCase)
                $reconciliation = $null
                if ($different) {
                    $reconciliation = Copy-Atomic -Source $source -Destination $destination -ExpectedHash $sourceHash
                    [void]$deployed.Add('rimworld-content')
                }
                else {
                    $reconciliation = [pscustomobject]@{
                        action = 'unchanged'
                        sourceSha256 = $sourceHash
                        destinationSha256 = $destinationBefore
                        previousDestinationSha256 = $destinationBefore
                        identityVerified = $true
                    }
                }
                Add-ArtifactRecord -Records $records -Component 'rimworld-content' -Source $source -Destination $destination `
                    -LoadedStatus 'not-proven' -DidDeploy:$different -SourceHash $reconciliation.sourceSha256 `
                    -DestinationHash $reconciliation.destinationSha256 `
                    -PreviousDestinationHash $reconciliation.previousDestinationSha256 `
                    -ReconciliationAction $reconciliation.action -IdentityVerified:$reconciliation.identityVerified `
                    -DeployRequired:$different
            }
        }

        if ($plan.deploy -contains 'test-recipes') {
            $recipeFiles = @($plan.fileClassifications |
                Where-Object { $_.changeClass -eq 'test-recipes' } |
                ForEach-Object { $_.path })
            foreach ($file in $recipeFiles) {
                $source = Join-Path $repoRoot ($file -replace '/', '\')
                if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
                    throw "Changed repository-owned recipe is not present for publishing: $source"
                }
                $destination = Join-Path $DeploymentRoot ($file -replace '/', '\')
                $sourceHash = Get-FileSha256 $source
                $destinationBefore = Get-DestinationSha256 $destination
                $different = $null -eq $destinationBefore -or
                    -not [string]::Equals($sourceHash, $destinationBefore, [StringComparison]::OrdinalIgnoreCase)
                $reconciliation = $null
                if ($different) {
                    $reconciliation = Copy-Atomic -Source $source -Destination $destination -ExpectedHash $sourceHash
                    [void]$deployed.Add('test-recipes')
                }
                else {
                    $reconciliation = [pscustomobject]@{
                        action = 'unchanged'
                        sourceSha256 = $sourceHash
                        destinationSha256 = $destinationBefore
                        previousDestinationSha256 = $destinationBefore
                        identityVerified = $true
                    }
                }
                Add-ArtifactRecord -Records $records -Component 'test-recipes' -Source $source -Destination $destination `
                    -LoadedStatus 'not-applicable' -DidDeploy:$different -SourceHash $reconciliation.sourceSha256 `
                    -DestinationHash $reconciliation.destinationSha256 `
                    -PreviousDestinationHash $reconciliation.previousDestinationSha256 `
                    -ReconciliationAction $reconciliation.action -IdentityVerified:$reconciliation.identityVerified `
                    -DeployRequired:$different
            }
        }

        if ($coordinatorShutdown -and $script:CoordinatorWasRunning) {
            Start-InstalledCoordinator
        }

        $runtimeManifest = $null
        $runtimeHealthAfter = $null
        if ($CompleteRuntime) {
            $runtimeArtifacts = @($records | Where-Object {
                $_.component -in @('coordinator', 'rimworld-mod', 'runtime-static')
            })
            if ($runtimeArtifacts.Count -ne 9 -or
                @($runtimeArtifacts | Where-Object {
                    -not $_.identityVerified -or [string]::IsNullOrWhiteSpace([string]$_.destinationHash)
                }).Count -gt 0) {
                throw 'DEVBRIDGE_RUNTIME_IDENTITY_UNPROVEN: complete runtime package was not fully hash-verified.'
            }
            $runtimeHealthAfter = Get-CompleteRuntimeHealth $runtimeExpectedArtifacts `
                $previousRuntimeManifest -IgnorePending
            if ($runtimeHealthAfter.MissingFiles.Count -gt 0 -or
                $runtimeHealthAfter.MismatchedFiles.Count -gt 0 -or
                $runtimeHealthAfter.AmbiguousManagedFiles.Count -gt 0 -or
                $runtimeHealthAfter.StaleManagedFiles.Count -gt 0 -or
                -not [string]::Equals($runtimeHealthAfter.InstalledPackageHash,
                    $runtimeHealthAfter.ExpectedPackageHash,
                    [StringComparison]::OrdinalIgnoreCase)) {
                throw 'DEVBRIDGE_RUNTIME_IDENTITY_UNPROVEN: installed runtime did not match the canonical staged package.'
            }
            $runtimePackageHash = $runtimeHealthAfter.ExpectedPackageHash
            $runtimeManifest = Write-RuntimeManifest $runtimeArtifacts `
                $runtimePackageHash $sourceCommit $sourceDirty
            Clear-RuntimePending
            $runtimeHealthAfter = Get-CompleteRuntimeHealth $runtimeExpectedArtifacts `
                (Read-RuntimeManifest)
            if ($runtimeHealthAfter.State -ne 'HEALTHY') {
                throw ('DEVBRIDGE_RUNTIME_HEALTH_UNPROVEN: complete runtime ended in ' +
                    $runtimeHealthAfter.State + '.')
            }
            $null = Invoke-ManagedRimWorldReady
        }
        $modDeployed = @($records | Where-Object { $_.component -eq 'rimworld-mod' -and $_.deployRequired }).Count -gt 0
        $contentDeployed = @($records | Where-Object { $_.component -eq 'rimworld-content' -and $_.deployRequired }).Count -gt 0
        $bridgeDeployed = @($records | Where-Object { $_.component -eq 'bridgeTools' -and $_.deployRequired }).Count -gt 0
        $coordinatorDeployed = @($records | Where-Object { $_.component -eq 'coordinator' -and $_.deployRequired }).Count -gt 0
        $restart = if ($modDeployed -or $contentDeployed) { $true } elseif ($bridgeDeployed) { $null } else { $false }
        $refresh = if ($modDeployed -or $contentDeployed) { 'rimworld' }
            elseif ($bridgeDeployed) { 'unknown' }
            elseif ($coordinatorDeployed) { 'coordinator' }
            else { 'none' }
        $report = [ordered]@{
            schemaVersion = 'devbridge-dev-publish/v1'
            status = 'pass'
            success = $true
            dryRun = $false
            productVersion = $productVersion
            plan = $plan
            built = @($built | Sort-Object -Unique)
            deployed = @($deployed | Sort-Object -Unique)
            artifacts = @($records)
            runtimeManifest = $runtimeManifest
            runtimeHealthBefore = $runtimeHealth
            runtimeHealthAfter = $runtimeHealthAfter
            runtimePendingPath = $script:RuntimePendingPath
            maintenanceLeaseId = $script:MaintenanceLeaseId
            maintenanceGenerationBefore = $script:MaintenanceGenerationBefore
            maintenanceGenerationAfter = $script:MaintenanceGenerationAfter
            coordinatorShutdown = $coordinatorShutdown
            coordinatorShutdownPerformed = $coordinatorShutdown -and $script:CoordinatorWasRunning
            managedRestarted = $null -ne $script:MaintenanceGenerationAfter
            deployRequired = @($records | Where-Object { $_.deployRequired }).Count -gt 0
            coordinatorRefreshRequired = $coordinatorDeployed
            rimWorldRestartRequired = $restart
            requiredRefresh = $refresh
            loadedCodeStatus = if ($modDeployed) {
                'not-proven-rimworld-must-restart'
            } elseif ($bridgeDeployed) {
                'not-proven-host-reload-unknown'
            } else {
                'unchanged-or-not-applicable'
            }
            notes = @(
                'Coordinator replacement is preceded by coordinator shutdown only when a coordinator artifact hash differs.'
                'A byte-identical build is reported as deployRequired=false and does not trigger refresh or restart.'
                'Complete runtime deployment manages only static wrapper, metadata, coordinator, and mod artifacts; Runtime state is preserved.'
                'Copied RimWorld and BridgeTools files are never reported as loaded code.'
            )
        }
    }
}
catch {
    $message = [string]$_.Exception.Message
    $codeMatch = [regex]::Match($message, '^(DEVBRIDGE_[A-Z0-9_]+):')
    $errorCode = if ($codeMatch.Success) { $codeMatch.Groups[1].Value } else { 'DEVBRIDGE_PUBLISH_FAILED' }
    $retrySafe = $errorCode -in @(
        'DEVBRIDGE_DEPLOYMENT_DESTINATION_LOCKED',
        'DEVBRIDGE_DEPLOYMENT_DESTINATION_UNAVAILABLE',
        'DEVBRIDGE_DEPLOYMENT_TEMP_CLEANUP_FAILED')
    $nextAction = switch ($errorCode) {
        'DEVBRIDGE_DEPLOYMENT_DESTINATION_LOCKED' {
            'Release the owning destination-file lock, then retry the supported publisher.'
            break
        }
        'DEVBRIDGE_DEPLOYMENT_DESTINATION_UNAVAILABLE' {
            'Restore destination write access, then retry the supported publisher.'
            break
        }
        'DEVBRIDGE_DEPLOYMENT_ROLLBACK_FAILED' {
            'Stop and inspect the deployment directory before retrying; rollback was not proven.'
            break
        }
        default {
            'Inspect the structured error and repair the reported source or deployment condition before retrying.'
            break
        }
    }
    $failure = [ordered]@{
        schemaVersion = 'devbridge-dev-publish/v1'
        status = 'failed'
        success = $false
        dryRun = [bool]$DryRun
        errorCode = $errorCode
        error = $message
        coordinatorShutdown = $coordinatorShutdown
        recoveryAttempted = $coordinatorShutdown
        recoveryResult = if ($coordinatorShutdown) { 'shutdown-requested' } else { 'not-attempted' }
        retrySafe = $retrySafe
        manualInterventionRequired = $errorCode -eq 'DEVBRIDGE_DEPLOYMENT_ROLLBACK_FAILED'
        artifacts = @($records)
        nextAction = $nextAction
    }
}
finally {
    if (-not $script:MaintenanceEstablished) {
        try { Release-ManagedRimWorldMaintenance } catch { }
    }
    Release-DeploymentLock
    if (Test-Path -LiteralPath $stagingRoot -PathType Container) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$ifFailure = $failure
if ($null -ne $ifFailure) {
    $failureJson = $ifFailure | ConvertTo-Json -Depth 16
    if ($Json) {
        Write-Output $failureJson
    }
    else {
        Write-Error $failureJson
    }
    exit 4
}

$reportJson = $report | ConvertTo-Json -Depth 16
if ($Json) {
    Write-Output $reportJson
}
else {
    Write-Host 'DevBridge development publish'
    Write-Host ('  Classification: ' + $report.plan.changeClass)
    Write-Host ('  Built:           ' + ($(if ($report.built.Count) { $report.built -join ', ' } else { 'none' })))
    Write-Host ('  Deployed:        ' + ($(if ($report.deployed.Count) { $report.deployed -join ', ' } else { 'none' })))
    Write-Host ('  Required refresh: ' + $report.requiredRefresh)
    Write-Host ('  Deploy required:  ' + $report.deployRequired)
    if ($null -eq $report.rimWorldRestartRequired) {
        Write-Host '  RimWorld restart: unknown (BridgeTools live reload is not proven)'
    } else {
        Write-Host ('  RimWorld restart: ' + $report.rimWorldRestartRequired)
    }
    Write-Host "`nMachine-readable report:"
    Write-Output $reportJson
}
