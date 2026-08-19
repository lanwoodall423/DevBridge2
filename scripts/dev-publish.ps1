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

function Invoke-CoordinatorShutdown {
    param([Parameter(Mandatory = $true)][string]$Root)

    $coordinatorExe = Join-Path $Root 'Coordinator\DevBridge.Coordinator.exe'
    if (-not (Test-Path -LiteralPath $coordinatorExe -PathType Leaf)) {
        return $false
    }
    $null = & $coordinatorExe '--root' $Root 'coordinator' 'shutdown' '--json' 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Graceful coordinator shutdown failed for $Root with exit code $LASTEXITCODE."
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
        previousDestinationSha256 = $PreviousDestinationHash
        deployRequired = $DeployRequired
        deployPerformed = [bool]$DidDeploy
        reconciliationAction = $ReconciliationAction
        identityVerified = $IdentityVerified
        loadedStatus = $LoadedStatus
    })
}

$plan = Get-Plan
$records = [System.Collections.Generic.List[object]]::new()
$built = [System.Collections.Generic.List[string]]::new()
$deployed = [System.Collections.Generic.List[string]]::new()
$coordinatorShutdown = $false
$stagingRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    'DevBridge2-dev-publish-' + $PID + '-' + [guid]::NewGuid().ToString('N'))
$buildProperties = Get-BuildProperties
$productVersion = Get-AuthoritativeProductVersion

$report = $null
$failure = $null
try {
    New-Item -ItemType Directory -Force -Path $stagingRoot | Out-Null
    $coordinatorStaging = Join-Path $stagingRoot 'Coordinator'
    $modStaging = Join-Path $stagingRoot 'Mod'
    $bridgeToolsDestination = $null
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
                # Read the running coordinator's current bytes before asking it
                # to shut down; replace/delete access is checked after shutdown.
                $destinationHash = Get-FileSha256 $destination
                $different = $null -eq $destinationHash -or
                    -not [string]::Equals($sourceHash, $destinationHash, [StringComparison]::OrdinalIgnoreCase)
                if ($different) { $coordinatorDeployRequired = $true }
                [void]$coordinatorArtifacts.Add([ordered]@{
                    file = $file
                    source = $source
                    destination = $destination
                    sourceHash = $sourceHash
                    destinationHash = $destinationHash
                    different = $different
                })
            }
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
            coordinatorShutdown = $coordinatorShutdown
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
