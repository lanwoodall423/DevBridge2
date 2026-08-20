[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')]
    [string]$Project,

    [string]$DescriptorPath,
    [string]$CoordinatorRoot,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')]
    [string]$RuntimeSlot,
    [string]$DeploymentRoot,
    [string[]]$DevelopmentRoot,
    [string[]]$AdditionalDevelopmentRoot,
    [ValidatePattern('^lease-[0-9A-Fa-f]{32}$')]
    [string]$LeaseId,
    [ValidatePattern('^[A-Za-z0-9._:-]{1,64}$')]
    [string]$WorkflowId,
    [ValidatePattern('^[0-9a-fA-F]{64}$')]
    [string]$SourceFingerprint,
    [switch]$SkipRecipe,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration,
    [ValidateRange(30, 1800)]
    [int]$BuildTimeoutSeconds = 300,
    [ValidateRange(60, 1800)]
    [int]$CoordinatorTimeoutSeconds = 300,
    [switch]$Json
)

$ErrorActionPreference = 'Stop'
$scriptRoot = (Resolve-Path $PSScriptRoot).Path
$repoRoot = (Resolve-Path (Join-Path $scriptRoot '..')).Path
if ([string]::IsNullOrWhiteSpace($CoordinatorRoot)) { $CoordinatorRoot = $repoRoot }
if ([string]::IsNullOrWhiteSpace($DeploymentRoot)) { $DeploymentRoot = $repoRoot }
if ($null -eq $DevelopmentRoot -or $DevelopmentRoot.Count -eq 0) { $DevelopmentRoot = @($repoRoot) }

$coordinatorRoot = [IO.Path]::GetFullPath($CoordinatorRoot)
$deploymentRoot = [IO.Path]::GetFullPath($DeploymentRoot)
$developmentRoots = @(
    @($DevelopmentRoot) + @($AdditionalDevelopmentRoot) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        ForEach-Object { [IO.Path]::GetFullPath($_) }
)
$transactionId = [Guid]::NewGuid().ToString('N')
$sessionId = 'mod-test-' + $transactionId
$registrationId = 'mod-test-' + $transactionId
$descriptorPath = if ([string]::IsNullOrWhiteSpace($DescriptorPath)) {
    Join-Path $repoRoot ('DevelopmentProjects\' + $Project + '.json')
} else { [IO.Path]::GetFullPath($DescriptorPath) }
$transactionRoot = Join-Path ([IO.Path]::GetTempPath()) ('DevBridge2-mod-test-' + $transactionId)
$stagingRoot = Join-Path $transactionRoot 'staging'
$tracePath = Join-Path $transactionRoot 'transaction-trace.jsonl'
$artifactStatePath = Join-Path $coordinatorRoot 'Runtime\mod-development-artifact.json'

$script:Report = [ordered]@{
    schemaVersion = 'devbridge-mod-development/v1'
    transactionId = $transactionId
    project = $Project
    descriptor = $descriptorPath
    workflowId = $WorkflowId
    sourceFingerprint = $SourceFingerprint
    success = $false
    stage = 'preflight'
    nextAction = 'inspect-result'
    exitCode = 1
    build = $null
    deployment = $null
    runtime = [ordered]@{
        generation = 0
        generationBefore = $null
        generationAfter = $null
        leaseId = $LeaseId
        registrationId = $registrationId
        maintenanceReady = $false
        intentionallyInMaintenance = $false
        acceptedProfileFingerprint = $null
        requestedProjects = @()
    }
    recipe = $null
    artifactFreshness = [ordered]@{
        sourceFingerprint = $SourceFingerprint
        builtArtifactSha256 = $null
        deployedArtifactSha256 = $null
        deploymentDecision = $null
        generationBefore = $null
        generationAfter = $null
        generation = $null
        transactionId = $transactionId
        workflowId = $WorkflowId
        leaseId = $LeaseId
        loadedArtifactFreshnessProven = $false
        proof = $null
        errorCode = $null
    }
    cleanup = [ordered]@{
        registrationReleased = $false
        leaseReleased = $false
        deferred = $false
        error = $null
    }
    failure = $null
    runtimeArtifacts = @()
}
$script:FailureRaised = $false
$script:LeaseCreated = $false
$script:RegistrationCreated = $false
$script:MaintenanceEstablished = $false
$script:KeepOwnership = $false
$script:TracePath = $tracePath
$script:OldAgent = [Environment]::GetEnvironmentVariable('DEVBRIDGE_AGENT', 'Process')
$script:OldSession = [Environment]::GetEnvironmentVariable('DEVBRIDGE_SESSION', 'Process')
if ([string]::IsNullOrWhiteSpace($LeaseId)) {
    $env:DEVBRIDGE_AGENT = 'mod-test-' + $transactionId
    $env:DEVBRIDGE_SESSION = $sessionId
}

function Limit-Text {
    param([AllowNull()][string]$Text, [int]$Limit = 4096)
    if ([string]::IsNullOrEmpty($Text)) { return $null }
    $value = $Text.Trim()
    if ($value.Length -le $Limit) { return $value }
    return $value.Substring(0, $Limit) + "`n...[truncated]"
}

function Format-Command {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)
    return (($Arguments | ForEach-Object {
        $value = [string]$_
        if ($value -match '[\s"]') { '"' + $value.Replace('"', '\"') + '"' } else { $value }
    }) -join ' ')
}

function Write-TransactionTrace {
    param([Parameter(Mandatory = $true)][string]$Stage,
        [string]$Detail,
        [string]$Command)
    try {
        if (-not (Test-Path -LiteralPath $script:TracePath)) {
            New-Item -ItemType File -Force -Path $script:TracePath | Out-Null
        }
        $entry = [ordered]@{
            timestampUtc = [DateTime]::UtcNow.ToString('o')
            stage = $Stage
            detail = Limit-Text $Detail 1024
            command = Limit-Text $Command 1024
        }
        Add-Content -LiteralPath $script:TracePath -Value ($entry | ConvertTo-Json -Compress) -Encoding UTF8
    } catch {
        # Diagnostics must never change transaction behavior.
    }
}

function Test-PathWithin {
    param([Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Root)
    $candidate = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    return $candidate.Equals($rootPath, [StringComparison]::OrdinalIgnoreCase) -or
        $candidate.StartsWith($rootPath + '\', [StringComparison]::OrdinalIgnoreCase)
}

function Assert-NoReparsePath {
    param([Parameter(Mandatory = $true)][string]$Path)
    $current = [IO.Path]::GetFullPath($Path)
    while (-not [string]::IsNullOrWhiteSpace($current)) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "reparse-point path is not allowed: $current"
            }
        }
        $parentInfo = [IO.Directory]::GetParent($current)
        $parent = if ($null -eq $parentInfo) { $null } else { $parentInfo.FullName }
        if ([string]::IsNullOrWhiteSpace($parent) -or $parent -eq $current) { break }
        $current = $parent
    }
}

function Assert-Directory {
    param([Parameter(Mandatory = $true)][string]$Path, [string]$Name = 'directory')
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { throw "$Name does not exist: $Path" }
    Assert-NoReparsePath $Path
}

function Get-SafeRelativePath {
    param([Parameter(Mandatory = $true)][string]$Value, [Parameter(Mandatory = $true)][string]$Name)
    if ([string]::IsNullOrWhiteSpace($Value) -or [IO.Path]::IsPathRooted($Value) -or
        $Value.Contains(':')) { throw "$Name must be a non-rooted relative path" }
    $segments = $Value -split '[\\/]'
    if ($segments.Count -eq 0 -or $segments | Where-Object { $_ -in @('', '.', '..') }) {
        throw "$Name contains an empty or traversal path segment"
    }
    return ($segments -join [IO.Path]::DirectorySeparatorChar)
}

function Resolve-SourceProject {
    param([Parameter(Mandatory = $true)][string]$RelativePath)
    $safe = Get-SafeRelativePath $RelativePath 'sourceProject'
    if (-not $safe.EndsWith('.csproj', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'sourceProject must name a .csproj file'
    }
    $attempted = [System.Collections.Generic.List[string]]::new()
    foreach ($root in $developmentRoots) {
        Assert-Directory $root 'development root'
        $candidate = [IO.Path]::GetFullPath((Join-Path $root $safe))
        [void]$attempted.Add($candidate)
        if ((Test-PathWithin $candidate $root) -and (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            Assert-NoReparsePath $candidate
            return $candidate
        }
    }
    throw "sourceProject '$RelativePath' was not found below the configured development roots. Expected one of: $($attempted -join '; '). Pass -DevelopmentRoot <root> or -AdditionalDevelopmentRoot <root> for the repository that owns the .csproj."
}

function Resolve-DeploymentTarget {
    param([Parameter(Mandatory = $true)][string]$RelativePath)
    $safe = Get-SafeRelativePath $RelativePath 'deploymentTarget'
    Assert-Directory $deploymentRoot 'deployment root'
    $target = [IO.Path]::GetFullPath((Join-Path $deploymentRoot $safe))
    if (-not (Test-PathWithin $target $deploymentRoot)) { throw 'deploymentTarget escapes deployment root' }
    $forbiddenRoots = @(
        (Join-Path $coordinatorRoot 'Runtime'),
        (Join-Path $coordinatorRoot 'Coordinator'),
        (Join-Path $coordinatorRoot 'BridgeTools'),
        (Join-Path $coordinatorRoot 'artifacts')
    )
    foreach ($forbidden in $forbiddenRoots) {
        if (Test-PathWithin $target $forbidden) { throw "deploymentTarget is DevBridge state or control output: $target" }
    }
    $parentInfo = [IO.Directory]::GetParent($target)
    $parent = if ($null -eq $parentInfo) { $null } else { $parentInfo.FullName }
    Assert-Directory $parent 'deployment target parent'
    Assert-NoReparsePath $target
    if (Test-Path -LiteralPath $target -PathType Container) { throw "deployment target is a directory: $target" }
    return $target
}

function Read-Descriptor {
    if (-not (Test-Path -LiteralPath $descriptorPath -PathType Leaf)) { throw "descriptor not found: $descriptorPath" }
    Assert-NoReparsePath $descriptorPath
    if ((Get-Item -LiteralPath $descriptorPath).Length -gt 131072) { throw 'descriptor exceeds the 128 KiB bound' }
    try { $value = Get-Content -LiteralPath $descriptorPath -Raw | ConvertFrom-Json -Depth 16 }
    catch { throw "descriptor is not bounded valid JSON: $($_.Exception.Message)" }
    $allowed = @('schemaVersion', 'project', 'sourceProject', 'configuration', 'expectedAssembly',
        'deploymentTarget', 'testRecipe')
    foreach ($property in $value.PSObject.Properties.Name) {
        if ($property -notin $allowed) { throw "descriptor field is not allowed: $property" }
    }
    if ([string]$value.schemaVersion -ne 'devbridge-mod-development/v1') { throw 'descriptor schemaVersion is unsupported' }
    if ([string]$value.project -ne $Project) { throw 'descriptor project does not match -Project' }
    if ([string]$value.configuration -notin @('Debug', 'Release')) { throw 'descriptor configuration must be Debug or Release' }
    if (-not [string]::IsNullOrWhiteSpace($Configuration) -and [string]$value.configuration -ne $Configuration) { throw 'command configuration differs from the descriptor' }
    foreach ($field in @('sourceProject', 'expectedAssembly', 'deploymentTarget', 'testRecipe')) {
        if ([string]::IsNullOrWhiteSpace([string]$value.$field)) { throw "descriptor field is required: $field" }
    }
    if ([string]$value.testRecipe -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$') { throw 'testRecipe is not a bounded recipe ID' }
    $value | Add-Member -NotePropertyName ResolvedSource -NotePropertyValue (Resolve-SourceProject ([string]$value.sourceProject))
    $value | Add-Member -NotePropertyName SafeExpectedAssembly -NotePropertyValue (Get-SafeRelativePath ([string]$value.expectedAssembly) 'expectedAssembly')
    $value | Add-Member -NotePropertyName ResolvedTarget -NotePropertyValue (Resolve-DeploymentTarget ([string]$value.deploymentTarget))
    return $value
}

function Get-Hash {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Read-ArtifactState {
    if (-not (Test-Path -LiteralPath $artifactStatePath -PathType Leaf)) { return $null }
    try {
        if ((Get-Item -LiteralPath $artifactStatePath -Force).Length -gt 32768) { return $null }
        $state = Get-Content -LiteralPath $artifactStatePath -Raw | ConvertFrom-Json -Depth 8
        if ([string]$state.schemaVersion -ne 'devbridge-artifact-state/v1' -or
            [string]::IsNullOrWhiteSpace([string]$state.project) -or
            [string]::IsNullOrWhiteSpace([string]$state.deployedArtifactSha256) -or
            [int]$state.generation -lt 1) { return $null }
        return $state
    } catch {
        return $null
    }
}

function Write-ArtifactState {
    param([Parameter(Mandatory = $true)][int]$Generation,
        [Parameter(Mandatory = $true)][string]$DeployedHash)
    $parentInfo = [IO.Directory]::GetParent($artifactStatePath)
    $parent = if ($null -eq $parentInfo) { $null } else { $parentInfo.FullName }
    Assert-Directory $parent 'artifact-state parent'
    Assert-NoReparsePath $artifactStatePath
    $temporary = Join-Path $parent ('.devbridge-artifact-' + $transactionId + '.tmp')
    $state = [ordered]@{
        schemaVersion = 'devbridge-artifact-state/v1'
        project = $Project
        deploymentTarget = [IO.Path]::GetFullPath($script:Report.deployment.targetPath)
        deployedArtifactSha256 = $DeployedHash
        generation = $Generation
        transactionId = $transactionId
        sourceFingerprint = $SourceFingerprint
        workflowId = $WorkflowId
        updatedUtc = [DateTime]::UtcNow.ToString('o')
    }
    try {
        $json = $state | ConvertTo-Json -Depth 8 -Compress
        [IO.File]::WriteAllText($temporary, $json, [Text.UTF8Encoding]::new($false))
        Assert-NoReparsePath $temporary
        [IO.File]::Move($temporary, $artifactStatePath, $true)
    } finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue }
    }
}

function Get-ArtifactPaths {
    $paths = @(
        $script:TracePath,
        $coordinatorRoot,
        (Join-Path $coordinatorRoot 'Runtime'),
        (Join-Path $coordinatorRoot 'Runtime\state.json'),
        (Join-Path $coordinatorRoot 'Runtime\readiness.json'),
        (Join-Path $coordinatorRoot 'Runtime\coordinator-events.jsonl'),
        $artifactStatePath,
        (Join-Path $coordinatorRoot 'Player.log'),
        $script:Report.deployment.targetPath
    )
    return @($paths | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        ForEach-Object { [IO.Path]::GetFullPath($_) } | Select-Object -Unique)
}

function Set-Failure {
    param([Parameter(Mandatory = $true)][string]$Stage,
        [Parameter(Mandatory = $true)][string]$NextAction,
        [string]$ErrorCode, [string]$Message, [string]$Command,
        [int]$ExitCode = 1, $Output, [bool]$KeepOwnership = $false)
    $script:Report.stage = $Stage
    $script:Report.nextAction = $NextAction
    $script:Report.exitCode = $ExitCode
    $script:Report.success = $false
    $script:Report.failure = [ordered]@{
        stage = $Stage
        command = $Command
        exitCode = $ExitCode
        errorCode = $ErrorCode
        message = Limit-Text $Message
        output = Limit-Text ([string]$Output)
    }
    if ($null -ne $script:Report.artifactFreshness) {
        $script:Report.artifactFreshness.errorCode = $ErrorCode
        $script:Report.artifactFreshness.loadedArtifactFreshnessProven = $false
    }
    $script:KeepOwnership = $KeepOwnership
    $script:FailureRaised = $true
    throw [InvalidOperationException]::new("$Stage failed: $Message")
}

function Read-JsonLine {
    param([string[]]$Lines)
    for ($index = $Lines.Count - 1; $index -ge 0; $index--) {
        $line = ([string]$Lines[$index]).Trim()
        if (-not $line.StartsWith('{')) { continue }
        try { return $line | ConvertFrom-Json -Depth 32 } catch { }
    }
    return $null
}

function Invoke-BridgeJson {
    param([Parameter(Mandatory = $true)][string[]]$CommandArguments)
    $wrapper = Join-Path $repoRoot 'DevBridge.cmd'
    $rootArguments = @('--root', $coordinatorRoot)
    if (-not [string]::IsNullOrWhiteSpace($RuntimeSlot)) {
        $rootArguments += @('--runtime-slot', $RuntimeSlot)
    }
    $arguments = $rootArguments + $CommandArguments + @('--json')
    $commandText = Format-Command (@('DevBridge.cmd') + $arguments)
    Write-TransactionTrace 'bridge-start' ("arguments=" + ($CommandArguments -join ' ')) $commandText
    $outputRoot = Join-Path $transactionRoot ('bridge-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
    $stdoutPath = Join-Path $outputRoot 'stdout.txt'
    $stderrPath = Join-Path $outputRoot 'stderr.txt'
    $process = $null
    $timedOut = $false
    try {
        $startParameters = @{
            FilePath = $wrapper
            ArgumentList = $arguments
            WorkingDirectory = $repoRoot
            WindowStyle = 'Hidden'
            RedirectStandardOutput = $stdoutPath
            RedirectStandardError = $stderrPath
            PassThru = $true
        }
        $process = Start-Process @startParameters
        $coordinatorTimeoutMilliseconds = [Math]::Min([int]::MaxValue,
            [Math]::Max(60, $CoordinatorTimeoutSeconds) * 1000)
        if (-not $process.WaitForExit($coordinatorTimeoutMilliseconds)) {
            $timedOut = $true
            try { $process.Kill() } catch { }
            try { $process.WaitForExit(5000) } catch { }
        }
        $exitCode = if ($timedOut) { 124 } else { [int]$process.ExitCode }
    } catch {
        $exitCode = 1
        $timedOut = $false
        $stderrPath = $null
        $stdoutPath = $null
        $startError = $_.Exception.Message
    } finally {
        if ($null -ne $process) { $process.Dispose() }
    }
    $stdout = if ($stdoutPath -and (Test-Path -LiteralPath $stdoutPath -PathType Leaf)) {
        Get-Content -LiteralPath $stdoutPath -Raw -ErrorAction SilentlyContinue
    } else { $null }
    $stderr = if ($stderrPath -and (Test-Path -LiteralPath $stderrPath -PathType Leaf)) {
        Get-Content -LiteralPath $stderrPath -Raw -ErrorAction SilentlyContinue
    } else { $null }
    if ($startError) { $stderr = $startError }
    $rawOutput = ((@($stdout, $stderr) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join "`n")
    $lines = @($rawOutput -split "`r?`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $response = Read-JsonLine $lines
    $output = Limit-Text $rawOutput
    if ($timedOut) { $output = Limit-Text ($output + "`nbridge wrapper exceeded its bounded command timeout") }
    Write-TransactionTrace 'bridge-complete' ("exitCode=$exitCode response=$($null -ne $response)") $commandText
    return [pscustomobject]@{
        Arguments = $arguments
        Command = Format-Command (@('DevBridge.cmd') + $arguments)
        ExitCode = $exitCode
        Response = $response
        Output = $output
    }
}

function Save-BridgeResult {
    param([Parameter(Mandatory = $true)][string]$Name, [Parameter(Mandatory = $true)]$Result)
    $record = [ordered]@{
        name = $Name
        command = $Result.Command
        exitCode = $Result.ExitCode
        success = ($Result.ExitCode -eq 0 -and $null -ne $Result.Response -and $Result.Response.success -ne $false)
        errorCode = if ($null -ne $Result.Response) { [string]$Result.Response.errorCode } else { $null }
        error = if ($null -ne $Result.Response) { Limit-Text ([string]$Result.Response.error) } else { $null }
        nextAction = if ($null -ne $Result.Response) { Limit-Text ([string]$Result.Response.nextAction) } else { $null }
        output = $Result.Output
        generation = if ($null -ne $Result.Response) { [int]$Result.Response.generation } else { 0 }
        state = if ($null -ne $Result.Response) { [string]$Result.Response.state } else { $null }
        maintenanceReady = if ($null -ne $Result.Response) { [bool]$Result.Response.maintenanceReady } else { $false }
    }
    if (-not $script:Report.Contains('commands')) { $script:Report.commands = [ordered]@{} }
    $script:Report.commands[$Name] = $record
    return $record
}

function Require-BridgeSuccess {
    param([Parameter(Mandatory = $true)][string]$Stage,
        [Parameter(Mandatory = $true)][string]$NextAction,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)]$Result,
        [bool]$KeepOwnership = $false)
    $record = Save-BridgeResult $Name $Result
    $response = $Result.Response
    if (-not $record.success) {
        Set-Failure $Stage $NextAction ([string]$record.errorCode) ([string]$record.error) $Result.Command $Result.ExitCode $Result.Output $KeepOwnership
    }
    return $response
}

function Invoke-BoundedBuild {
    param([Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][int]$TimeoutSeconds)
    $dotnetCommand = @(Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1)
    if ($dotnetCommand.Count -eq 0) { throw 'dotnet executable could not be located' }
    $dotnet = [string]$dotnetCommand[0].Source
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $dotnet
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $Arguments) { [void]$startInfo.ArgumentList.Add([string]$argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) { throw 'dotnet build process could not be started' }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $completed = $process.WaitForExit($TimeoutSeconds * 1000)
    $timedOut = -not $completed
    if ($timedOut) {
        try { $process.Kill($true) } catch { }
        $process.WaitForExit()
    }
    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    $exitCode = if ($timedOut) { 124 } else { $process.ExitCode }
    $process.Dispose()
    return [pscustomobject]@{
        ExitCode = $exitCode
        TimedOut = $timedOut
        Output = Limit-Text ((@($stdout, $stderr) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join "`n")
    }
}

function Copy-AtomicFile {
    param([Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Target)
    $parentInfo = [IO.Directory]::GetParent($Target)
    $parent = if ($null -eq $parentInfo) { $null } else { $parentInfo.FullName }
    $temporary = Join-Path $parent ('.devbridge-' + $transactionId + '.tmp')
    try {
        Assert-NoReparsePath $parent
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
        $sourceStream = [IO.File]::OpenRead($Source)
        $targetStream = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try {
            $sourceStream.CopyTo($targetStream)
            $targetStream.Flush($true)
        } finally {
            $targetStream.Dispose()
            $sourceStream.Dispose()
        }
        Assert-NoReparsePath $temporary
        [IO.File]::Move($temporary, $Target, $true)
    } finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue }
    }
}

function Release-OwnedResources {
    if ($script:KeepOwnership) {
        $script:Report.cleanup.deferred = $true
        return
    }
    if ($script:LeaseCreated) {
        try {
            $end = Invoke-BridgeJson @('test', 'end', [string]$script:Report.runtime.leaseId)
            $record = Save-BridgeResult 'test-end' $end
            if ($record.success) { $script:Report.cleanup.leaseReleased = $true; $script:LeaseCreated = $false }
            else { $script:Report.cleanup.error = Limit-Text $record.error; $script:Report.success = $false; $script:Report.stage = 'cleanup'; $script:Report.nextAction = 'end-lease'; return }
        } catch { $script:Report.cleanup.error = Limit-Text $_.Exception.Message; $script:Report.success = $false; $script:Report.stage = 'cleanup'; $script:Report.nextAction = 'end-lease'; return }
    }
    if ($script:RegistrationCreated) {
        try {
            $release = Invoke-BridgeJson @('project', 'release', $registrationId)
            $record = Save-BridgeResult 'project-release' $release
            if ($record.success) { $script:Report.cleanup.registrationReleased = $true }
            else { $script:Report.cleanup.error = Limit-Text $record.error; $script:Report.success = $false; $script:Report.stage = 'cleanup'; $script:Report.nextAction = 'release-registration' }
        } catch { $script:Report.cleanup.error = Limit-Text $_.Exception.Message; $script:Report.success = $false; $script:Report.stage = 'cleanup'; $script:Report.nextAction = 'release-registration' }
    }
}

try {
    New-Item -ItemType Directory -Force -Path $transactionRoot, $stagingRoot | Out-Null
    Write-TransactionTrace 'preflight' 'descriptor and coordinator planning started'
    $descriptor = Read-Descriptor
    $script:Report.descriptor = [IO.Path]::GetFullPath($descriptorPath)
    $script:Report.project = [string]$descriptor.project

    $show = Invoke-BridgeJson @('test', 'recipe', 'show', [string]$descriptor.testRecipe)
    Write-TransactionTrace 'planning' 'recipe show completed'
    $recipeInfo = Require-BridgeSuccess 'planning' 'fix-recipe-descriptor' 'recipe-show' $show
    $recipeProjects = @($recipeInfo.recipe.projects | ForEach-Object { [string]$_ })
    if ($recipeProjects -notcontains $Project) {
        Set-Failure 'planning' 'use-a-recipe-for-the-declared-project' 'DEVELOPMENT_RECIPE_PROJECT_MISMATCH' "recipe '$($descriptor.testRecipe)' does not request project '$Project'" $show.Command $show.ExitCode $show.Output $false
    }

    $projectPlanResult = Invoke-BridgeJson @('project', 'resolve', $Project)
    Write-TransactionTrace 'planning' 'project resolve completed'
    $projectPlan = Require-BridgeSuccess 'planning' 'fix-project-resolution' 'project-resolve' $projectPlanResult
    $recipePlanResult = Invoke-BridgeJson @('test', 'recipe', 'plan', [string]$descriptor.testRecipe)
    Write-TransactionTrace 'planning' 'recipe plan completed'
    $recipePlan = Require-BridgeSuccess 'planning' 'fix-recipe-plan' 'recipe-plan-before-build' $recipePlanResult
    $script:Report.planning = [ordered]@{
        projectProfileFingerprint = [string]$projectPlan.projectResolution.profileFingerprint
        recipeProfileFingerprint = [string]$recipePlan.profileFingerprint
        recipeAlreadySatisfiedBeforeBuild = [bool]$recipePlan.alreadySatisfied
        projectResolve = $projectPlanResult.Output
        recipePlan = $recipePlanResult.Output
    }

    Assert-Directory $deploymentRoot 'deployment root'
    $expectedArtifact = [IO.Path]::GetFullPath((Join-Path $stagingRoot $descriptor.SafeExpectedAssembly))
    if (-not (Test-PathWithin $expectedArtifact $stagingRoot)) { throw 'expectedAssembly escapes staging root' }
    $script:Report.stage = 'build'
    $buildArguments = @('build', $descriptor.ResolvedSource, '--configuration', [string]$descriptor.configuration,
        '--output', $stagingRoot, '--nologo',
        ('-p:IntermediateOutputPath=' + (Join-Path $transactionRoot 'obj\')),
        ('-p:MSBuildProjectExtensionsPath=' + (Join-Path $transactionRoot 'obj\')))
    $buildResult = Invoke-BoundedBuild $buildArguments $BuildTimeoutSeconds
    Write-TransactionTrace 'build' ("exitCode=$($buildResult.ExitCode) timedOut=$($buildResult.TimedOut)") (Format-Command (@('dotnet') + $buildArguments))
    $buildExit = [int]$buildResult.ExitCode
    $script:Report.build = [ordered]@{
        command = Format-Command (@('dotnet') + $buildArguments)
        exitCode = $buildExit
        output = $buildResult.Output
        stagingPath = $stagingRoot
        sourceProject = $descriptor.ResolvedSource
        timedOut = [bool]$buildResult.TimedOut
    }
    if ($buildExit -ne 0) {
        $buildCode = if ($buildResult.TimedOut) { 'DEVELOPMENT_BUILD_TIMEOUT' } else { 'DEVELOPMENT_BUILD_FAILED' }
        $buildMessage = if ($buildResult.TimedOut) { 'the declared project build exceeded its bounded timeout' } else { 'the declared project build failed' }
        Set-Failure 'build' 'fix-build' $buildCode $buildMessage $script:Report.build.command $buildExit $script:Report.build.output $false
    }
    if (-not (Test-Path -LiteralPath $expectedArtifact -PathType Leaf)) {
        Set-Failure 'build' 'fix-build-artifact' 'DEVELOPMENT_ARTIFACT_MISSING' "expected build artifact was not produced: $($descriptor.SafeExpectedAssembly)" $script:Report.build.command 1 $script:Report.build.output $false
    }
    Assert-NoReparsePath $expectedArtifact
    $builtHash = Get-Hash $expectedArtifact
    $deployedBefore = Get-Hash $descriptor.ResolvedTarget
    $script:Report.build.builtSha256 = $builtHash
    $script:Report.deployment = [ordered]@{
        targetPath = $descriptor.ResolvedTarget
        deployedSha256Before = $deployedBefore
        builtSha256 = $builtHash
        stagedSha256 = $builtHash
        changed = ($builtHash -ne $deployedBefore)
        atomicReplacement = $false
        deployedSha256After = $deployedBefore
        stagingPath = $stagingRoot
    }
    $script:Report.artifactFreshness.builtArtifactSha256 = $builtHash
    $script:Report.artifactFreshness.deployedArtifactSha256 = $deployedBefore
    $script:Report.artifactFreshness.deploymentDecision = if ($script:Report.deployment.changed) { 'deployed' } else { 'unchanged' }
    $artifactState = Read-ArtifactState

    $statusBefore = Invoke-BridgeJson @('status')
    $statusBeforeResponse = Require-BridgeSuccess 'planning' 'inspect-runtime-status' 'status-before-registration' $statusBefore
    $generationBefore = [int]$statusBeforeResponse.generation
    $script:Report.runtime.generationBefore = $generationBefore
    $script:Report.artifactFreshness.generationBefore = $generationBefore
    $profileIncludesProject = @($statusBeforeResponse.requestedProjects | ForEach-Object { [string]$_ }) -contains $Project

    # A READY generation that does not include the requested alias cannot grant
    # a lease after registration. Acquire the current lease first in that one
    # case, then queue the owned registration before stopping it.
    $leaseBeforeRegistration = (-not $profileIncludesProject -or [string]$statusBeforeResponse.state -ne 'READY')
    if (-not [string]::IsNullOrWhiteSpace($LeaseId)) {
        $script:Report.runtime.leaseId = $LeaseId
    } elseif ($leaseBeforeRegistration) {
        $begin = Invoke-BridgeJson @('test', 'begin')
        $beginResponse = Require-BridgeSuccess 'lease' 'resolve-lease-contention' 'test-begin-before-registration' $begin
        $script:Report.runtime.leaseId = [string]$beginResponse.leaseId
        if ([string]::IsNullOrWhiteSpace($script:Report.runtime.leaseId)) {
            Set-Failure 'lease' 'inspect-runtime-status' 'DEVELOPMENT_LEASE_ID_MISSING' 'test begin did not return a full lease ID' $begin.Command $begin.ExitCode $begin.Output $false
        }
        $script:LeaseCreated = $true
    }

    $register = Invoke-BridgeJson @('project', 'register', $Project, '--id', $registrationId)
    Require-BridgeSuccess 'registration' 'resolve-registration-conflict' 'project-register' $register | Out-Null
    $script:RegistrationCreated = $true

    if ([string]::IsNullOrWhiteSpace($script:Report.runtime.leaseId)) {
        $begin = Invoke-BridgeJson @('test', 'begin')
        $beginResponse = Require-BridgeSuccess 'lease' 'resolve-lease-contention' 'test-begin' $begin
        $script:Report.runtime.leaseId = [string]$beginResponse.leaseId
        if ([string]::IsNullOrWhiteSpace($script:Report.runtime.leaseId)) {
            Set-Failure 'lease' 'inspect-runtime-status' 'DEVELOPMENT_LEASE_ID_MISSING' 'test begin did not return a full lease ID' $begin.Command $begin.ExitCode $begin.Output $false
        }
        $script:LeaseCreated = $true
    }
    if ([string]$script:Report.runtime.leaseId -notmatch '^lease-[0-9A-Fa-f]{32}$') {
        Set-Failure 'lease' 'inspect-runtime-status' 'DEVELOPMENT_LEASE_INVALID' 'the coordinator returned an invalid lease capability ID' 'test begin' 4 $script:Report.runtime.leaseId $false
    }

    $postPlanResult = Invoke-BridgeJson @('test', 'recipe', 'plan', [string]$descriptor.testRecipe)
    $postPlan = Require-BridgeSuccess 'planning' 'fix-recipe-plan' 'recipe-plan-after-registration' $postPlanResult
    $artifactStateMatches = $null -ne $artifactState -and
        [string]$artifactState.project -eq $Project -and
        [string]$artifactState.deployedArtifactSha256 -eq $builtHash -and
        [int]$artifactState.generation -eq $generationBefore
    $noOp = (-not [bool]$script:Report.deployment.changed) -and
        [bool]$postPlan.alreadySatisfied -and
        $artifactStateMatches
    if (-not $noOp) {
        $renew = Invoke-BridgeJson @('test', 'renew', [string]$script:Report.runtime.leaseId)
        Require-BridgeSuccess 'lease' 'renew-or-end-lease' 'test-renew-before-stop' $renew | Out-Null
        $stop = Invoke-BridgeJson @('stop', [string]$script:Report.runtime.leaseId)
        $stopResponse = Require-BridgeSuccess 'stop' 'inspect-maintenance-evidence' 'stop' $stop $true
        if (-not [bool]$stopResponse.maintenanceReady -or [string]$stopResponse.gameState -ne 'STOPPED') {
            Set-Failure 'stop' 'inspect-maintenance-evidence' 'DEVELOPMENT_MAINTENANCE_NOT_CONFIRMED' 'stop did not return authoritative maintenanceReady=true and STOPPED evidence' $stop.Command $stop.ExitCode $stop.Output $true
        }
        $script:MaintenanceEstablished = $true
        $script:Report.runtime.maintenanceReady = $true
        $script:Report.runtime.intentionallyInMaintenance = $true

        if ($script:Report.deployment.changed) {
            try {
                Copy-AtomicFile $expectedArtifact $descriptor.ResolvedTarget
            } catch {
                Set-Failure 'deployment' 'repair-deployment-then-ensure-ready' 'DEVELOPMENT_DEPLOYMENT_FAILED' $_.Exception.Message 'atomic deployment' 4 $null $true
            }
        }
        $deployedAfter = Get-Hash $descriptor.ResolvedTarget
        $script:Report.deployment.deployedSha256After = $deployedAfter
        $script:Report.deployment.atomicReplacement = [bool]$script:Report.deployment.changed
        if ($deployedAfter -ne $builtHash) {
            Set-Failure 'deployment' 'repair-deployment-then-ensure-ready' 'DEVELOPMENT_DEPLOYMENT_HASH_MISMATCH' 'deployed artifact hash does not match the staged build' 'atomic deployment' 4 $deployedAfter $true
        }

        $renew = Invoke-BridgeJson @('test', 'renew', [string]$script:Report.runtime.leaseId)
        Require-BridgeSuccess 'lease' 'renew-or-end-lease' 'test-renew-before-ensure-ready' $renew | Out-Null
        $ensure = Invoke-BridgeJson @('ensure-ready', [string]$script:Report.runtime.leaseId)
        Require-BridgeSuccess 'ensure-ready' 'reconnect-and-wait-ready' 'ensure-ready' $ensure $true | Out-Null
        $ready = Invoke-BridgeJson @('wait-ready')
        $readyResponse = Require-BridgeSuccess 'ensure-ready' 'reconnect-and-wait-ready' 'wait-ready' $ready $true
        $script:Report.runtime.maintenanceReady = [bool]$readyResponse.maintenanceReady
        $script:Report.runtime.intentionallyInMaintenance = $false
        $script:MaintenanceEstablished = $false
        $expectedProjects = @($recipeProjects + $Project | Select-Object -Unique)
        $actualProjects = @($readyResponse.requestedProjects | ForEach-Object { [string]$_ })
        if ([string]$readyResponse.state -ne 'READY' -or ($expectedProjects | Where-Object { $_ -notin $actualProjects }).Count -gt 0) {
            Set-Failure 'ensure-ready' 'reconnect-and-inspect-generation' 'DEVELOPMENT_GENERATION_PROFILE_MISMATCH' 'accepted generation is not READY with the intended project profile' $ready.Command $ready.ExitCode $ready.Output $true
        }
        $generationAfter = [int]$readyResponse.generation
        if ($generationAfter -le $generationBefore) {
            Set-Failure 'ensure-ready' 'reconnect-and-inspect-generation' 'DEVELOPMENT_GENERATION_MISMATCH' 'deployment did not establish a newer accepted generation' $ready.Command $ready.ExitCode $ready.Output $true
        }
    } else {
        $deployedAfter = Get-Hash $descriptor.ResolvedTarget
        $script:Report.deployment.deployedSha256After = $deployedAfter
        if ($deployedAfter -ne $builtHash) {
            Set-Failure 'freshness' 'repair-deployment-then-ensure-ready' 'DEVELOPMENT_DEPLOYED_ARTIFACT_CHANGED' 'the deployed artifact changed after the byte-identical fast path check' 'artifact hash verification' 4 $deployedAfter $false
        }
        $generationAfter = $generationBefore
    }

    $script:Report.runtime.generationAfter = $generationAfter
    $script:Report.runtime.generation = $generationAfter
    $script:Report.artifactFreshness.deployedArtifactSha256 = $script:Report.deployment.deployedSha256After
    $script:Report.artifactFreshness.generationAfter = $generationAfter
    $script:Report.artifactFreshness.generation = $generationAfter
    if ($script:Report.deployment.changed -or $generationAfter -gt $generationBefore) {
        $script:Report.artifactFreshness.loadedArtifactFreshnessProven = $true
        $script:Report.artifactFreshness.proof = 'deployment-hash-plus-new-owned-generation'
    } elseif ($artifactStateMatches) {
        $script:Report.artifactFreshness.loadedArtifactFreshnessProven = $true
        $script:Report.artifactFreshness.proof = 'identical-deployment-hash-plus-owned-generation-state'
    } else {
        Set-Failure 'freshness' 'rebuild-or-establish-artifact-state' 'DEVELOPMENT_ARTIFACT_FRESHNESS_UNKNOWN' 'the current generation has no matching DevBridge artifact state evidence' 'artifact freshness proof' 4 $null $false
    }
    Write-ArtifactState $generationAfter ([string]$script:Report.deployment.deployedSha256After)

    if (-not $SkipRecipe) {
        $renew = Invoke-BridgeJson @('test', 'renew', [string]$script:Report.runtime.leaseId)
        Require-BridgeSuccess 'lease' 'renew-or-end-lease' 'test-renew-before-recipe' $renew | Out-Null
        $recipeArguments = @('test', 'recipe', 'run', [string]$descriptor.testRecipe, '--lease', [string]$script:Report.runtime.leaseId)
        if (-not [string]::IsNullOrWhiteSpace($WorkflowId)) {
            $recipeArguments += @('--workflow-id', $WorkflowId)
        }
        $recipeRun = Invoke-BridgeJson $recipeArguments
        $recipeResponse = Require-BridgeSuccess 'recipe' 'inspect-recipe-evidence' 'recipe-run' $recipeRun $false
        $script:Report.recipe = [ordered]@{
            id = [string]$descriptor.testRecipe
            success = [bool]$recipeResponse.success
            generation = [int]$recipeResponse.generation
            leaseId = [string]$recipeResponse.leaseId
            runId = [string]$recipeResponse.runId
            workflowId = [string]$recipeResponse.workflowId
            operationIds = @($recipeResponse.operations | ForEach-Object { [string]$_.operationId } | Where-Object { $_ }) | Select-Object -First 8
            failureFingerprint = [string]$recipeResponse.failureFingerprint
            evidence = [string]$recipeResponse.evidence
            finalNextAction = Limit-Text ([string]$recipeResponse.finalNextAction)
            output = $recipeRun.Output
        }
        $script:Report.runtime.acceptedProfileFingerprint = [string]$recipeResponse.profileFingerprint
        $script:Report.runtime.requestedProjects = @($recipeResponse.requestedProjects)
    }
    $script:Report.artifactFreshness.transactionId = $transactionId
    $script:Report.artifactFreshness.workflowId = $WorkflowId
    $script:Report.artifactFreshness.leaseId = $script:Report.runtime.leaseId
    $script:Report.success = $true
    $script:Report.stage = 'complete'
    $script:Report.nextAction = 'safe-next-action'
    $script:Report.exitCode = 0
}
catch {
    if (-not $script:FailureRaised) {
        $script:Report.stage = if ($script:MaintenanceEstablished) { 'deployment' } else { $script:Report.stage }
        $script:Report.nextAction = if ($script:MaintenanceEstablished) { 'inspect-maintenance-evidence' } else { 'inspect-result' }
        $script:Report.exitCode = 1
        $script:Report.failure = [ordered]@{
            stage = $script:Report.stage
            command = 'mod-test.ps1'
            exitCode = 1
            errorCode = 'DEVELOPMENT_TRANSACTION_FAILED'
            message = Limit-Text $_.Exception.Message
            output = Limit-Text $_.ScriptStackTrace
        }
        $script:Report.artifactFreshness.errorCode = 'DEVELOPMENT_TRANSACTION_FAILED'
        $script:Report.artifactFreshness.loadedArtifactFreshnessProven = $false
        $script:KeepOwnership = $script:MaintenanceEstablished
    }
}
finally {
    if ($script:Report.success -or (-not $script:KeepOwnership -and -not $script:MaintenanceEstablished)) {
        Release-OwnedResources
    } else {
        $script:Report.cleanup.deferred = $true
    }
    $script:Report.runtimeArtifacts = @(Get-ArtifactPaths)
    [Environment]::SetEnvironmentVariable('DEVBRIDGE_AGENT', $script:OldAgent, 'Process')
    [Environment]::SetEnvironmentVariable('DEVBRIDGE_SESSION', $script:OldSession, 'Process')
}

function Get-CompactJsonReport {
    $build = if ($null -eq $script:Report.build) {
        $null
    } else {
        [ordered]@{
            exitCode = [int]$script:Report.build.exitCode
            timedOut = [bool]$script:Report.build.timedOut
            builtSha256 = [string]$script:Report.build.builtSha256
        }
    }
    $deployment = if ($null -eq $script:Report.deployment) {
        $null
    } else {
        [ordered]@{
            changed = [bool]$script:Report.deployment.changed
            atomicReplacement = [bool]$script:Report.deployment.atomicReplacement
            builtSha256 = [string]$script:Report.deployment.builtSha256
            stagedSha256 = [string]$script:Report.deployment.stagedSha256
            deployedSha256Before = [string]$script:Report.deployment.deployedSha256Before
            deployedSha256After = [string]$script:Report.deployment.deployedSha256After
        }
    }
    $runtime = [ordered]@{
        generation = [int]$script:Report.runtime.generation
        generationBefore = $script:Report.runtime.generationBefore
        generationAfter = $script:Report.runtime.generationAfter
        leaseId = $script:Report.runtime.leaseId
        registrationId = $script:Report.runtime.registrationId
        maintenanceReady = [bool]$script:Report.runtime.maintenanceReady
        requestedProjects = @($script:Report.runtime.requestedProjects | Select-Object -First 8)
    }
    $recipe = if ($null -eq $script:Report.recipe) {
        $null
    } else {
        [ordered]@{
            id = [string]$script:Report.recipe.id
            success = [bool]$script:Report.recipe.success
            generation = [int]$script:Report.recipe.generation
            runId = [string]$script:Report.recipe.runId
            workflowId = [string]$script:Report.recipe.workflowId
            operationIds = @($script:Report.recipe.operationIds | Select-Object -First 8)
            failureFingerprint = [string]$script:Report.recipe.failureFingerprint
        }
    }
    $failure = if ($null -eq $script:Report.failure) {
        $null
    } else {
        [ordered]@{
            stage = [string]$script:Report.failure.stage
            errorCode = [string]$script:Report.failure.errorCode
            message = Limit-Text ([string]$script:Report.failure.message) 1024
        }
    }
    return [ordered]@{
        schemaVersion = $script:Report.schemaVersion
        transactionId = $script:Report.transactionId
        project = $script:Report.project
        workflowId = $script:Report.workflowId
        sourceFingerprint = $script:Report.sourceFingerprint
        success = [bool]$script:Report.success
        stage = $script:Report.stage
        nextAction = $script:Report.nextAction
        exitCode = [int]$script:Report.exitCode
        build = $build
        deployment = $deployment
        runtime = $runtime
        artifactFreshness = $script:Report.artifactFreshness
        recipe = $recipe
        failure = $failure
        cleanup = [ordered]@{
            registrationReleased = [bool]$script:Report.cleanup.registrationReleased
            leaseReleased = [bool]$script:Report.cleanup.leaseReleased
            deferred = [bool]$script:Report.cleanup.deferred
        }
    }
}

if ($Json) {
    Get-CompactJsonReport | ConvertTo-Json -Depth 20 -Compress
} else {
    if ($script:Report.success) {
        Write-Output ("PASS mod-test project={0} generation={1} builtSha256={2} deployedSha256={3}" -f
            $Project, $script:Report.runtime.generation, $script:Report.build.builtSha256,
            $script:Report.deployment.deployedSha256After)
    } else {
        Write-Error ("FAIL mod-test stage={0} nextAction={1}: {2}" -f $script:Report.stage,
            $script:Report.nextAction, $script:Report.failure.message)
    }
}
exit ([int]$script:Report.exitCode)
