[CmdletBinding()]
param(
    [string]$DevBridgeRoot,
    [string]$SourceRepositoryRoot,
    [string]$RimWorldRoot,
    [Alias('RimTestRoot')]
    [string]$RimLiaisonRoot,
    [Alias('RimTestPath')]
    [string]$RimLiaisonPath,
    [string]$RimErrorPath,
    [string]$RimErrorStorePath,
    [string]$RimErrorLogPath,
    [string]$FixtureDeploymentRoot,
    [string]$CompatibilityPath,
    [string]$ReportPath,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')]
    [string]$Project = 'frontier',
    [string]$DescriptorPath,
    [ValidateRange(60, 3600)]
    [int]$TimeoutSeconds = 900,
    [switch]$Plan,
    [switch]$Json,
    [switch]$AllowUiSkip
)

$ErrorActionPreference = 'Stop'
$script:MaxOutputChars = 32768
$script:MaxChildOutputChars = 65536
$script:SmokeStartedUtc = [DateTime]::UtcNow
$script:CoreSuccess = $false
$script:PlanOnly = [bool]$Plan
$script:LeaseId = $null
$script:RegistrationId = $null
$script:TransactionReport = $null
$script:FixtureModInfo = $null
$script:FixtureDeploymentRoot = $null
$script:TempFiles = [System.Collections.Generic.List[string]]::new()

function Resolve-FullPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    return [IO.Path]::GetFullPath($Path)
}

$scriptRoot = (Resolve-Path $PSScriptRoot).Path
$defaultDevBridgeRoot = (Resolve-Path (Join-Path $scriptRoot '..')).Path
if ([string]::IsNullOrWhiteSpace($DevBridgeRoot)) {
    $DevBridgeRoot = $defaultDevBridgeRoot
}
$DevBridgeRoot = Resolve-FullPath $DevBridgeRoot
if ([string]::IsNullOrWhiteSpace($SourceRepositoryRoot)) {
    $SourceRepositoryRoot = $DevBridgeRoot
}
$SourceRepositoryRoot = Resolve-FullPath $SourceRepositoryRoot

if ([string]::IsNullOrWhiteSpace($RimWorldRoot)) {
    $RimWorldRoot = [Environment]::GetEnvironmentVariable('RIMWORLD_ROOT')
}
if ([string]::IsNullOrWhiteSpace($RimWorldRoot)) {
    $RimWorldRoot = Split-Path (Split-Path $DevBridgeRoot -Parent) -Parent
}
$RimWorldRoot = Resolve-FullPath $RimWorldRoot

if ([string]::IsNullOrWhiteSpace($RimLiaisonRoot)) {
    $RimLiaisonRoot = [Environment]::GetEnvironmentVariable('RIMLIAISON_ROOT')
}
if ([string]::IsNullOrWhiteSpace($RimLiaisonRoot)) {
    $RimLiaisonRoot = [Environment]::GetEnvironmentVariable('RIMTEST_ROOT')
}
if ([string]::IsNullOrWhiteSpace($RimLiaisonRoot)) {
    $RimLiaisonRoot = Join-Path (Split-Path $DevBridgeRoot -Parent) 'RimLiaison'
}
$RimLiaisonRoot = Resolve-FullPath $RimLiaisonRoot

$devBridgeCommand = Join-Path $DevBridgeRoot 'DevBridge.cmd'
$modTestScript = Join-Path $DevBridgeRoot 'scripts\mod-test.ps1'
$descriptorPath = if ([string]::IsNullOrWhiteSpace($DescriptorPath)) {
    Join-Path $DevBridgeRoot 'DevelopmentProjects\live-stack-fixture.json'
} else {
    Resolve-FullPath $DescriptorPath
}

if ([string]::IsNullOrWhiteSpace($RimLiaisonPath)) {
    $RimLiaisonPath = [Environment]::GetEnvironmentVariable('RIMLIAISON_CMD')
}
if ([string]::IsNullOrWhiteSpace($RimLiaisonPath)) {
    $RimLiaisonPath = [Environment]::GetEnvironmentVariable('RIMTEST_CMD')
}
if ([string]::IsNullOrWhiteSpace($RimLiaisonPath)) {
    $RimLiaisonPath = Join-Path $RimLiaisonRoot 'src\RimLiaison.Cli\bin\Release\net8.0\rimliaison.exe'
}
if (-not [string]::IsNullOrWhiteSpace($RimLiaisonPath) -and $RimLiaisonPath -notmatch '^[A-Za-z]:[\\/]') {
    $RimLiaisonPath = Resolve-FullPath $RimLiaisonPath
}

if ([string]::IsNullOrWhiteSpace($RimErrorPath)) {
    $RimErrorPath = [Environment]::GetEnvironmentVariable('RIMERROR_CMD')
}
if ([string]::IsNullOrWhiteSpace($RimErrorPath)) {
    $rimErrorRoot = Join-Path (Split-Path $RimLiaisonRoot -Parent) 'RimError'
    $RimErrorPath = Join-Path $rimErrorRoot 'src\RimError.Cli\bin\Release\net8.0\rimerror.exe'
}
if (-not [string]::IsNullOrWhiteSpace($RimErrorPath) -and $RimErrorPath -notmatch '^[A-Za-z]:[\\/]') {
    $RimErrorPath = Resolve-FullPath $RimErrorPath
}

if ([string]::IsNullOrWhiteSpace($RimErrorStorePath)) {
    $RimErrorStorePath = Join-Path ([IO.Path]::GetTempPath()) ('DevBridge2-live-stack-rimerror-' +
        [Guid]::NewGuid().ToString('N') + '.json')
}
$RimErrorStorePath = Resolve-FullPath $RimErrorStorePath

if ([string]::IsNullOrWhiteSpace($CompatibilityPath)) {
    $CompatibilityPath = Join-Path $DevBridgeRoot 'RimBridgeProtocolCompatibility.json'
}
$CompatibilityPath = Resolve-FullPath $CompatibilityPath

if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $ReportPath = if ($Plan) { $null } else { Join-Path $DevBridgeRoot 'Runtime\live-stack-smoke-last.json' }
} elseif (-not [string]::IsNullOrWhiteSpace($ReportPath)) {
    $ReportPath = Resolve-FullPath $ReportPath
}

$transactionId = [Guid]::NewGuid().ToString('N')
$workflowId = 'live-stack-' + $transactionId
$sessionId = 'live-stack-' + $transactionId
$agentId = 'live-stack-' + $transactionId
$oldAgent = [Environment]::GetEnvironmentVariable('DEVBRIDGE_AGENT', 'Process')
$oldSession = [Environment]::GetEnvironmentVariable('DEVBRIDGE_SESSION', 'Process')
$env:DEVBRIDGE_AGENT = $agentId
$env:DEVBRIDGE_SESSION = $sessionId

$script:Report = [ordered]@{
    schemaVersion = 'devbridge-live-stack-smoke/v1'
    success = $false
    status = 'running'
    plan = [bool]$Plan
    transactionId = $transactionId
    workflowId = $workflowId
    startedAtUtc = $script:SmokeStartedUtc.ToString('o')
    finishedAtUtc = $null
    preflight = [ordered]@{
        status = 'pending'
        checks = [ordered]@{}
    }
    fixture = [ordered]@{
        project = $Project
        descriptor = $descriptorPath
        deploymentRoot = $null
        deploymentTarget = $null
        modPackageId = $null
        sourceFingerprint = $null
        transactionId = $null
        builtArtifactSha256 = $null
        deployedArtifactSha256 = $null
        deploymentDecision = $null
        freshnessProof = $null
        loadedArtifactFreshnessProven = $false
        cleanupDeferred = $false
        transactionFailureCode = $null
        transactionFailureMessage = $null
    }
    runtime = [ordered]@{
        rimWorldVersion = $null
        rimBridgeServerVersion = $null
        rimBridgeServerPackage = 'brrainz.rimbridgeserver'
        rimBridgeServerSdkVersion = $null
        generation = $null
        launchId = $null
        processId = $null
        leaseId = $null
        registrationId = $null
        state = $null
        readiness = $null
        companionVerified = $false
        recovery = [ordered]@{
            attempted = $false
            action = $null
            fromState = $null
            fromGeneration = $null
            registrationIds = @()
            registrationsReleased = $false
        }
        devBridge2 = [ordered]@{
            productVersion = $null
            commit = $null
            dirty = $null
        }
        # Stable devbridge-live-stack-smoke/v1 field retained for existing report consumers.
        rimTest = [ordered]@{
            path = $RimLiaisonPath
            commit = $null
        }
    }
    capabilities = [ordered]@{
        status = 'not-run'
        expected = @('rimbridge/ping', 'rimworld/get_screen_targets', 'rimworld/take_screenshot')
        observed = @()
        totalMatches = 0
        truncated = $false
    }
    recipe = [ordered]@{
        id = 'live-stack-smoke'
        success = $false
        generation = $null
        workflowId = $workflowId
        runId = $null
        evidenceId = $null
        operationIds = @()
        operations = @()
    }
    ui = [ordered]@{
        status = 'not-run'
        supported = $null
        captureMode = $null
        targetCount = 0
        targetId = $null
        cellRect = $null
        screenshotPath = $null
        operationId = $null
        workflowId = $null
        evidenceId = $null
        evidenceIdSource = $null
    }
    diagnostic = [ordered]@{
        status = 'not-run'
        recipeId = 'live-stack-diagnostic'
        operationId = $null
        operationName = 'rimbridge/get_operation'
        routeSuccess = $null
        errorCode = $null
        workflowId = $workflowId
        generation = $null
        rimError = [ordered]@{
            ingested = $false
            diagnosticId = $null
            operationId = $null
            workflowId = $null
            generation = $null
            correlationConfidence = $null
            correlated = $false
        }
    }
    cleanup = [ordered]@{
        attempted = $false
        leaseEnded = $false
        registrationReleased = $false
        activeLeaseConfirmedAbsent = $false
        state = $null
        error = $null
    }
    compatibility = [ordered]@{
        updated = $false
        path = $CompatibilityPath
        recordKey = $null
    }
    failure = $null
}

function Limit-Text {
    param([AllowNull()][string]$Text, [int]$Limit = 512)
    if ([string]::IsNullOrWhiteSpace($Text)) { return $null }
    $value = $Text.Trim()
    if ($value.Length -le $Limit) { return $value }
    return $value.Substring(0, $Limit) + '...[truncated]'
}

function Get-Value {
    param([AllowNull()]$Object, [Parameter(Mandatory = $true)][string]$Name)
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Get-NestedValue {
    param([AllowNull()]$Object, [Parameter(Mandatory = $true)][string[]]$Names)
    $current = $Object
    foreach ($name in $Names) {
        $current = Get-Value $current $name
        if ($null -eq $current) { return $null }
    }
    return $current
}

function Set-Failure {
    param(
        [Parameter(Mandatory = $true)][string]$Code,
        [Parameter(Mandatory = $true)][string]$Message,
        [Parameter(Mandatory = $true)][string]$Phase,
        [string]$NextAction = 'inspect-result',
        [switch]$Blocked
    )
    $script:Report.status = if ($Blocked) { 'blocked' } else { 'fail' }
    if ($Phase -eq 'preflight') {
        $script:Report.preflight.status = if ($Blocked) { 'blocked' } else { 'failed' }
    }
    $script:Report.failure = [ordered]@{
        phase = Limit-Text $Phase 96
        code = Limit-Text $Code 128
        message = Limit-Text $Message
        nextAction = Limit-Text $NextAction 256
    }
    throw [InvalidOperationException]::new($Message)
}

function Get-JsonFromOutput {
    param([AllowNull()][string]$Output)
    if ([string]::IsNullOrWhiteSpace($Output)) { return $null }
    $candidate = $null
    foreach ($line in ($Output -split '\r?\n')) {
        $trimmed = $line.Trim()
        if ($trimmed.Length -eq 0) { continue }
        try {
            $parsed = $trimmed | ConvertFrom-Json -ErrorAction Stop
            if ($null -ne $parsed) { $candidate = $parsed }
        } catch {
            continue
        }
    }
    return $candidate
}

function Quote-CmdArgument {
    param([Parameter(Mandatory = $true)][string]$Value)
    if ($Value -notmatch '[\s"]') { return $Value }
    return '"' + $Value.Replace('"', '\"') + '"'
}

function Invoke-BoundedProcess {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][int]$TimeoutSeconds
    )
    $process = $null
    try {
        $info = [Diagnostics.ProcessStartInfo]::new()
        $isCmd = $FilePath.EndsWith('.cmd', [StringComparison]::OrdinalIgnoreCase) -or
            $FilePath.EndsWith('.bat', [StringComparison]::OrdinalIgnoreCase)
        if ($isCmd) {
            $info.FileName = $env:ComSpec
            $commandLine = '"' + $FilePath + '" ' + (($Arguments | ForEach-Object {
                Quote-CmdArgument ([string]$_)
            }) -join ' ')
            # ProcessStartInfo.ArgumentList quotes the complete /c payload a
            # second time. Use the raw cmd.exe form so paths containing spaces
            # remain executable on a normal Steam installation.
            $info.Arguments = '/d /s /c "' + $commandLine + '"'
        } else {
            $info.FileName = $FilePath
            foreach ($argument in $Arguments) {
                [void]$info.ArgumentList.Add([string]$argument)
            }
        }
        $info.UseShellExecute = $false
        $info.CreateNoWindow = $true
        $info.RedirectStandardOutput = $true
        $info.RedirectStandardError = $true
        $process = [Diagnostics.Process]::new()
        $process.StartInfo = $info
        [void]$process.Start()
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $timeoutMs = [Math]::Min([int]::MaxValue, $TimeoutSeconds * 1000)
        $finished = $process.WaitForExit($timeoutMs)
        $timedOut = -not $finished
        if ($timedOut) {
            try { $process.Kill($true) } catch { }
            [void]$process.WaitForExit(5000)
        }
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        $combined = (($stdout + [Environment]::NewLine + $stderr).Trim())
        if ($combined.Length -gt $script:MaxChildOutputChars) {
            $combined = $combined.Substring(0, $script:MaxChildOutputChars) + [Environment]::NewLine + '[truncated]'
        }
        return [pscustomobject]@{
            ExitCode = if ($timedOut) { 124 } else { $process.ExitCode }
            TimedOut = $timedOut
            Output = $combined
            Json = Get-JsonFromOutput $combined
            StartError = $null
        }
    } catch {
        return [pscustomobject]@{
            ExitCode = 125
            TimedOut = $false
            Output = $null
            Json = $null
            StartError = Limit-Text $_.Exception.Message
        }
    } finally {
        if ($null -ne $process) { $process.Dispose() }
    }
}

function Invoke-DevBridge {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)
    return Invoke-BoundedProcess $devBridgeCommand (@('--root', $DevBridgeRoot) + $Arguments) $TimeoutSeconds
}

function Invoke-RimLiaison {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)
    return Invoke-BoundedProcess $RimLiaisonPath $Arguments ([Math]::Min($TimeoutSeconds, 300))
}

function Invoke-RimError {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)
    return Invoke-BoundedProcess $RimErrorPath $Arguments ([Math]::Min($TimeoutSeconds, 120))
}

function Invoke-OwnerRequired {
    param(
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$Phase,
        [Parameter(Mandatory = $true)][string]$Code
    )
    $result = Invoke-DevBridge $Arguments
    if ($null -eq $result.Json) {
        Set-Failure $Code ($result.StartError ?? $result.Output ?? 'DevBridge returned no machine-readable JSON.') $Phase
    }
    if ((Get-Value $result.Json 'success') -ne $true) {
        $errorCode = Get-Value $result.Json 'errorCode'
        $message = Get-Value $result.Json 'error'
        if ([string]::IsNullOrWhiteSpace([string]$message)) { $message = Get-Value $result.Json 'nextAction' }
        Set-Failure ([string]($errorCode ?? $Code)) ([string]($message ?? 'DevBridge owner command failed.')) $Phase
    }
    return $result.Json
}

function Get-Sha256 {
    param([Parameter(Mandatory = $true)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-SourceFingerprint {
    param([Parameter(Mandatory = $true)][string]$Descriptor)
    $descriptorDocument = Get-Content -LiteralPath $Descriptor -Raw | ConvertFrom-Json
    $sourceProjectValue = [string]$descriptorDocument.sourceProject
    if ([string]::IsNullOrWhiteSpace($sourceProjectValue)) {
        Set-Failure 'LIVE_FIXTURE_DESCRIPTOR_INVALID' 'The development descriptor has no sourceProject.' 'preflight'
    }
    $sourceProject = Resolve-FullPath (Join-Path $DevBridgeRoot $sourceProjectValue)
    if (-not (Test-Path -LiteralPath $sourceProject -PathType Leaf)) {
        Set-Failure 'LIVE_FIXTURE_SOURCE_MISSING' 'The deterministic fixture source project is missing.' 'preflight'
    }
    $sourceRoot = Split-Path $sourceProject -Parent
    $files = @(
        $Descriptor
        $sourceProject
        Get-ChildItem -LiteralPath $sourceRoot -Recurse -File -ErrorAction Stop |
            Where-Object { $_.FullName -notmatch '[\\/]((bin)|(obj))[\\/]' } |
            Sort-Object FullName |
            Select-Object -ExpandProperty FullName
    ) | Select-Object -Unique
    $lines = foreach ($file in $files) {
        $relative = if ($file.StartsWith($DevBridgeRoot, [StringComparison]::OrdinalIgnoreCase)) {
            $file.Substring($DevBridgeRoot.Length).TrimStart('\', '/')
        } else {
            [IO.Path]::GetFileName($file)
        }
        $relative + [char]0 + (Get-Sha256 $file)
    }
    $bytes = [Text.Encoding]::UTF8.GetBytes(($lines -join [Environment]::NewLine))
    $hash = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($hash.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant() }
    finally { $hash.Dispose() }
}

function Get-InstalledRimBridgeInfo {
    $modsRoot = Join-Path $RimWorldRoot 'Mods'
    if (-not (Test-Path -LiteralPath $modsRoot -PathType Container)) { return $null }
    foreach ($directory in (Get-ChildItem -LiteralPath $modsRoot -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -notlike '_quarantine*' })) {
        $aboutPath = Join-Path $directory.FullName 'About\About.xml'
        if (-not (Test-Path -LiteralPath $aboutPath -PathType Leaf)) { continue }
        try {
            $about = [xml](Get-Content -LiteralPath $aboutPath -Raw)
            $package = [string]$about.ModMetaData.packageId
            if ($package -ne 'brrainz.rimbridgeserver') { continue }
            return [ordered]@{
                path = $directory.FullName
                packageId = $package
                version = [string]$about.ModMetaData.modVersion
            }
        } catch {
            continue
        }
    }
    return $null
}

function Get-ProjectPackageId {
    $known = @{
        'deferred-reality' = 'lan.deferredreality.framework'
        'insight-canvas' = 'lan.insightcanvas'
        'knowledge-framework' = 'lan.knowledgeframework'
        'frontier' = 'lan.frontier'
        'aquaculture' = 'lan.aquaculture.fishing'
        'horticulture' = 'lan.horticulture.novelseeds'
        'wildlife' = 'lan.wildlife'
    }
    $key = $Project.ToLowerInvariant()
    if ($known.ContainsKey($key)) { return [string]$known[$key] }
    return $null
}

function Get-InstalledProjectModInfo {
    $modsRoot = Join-Path $RimWorldRoot 'Mods'
    $expectedPackage = Get-ProjectPackageId
    if ([string]::IsNullOrWhiteSpace($expectedPackage) -or
        -not (Test-Path -LiteralPath $modsRoot -PathType Container)) {
        return $null
    }
    $candidates = if (-not [string]::IsNullOrWhiteSpace($FixtureDeploymentRoot)) {
        @([IO.DirectoryInfo](Resolve-FullPath $FixtureDeploymentRoot))
    } else {
        @(Get-ChildItem -LiteralPath $modsRoot -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -notlike '_quarantine*' })
    }
    foreach ($directory in $candidates) {
        if ($null -eq $directory -or -not (Test-Path -LiteralPath $directory.FullName -PathType Container)) { continue }
        $aboutPath = Join-Path $directory.FullName 'About\About.xml'
        if (-not (Test-Path -LiteralPath $aboutPath -PathType Leaf)) { continue }
        try {
            $about = [xml](Get-Content -LiteralPath $aboutPath -Raw)
            $package = [string]$about.ModMetaData.packageId
            if ($package -ne $expectedPackage) { continue }
            return [ordered]@{
                path = $directory.FullName
                packageId = $package
                version = [string]$about.ModMetaData.modVersion
            }
        } catch {
            continue
        }
    }
    return $null
}

function Get-TextVersion {
    $path = Join-Path $RimWorldRoot 'Version.txt'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $null }
    return Limit-Text ((Get-Content -LiteralPath $path -Raw).Trim()) 128
}

function Get-GitCommit {
    param([Parameter(Mandatory = $true)][string]$Root)
    if (-not (Test-Path -LiteralPath (Join-Path $Root '.git'))) { return $null }
    try {
        $value = & git -C $Root rev-parse HEAD 2>$null
        if ($LASTEXITCODE -eq 0) { return ([string]$value).Trim() }
    } catch { }
    return $null
}

function Get-SdkVersion {
    $projectPath = Join-Path $DevBridgeRoot 'Source\BridgeTools\DevBridge2.BridgeTools.csproj'
    if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) { return $null }
    $match = [regex]::Match((Get-Content -LiteralPath $projectPath -Raw),
        '<PackageReference\s+Include="RimBridgeServer\.Sdk"\s+Version="([^"]+)"')
    if ($match.Success) { return $match.Groups[1].Value }
    return $null
}

function Initialize-Preflight {
    $rimBridgeInfo = Get-InstalledRimBridgeInfo
    $script:FixtureModInfo = Get-InstalledProjectModInfo
    if ($null -ne $script:FixtureModInfo) {
        $script:FixtureDeploymentRoot = [string]$script:FixtureModInfo.path
        $script:Report.fixture.deploymentRoot = $script:FixtureDeploymentRoot
        $script:Report.fixture.modPackageId = [string]$script:FixtureModInfo.packageId
    }
    $checks = [ordered]@{
        windows = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT
        devBridgeCommand = Test-Path -LiteralPath $devBridgeCommand -PathType Leaf
        modTransaction = Test-Path -LiteralPath $modTestScript -PathType Leaf
        rimWorldExecutable = Test-Path -LiteralPath (Join-Path $RimWorldRoot 'RimWorldWin64.exe') -PathType Leaf
        rimWorldManaged = Test-Path -LiteralPath (Join-Path $RimWorldRoot 'RimWorldWin64_Data\Managed') -PathType Container
        rimWorldVersion = Test-Path -LiteralPath (Join-Path $RimWorldRoot 'Version.txt') -PathType Leaf
        rimTest = Test-Path -LiteralPath $RimLiaisonPath -PathType Leaf
        rimError = Test-Path -LiteralPath $RimErrorPath -PathType Leaf
        descriptor = Test-Path -LiteralPath $descriptorPath -PathType Leaf
        compatibilityMetadata = Test-Path -LiteralPath $CompatibilityPath -PathType Leaf
        rimBridgeServer = $null -ne $rimBridgeInfo
        fixtureMod = $null -ne $script:FixtureModInfo
    }
    $script:Report.preflight.checks = $checks
    if ($checks.descriptor) {
        try {
            $script:Report.fixture.deploymentTarget = [string]((Get-Content -LiteralPath $descriptorPath -Raw | ConvertFrom-Json).deploymentTarget)
        } catch {
            # The normal descriptor validation below will report the bounded failure.
        }
    }
    if (-not $checks.windows) {
        Set-Failure 'LIVE_WINDOWS_REQUIRED' 'The live-stack gate requires a Windows RimWorld installation.' 'preflight' -Blocked
    }
    $missing = @($checks.Keys | Where-Object { -not [bool]$checks[$_] })
    if ($missing.Count -gt 0) {
        $next = if ($missing -contains 'rimBridgeServer') {
            'Install the active brrainz.rimbridgeserver mod under RimWorld\Mods (not _quarantine), then rerun the smoke.'
        } elseif ($missing -contains 'fixtureMod') {
            'Install the declared project mod under RimWorld\Mods, or pass -FixtureDeploymentRoot for its active mod directory, then rerun the smoke.'
        } else {
            'Install/build the missing self-hosted prerequisite and rerun the smoke.'
        }
        Set-Failure 'LIVE_PREREQUISITE_MISSING' ('Required live prerequisite(s) missing: ' + ($missing -join ', ')) 'preflight' $next -Blocked
    }
    try {
        $null = Get-Content -LiteralPath $CompatibilityPath -Raw | ConvertFrom-Json
    } catch {
        Set-Failure 'LIVE_COMPATIBILITY_METADATA_INVALID' 'RimBridgeProtocolCompatibility.json is missing or invalid JSON.' 'preflight'
    }
    $script:Report.runtime.rimWorldVersion = Get-TextVersion
    $script:Report.runtime.rimBridgeServerVersion = [string]$rimBridgeInfo.version
    $script:Report.runtime.rimBridgeServerSdkVersion = Get-SdkVersion
    $script:Report.runtime.devBridge2.commit = Get-GitCommit $SourceRepositoryRoot
    $script:Report.runtime.rimTest.commit = Get-GitCommit $RimLiaisonRoot
    $aboutPath = Join-Path $DevBridgeRoot 'About\About.xml'
    if (Test-Path -LiteralPath $aboutPath -PathType Leaf) {
        try { $script:Report.runtime.devBridge2.productVersion = [string]([xml](Get-Content $aboutPath -Raw)).ModMetaData.modVersion } catch { }
    }
    $descriptorDocument = Get-Content -LiteralPath $descriptorPath -Raw | ConvertFrom-Json
    $script:Report.fixture.deploymentTarget = [string]$descriptorDocument.deploymentTarget
    $script:Report.fixture.sourceFingerprint = Get-SourceFingerprint $descriptorPath
    $script:Report.preflight.status = 'ready'
}

function Update-RuntimeIdentity {
    param([Parameter(Mandatory = $true)]$Status)
    $script:Report.runtime.generation = [int](Get-Value $Status 'generation')
    $script:Report.runtime.launchId = [string](Get-Value $Status 'launchId')
    $script:Report.runtime.processId = [int](Get-Value $Status 'rimworldPid')
    $script:Report.runtime.leaseId = $script:LeaseId
    $script:Report.runtime.state = [string](Get-Value $Status 'state')
    $script:Report.runtime.readiness = [string](Get-Value $Status 'gameState')
    $script:Report.runtime.companionVerified = [bool](Get-NestedValue $Status @('rimBridge', 'CompanionVerified'))
    $script:Report.runtime.rimBridgeServerVersion = [string](Get-NestedValue $Status @('rimBridge', 'Version'))
    if ([string]::IsNullOrWhiteSpace([string]$script:Report.runtime.rimBridgeServerSdkVersion)) {
        $script:Report.runtime.rimBridgeServerSdkVersion = Get-SdkVersion
    }
}

function Ensure-ReadyGeneration {
    $initialResult = Invoke-DevBridge @('status', '--json')
    if ($null -eq $initialResult.Json) {
        Set-Failure 'LIVE_STATUS_FAILED' ($initialResult.StartError ?? $initialResult.Output ?? 'DevBridge returned no machine-readable status.') 'readiness'
    }
    if ((Get-Value $initialResult.Json 'success') -ne $true) {
        $message = Get-Value $initialResult.Json 'error'
        if ([string]::IsNullOrWhiteSpace([string]$message)) { $message = Get-Value $initialResult.Json 'nextAction' }
        Set-Failure ([string]((Get-Value $initialResult.Json 'errorCode') ?? 'LIVE_STATUS_FAILED')) ([string]($message ?? 'DevBridge status was not successful.')) 'readiness'
    }

    $initial = $initialResult.Json
    $initialState = [string](Get-Value $initial 'state')
    $initialGameState = [string](Get-Value $initial 'gameState')
    $initialGeneration = [int](Get-Value $initial 'generation')
    $initialPid = [int](Get-Value $initial 'rimworldPid')
    $initialCompanion = [bool](Get-NestedValue $initial @('rimBridge', 'CompanionVerified'))
    $ready = $initialState -eq 'READY' -and $initialGameState -eq 'READY' -and
        $initialGeneration -ge 1 -and $initialPid -gt 0 -and $initialCompanion
    if ($ready) {
        return
    }

    $script:Report.runtime.recovery.attempted = $true
    $script:Report.runtime.recovery.fromState = $initialState
    $script:Report.runtime.recovery.fromGeneration = $initialGeneration
    $transitioning = [bool](Get-Value $initial 'restartPending') -or
        $initialState -in @('DRAINING', 'RESTARTING', 'LOADING', 'WAITING_FOR_BRIDGE')
    if ($transitioning) {
        $script:Report.runtime.recovery.action = 'wait-ready'
        $null = Invoke-OwnerRequired @('wait-ready', '--json') 'readiness' 'LIVE_RECOVERY_READINESS_FAILED'
    } elseif ($initialState -eq 'STOPPED' -or $initialPid -le 0 -or
        [string](Get-Value $initial 'errorCode') -eq 'PROCESS_EXITED') {
        # A stopped/process-exited generation cannot grant a test lease. Ask
        # DevBridge to own the replacement launch and wait for its READY proof;
        # RimLiaison never launches or kills RimWorld itself.
        $script:Report.runtime.recovery.action = 'restart'
        $null = Invoke-OwnerRequired @('restart', '--projects', $Project, '--json') 'readiness' 'LIVE_RECOVERY_RESTART_FAILED'
    } else {
        Set-Failure 'LIVE_GENERATION_NOT_READY' ('DevBridge reported an untrusted runtime state (' + $initialState + '); no lease was requested.') 'readiness'
    }

    $final = Invoke-OwnerRequired @('status', '--json') 'readiness' 'LIVE_RECOVERY_STATUS_FAILED'
    Update-RuntimeIdentity $final
    if ([string]$script:Report.runtime.state -ne 'READY' -or
        [string]$script:Report.runtime.readiness -ne 'READY' -or
        [int]$script:Report.runtime.generation -lt 1 -or
        [int]$script:Report.runtime.processId -le 0 -or
        -not $script:Report.runtime.companionVerified) {
        Set-Failure 'LIVE_RECOVERY_NOT_READY' 'DevBridge did not prove a READY generation with a live, verified RimBridge companion after recovery.' 'readiness'
    }

    # restart --projects may create a compatibility project intent so the
    # requested profile can be frozen. The generation is now immutable and
    # usable, so release only the temporary intent owned by this smoke
    # transaction; never mutate another caller's registration.
    $ownedRecoveryRegistrations = @((Get-Value $final 'activeProjectIntents') | Where-Object {
        [string](Get-Value $_ 'owner') -eq $agentId -and
        [string](Get-Value $_ 'sessionId') -eq $sessionId
    })
    foreach ($registration in $ownedRecoveryRegistrations) {
        $registrationId = [string](Get-Value $registration 'id')
        if ([string]::IsNullOrWhiteSpace($registrationId)) { continue }
        $script:Report.runtime.recovery.registrationIds += $registrationId
        $null = Invoke-OwnerRequired @('project', 'release', $registrationId, '--json') 'readiness' 'LIVE_RECOVERY_REGISTRATION_RELEASE_FAILED'
    }
    $script:Report.runtime.recovery.registrationsReleased = $true
}

function Run-ModTransaction {
    $null = Ensure-ReadyGeneration
    $begin = Invoke-OwnerRequired @('test', 'begin', '--json') 'lease' 'LIVE_LEASE_FAILED'
    $script:LeaseId = [string](Get-Value $begin 'leaseId')
    if ($script:LeaseId -notmatch '^lease-[0-9A-Fa-f]{32}$') {
        Set-Failure 'LIVE_LEASE_ID_MISSING' 'DevBridge did not return a complete test lease capability.' 'lease'
    }
    $script:Report.runtime.leaseId = $script:LeaseId
    $transactionArgs = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $modTestScript,
        '-Project', $Project,
        '-DescriptorPath', $descriptorPath,
        '-CoordinatorRoot', $DevBridgeRoot,
        '-DeploymentRoot', [string]$script:FixtureDeploymentRoot,
        '-DevelopmentRoot', $DevBridgeRoot,
        '-LeaseId', $script:LeaseId,
        '-WorkflowId', $workflowId,
        '-SourceFingerprint', [string]$script:Report.fixture.sourceFingerprint,
        '-SkipRecipe',
        '-BuildTimeoutSeconds', ([Math]::Min(300, $TimeoutSeconds)).ToString(),
        '-CoordinatorTimeoutSeconds', ([Math]::Min(600, $TimeoutSeconds)).ToString(),
        '-Json'
    )
    $pwshCommand = (Get-Command pwsh -ErrorAction SilentlyContinue).Source
    if ([string]::IsNullOrWhiteSpace($pwshCommand)) {
        Set-Failure 'LIVE_PWSH_MISSING' 'PowerShell 7 (pwsh) is required to invoke the owner transaction.' 'fixture'
    }
    $transaction = Invoke-BoundedProcess $pwshCommand $transactionArgs ([Math]::Min($TimeoutSeconds, 600))
    if ($null -eq $transaction.Json) {
        Set-Failure 'LIVE_FIXTURE_TRANSACTION_INVALID' ($transaction.StartError ?? 'mod-test returned no JSON.') 'fixture'
    }
    $script:TransactionReport = $transaction.Json
    $script:RegistrationId = [string](Get-NestedValue $transaction.Json @('runtime', 'registrationId'))
    $script:Report.fixture.transactionId = [string](Get-Value $transaction.Json 'transactionId')
    $script:Report.fixture.builtArtifactSha256 = [string](Get-NestedValue $transaction.Json @('artifactFreshness', 'builtArtifactSha256'))
    $script:Report.fixture.deployedArtifactSha256 = [string](Get-NestedValue $transaction.Json @('artifactFreshness', 'deployedArtifactSha256'))
    $script:Report.fixture.deploymentDecision = [string](Get-NestedValue $transaction.Json @('artifactFreshness', 'deploymentDecision'))
    $script:Report.fixture.freshnessProof = [string](Get-NestedValue $transaction.Json @('artifactFreshness', 'proof'))
    $script:Report.fixture.loadedArtifactFreshnessProven = [bool](Get-NestedValue $transaction.Json @('artifactFreshness', 'loadedArtifactFreshnessProven'))
    $script:Report.fixture.cleanupDeferred = [bool](Get-NestedValue $transaction.Json @('cleanup', 'deferred'))
    $script:Report.fixture.transactionFailureCode = [string]((Get-NestedValue $transaction.Json @('failure', 'errorCode')) ?? (Get-NestedValue $transaction.Json @('artifactFreshness', 'errorCode')))
    $script:Report.fixture.transactionFailureMessage = Limit-Text ([string](Get-NestedValue $transaction.Json @('failure', 'message')))
    if ((Get-Value $transaction.Json 'success') -ne $true -or -not $script:Report.fixture.loadedArtifactFreshnessProven) {
        $code = [string](Get-NestedValue $transaction.Json @('failure', 'errorCode'))
        if ([string]::IsNullOrWhiteSpace($code)) { $code = 'LIVE_ARTIFACT_FRESHNESS_UNPROVEN' }
        Set-Failure $code 'The deterministic fixture transaction did not prove the current artifact is owned by the current generation.' 'fixture'
    }
    $status = Invoke-OwnerRequired @('status', '--json') 'readiness' 'LIVE_STATUS_FAILED'
    Update-RuntimeIdentity $status
    if ([string]$script:Report.runtime.state -ne 'READY' -or [int]$script:Report.runtime.generation -lt 1 -or -not $script:Report.runtime.companionVerified) {
        Set-Failure 'LIVE_READY_EVIDENCE_MISSING' 'DevBridge did not report a READY generation with verified RimBridge companion identity.' 'readiness'
    }
    if ([int](Get-NestedValue $transaction.Json @('runtime', 'generation')) -ne [int]$script:Report.runtime.generation) {
        Set-Failure 'LIVE_GENERATION_MISMATCH' 'The fixture transaction generation differs from the current DevBridge generation.' 'readiness'
    }
}

function Run-LiveRecipe {
    param([Parameter(Mandatory = $true)][string]$RecipeId)
    $result = Invoke-OwnerRequired @('test', 'recipe', 'run', $RecipeId, '--lease', $script:LeaseId, '--workflow-id', $workflowId, '--json') 'recipe' 'LIVE_RECIPE_FAILED'
    return $result
}

function Assert-RecipeIdentity {
    param([Parameter(Mandatory = $true)]$Recipe, [Parameter(Mandatory = $true)][string]$Name)
    if ((Get-Value $Recipe 'success') -ne $true) {
        Set-Failure ([string]((Get-Value $Recipe 'errorCode') ?? 'LIVE_RECIPE_FAILED')) ([string]((Get-Value $Recipe 'error') ?? ($Name + ' did not pass.'))) 'recipe'
    }
    if ([string](Get-Value $Recipe 'workflowId') -ne $workflowId -or [int](Get-Value $Recipe 'generation') -ne [int]$script:Report.runtime.generation) {
        Set-Failure 'LIVE_RECIPE_IDENTITY_MISMATCH' ($Name + ' did not execute in the verified workflow/generation.') 'recipe'
    }
    $operations = @((Get-Value $Recipe 'operations'))
    if ($operations.Count -eq 0) {
        Set-Failure 'LIVE_RECIPE_OPERATION_MISSING' ($Name + ' returned no operation evidence.') 'recipe'
    }
    foreach ($operation in $operations | Select-Object -First 8) {
        if ([string]::IsNullOrWhiteSpace([string](Get-Value $operation 'operationId')) -or [string](Get-Value $operation 'workflowId') -ne $workflowId -or [int](Get-Value $operation 'generation') -ne [int]$script:Report.runtime.generation) {
            Set-Failure 'LIVE_OPERATION_IDENTITY_MISMATCH' ($Name + ' returned incomplete operation identity.') 'recipe'
        }
    }
}

function Run-CapabilityProbe {
    # Capability discovery is intentionally bounded, but a first-page query
    # of 20 can omit the UI provider from a real installation.  Ask for the
    # CLI's bounded maximum so the smoke verifies the complete supported
    # surface without allowing an unbounded registry response.
    $probe = Invoke-RimLiaison @('--devbridge', $devBridgeCommand, '--devbridge-root', $DevBridgeRoot, 'capabilities', '--limit', '100', '--json')
    if ($null -eq $probe.Json -or [string](Get-Value $probe.Json 'status') -ne 'ok') {
        Set-Failure ([string]((Get-Value $probe.Json 'code') ?? 'LIVE_CAPABILITY_DISCOVERY_FAILED')) ([string]((Get-Value $probe.Json 'error') ?? 'RimLiaison could not discover the live capability registry.')) 'capabilities'
    }
    $capabilities = @((Get-Value $probe.Json 'capabilities'))
    $ids = @($capabilities | ForEach-Object { [string]((Get-Value $_ 'id') ?? (Get-Value $_ 'name')) } | Where-Object { $_ } | Select-Object -First 100)
    $script:Report.capabilities.observed = $ids
    $script:Report.capabilities.totalMatches = [int]((Get-Value $probe.Json 'totalMatches') ?? $ids.Count)
    $script:Report.capabilities.truncated = [bool](Get-Value $probe.Json 'truncated')
    $script:Report.capabilities.status = 'ok'
    if ('rimbridge/ping' -notin $ids) {
        Set-Failure 'LIVE_CAPABILITY_PING_MISSING' 'The live RimBridge capability registry did not expose rimbridge/ping.' 'capabilities'
    }
    if (-not (($ids -contains 'rimworld/get_screen_targets') -or ($ids -contains 'rimworld/take_screenshot'))) {
        Set-Failure 'LIVE_CAPABILITY_UI_MISSING' 'The live RimBridge capability registry did not expose a supported UI evidence surface.' 'capabilities'
    }
}

function Complete-UiEvidence {
    param([Parameter(Mandatory = $true)]$Capture)
    if ($null -eq $capture.Json -or [string](Get-Value $capture.Json 'status') -ne 'ok') {
        Set-Failure ([string]((Get-Value $capture.Json 'code') ?? 'LIVE_UI_SCREENSHOT_FAILED')) ([string]((Get-Value $capture.Json 'error') ?? 'RimLiaison could not capture bounded UI evidence.')) 'ui'
    }
    $script:Report.ui.status = 'ok'
    $script:Report.ui.screenshotPath = Limit-Text ([string](Get-Value $capture.Json 'path')) 512
    $script:Report.ui.operationId = [string](Get-Value $capture.Json 'operationId')
    $script:Report.ui.workflowId = [string](Get-Value $capture.Json 'workflowId')
    $script:Report.ui.evidenceId = [string](Get-Value $capture.Json 'evidenceId')
    if ([string]::IsNullOrWhiteSpace($script:Report.ui.screenshotPath) -or -not (Test-Path -LiteralPath $script:Report.ui.screenshotPath -PathType Leaf)) {
        Set-Failure 'LIVE_UI_SCREENSHOT_EVIDENCE_MISSING' 'RimLiaison returned no existing screenshot evidence path.' 'ui'
    }
    # Current RimBridgeServer legacy screenshot aliases expose the server
    # operation identity but do not expose a separate evidence ID.  Bind the
    # bounded, verified file to a local content identity rather than inventing
    # a server-side correlation claim.
    if ([string]::IsNullOrWhiteSpace($script:Report.ui.evidenceId)) {
        $script:Report.ui.evidenceId = 'sha256:' + (Get-Sha256 $script:Report.ui.screenshotPath)
        $script:Report.ui.evidenceIdSource = 'screenshot-sha256'
    } else {
        $script:Report.ui.evidenceIdSource = 'rimtest'
    }
    if ([string]::IsNullOrWhiteSpace($script:Report.ui.operationId) -or [string]::IsNullOrWhiteSpace($script:Report.ui.evidenceId)) {
        Set-Failure 'LIVE_UI_CORRELATION_MISSING' 'The UI evidence response did not include operation and evidence identities.' 'ui'
    }
}

function Run-UiEvidence {
    $targets = Invoke-RimLiaison @('--devbridge', $devBridgeCommand, '--devbridge-root', $DevBridgeRoot, 'ui', 'targets', '--json')
    $targetCode = [string](Get-Value $targets.Json 'code')
    $targetStatus = [string](Get-Value $targets.Json 'status')
    if ($targetStatus -ne 'ok' -and $targetCode -ne 'RIMTEST_UI_TARGETS_SCHEMA_UNSUPPORTED') {
        if ($AllowUiSkip) {
            $script:Report.ui.status = 'skipped-unsupported'
            $script:Report.ui.supported = $false
            return
        }
        Set-Failure ([string]($targetCode ?? 'LIVE_UI_TARGETS_FAILED')) ([string]((Get-Value $targets.Json 'error') ?? 'RimLiaison could not enumerate live UI targets.')) 'ui'
    }

    $targetList = @((Get-Value $targets.Json 'targets'))
    if ($targetStatus -eq 'ok' -and $targetList.Count -gt 0) {
        $targetId = [string](Get-Value $targetList[0] 'id')
        if ([string]::IsNullOrWhiteSpace($targetId)) {
            Set-Failure 'LIVE_UI_TARGET_DESCRIPTOR_INVALID' 'RimLiaison returned a UI target without an identity.' 'ui'
        }
        $script:Report.ui.captureMode = 'target'
        $script:Report.ui.targetCount = $targetList.Count
        $script:Report.ui.targetId = $targetId
        $script:Report.ui.supported = $true
        $capture = Invoke-RimLiaison @('--devbridge', $devBridgeCommand, '--devbridge-root', $DevBridgeRoot, 'ui', 'screenshot', '--target', $targetId, '--json')
        Complete-UiEvidence $capture
        return
    }

    if ($AllowUiSkip) {
        $script:Report.ui.status = 'skipped-no-targets'
        $script:Report.ui.supported = $false
        return
    }

    # RimBridgeServer 2.1's live get_screen_targets result is a structured
    # screen snapshot, not the older array-of-target-descriptors shape.  The
    # cell-rectangle screenshot capability is the compatible bounded surface.
    $cellRect = '0,0,1,1'
    $script:Report.ui.captureMode = 'cell-rect'
    $script:Report.ui.cellRect = $cellRect
    $script:Report.ui.targetCount = 0
    $script:Report.ui.supported = $true
    $capture = Invoke-RimLiaison @('--devbridge', $devBridgeCommand, '--devbridge-root', $DevBridgeRoot, 'ui', 'screenshot', '--cell-rect', $cellRect, '--json')
    Complete-UiEvidence $capture
}

function Run-DiagnosticCorrelation {
    $diagnosticRecipe = Run-LiveRecipe 'live-stack-diagnostic'
    Assert-RecipeIdentity $diagnosticRecipe 'live-stack-diagnostic'
    $operation = @((Get-Value $diagnosticRecipe 'operations'))[0]
    $operationId = [string](Get-Value $operation 'operationId')
    if ([string]::IsNullOrWhiteSpace($operationId)) {
        Set-Failure 'LIVE_DIAGNOSTIC_OPERATION_ID_MISSING' 'The controlled live diagnostic did not produce an operation identity.' 'diagnostic'
    }
    $script:Report.diagnostic.status = 'controlled-failure-observed'
    $script:Report.diagnostic.operationId = $operationId
    $script:Report.diagnostic.routeSuccess = $false
    $script:Report.diagnostic.errorCode = [string](Get-Value $operation 'errorCode')
    $script:Report.diagnostic.generation = [int](Get-Value $operation 'generation')

    $sourcePath = Join-Path ([IO.Path]::GetTempPath()) ('live-stack-diagnostic-' + $transactionId + '.log')
    $integrationPath = Join-Path ([IO.Path]::GetTempPath()) ('live-stack-integration-' + $transactionId + '.json')
    $script:TempFiles.Add($sourcePath)
    $script:TempFiles.Add($integrationPath)
    $diagnosticText = 'ERROR System.InvalidOperationException: [LIVE-STACK-SMOKE] operationId=' + $operationId + ' tool=rimbridge/get_operation errorCode=' + $script:Report.diagnostic.errorCode + ' workflowId=' + $workflowId + ' generation=' + $script:Report.diagnostic.generation
    Set-Content -LiteralPath $sourcePath -Value $diagnosticText -Encoding utf8
    $integration = [ordered]@{
        schemaVersion = 'rimerror-integration/v1'
        devBridge = [ordered]@{
            schemaVersion = 'devbridge-test-recipe-run/v1'
            workflowId = $workflowId
            runId = [string](Get-Value $diagnosticRecipe 'runId')
            testId = 'live-stack-diagnostic'
            leaseId = $script:LeaseId
            generation = $script:Report.diagnostic.generation
            launchId = $script:Report.runtime.launchId
            profileFingerprint = Get-NestedValue $script:TransactionReport @('runtime', 'acceptedProfileFingerprint')
            phase = 'READY'
            evidence = 'controlled-live-rimbridge-failure'
        }
        rimBridge = [ordered]@{
            schemaVersion = 'rimbridge-operation/v1'
            workflowId = $workflowId
            generation = $script:Report.diagnostic.generation
            launchId = $script:Report.runtime.launchId
            operations = @([ordered]@{
                schemaVersion = 'rimbridge-operation/v1'
                operationId = $operationId
                operationName = 'rimbridge/get_operation'
                workflowId = $workflowId
                success = $false
                status = 'Failed'
                errorCode = $script:Report.diagnostic.errorCode
                error = 'Controlled missing-operation diagnostic.'
                generation = $script:Report.diagnostic.generation
                launchId = $script:Report.runtime.launchId
                timestampUtc = [DateTime]::UtcNow.ToString('o')
            })
        }
    }
    $integration | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $integrationPath -Encoding utf8
    $ingest = Invoke-RimError @('ingest', $sourcePath, '--store', $RimErrorStorePath, '--run', [string](Get-Value $diagnosticRecipe 'runId'), '--test', 'live-stack-diagnostic', '--operation', $operationId, '--operation-name', 'rimbridge/get_operation', '--integration', $integrationPath)
    if ($null -eq $ingest.Json) {
        Set-Failure 'LIVE_RIMERROR_INGEST_FAILED' ($ingest.StartError ?? 'RimError returned no machine-readable ingestion result.') 'diagnostic'
    }
    $export = Invoke-RimError @('export', '--json', '--store', $RimErrorStorePath)
    if ($null -eq $export.Json) {
        Set-Failure 'LIVE_RIMERROR_EXPORT_FAILED' ($export.StartError ?? 'RimError returned no machine-readable export.') 'diagnostic'
    }
    $items = @((Get-Value $export.Json 'items'))
    $item = $items | Where-Object {
        [string]((Get-Value $_ 'operationId') ?? (Get-Value $_ 'op')) -eq $operationId
    } | Select-Object -First 1
    if ($null -eq $item) {
        Set-Failure 'LIVE_RIMERROR_CORRELATION_MISSING' 'RimError did not retain the controlled live operation identity.' 'diagnostic'
    }
    $storedOperationId = [string]((Get-Value $item 'operationId') ?? (Get-Value $item 'op'))
    $confidence = [string]((Get-Value $item 'correlationConfidence') ?? (Get-Value $item 'corr'))
    $correlationGeneration = (Get-Value $item 'correlationGeneration') ?? (Get-Value $item 'bridgeGen')
    $correlationWorkflow = [string](Get-Value $item 'workflowId')
    $script:Report.diagnostic.rimError.ingested = $true
    $script:Report.diagnostic.rimError.diagnosticId = [string](Get-Value $item 'id')
    $script:Report.diagnostic.rimError.operationId = $storedOperationId
    $script:Report.diagnostic.rimError.workflowId = $correlationWorkflow
    $script:Report.diagnostic.rimError.generation = [int]$correlationGeneration
    $script:Report.diagnostic.rimError.correlationConfidence = $confidence
    $script:Report.diagnostic.rimError.correlated = $storedOperationId -eq $operationId -and $correlationWorkflow -eq $workflowId -and [int]$correlationGeneration -eq [int]$script:Report.diagnostic.generation -and $confidence -in @('high', 'medium')
    if (-not $script:Report.diagnostic.rimError.correlated) {
        Set-Failure 'LIVE_RIMERROR_CORRELATION_UNTRUSTWORTHY' 'RimError correlation did not prove operation, workflow, and generation identity.' 'diagnostic'
    }
}

function Update-CompatibilityMetadata {
    $metadata = Get-Content -LiteralPath $CompatibilityPath -Raw | ConvertFrom-Json
    $server = Get-Value $metadata 'rimBridgeServer'
    if ($null -eq $server) {
        Set-Failure 'LIVE_COMPATIBILITY_METADATA_INVALID' 'Compatibility metadata has no rimBridgeServer section.' 'compatibility'
    }
    $record = [ordered]@{
        rimWorldVersion = $script:Report.runtime.rimWorldVersion
        rimBridgeServerVersion = $script:Report.runtime.rimBridgeServerVersion
        rimBridgeServerSdkVersion = $script:Report.runtime.rimBridgeServerSdkVersion
        devBridge2Version = $script:Report.runtime.devBridge2.productVersion
        devBridge2Commit = $script:Report.runtime.devBridge2.commit
        rimTestCommit = $script:Report.runtime.rimTest.commit
        fixtureProject = $Project
        fixtureModPackageId = $script:Report.fixture.modPackageId
        fixtureDeploymentTarget = $script:Report.fixture.deploymentTarget
        fixtureArtifactSha256 = $script:Report.fixture.builtArtifactSha256
        verifiedAtUtc = [DateTime]::UtcNow.ToString('o')
        result = 'pass'
        workflowId = $workflowId
        generation = [int]$script:Report.runtime.generation
        capabilitiesExpected = @($script:Report.capabilities.expected)
        capabilitiesObserved = @($script:Report.capabilities.observed | Select-Object -First 100)
        evidenceIds = @($script:Report.ui.evidenceId, $script:Report.diagnostic.rimError.diagnosticId) | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } | Select-Object -First 8
        diagnosticCorrelation = [bool]$script:Report.diagnostic.rimError.correlated
    }
    $key = ([string]$record.rimWorldVersion + '|' + [string]$record.rimBridgeServerVersion + '|' + [string]$record.rimBridgeServerSdkVersion)
    $existing = @((Get-Value $server 'testedVersions'))
    $kept = @($existing | Where-Object {
        $candidateKey = ([string](Get-Value $_ 'rimWorldVersion') + '|' + [string](Get-Value $_ 'rimBridgeServerVersion') + '|' + [string](Get-Value $_ 'rimBridgeServerSdkVersion'))
        $candidateKey -ne $key
    })
    $server.testedVersions = @($kept + $record)
    $server.supportStatement = 'Only the exact runtime tuples recorded in testedVersions were verified by the DevBridge-owned live-stack smoke; no other versions are claimed.'
    $metadata | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $CompatibilityPath -Encoding utf8
    $script:Report.compatibility.updated = $true
    $script:Report.compatibility.recordKey = $key
}

function Cleanup-OwnerState {
    $script:Report.cleanup.attempted = $true
    if ([string]::IsNullOrWhiteSpace($script:LeaseId)) {
        $script:Report.cleanup.activeLeaseConfirmedAbsent = $true
        return
    }
    try {
        if ($script:Report.fixture.cleanupDeferred) {
            $null = Invoke-OwnerRequired @('ensure-ready', $script:LeaseId, '--json') 'cleanup' 'LIVE_CLEANUP_REPAIR_FAILED'
            $null = Invoke-OwnerRequired @('wait-ready', '--json') 'cleanup' 'LIVE_CLEANUP_READINESS_FAILED'
        }
        $registrationReleasedByTransaction = [bool](Get-NestedValue $script:TransactionReport @('cleanup', 'registrationReleased'))
        if (-not $registrationReleasedByTransaction -and -not [string]::IsNullOrWhiteSpace($script:RegistrationId)) {
            $release = Invoke-OwnerRequired @('project', 'release', $script:RegistrationId, '--json') 'cleanup' 'LIVE_CLEANUP_REGISTRATION_RELEASE_FAILED'
            if ((Get-Value $release 'success') -ne $true) {
                throw 'DevBridge did not confirm release of the smoke project registration.'
            }
            $registrationReleasedByTransaction = $true
        }
        $script:Report.cleanup.registrationReleased = $registrationReleasedByTransaction
        $null = Invoke-OwnerRequired @('test', 'end', $script:LeaseId, '--json') 'cleanup' 'LIVE_CLEANUP_LEASE_END_FAILED'
        $script:Report.cleanup.leaseEnded = $true
        $status = Invoke-OwnerRequired @('status', '--json') 'cleanup' 'LIVE_CLEANUP_STATUS_FAILED'
        $script:Report.cleanup.state = [string](Get-Value $status 'state')
        $leases = @((Get-Value $status 'leases'))
        $script:Report.cleanup.activeLeaseConfirmedAbsent = -not ($leases | Where-Object { [string](Get-Value $_ 'id') -eq $script:LeaseId })
        if (-not $script:Report.cleanup.activeLeaseConfirmedAbsent) {
            Set-Failure 'LIVE_CLEANUP_LEASE_REMAINS' 'The smoke lease remained active after owner cleanup.' 'cleanup'
        }
    } catch {
        $script:Report.cleanup.error = Limit-Text $_.Exception.Message
        if ($script:CoreSuccess) {
            $script:CoreSuccess = $false
            $script:Report.status = 'fail'
            $script:Report.failure = [ordered]@{
                phase = 'cleanup'
                code = 'LIVE_CLEANUP_FAILED'
                message = $script:Report.cleanup.error
                nextAction = 'inspect DevBridge.cmd status --json before another run'
            }
        }
    }
}

try {
    Initialize-Preflight
    if ($Plan) {
        $script:Report.status = 'plan'
        $script:Report.success = $true
        $script:CoreSuccess = $true
    } else {
        Run-ModTransaction
        $semantic = Run-LiveRecipe 'live-stack-smoke'
        Assert-RecipeIdentity $semantic 'live-stack-smoke'
        $script:Report.recipe.success = $true
        $script:Report.recipe.generation = [int](Get-Value $semantic 'generation')
        $script:Report.recipe.runId = [string](Get-Value $semantic 'runId')
        $script:Report.recipe.workflowId = [string](Get-Value $semantic 'workflowId')
        $script:Report.recipe.evidenceId = [string](Get-Value $semantic 'evidenceId')
        $script:Report.recipe.operationIds = @((Get-Value $semantic 'operations') | ForEach-Object { [string](Get-Value $_ 'operationId') } | Where-Object { $_ } | Select-Object -First 8)
        $script:Report.recipe.operations = @((Get-Value $semantic 'operations') | Select-Object -First 8 | ForEach-Object {
            [ordered]@{
                tool = [string](Get-Value $_ 'tool')
                operationId = [string](Get-Value $_ 'operationId')
                workflowId = [string](Get-Value $_ 'workflowId')
                generation = [int](Get-Value $_ 'generation')
                success = [bool](Get-Value $_ 'success')
                errorCode = [string](Get-Value $_ 'errorCode')
            }
        })
        Run-CapabilityProbe
        Run-UiEvidence
        Run-DiagnosticCorrelation
        $script:CoreSuccess = $true
    }
} catch {
    if ($null -eq $script:Report.failure) {
        $script:Report.status = 'fail'
        $script:Report.failure = [ordered]@{
            phase = 'unexpected'
            code = 'LIVE_SMOKE_FAILED'
            message = Limit-Text $_.Exception.Message
            nextAction = 'inspect the compact smoke report and DevBridge status'
        }
    }
    $script:CoreSuccess = $false
} finally {
    if (-not $script:PlanOnly) {
        Cleanup-OwnerState
        if ($script:CoreSuccess -and $script:Report.cleanup.leaseEnded -and $script:Report.cleanup.activeLeaseConfirmedAbsent) {
            try {
                Update-CompatibilityMetadata
                $script:Report.success = $true
                $script:Report.status = 'pass'
            } catch {
                $script:Report.success = $false
                $script:Report.status = 'fail'
                $script:Report.failure = [ordered]@{
                    phase = 'compatibility'
                    code = 'LIVE_COMPATIBILITY_UPDATE_FAILED'
                    message = Limit-Text $_.Exception.Message
                    nextAction = 'repair compatibility metadata and rerun the live smoke'
                }
            }
        }
    }
    foreach ($tempFile in $script:TempFiles) {
        try { Remove-Item -LiteralPath $tempFile -Force -ErrorAction SilentlyContinue } catch { }
    }
    [Environment]::SetEnvironmentVariable('DEVBRIDGE_AGENT', $oldAgent, 'Process')
    [Environment]::SetEnvironmentVariable('DEVBRIDGE_SESSION', $oldSession, 'Process')
    $script:Report.finishedAtUtc = [DateTime]::UtcNow.ToString('o')
    $outputJson = ConvertTo-Json -InputObject $script:Report -Compress -Depth 24
    if ($outputJson.Length -gt $script:MaxOutputChars) {
        $script:Report.preflight.checks = [ordered]@{ status = [string]$script:Report.preflight.status }
        $script:Report.fixture.descriptor = [IO.Path]::GetFileName($descriptorPath)
        $script:Report.runtime.rimTest.path = [IO.Path]::GetFileName($RimLiaisonPath)
        $script:Report.capabilities.observed = @($script:Report.capabilities.observed | Select-Object -First 8)
        $script:Report.recipe.operations = @($script:Report.recipe.operations | Select-Object -First 2)
        $outputJson = ConvertTo-Json -InputObject $script:Report -Compress -Depth 24
    }
    if (-not [string]::IsNullOrWhiteSpace($ReportPath)) {
        try {
            $reportDirectory = Split-Path $ReportPath -Parent
            if (-not [string]::IsNullOrWhiteSpace($reportDirectory)) { New-Item -ItemType Directory -Force -Path $reportDirectory | Out-Null }
            Set-Content -LiteralPath $ReportPath -Value $outputJson -Encoding utf8
        } catch {
            if ($script:Report.status -eq 'pass') {
                $script:Report.status = 'fail'
                $script:Report.success = $false
                $script:Report.failure = [ordered]@{
                    phase = 'report'
                    code = 'LIVE_REPORT_WRITE_FAILED'
                    message = Limit-Text $_.Exception.Message
                    nextAction = 'provide a writable report path and rerun the smoke'
                }
                $outputJson = ConvertTo-Json -InputObject $script:Report -Compress -Depth 24
            }
        }
    }
    Write-Output $outputJson
}

if ($script:Report.status -eq 'pass' -or $script:Report.status -eq 'plan') { exit 0 }
if ($script:Report.status -eq 'blocked') { exit 2 }
exit 1
