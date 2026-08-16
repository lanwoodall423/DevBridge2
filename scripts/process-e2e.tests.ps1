[CmdletBinding()]
param(
    [switch]$KeepRoots
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Set-Location -LiteralPath $repoRoot

$coordinatorProject = Join-Path $repoRoot 'Source\Coordinator\DevBridge.Coordinator.csproj'
$fakeProject = Join-Path $repoRoot 'Source\FakeRimWorld\FakeRimWorld.csproj'
$configuration = 'Release'
$coordinatorExe = Join-Path $repoRoot 'Source\Coordinator\bin\Release\net8.0\DevBridge.Coordinator.exe'
$fakeExe = Join-Path $repoRoot 'Source\FakeRimWorld\bin\Release\net8.0\DevBridge.FakeRimWorld.exe'
$alwaysOnPackages = @(
    'zetrith.prepatcher',
    'brrainz.harmony',
    'taranchuk.fastergameloading',
    'ilyvion.loadingprogress',
    'ludeon.rimworld',
    'ludeon.rimworld.royalty',
    'ludeon.rimworld.ideology',
    'ludeon.rimworld.biotech',
    'ludeon.rimworld.anomaly',
    'ludeon.rimworld.odyssey',
    'lan.devbridge2',
    'mlie.dingongameloaded',
    'dubwise.dubsperformanceanalyzer.steam',
    'astryl.moderndevtools',
    'brrainz.rimbridgeserver'
)
$diagnosticTextLimit = 4000
$script:CurrentFixture = $null
$script:LastBridgeResponse = $null

if (-not (Test-Path -LiteralPath $coordinatorExe -PathType Leaf) -or
    -not (Test-Path -LiteralPath $fakeExe -PathType Leaf)) {
    throw "Release process-level outputs are missing. Build $coordinatorProject and $fakeProject first."
}

function Write-Utf8File {
    param([Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Text)
    [System.IO.File]::WriteAllText($Path, $Text, [System.Text.UTF8Encoding]::new($false))
}

function Write-InstalledMetadata {
    param([Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$PackageId)
    $directory = Join-Path $Root $PackageId
    $about = Join-Path $directory 'About'
    New-Item -ItemType Directory -Force -Path $about | Out-Null
    $xml = @"
<ModMetaData>
  <name>Fake $PackageId</name>
  <packageId>$PackageId</packageId>
  <author>DevBridge process test</author>
  <supportedVersions><li>1.6</li></supportedVersions>
</ModMetaData>
"@
    Write-Utf8File (Join-Path $about 'About.xml') $xml
}

function New-Fixture {
    param([Parameter(Mandatory = $true)][string]$Name,
        [string]$ScenarioName = 'ready-immediately',
        [ValidateSet('off', 'optional', 'required')][string]$RimBridgeMode = 'off',
        [hashtable]$ScenarioOverrides = @{})

    $workRoot = Join-Path $repoRoot '.process-e2e-temp'
    New-Item -ItemType Directory -Force -Path $workRoot | Out-Null
    $fixtureId = [Guid]::NewGuid().ToString('N')
    $root = Join-Path $workRoot ('scenario-' + $Name + '-' + $fixtureId)
    $runtime = Join-Path $root 'Runtime'
    $modsRoot = Join-Path $root 'InstalledMods'
    $logPath = Join-Path $root 'Player.log'
    $readyGatePath = Join-Path $runtime 'ready.gate'
    $readyWaitingPath = $readyGatePath + '.waiting'
    New-Item -ItemType Directory -Force -Path $runtime, $modsRoot | Out-Null

    foreach ($packageId in $alwaysOnPackages) {
        Write-InstalledMetadata -Root $modsRoot -PackageId $packageId
    }

    $activeMods = ($alwaysOnPackages | ForEach-Object { "    <li>$_</li>" }) -join "`n"
    Write-Utf8File (Join-Path $root 'ModsConfig.xml') @"
<ModsConfigData>
  <activeMods>
$activeMods
  </activeMods>
</ModsConfigData>
"@

    $scenario = [ordered]@{
        schemaVersion = 1
        contract = 'devbridge-fake-rimworld/v1'
        name = $ScenarioName
    }
    foreach ($key in $ScenarioOverrides.Keys) {
        $scenario[$key] = $ScenarioOverrides[$key]
    }
    if ($ScenarioName -eq 'ready-delayed' -and -not $scenario.Contains('readyGatePath')) {
        $scenario['readyGatePath'] = $readyGatePath
        $scenario['readyWaitingPath'] = $readyWaitingPath
    }
    Write-Utf8File (Join-Path $runtime 'fake-rimworld-scenario.json') (($scenario | ConvertTo-Json -Depth 8))

    $recipesSource = Join-Path $repoRoot 'TestRecipes'
    Copy-Item -LiteralPath $recipesSource -Destination (Join-Path $root 'TestRecipes') -Recurse

    $slot = 'e2e-' + $fixtureId.Substring(0, 8)
    $env:DEVBRIDGE_TEST_RIMWORLD_PATH = $fakeExe
    $env:DEVBRIDGE_TEST_INSTALLED_MODS_ROOTS = $modsRoot
    $env:DEVBRIDGE_TEST_MODS_CONFIG = Join-Path $root 'ModsConfig.xml'
    $env:DEVBRIDGE_TEST_PLAYER_LOG = $logPath
    $env:DEVBRIDGE_PLAYER_LOG = $logPath
    $env:DEVBRIDGE_TEST_READINESS_TIMEOUT_SECONDS = '2'
    $env:DEVBRIDGE_RIMBRIDGE_MODE = $RimBridgeMode
    $env:DEVBRIDGE_AGENT = 'process-e2e'

    $fixture = [pscustomobject]@{
        Name = $Name
        ScenarioName = $ScenarioName
        Root = $root
        Runtime = $runtime
        Slot = $slot
        LogPath = $logPath
        ScenarioPath = Join-Path $runtime 'fake-rimworld-scenario.json'
        ReadyGatePath = $readyGatePath
        ReadyWaitingPath = $readyWaitingPath
        FakeExecutable = $fakeExe
        LastResponseBeforeCleanup = $null
    }
    $script:CurrentFixture = $fixture
    return $fixture
}

function Limit-DiagnosticText {
    param([AllowEmptyString()][string]$Text,
        [int]$Limit = $diagnosticTextLimit)
    if ([string]::IsNullOrEmpty($Text)) { return '<empty>' }
    $normalized = $Text.Trim()
    if ($normalized.Length -le $Limit) { return $normalized }
    return $normalized.Substring(0, $Limit) + "`n...[truncated to $Limit characters]"
}

function Format-Command {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)
    return (($Arguments | ForEach-Object {
        $value = [string]$_
        if ($value -match '[\s"]') { '"' + $value.Replace('"', '\"') + '"' } else { $value }
    }) -join ' ')
}

function Get-DiagnosticArtifactPaths {
    param([Parameter(Mandatory = $true)][string]$Root)
    $runtime = Join-Path $Root 'Runtime'
    $paths = [System.Collections.Generic.List[string]]::new()
    foreach ($path in @(
        $Root,
        $runtime,
        (Join-Path $runtime 'coordinator-events.jsonl'),
        (Join-Path $runtime 'state.json'),
        (Join-Path $runtime 'readiness.json'),
        (Join-Path $runtime 'quicktest-failure.json'),
        (Join-Path $Root 'Player.log'),
        (Join-Path $Root 'ModsConfig.xml')
    )) {
        $paths.Add([IO.Path]::GetFullPath($path))
    }
    if (Test-Path -LiteralPath $runtime -PathType Container) {
        Get-ChildItem -LiteralPath $runtime -Filter 'e2e-result-*.json' -File -ErrorAction SilentlyContinue |
            ForEach-Object { $paths.Add($_.FullName) }
    }
    return @($paths | Select-Object -Unique)
}

function Get-DiagnosticArtifactText {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    try {
        return Limit-DiagnosticText ([IO.File]::ReadAllText($Path))
    } catch {
        return "<could not read: $($_.Exception.Message)>"
    }
}

function Format-BridgeFailure {
    param([Parameter(Mandatory = $true)][string]$Scenario,
        [string]$HostScenario,
        [Parameter(Mandatory = $true)][string]$Stage,
        [Parameter(Mandatory = $true)][string]$ExceptionMessage,
        $Response,
        [Parameter(Mandatory = $true)][string]$Root)
    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("scenario: $Scenario")
    if (-not [string]::IsNullOrWhiteSpace($HostScenario)) {
        $lines.Add("fake host scenario: $HostScenario")
    }
    $lines.Add("stage: $Stage")
    $lines.Add("exception: $(Limit-DiagnosticText $ExceptionMessage)")
    if ($null -ne $Response) {
        $lines.Add("command: $($Response.Command)")
        $lines.Add("exit code: $($Response.ExitCode)")
        $lines.Add("result path: $($Response.ResultPath)")
        $lines.Add("result: $(Limit-DiagnosticText ([string]$Response.Output))")
        $lines.Add("child stdout: $(Limit-DiagnosticText ([string]$Response.Stdout))")
        $lines.Add("child stderr: $(Limit-DiagnosticText ([string]$Response.Stderr))")
    }
    $lines.Add('runtime artifacts:')
    foreach ($path in @(Get-DiagnosticArtifactPaths $Root)) {
        $lines.Add("- $path")
        $artifactText = Get-DiagnosticArtifactText $path
        if ($null -ne $artifactText) {
            $lines.Add("  bounded contents: $artifactText")
        }
        elseif (-not (Test-Path -LiteralPath $path)) {
            $lines.Add('  status: missing')
        }
    }
    return ($lines -join "`n")
}

function Read-BoundedProcessStream {
    param([Parameter(Mandatory = $true)]$Task,
        [Parameter(Mandatory = $true)][string]$Name)
    try {
        if (-not $Task.Wait(2000)) {
            return "<$Name stream did not close within 2000 ms>"
        }
        return [string]$Task.GetAwaiter().GetResult()
    } catch {
        return "<$Name read failed: $($_.Exception.Message)>"
    }
}

function Get-JsonResponse {
    param([Parameter(Mandatory = $true)][string]$Text)
    $trimmed = $Text.Trim()
    try { return $trimmed | ConvertFrom-Json -Depth 30 } catch { }
    $lines = @($trimmed -split "`r?`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    for ($index = $lines.Count - 1; $index -ge 0; $index--) {
        try { return $lines[$index] | ConvertFrom-Json -Depth 30 } catch { }
    }
    throw "Command did not return JSON: $Text"
}

function Invoke-Bridge {
    param([Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Slot,
        [Parameter(Mandatory = $true)][string[]]$Arguments)
    $cliArguments = @('--root', $Root, '--runtime-slot', $Slot) + $Arguments
    if ($cliArguments -notcontains '--json') {
        $cliArguments += '--json'
    }
    $resultPath = Join-Path $Root ("Runtime\e2e-result-" + [Guid]::NewGuid().ToString('N') + '.json')
    $start = [System.Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $coordinatorExe
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Environment['DEVBRIDGE_TEST_RESULT_FILE'] = $resultPath
    foreach ($argument in $cliArguments) { [void]$start.ArgumentList.Add([string]$argument) }
    $command = Format-Command (@($start.FileName) + $cliArguments)
    $process = [System.Diagnostics.Process]::Start($start)
    $stdoutDrain = $process.StandardOutput.ReadToEndAsync()
    $stderrDrain = $process.StandardError.ReadToEndAsync()
    $timedOut = -not $process.WaitForExit(65000)
    if ($timedOut) {
        try { $process.Kill($true) } catch { try { $process.Kill() } catch { } }
        try { $process.WaitForExit(5000) } catch { }
    }
    $exitCode = if ($timedOut) { 'TIMEOUT' } else { $process.ExitCode }
    $stdout = Read-BoundedProcessStream $stdoutDrain 'stdout'
    $stderr = Read-BoundedProcessStream $stderrDrain 'stderr'
    $process.Dispose()
    $output = if (Test-Path -LiteralPath $resultPath) {
        [System.IO.File]::ReadAllText($resultPath).Trim()
    } else { '' }
    $json = $null
    try { $json = Get-JsonResponse -Text $output } catch { }
    $response = [pscustomobject]@{
        Command = $command
        Output = $output
        Stdout = $stdout
        Stderr = $stderr
        ExitCode = $exitCode
        Json = $json
        ResultPath = $resultPath
        Stage = $null
    }
    $failed = $timedOut -or $exitCode -ne 0 -or $null -eq $json -or
        (($json.PSObject.Properties.Name -contains 'success') -and -not [bool]$json.success)
    if (-not $failed) {
        Remove-Item -LiteralPath $resultPath -Force -ErrorAction SilentlyContinue
    }
    $script:LastBridgeResponse = $response
    if ($timedOut) {
        throw (Format-BridgeFailure -Scenario $script:CurrentFixture.Name `
            -HostScenario $script:CurrentFixture.ScenarioName -Stage 'CLI timeout' `
            -ExceptionMessage 'CLI command timed out after 65000 ms.' -Response $response -Root $Root)
    }
    return $response
}

function Assert-Success {
    param([Parameter(Mandatory = $true)]$Response,
        [string]$Context = 'command')
    if ($Response.ExitCode -ne 0 -or $null -eq $Response.Json -or
        ($Response.Json.PSObject.Properties.Name -contains 'success' -and
            -not [bool]$Response.Json.success)) {
        $Response.Stage = $Context
        throw "$Context failed (exit $($Response.ExitCode)): $($Response.Output)"
    }
}

function Get-Status {
    param([Parameter(Mandatory = $true)]$Fixture)
    $response = Invoke-Bridge -Root $Fixture.Root -Slot $Fixture.Slot -Arguments @('status')
    Assert-Success $response 'status'
    return $response.Json
}

function Wait-GameState {
    param([Parameter(Mandatory = $true)]$Fixture,
        [Parameter(Mandatory = $true)][string]$Expected,
        [int]$TimeoutMs = 5000)
    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMs)
    do {
        try {
            $status = Get-Status $Fixture
            if ([string]$status.gameState -eq $Expected) { return $status }
        } catch { }
        Start-Sleep -Milliseconds 50
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out waiting for gameState=$Expected."
}

function Wait-File {
    param([Parameter(Mandatory = $true)][string]$Path, [int]$TimeoutMs = 5000)
    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMs)
    do {
        if (Test-Path -LiteralPath $Path -PathType Leaf) { return }
        Start-Sleep -Milliseconds 25
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out waiting for synchronization file: $Path"
}

function Get-FakePid {
    param([Parameter(Mandatory = $true)]$Status)
    $processId = [int]$Status.rimworldPid
    if ($processId -le 0) { throw 'Coordinator did not publish a fake RimWorld PID.' }
    return $processId
}

function Start-Ready {
    param([Parameter(Mandatory = $true)]$Fixture)
    $response = Invoke-Bridge -Root $Fixture.Root -Slot $Fixture.Slot -Arguments @('restart', '--projects', 'none')
    Assert-Success $response 'restart'
    $ready = Wait-GameState $Fixture 'READY'
    return $ready
}

function Stop-Ready {
    param([Parameter(Mandatory = $true)]$Fixture)
    $lease = Invoke-Bridge -Root $Fixture.Root -Slot $Fixture.Slot -Arguments @('test', 'begin')
    Assert-Success $lease 'test begin'
    $leaseId = [string]$lease.Json.leaseId
    if ([string]::IsNullOrWhiteSpace($leaseId)) { throw 'test begin returned no leaseId.' }
    $stop = Invoke-Bridge -Root $Fixture.Root -Slot $Fixture.Slot -Arguments @('stop', $leaseId)
    Assert-Success $stop 'stop'
    Wait-GameState $Fixture 'STOPPED' | Out-Null
}

function Shutdown-Coordinator {
    param([Parameter(Mandatory = $true)]$Fixture)
    $response = Invoke-Bridge -Root $Fixture.Root -Slot $Fixture.Slot -Arguments @('coordinator', 'shutdown')
    Assert-Success $response 'coordinator shutdown'
}

function Remove-Fixture {
    param([Parameter(Mandatory = $true)]$Fixture)
    $Fixture.LastResponseBeforeCleanup = $script:LastBridgeResponse
    $processId = 0
    try {
        $status = Get-Status $Fixture
        $processId = [int]$status.rimworldPid
    } catch { }
    try {
        Shutdown-Coordinator $Fixture
    } catch { }
    if ($processId -gt 0) {
        Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue
    }
}

function Complete-Fixture {
    param([Parameter(Mandatory = $true)]$Fixture,
        [Parameter(Mandatory = $true)][bool]$Preserve)
    if (-not $Preserve -and -not $KeepRoots) {
        Remove-Item -LiteralPath $Fixture.Root -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-Case {
    param([Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][scriptblock]$Body)
    $timer = [System.Diagnostics.Stopwatch]::StartNew()
    $script:CurrentFixture = $null
    $script:LastBridgeResponse = $null
    try {
        & $Body
        $timer.Stop()
        if ($null -ne $script:CurrentFixture) {
            Complete-Fixture $script:CurrentFixture $false
        }
        Write-Host ("PASS {0} ({1} ms)" -f $Name, $timer.ElapsedMilliseconds)
        return [pscustomobject]@{ Name = $Name; Passed = $true; DurationMs = $timer.ElapsedMilliseconds; Error = $null }
    } catch {
        $timer.Stop()
        $fixture = $script:CurrentFixture
        $response = if ($null -ne $fixture -and $null -ne $fixture.LastResponseBeforeCleanup) {
            $fixture.LastResponseBeforeCleanup
        } else { $script:LastBridgeResponse }
        $stage = if ($null -ne $response -and -not [string]::IsNullOrWhiteSpace([string]$response.Stage)) {
            [string]$response.Stage
        } else { 'scenario assertion' }
        $root = if ($null -ne $fixture) { $fixture.Root } else { $repoRoot }
        $hostScenario = if ($null -ne $fixture) { $fixture.ScenarioName } else { $null }
        $report = Format-BridgeFailure -Scenario $Name -HostScenario $hostScenario -Stage $stage `
            -ExceptionMessage $_.Exception.Message -Response $response -Root $root
        if ($null -ne $fixture) {
            Complete-Fixture $fixture $true
        }
        Write-Host ("FAIL {0} ({1} ms):`n{2}" -f $Name, $timer.ElapsedMilliseconds, $report)
        return [pscustomobject]@{
            Name = $Name
            Passed = $false
            DurationMs = $timer.ElapsedMilliseconds
            Error = $report
            ArtifactPaths = @(Get-DiagnosticArtifactPaths $root)
        }
    }
}

$results = [System.Collections.Generic.List[object]]::new()

$results.Add((Invoke-Case 'cold start through CLI and named pipe' {
    $fixture = New-Fixture 'cold-start'
    try {
        $status = Start-Ready $fixture
        if ((Get-FakePid $status) -le 0) { throw 'No process identity was published.' }
    } finally { Remove-Fixture $fixture }
}))

$results.Add((Invoke-Case 'restart replaces the real child process' {
    $fixture = New-Fixture 'restart'
    try {
        $first = Start-Ready $fixture
        $firstPid = Get-FakePid $first
        $second = Start-Ready $fixture
        if ((Get-FakePid $second) -eq $firstPid) { throw 'Restart retained the original child PID.' }
    } finally { Remove-Fixture $fixture }
}))

$results.Add((Invoke-Case 'graceful stop uses the lease and child boundary' {
    $fixture = New-Fixture 'graceful-stop' -ScenarioName 'graceful-stop'
    try {
        Start-Ready $fixture | Out-Null
        Stop-Ready $fixture
    } finally { Remove-Fixture $fixture }
}))

$results.Add((Invoke-Case 'hung stop fails closed before force termination' {
    $fixture = New-Fixture 'hung-stop' -ScenarioName 'hung-stop'
    try {
        $status = Start-Ready $fixture
        $lease = Invoke-Bridge -Root $fixture.Root -Slot $fixture.Slot -Arguments @('test', 'begin')
        Assert-Success $lease 'test begin'
        $stop = Invoke-Bridge -Root $fixture.Root -Slot $fixture.Slot -Arguments @('stop', [string]$lease.Json.leaseId)
        if ($stop.ExitCode -eq 0 -or [string]$stop.Json.errorCode -ne 'STOP_FAILED') {
            throw "Hung stop was not reported as STOP_FAILED: $($stop.Output)"
        }
        if (-not (Get-Process -Id (Get-FakePid $status) -ErrorAction SilentlyContinue)) {
            throw 'Hung stop unexpectedly terminated the fake child before force cleanup.'
        }
    } finally { Remove-Fixture $fixture }
}))

$results.Add((Invoke-Case 'coordinator shutdown preserves the child process' {
    $fixture = New-Fixture 'coordinator-shutdown'
    try {
        $status = Start-Ready $fixture
        $processId = Get-FakePid $status
        Shutdown-Coordinator $fixture
        if (-not (Get-Process -Id $processId -ErrorAction SilentlyContinue)) {
            throw 'Coordinator shutdown stopped the fake RimWorld child.'
        }
    } finally { Remove-Fixture $fixture }
}))

$results.Add((Invoke-Case 'lazy coordinator recovery preserves ready child identity' {
    $fixture = New-Fixture 'coordinator-recovery'
    try {
        $status = Start-Ready $fixture
        $processId = Get-FakePid $status
        Shutdown-Coordinator $fixture
        $recovered = Get-Status $fixture
        if ([int]$recovered.rimworldPid -ne $processId -or [string]$recovered.gameState -ne 'READY') {
            throw 'Lazy coordinator recovery did not preserve the ready child.'
        }
    } finally { Remove-Fixture $fixture }
}))

$results.Add((Invoke-Case 'delayed readiness is observable through agent wait-event' {
    $fixture = New-Fixture 'delayed-ready' -ScenarioName 'ready-delayed' -ScenarioOverrides @{ readyAfterMs = 500 }
    try {
        $restart = Start-Process -FilePath $coordinatorExe -ArgumentList @('--root', $fixture.Root,
            '--runtime-slot', $fixture.Slot, 'restart', '--projects', 'none', '--json') -PassThru -WindowStyle Hidden
        try {
            Wait-File $fixture.ReadyWaitingPath
            $cursor = Invoke-Bridge -Root $fixture.Root -Slot $fixture.Slot -Arguments @('agent', 'snapshot')
            Assert-Success $cursor 'agent snapshot before readiness release'
            $cursorSequence = [int64]$cursor.Json.sequence
            $cursorEpoch = [string]$cursor.Json.epoch
            New-Item -ItemType File -Force -Path $fixture.ReadyGatePath | Out-Null
            $event = Invoke-Bridge -Root $fixture.Root -Slot $fixture.Slot -Arguments @(
                'agent', 'wait-event', '--since-seq', [string]$cursorSequence,
                '--epoch', $cursorEpoch, '--until', 'ready', '--timeout-ms', '3000')
            Assert-Success $event 'agent wait-event'
            if ([string]$event.Json.result -ne 'condition-met' -or
                [int64]$event.Json.toSeq -le $cursorSequence) {
                throw "Agent wait-event did not observe a later ready transition: $($event.Output)"
            }
        } finally {
            Wait-Process -Id $restart.Id -Timeout 5 -ErrorAction SilentlyContinue
        }
        Wait-GameState $fixture 'READY' | Out-Null
    } finally { Remove-Fixture $fixture }
}))

$results.Add((Invoke-Case 'never-ready timeout is reported without a false ready state' {
    $fixture = New-Fixture 'never-ready' -ScenarioName 'never-ready'
    try {
        $response = Invoke-Bridge -Root $fixture.Root -Slot $fixture.Slot -Arguments @(
            'restart', '--projects', 'none')
        if ($response.ExitCode -eq 0 -or [string]$response.Json.errorCode -notmatch 'READINESS|TIMEOUT') {
            throw "Never-ready scenario unexpectedly succeeded: $($response.Output)"
        }
    } finally { Remove-Fixture $fixture }
}))

$results.Add((Invoke-Case 'crash-before-readiness preserves process failure attribution' {
    $fixture = New-Fixture 'crash-before-ready' -ScenarioName 'crash-before-ready'
    try {
        $response = Invoke-Bridge -Root $fixture.Root -Slot $fixture.Slot -Arguments @(
            'restart', '--projects', 'none')
        if ($response.ExitCode -eq 0 -or [string]$response.Json.errorCode -notmatch 'PROCESS|LAUNCH|READINESS') {
            throw "Crash-before-ready scenario was not rejected: $($response.Output)"
        }
    } finally { Remove-Fixture $fixture }
}))

$results.Add((Invoke-Case 'quicktest recipe succeeds through process IPC' {
    $fixture = New-Fixture 'recipe-success'
    try {
        $response = Invoke-Bridge -Root $fixture.Root -Slot $fixture.Slot -Arguments @(
            'test', 'recipe', 'run', 'quicktest-smoke')
        Assert-Success $response 'quicktest recipe'
    } finally { Remove-Fixture $fixture }
}))

$results.Add((Invoke-Case 'quicktest failure writes bounded failure evidence' {
    $fixture = New-Fixture 'recipe-failure' -ScenarioName 'quicktest-failure'
    try {
        $response = Invoke-Bridge -Root $fixture.Root -Slot $fixture.Slot -Arguments @(
            'test', 'recipe', 'run', 'quicktest-smoke')
        if ($response.ExitCode -eq 0 -or [string]::IsNullOrWhiteSpace([string]$response.Json.failureFingerprint)) {
            throw "Quicktest failure did not return a normalized fingerprint: $($response.Output)"
        }
        $evidence = Invoke-Bridge -Root $fixture.Root -Slot $fixture.Slot -Arguments @('agent', 'snapshot')
        Assert-Success $evidence 'failure snapshot'
        if ([string]::IsNullOrWhiteSpace([string]$evidence.Json.failure.evidenceId)) {
            throw 'Failure snapshot did not expose lazy evidence ID.'
        }
    } finally { Remove-Fixture $fixture }
}))

$results.Add((Invoke-Case 'equivalent repeated recipe failure short-circuits' {
    $fixture = New-Fixture 'recipe-repeat' -ScenarioName 'quicktest-failure'
    try {
        $first = Invoke-Bridge -Root $fixture.Root -Slot $fixture.Slot -Arguments @(
            'test', 'recipe', 'run', 'quicktest-smoke')
        if ($first.ExitCode -eq 0) { throw 'Initial recipe failure unexpectedly succeeded.' }
        $second = Invoke-Bridge -Root $fixture.Root -Slot $fixture.Slot -Arguments @(
            'test', 'recipe', 'run', 'quicktest-smoke')
        if ([string]$second.Json.errorCode -ne 'AUTONOMOUS_REPEATED_FAILURE') {
            throw "Repeated recipe failure was not short-circuited: $($second.Output)"
        }
    } finally { Remove-Fixture $fixture }
}))

$results.Add((Invoke-Case 'optional RimBridge absence remains nonblocking' {
    $fixture = New-Fixture 'rimbridge-optional' -RimBridgeMode optional -ScenarioName 'rimbridge-unavailable'
    try {
        $status = Start-Ready $fixture
        if ([string]$status.gameState -ne 'READY') { throw 'Optional RimBridge absence blocked readiness.' }
    } finally { Remove-Fixture $fixture }
}))

$results.Add((Invoke-Case 'required RimBridge absence is fail-closed' {
    $fixture = New-Fixture 'rimbridge-required' -RimBridgeMode required -ScenarioName 'rimbridge-unavailable'
    try {
        $response = Invoke-Bridge -Root $fixture.Root -Slot $fixture.Slot -Arguments @(
            'restart', '--projects', 'none')
        if ($response.ExitCode -eq 0 -or [string]$response.Json.errorCode -notmatch 'RIMBRIDGE|COMPANION') {
            throw "Required RimBridge absence was not rejected: $($response.Output)"
        }
    } finally { Remove-Fixture $fixture }
}))

$results.Add((Invoke-Case 'companion generation mismatch is reported' {
    $fixture = New-Fixture 'rimbridge-mismatch' -RimBridgeMode required -ScenarioName 'rimbridge-companion-generation-mismatch'
    try {
        $response = Invoke-Bridge -Root $fixture.Root -Slot $fixture.Slot -Arguments @(
            'restart', '--projects', 'none')
        if ($response.ExitCode -eq 0 -or [string]$response.Json.errorCode -notmatch 'RIMBRIDGE|COMPANION|GENERATION') {
            throw "Companion mismatch was not rejected: $($response.Output)"
        }
    } finally { Remove-Fixture $fixture }
}))

$results.Add((Invoke-Case 'required RimBridge verifies GABP companion with delayed response' {
    $fixture = New-Fixture 'rimbridge-ready' -RimBridgeMode required -ScenarioName 'rimbridge-ready' `
        -ScenarioOverrides @{ ResponseDelayMs = 25 }
    try {
        $status = Start-Ready $fixture
        if ([string]$status.gameState -ne 'READY' -or
            [bool]$status.rimBridge.companionVerified -ne $true) {
            throw "Required RimBridge companion was not verified through GABP: $($status | ConvertTo-Json -Depth 8 -Compress)"
        }
    } finally { Remove-Fixture $fixture }
}))

$results.Add((Invoke-Case 'RimBridge authentication failure remains explicit' {
    $fixture = New-Fixture 'rimbridge-auth' -RimBridgeMode required -ScenarioName 'rimbridge-auth-failure'
    try {
        $response = Invoke-Bridge -Root $fixture.Root -Slot $fixture.Slot -Arguments @(
            'restart', '--projects', 'none')
        Assert-Success $response 'RimBridge auth scenario'
        if ([bool]$response.Json.rimBridge.companionVerified -or
            [string]::IsNullOrWhiteSpace([string]$response.Json.rimBridge.companionError)) {
            throw "RimBridge authentication failure was not kept explicit: $($response.Output)"
        }
    } finally { Remove-Fixture $fixture }
}))

$results.Add((Invoke-Case 'missing companion tool remains explicit' {
    $fixture = New-Fixture 'rimbridge-tool-missing' -RimBridgeMode required -ScenarioName 'rimbridge-companion-unavailable'
    try {
        $response = Invoke-Bridge -Root $fixture.Root -Slot $fixture.Slot -Arguments @(
            'restart', '--projects', 'none')
        Assert-Success $response 'missing companion tool scenario'
        if ([bool]$response.Json.rimBridge.companionVerified -or
            [string]::IsNullOrWhiteSpace([string]$response.Json.rimBridge.companionError)) {
            throw "Missing companion tool was not kept explicit: $($response.Output)"
        }
    } finally { Remove-Fixture $fixture }
}))

$results.Add((Invoke-Case 'semantic logs are bounded and deduplicated' {
    $fixture = New-Fixture 'semantic-logs' -ScenarioName 'repeat-log-errors'
    try {
        Start-Ready $fixture | Out-Null
        Start-Sleep -Milliseconds 100
        $logs = Invoke-Bridge -Root $fixture.Root -Slot $fixture.Slot -Arguments @('logs', 'query', '--limit', '20')
        Assert-Success $logs 'logs query'
        if ([int]$logs.Json.records.Count -lt 1 -or
            [int64]$logs.Json.semanticBytes -ge [int64]$logs.Json.rawBytes) {
            throw "Semantic log compaction was not demonstrated: $($logs.Output)"
        }
    } finally { Remove-Fixture $fixture }
}))

$results.Add((Invoke-Case 'coordinator-only refresh leaves fake child running' {
    $fixture = New-Fixture 'coordinator-refresh'
    try {
        $status = Start-Ready $fixture
        $processId = Get-FakePid $status
        Shutdown-Coordinator $fixture
        $reloaded = Get-Status $fixture
        if ([int]$reloaded.rimworldPid -ne $processId -or
            -not (Get-Process -Id $processId -ErrorAction SilentlyContinue)) {
            throw 'Coordinator-only refresh changed the fake RimWorld process.'
        }
    } finally { Remove-Fixture $fixture }
}))

$planJson = & pwsh -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repoRoot 'scripts\dev-plan.ps1') `
    -ChangedFiles 'Source/Mod/IntegrationMarker.cs' -Json 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) { throw "Development plan command failed: $planJson" }
$plan = Get-JsonResponse $planJson
if ([string]$plan.requiredRefresh -ne 'rimworld' -or [bool]$plan.rimWorldRestartRequired -ne $true) {
    throw "Mod restart plan was not explicit: $planJson"
}
Write-Host 'PASS mod assembly plan requires RimWorld restart (process-level planning seam)'

$passed = @($results | Where-Object Passed).Count
$failed = @($results | Where-Object { -not $_.Passed }).Count
$duration = [int64](($results | Measure-Object -Property DurationMs -Sum).Sum)
Write-Host "PROCESS E2E: $passed passed, $failed failed, $duration ms total, 0 real RimWorld launches."
Write-Host ("Scenarios: " + (($results | ForEach-Object Name) -join ', '))

if ($failed -gt 0) {
    $failureSummary = ($results | Where-Object { -not $_.Passed } | ForEach-Object {
        "[$($_.Name)] artifacts: $($_.ArtifactPaths -join ', ')"
    }) -join "`n"
    throw "Process-level E2E failed: $failed scenario(s).`n$failureSummary"
}

if (-not $KeepRoots) {
    Remove-Item -LiteralPath (Join-Path $repoRoot '.process-e2e-temp') -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host 'PROCESS E2E PASS'
