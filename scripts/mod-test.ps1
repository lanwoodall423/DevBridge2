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
    deploymentStarted = $false
    deploymentCommitted = $false
    retrySafety = $null
    build = $null
    buildDiscrimination = $null
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
$script:DeploymentMutex = $null
$script:DeploymentLockAcquired = $false
$script:LegacyBackupPath = $null
$script:LegacyRootMoved = $false
$script:DeploymentPendingPath = $null

$script:BuildDiagnosticOutputLimit = 16384
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

function Limit-BuildDiagnosticText {
    param(
        [AllowNull()][string]$Text,
        [int]$Limit = $script:BuildDiagnosticOutputLimit,
        [switch]$AlreadyTruncated)
    if ([string]::IsNullOrEmpty($Text)) {
        return [pscustomobject]@{ Text = $null; Truncated = [bool]$AlreadyTruncated }
    }

    $value = $Text.Trim()
    $marker = "`n...[truncated to $Limit characters]"
    if (-not $AlreadyTruncated -and $value.Length -le $Limit) {
        return [pscustomobject]@{ Text = $value; Truncated = $false }
    }

    $prefixLength = [Math]::Max(0, $Limit - $marker.Length)
    $prefix = if ($prefixLength -eq 0) { '' } else { $value.Substring(0, [Math]::Min($prefixLength, $value.Length)) }
    return [pscustomobject]@{
        Text = $prefix + $marker
        Truncated = $true
    }
}

function Get-BuildDiagnosticSummary {
    param(
        [AllowNull()][string]$Text,
        [int]$Limit = $script:BuildDiagnosticOutputLimit)
    if ([string]::IsNullOrWhiteSpace($Text)) { return [pscustomobject]@{ Text = $null; Truncated = $false; Signature = $null } }

    $lines = @($Text -split "`r?`n")
    $codePattern = '\b(?:CS|MSB|NU)\d{3,5}\b'
    $primaryIndex = -1
    $primarySignature = $null
    for ($index = 0; $index -lt $lines.Count; $index++) {
        $line = ([string]$lines[$index]).Trim()
        if ($line -match '(?i)\bRIMWORLD_DIR\s+is\s+required\b') {
            $primaryIndex = $index
            $primarySignature = 'RIMWORLD_DIR_REQUIRED'
            break
        }
    }
    if ($primaryIndex -lt 0) {
        for ($index = 0; $index -lt $lines.Count; $index++) {
            $line = ([string]$lines[$index]).Trim()
            if ($line -match '(?i)\bfatal\s+error\b' -or
                ($line -match '(?i)\berror\b' -and $line -notmatch '(?i)\bwarning\b')) {
                $primaryIndex = $index
                break
            }
        }
    }
    if ($primaryIndex -lt 0) {
        for ($index = 0; $index -lt $lines.Count; $index++) {
            $line = ([string]$lines[$index]).Trim()
            if ($line -match $codePattern -and $line -notmatch '(?i)\bwarning\b') {
                $primaryIndex = $index
                break
            }
        }
    }
    $selected = [System.Collections.Generic.List[string]]::new()
    $selectedSet = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    if ($primaryIndex -ge 0) {
        foreach ($near in @(($primaryIndex - 1), $primaryIndex, ($primaryIndex + 1))) {
            if ($near -ge 0 -and $near -lt $lines.Count) {
                $context = ([string]$lines[$near]).Trim()
                if (-not [string]::IsNullOrWhiteSpace($context) -and $selectedSet.Add($context)) {
                    [void]$selected.Add($context)
                }
            }
        }
    }
    if ($selected.Count -eq 0) {
        foreach ($line in $lines) {
            $meaningful = ([string]$line).Trim()
            if (-not [string]::IsNullOrWhiteSpace($meaningful) -and
                $meaningful -notmatch '(?i)^(?:determining projects to restore|all projects are up-to-date|build started|build succeeded)') {
                [void]$selected.Add($meaningful)
                break
            }
        }
    }
    $codes = [System.Collections.Generic.List[string]]::new()
    $signatureLines = @($selected | Where-Object { $_ -notmatch '(?i)\bwarning\b' })
    if ($signatureLines.Count -eq 0) { $signatureLines = @($selected) }
    foreach ($match in [regex]::Matches(($signatureLines -join "`n"), $codePattern,
            [Text.RegularExpressions.RegexOptions]::IgnoreCase)) {
        $code = $match.Value.ToUpperInvariant()
        if (-not $codes.Contains($code)) { [void]$codes.Add($code) }
    }
    $summary = Limit-BuildDiagnosticText (($selected -join "`n")) $Limit
    [pscustomobject]@{
        Text = $summary.Text
        Truncated = $summary.Truncated
        Signature = if ($null -ne $primarySignature) { $primarySignature } elseif ($codes.Count -gt 0) { $codes -join ',' } else { $null }
    }
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
    $allowed = @('schemaVersion', 'entityType', 'productionEligible', 'project', 'sourceProject', 'configuration', 'expectedAssembly',
        'deploymentTarget', 'testRecipe', 'testRecipePath', 'buildProperties', 'runtimePackage', 'deploymentRole')
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
    if (-not [string]::IsNullOrWhiteSpace([string]$value.deploymentRole) -and
        [string]$value.deploymentRole -notin @('mod', 'tooling-only')) {
        throw 'deploymentRole must be mod or tooling-only'
    }
    if ($null -ne $value.productionEligible -and
        $value.productionEligible -isnot [bool]) {
        throw 'productionEligible must be a JSON boolean'
    }
    $nonProductionEntityTypes = @('fixture', 'test', 'internal', 'example')
    $toolingDescriptorRoot = Join-Path $repoRoot 'DevelopmentProjects'
    $isToolingDescriptor = Test-PathWithin $descriptorPath $toolingDescriptorRoot
    if ($isToolingDescriptor -and
        ($value.entityType -notin $nonProductionEntityTypes -or
            $value.productionEligible -ne $false)) {
        throw 'EXTERNAL_PRODUCTION_DESCRIPTOR_IN_TOOLING: DevelopmentProjects descriptors must be explicitly non-production fixtures'
    }
    if ($null -ne $value.entityType -and $value.entityType -notin $nonProductionEntityTypes) {
        throw 'entityType must be fixture, test, internal, or example'
    }
    if ($value.entityType -in $nonProductionEntityTypes -and
        $value.productionEligible -ne $false) {
        throw 'non-production descriptors must set productionEligible to false'
    }
    $value | Add-Member -NotePropertyName ResolvedSource -NotePropertyValue (Resolve-SourceProject ([string]$value.sourceProject))
    $value | Add-Member -NotePropertyName SafeExpectedAssembly -NotePropertyValue (Get-SafeRelativePath ([string]$value.expectedAssembly) 'expectedAssembly')
    $value | Add-Member -NotePropertyName ResolvedTarget -NotePropertyValue (Resolve-DeploymentTarget ([string]$value.deploymentTarget))
    $value | Add-Member -NotePropertyName ResolvedTargetRoot -NotePropertyValue $deploymentRoot
    return $value
}

function Get-RecipeArguments {
    param([Parameter(Mandatory = $true)]$Descriptor)
    if ([string]::IsNullOrWhiteSpace([string]$Descriptor.testRecipePath)) {
        return @()
    }
    $safe = Get-SafeRelativePath ([string]$Descriptor.testRecipePath) 'testRecipePath'
    if ([IO.Path]::GetExtension($safe) -ine '.json') { throw 'testRecipePath must identify a JSON file' }
    $descriptorDirectory = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($descriptorPath))
    $recipePath = [IO.Path]::GetFullPath((Join-Path $descriptorDirectory $safe))
    if (-not (Test-PathWithin $recipePath $descriptorDirectory)) { throw 'testRecipePath escapes the descriptor directory' }
    if (-not (Test-Path -LiteralPath $recipePath -PathType Leaf)) { throw "testRecipePath was not found: $recipePath" }
    return @('--recipe-file', $recipePath)
}

function Get-DescriptorBuildProperties {
    param([Parameter(Mandatory = $true)]$Descriptor)
    $properties = [ordered]@{}
    if ($null -eq $Descriptor.buildProperties) { return $properties }
    if ($Descriptor.buildProperties -is [Array] -or
        $Descriptor.buildProperties -isnot [System.Management.Automation.PSCustomObject]) {
        throw 'descriptor buildProperties must be a JSON object'
    }
    foreach ($property in @($Descriptor.buildProperties.PSObject.Properties | Sort-Object Name)) {
        $name = [string]$property.Name
        if ($name -notmatch '^[A-Za-z_][A-Za-z0-9_.-]{0,63}$') {
            throw "descriptor build property name is invalid: $name"
        }
        if ($name -in @('CustomBeforeDirectoryBuildProps', 'DevBridgeModTestIntermediateRoot',
                'BaseIntermediateOutputPath', 'IntermediateOutputPath', 'MSBuildProjectExtensionsPath',
                'OutputPath')) {
            throw "descriptor build property is reserved by DevBridge2: $name"
        }
        if ($properties.Keys | Where-Object { $_.Equals($name, [StringComparison]::OrdinalIgnoreCase) }) {
            throw "descriptor build properties contain duplicate name: $name"
        }
        if ($null -eq $property.Value -or $property.Value -is [Array] -or
            $property.Value -is [System.Management.Automation.PSCustomObject]) {
            throw "descriptor build property must be a scalar string: $name"
        }
        $text = [string]$property.Value
        if ($text.Length -gt 4096) { throw "descriptor build property exceeds the 4096-character bound: $name" }
        $properties[$name] = $text
    }
    return $properties
}

function Get-BuildPropertyArguments {
    param([Parameter(Mandatory = $true)][System.Collections.IDictionary]$Properties,
        [Parameter(Mandatory = $true)][string]$RimWorldDirectory)
    $effective = [ordered]@{}
    foreach ($name in $Properties.Keys) { $effective[[string]$name] = [string]$Properties[$name] }
    $rimWorldProperty = @($effective.Keys | Where-Object {
        $_.Equals('RIMWORLD_DIR', [StringComparison]::OrdinalIgnoreCase)
    })
    if ($rimWorldProperty.Count -eq 0) {
        $effective['RIMWORLD_DIR'] = $RimWorldDirectory
    } else {
        $effective['RIMWORLD_DIR'] = [string]$effective[$rimWorldProperty[0]]
        if ($rimWorldProperty[0] -cne 'RIMWORLD_DIR') { $effective.Remove($rimWorldProperty[0]) }
    }
    return @($effective.Keys | Sort-Object | ForEach-Object {
        '-p:{0}={1}' -f $_, [string]$effective[$_]
    })
}

function Resolve-RimWorldBuildDirectory {
    param([Parameter(Mandatory = $true)]$Status,
        [Parameter(Mandatory = $true)][System.Collections.IDictionary]$Properties)
    $override = @($Properties.Keys | Where-Object {
        $_.Equals('RIMWORLD_DIR', [StringComparison]::OrdinalIgnoreCase) -or
        $_.Equals('RimWorldDir', [StringComparison]::OrdinalIgnoreCase)
    })
    $candidate = if ($override.Count -gt 0) {
        [string]$Properties[$override[0]]
    } else {
        [string]$Status.rimworldRoot
    }
    if ([string]::IsNullOrWhiteSpace($candidate)) {
        throw 'the coordinator did not resolve a RimWorld installation root'
    }
    try { $full = [IO.Path]::GetFullPath($candidate) } catch {
        throw "the resolved RimWorld installation root is not a valid path: $candidate"
    }
    if (-not (Test-Path -LiteralPath $full -PathType Container)) {
        throw "the resolved RimWorld installation root does not exist: $full"
    }
    $managedAssembly = Join-Path $full 'RimWorldWin64_Data\Managed\Assembly-CSharp.dll'
    if (-not (Test-Path -LiteralPath $managedAssembly -PathType Leaf)) {
        throw "RimWorld managed assemblies were not found under the resolved installation root: $managedAssembly"
    }
    return $full
}

function Get-Hash {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}
function Test-WildcardRelativePath {
    param([Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Pattern)
    $normalizedPath = $Path.Replace('\', '/')
    $normalizedPattern = $Pattern.Replace('\', '/')
    return [System.Management.Automation.WildcardPattern]::new(
        $normalizedPattern,
        [System.Management.Automation.WildcardOptions]::IgnoreCase).IsMatch($normalizedPath)
}

function Resolve-RuntimePackagePlan {
    param([Parameter(Mandatory = $true)]$Descriptor)
    if ($null -eq $Descriptor.runtimePackage) {
        return @()
    }
    if ($Descriptor.runtimePackage -is [Array] -or
        $Descriptor.runtimePackage -isnot [System.Management.Automation.PSCustomObject]) {
        throw 'descriptor runtimePackage must be a JSON object'
    }
    $allowed = @('sourceRoot', 'include', 'exclude')
    foreach ($property in $Descriptor.runtimePackage.PSObject.Properties.Name) {
        if ($property -notin $allowed) { throw "descriptor runtimePackage field is not allowed: $property" }
    }
    $sourceRootValue = if ([string]::IsNullOrWhiteSpace([string]$Descriptor.runtimePackage.sourceRoot)) {
        '.'
    } else { [string]$Descriptor.runtimePackage.sourceRoot }
    $safeSourceRoot = if ($sourceRootValue -eq '.') { '' } else {
        Get-SafeRelativePath $sourceRootValue 'runtimePackage.sourceRoot'
    }
    $sourceRoot = if ([string]::IsNullOrWhiteSpace($safeSourceRoot)) {
        [IO.Path]::GetFullPath($developmentRoots[0])
    } else {
        [IO.Path]::GetFullPath((Join-Path $developmentRoots[0] $safeSourceRoot))
    }
    Assert-Directory $sourceRoot 'runtime package source root'
    $includes = @($Descriptor.runtimePackage.include | ForEach-Object { [string]$_ } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($includes.Count -eq 0) {
        throw 'descriptor runtimePackage.include must contain at least one pattern'
    }
    $excludes = @($Descriptor.runtimePackage.exclude | ForEach-Object { [string]$_ } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $developmentRoots = @('.git', 'Source', 'bin', 'obj', 'tests', 'TestResults', '.rimdev', '.rimctx', '.rimerror')
    $explicitDevelopmentRoots = @($includes | ForEach-Object {
        $first = $_.Replace('\', '/').Split('/')[0]
        if ($first -in $developmentRoots) { $first }
    } | Select-Object -Unique)
    $excludes = @($developmentRoots | Where-Object {
        $_ -notin $explicitDevelopmentRoots
    } | ForEach-Object { "$_/**" }) + $excludes
    foreach ($pattern in @($includes + $excludes)) {
        $segments = $pattern.Replace('\', '/').Split('/')
        if ($pattern.Length -gt 256 -or [IO.Path]::IsPathRooted($pattern) -or
            $pattern.Contains(':') -or $segments -contains '..') {
            throw "runtime package pattern is unsafe: $pattern"
        }
    }
    $files = [System.Collections.Generic.List[object]]::new()
    foreach ($path in [IO.Directory]::EnumerateFiles($sourceRoot, '*', [IO.SearchOption]::AllDirectories)) {
        Assert-NoReparsePath $path
        $relative = [IO.Path]::GetRelativePath($sourceRoot, $path).Replace('\', '/')
        if ((@($includes | Where-Object { Test-WildcardRelativePath $relative $_ })).Count -eq 0 -or
            (@($excludes | Where-Object { Test-WildcardRelativePath $relative $_ })).Count -gt 0) {
            continue
        }
        $files.Add([pscustomobject]@{
            SourcePath = [IO.Path]::GetFullPath($path)
            PackagePath = $relative
        })
    }
    return @($files | Sort-Object PackagePath)
}

function Get-PackageIdentity {
    param([Parameter(Mandatory = $true)][object[]]$Files)
    $lines = [System.Collections.Generic.List[string]]::new()
    foreach ($file in @($Files | Sort-Object TargetPath)) {
        [void]$lines.Add(([string]$file.TargetPath).Replace('\', '/') + "`0" + [string]$file.Sha256)
    }
    $bytes = [Text.Encoding]::UTF8.GetBytes(($lines -join "`n"))
    return ([Security.Cryptography.SHA256]::Create().ComputeHash($bytes) |
        ForEach-Object { $_.ToString('x2') }) -join ''
}

function Get-DeploymentManifestPath {
    $identityBytes = [Text.Encoding]::UTF8.GetBytes(
        $descriptor.ResolvedTargetRoot.ToLowerInvariant())
    $identityHash = ([Security.Cryptography.SHA256]::Create().ComputeHash($identityBytes) |
        ForEach-Object { $_.ToString('x2') }) -join ''

    $directory = Join-Path $coordinatorRoot 'Runtime\deployment-manifests'
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    return Join-Path $directory ($Project + '-' + $identityHash.Substring(0, 24) + '.json')
}

function Get-ProjectPackageId {
    param([Parameter(Mandatory = $true)]$Descriptor)
    $candidates = [System.Collections.Generic.List[string]]::new()
    if ($null -ne $Descriptor.runtimePackage -and
        -not [string]::IsNullOrWhiteSpace([string]$Descriptor.runtimePackage.sourceRoot)) {
        $sourceRootValue = [string]$Descriptor.runtimePackage.sourceRoot
        if ($sourceRootValue -eq '.') {
            [void]$candidates.Add([IO.Path]::GetFullPath($developmentRoots[0]))
        } else {
            $safeSourceRoot = Get-SafeRelativePath $sourceRootValue 'runtimePackage.sourceRoot'
            [void]$candidates.Add([IO.Path]::GetFullPath(
                    (Join-Path $developmentRoots[0] $safeSourceRoot)))
        }
    }
    $current = [IO.Directory]::GetParent([IO.Path]::GetFullPath([string]$Descriptor.ResolvedSource))
    for ($depth = 0; $depth -lt 4 -and $null -ne $current; $depth++) {
        [void]$candidates.Add($current.FullName)
        $current = $current.Parent
    }
    foreach ($candidate in $candidates | Select-Object -Unique) {
        $aboutPath = Join-Path $candidate 'About\About.xml'
        if (-not (Test-Path -LiteralPath $aboutPath -PathType Leaf)) { continue }
        try {
            $about = [xml](Get-Content -LiteralPath $aboutPath -Raw)
            $packageId = [string]$about.ModMetaData.packageId
            if (-not [string]::IsNullOrWhiteSpace($packageId)) { return $packageId.Trim() }
        } catch {
            Throw-CausalFailure 'DEVBRIDGE_DEPLOYMENT_LEGACY_IDENTITY_MISMATCH' 'deployment' `
                'the source package About/About.xml is not valid XML' `
                'legacy runtime identity validation' @{ aboutPath = $aboutPath }
        }
    }
    Throw-CausalFailure 'DEVBRIDGE_DEPLOYMENT_LEGACY_IDENTITY_MISMATCH' 'deployment' `
        'the source project has no readable About/About.xml package identity' `
        'legacy runtime identity validation' @{}
}

function Assert-LegacyRuntimeIdentity {
    param([Parameter(Mandatory = $true)]$Descriptor)
    $runtimeRoot = [IO.Path]::GetFullPath([string]$Descriptor.ResolvedTargetRoot)
    $sourceRoot = [IO.Directory]::GetParent(
        [IO.Directory]::GetParent([IO.Path]::GetFullPath([string]$Descriptor.ResolvedSource)).FullName).FullName
    if ([string]::Equals($runtimeRoot, $sourceRoot, [StringComparison]::OrdinalIgnoreCase)) {
        Throw-CausalFailure 'DEVBRIDGE_DEPLOYMENT_LEGACY_IDENTITY_MISMATCH' 'deployment' `
            'the runtime root is the source repository' 'legacy runtime identity validation' @{
                sourceRoot = $sourceRoot
                runtimeRoot = $runtimeRoot
            }
    }
    $parent = [IO.Directory]::GetParent($runtimeRoot)
    if ($null -ne $parent -and $parent.Name -eq 'Mods') {
        $rimWorld = $parent.Parent
        if ($null -eq $rimWorld -or $rimWorld.Name -ne 'RimWorld') {
            Throw-CausalFailure 'DEVBRIDGE_DEPLOYMENT_LEGACY_IDENTITY_MISMATCH' 'deployment' `
                'the runtime root is not below the canonical RimWorld Mods boundary' `
                'legacy runtime identity validation' @{ runtimeRoot = $runtimeRoot }
        }
    } elseif ($runtimeRoot -match '(?i)\\RimWorld\\') {
        Throw-CausalFailure 'DEVBRIDGE_DEPLOYMENT_LEGACY_IDENTITY_MISMATCH' 'deployment' `
            'the runtime root is outside the canonical RimWorld Mods boundary' `
            'legacy runtime identity validation' @{ runtimeRoot = $runtimeRoot }
    }
    $expectedPackageId = Get-ProjectPackageId $Descriptor
    $aboutPath = Join-Path $runtimeRoot 'About\About.xml'
    if (-not (Test-Path -LiteralPath $aboutPath -PathType Leaf)) {
        Throw-CausalFailure 'DEVBRIDGE_DEPLOYMENT_LEGACY_IDENTITY_MISMATCH' 'deployment' `
            'the existing runtime has no About/About.xml package identity' `
            'legacy runtime identity validation' @{ aboutPath = $aboutPath }
    }
    try {
        $runtimeAbout = [xml](Get-Content -LiteralPath $aboutPath -Raw)
        $actualPackageId = [string]$runtimeAbout.ModMetaData.packageId
    } catch {
        Throw-CausalFailure 'DEVBRIDGE_DEPLOYMENT_LEGACY_IDENTITY_MISMATCH' 'deployment' `
            'the existing runtime About/About.xml is not valid XML' `
            'legacy runtime identity validation' @{ aboutPath = $aboutPath }
    }
    if (-not [string]::Equals($actualPackageId.Trim(), $expectedPackageId,
            [StringComparison]::OrdinalIgnoreCase)) {
        Throw-CausalFailure 'DEVBRIDGE_DEPLOYMENT_LEGACY_IDENTITY_MISMATCH' 'deployment' `
            'the existing runtime packageId does not match the intended project' `
            'legacy runtime identity validation' @{
                expectedPackageId = $expectedPackageId
                actualPackageId = $actualPackageId
            }
    }
    return [ordered]@{
        sourceRoot = $sourceRoot
        runtimeRoot = $runtimeRoot
        packageId = $expectedPackageId
    }
}
function Prepare-RuntimePackage {
    param([Parameter(Mandatory = $true)]$Descriptor,
        [Parameter(Mandatory = $true)][string]$ExpectedArtifact,
        [object[]]$ContentPlan)
    $targetRoot = [IO.Path]::GetFullPath($Descriptor.ResolvedTargetRoot)
    $assemblyTarget = [IO.Path]::GetRelativePath(
        $targetRoot,
        [IO.Path]::GetFullPath($Descriptor.ResolvedTarget)).Replace('\', '/')
    if ([IO.Path]::IsPathRooted($assemblyTarget) -or $assemblyTarget.StartsWith('../')) {
        throw 'deployment target is not relative to the active runtime root'
    }
    $ContentPlan = @($ContentPlan | Where-Object {
        -not [string]::Equals(
            [string]$_.PackagePath,
            $assemblyTarget,
            [StringComparison]::OrdinalIgnoreCase)
    })
    $entries = [System.Collections.Generic.List[object]]::new()
    $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($content in @($ContentPlan)) {
        if ($null -eq $content -or
            ($content -is [string] -and [string]::IsNullOrWhiteSpace($content))) { continue }
        $targetPath = Get-SafeRelativePath ([string]$content.PackagePath) 'runtime package path'
        $normalizedTargetPath = $targetPath.Replace('\', '/')
        if (-not $seen.Add($normalizedTargetPath)) {
            $previous = @($entries | Where-Object { $_.TargetPath -eq $normalizedTargetPath } | Select-Object -First 1)
            Throw-CausalFailure `
                'DEVBRIDGE_PACKAGE_DUPLICATE_DESTINATION' `
                'package' `
                'runtime package construction' `
                "runtime package contains duplicate path: $normalizedTargetPath" `
                @{
                    path = $normalizedTargetPath
                    expected = 'one authoritative package source per destination'
                    actual = @([string]$previous.SourcePath, [string]$content.SourcePath)
                }
        }
        $staged = [IO.Path]::GetFullPath((Join-Path $stagingRoot $targetPath))
        if (-not (Test-PathWithin $staged $stagingRoot)) { throw 'runtime package path escapes staging root' }
        $parentInfo = [IO.Directory]::GetParent($staged)
        $parent = if ($null -eq $parentInfo) { $null } else { $parentInfo.FullName }
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
        Copy-Item -LiteralPath ([string]$content.SourcePath) -Destination $staged -Force
        Assert-NoReparsePath $staged
        $entries.Add([pscustomobject]@{
            SourcePath = [string]$content.SourcePath
            TargetPath = $targetPath.Replace('\', '/')
            Sha256 = Get-Hash $staged
            Size = [int64](Get-Item -LiteralPath $staged -Force).Length
        })
    }
    if (-not $seen.Add($assemblyTarget)) {
        Throw-CausalFailure `
            'DEVBRIDGE_PACKAGE_ASSEMBLY_COLLISION' `
            'package' `
            'runtime package construction' `
            "runtime package content collides with the built assembly: $assemblyTarget" `
            @{
                path = $assemblyTarget
                expected = 'the built artifact is the sole authoritative source for the assembly destination'
                actual = 'another runtime package entry remained at the built assembly destination'
            }
    }
    $entries.Add([pscustomobject]@{
        SourcePath = $ExpectedArtifact
        TargetPath = $assemblyTarget
        Sha256 = Get-Hash $ExpectedArtifact
        Size = [int64](Get-Item -LiteralPath $ExpectedArtifact -Force).Length
    })
    return @($entries | Sort-Object TargetPath)
}

function Read-DeploymentManifest {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    try {
        if ((Get-Item -LiteralPath $Path -Force).Length -gt 256 * 1024) { return $null }
        $value = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -Depth 16
        if ([string]$value.schemaVersion -ne 'devbridge-deployment-manifest/v1' -or
            [string]$value.project -ne $Project -or
            [string]::IsNullOrWhiteSpace([string]$value.packageSha256)) { return $null }
        return $value
    } catch {
        return $null
    }
}

function Write-DeploymentManifest {
    param([Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][object[]]$Files,
        [Parameter(Mandatory = $true)][string]$PackageSha256,
        [Parameter(Mandatory = $true)][int]$Generation,
        [string]$OwnershipProvenance = 'NORMAL_DEVBRIDGE_DEPLOYMENT',
        [string]$MigrationClassification = $null)
    $parentInfo = [IO.Directory]::GetParent($Path)
    $parent = if ($null -eq $parentInfo) { $null } else { $parentInfo.FullName }
    Assert-Directory $parent 'deployment manifest parent'
    $manifest = [ordered]@{
        schemaVersion = 'devbridge-deployment-manifest/v1'
        project = $Project
        deploymentRoot = $script:Report.deployment.targetRoot
        sourceFingerprint = $script:Report.sourceFingerprint
        packageSha256 = $PackageSha256
        transactionId = $script:Report.transactionId
        ownershipProvenance = $OwnershipProvenance
        adoption = if ([string]::IsNullOrWhiteSpace($MigrationClassification)) {
            $null
        } else {
            [ordered]@{
                classification = $MigrationClassification
                adoptedAtUtc = [DateTime]::UtcNow.ToString('o')
                transactionId = $script:Report.transactionId
            }
        }
        workflowId = $script:Report.workflowId
        generation = $Generation
        files = @($Files | Sort-Object TargetPath | ForEach-Object {
            [ordered]@{
                path = [string]$_.TargetPath
                sha256 = [string]$_.Sha256
                size = [int64]$_.Size
            }
        })
    }
    $temporary = $Path + '.' + $transactionId + '.tmp'
    [IO.File]::WriteAllText(
        $temporary,
        ($manifest | ConvertTo-Json -Depth 16 -Compress),
        [Text.UTF8Encoding]::new($false))
    Assert-NoReparsePath $temporary
    [IO.File]::Move($temporary, $Path, $true)
}

function Acquire-DeploymentMutationLock {
    $nameBytes = [Text.Encoding]::UTF8.GetBytes(
        $descriptor.ResolvedTargetRoot.ToLowerInvariant())
    $nameHash = ([Security.Cryptography.SHA256]::Create().ComputeHash($nameBytes) |
        ForEach-Object { $_.ToString('x2') }) -join ''
    $script:DeploymentMutex = [Threading.Mutex]::new(
        $false,
        'Global\DevBridge2-Deployment-' + $nameHash)
    if (-not $script:DeploymentMutex.WaitOne(0)) {
        $script:DeploymentMutex.Dispose()
        $script:DeploymentMutex = $null
        Set-Failure 'deployment' 'retry-after-deployment-contention' `
            'DEVBRIDGE_DEPLOYMENT_CONTENTION' `
            'another agent is mutating the same active mod deployment' `
            'deployment mutation lock' 10 $null $false
    }
    $script:DeploymentLockAcquired = $true
}

function Release-DeploymentMutationLock {
    if ($script:DeploymentLockAcquired -and $null -ne $script:DeploymentMutex) {
        try { $script:DeploymentMutex.ReleaseMutex() } catch { }
        try { $script:DeploymentMutex.Dispose() } catch { }
    }
    $script:DeploymentLockAcquired = $false
    $script:DeploymentMutex = $null
}

function Move-LegacyRuntimeToRollback {
    param([Parameter(Mandatory = $true)][string]$RuntimeRoot)
    if (-not (Test-Path -LiteralPath $RuntimeRoot -PathType Container)) { return }
    Assert-NoReparsePath $RuntimeRoot
    $backup = Join-Path $transactionRoot 'legacy-runtime'
    if (Test-Path -LiteralPath $backup) {
        Throw-CausalFailure 'DEVBRIDGE_DEPLOYMENT_ADOPTION_UNSAFE' 'deployment' `
            'the bounded legacy rollback location already exists' `
            'legacy runtime replacement' @{ rollbackPath = $backup }
    }
    Move-Item -LiteralPath $RuntimeRoot -Destination $backup
    $script:LegacyBackupPath = $backup
    $script:LegacyRootMoved = $true
    New-Item -ItemType Directory -Force -Path $RuntimeRoot | Out-Null
    Assert-NoReparsePath $RuntimeRoot
}

function Restore-LegacyRuntimeFromRollback {
    if (-not $script:LegacyRootMoved -or
        [string]::IsNullOrWhiteSpace($script:LegacyBackupPath)) { return }
    $runtimeRoot = [IO.Path]::GetFullPath([string]$descriptor.ResolvedTargetRoot)
    try {
        if (Test-Path -LiteralPath $runtimeRoot -PathType Container) {
            Assert-NoReparsePath $runtimeRoot
            Remove-Item -LiteralPath $runtimeRoot -Recurse -Force
        }
        if (Test-Path -LiteralPath $script:LegacyBackupPath -PathType Container) {
            Move-Item -LiteralPath $script:LegacyBackupPath -Destination $runtimeRoot
            $script:Report.deployment.rollbackState = 'restored'
        } else {
            $script:Report.deployment.rollbackState = 'rollback-missing'
        }
    } catch {
        $script:Report.deployment.rollbackState = 'restore-failed'
        $script:Report.cleanup.error = Limit-Text $_.Exception.Message
    }
    $script:LegacyRootMoved = $false
}

function Confirm-LegacyExactUnchanged {
    param([Parameter(Mandatory = $true)]$Descriptor,
        [Parameter(Mandatory = $true)][object[]]$PackageFiles)
    $expected = @{}
    foreach ($file in $PackageFiles) { $expected[[string]$file.TargetPath] = [string]$file.Sha256 }
    $observed = @{}
    foreach ($path in [IO.Directory]::EnumerateFiles($Descriptor.ResolvedTargetRoot, '*', [IO.SearchOption]::AllDirectories)) {
        Assert-NoReparsePath $path
        $relative = [IO.Path]::GetRelativePath(
            $Descriptor.ResolvedTargetRoot, $path).Replace('\', '/')
        $observed[$relative] = Get-Hash $path
    }
    if ($observed.Count -ne $expected.Count) {
        Throw-CausalFailure 'DEVBRIDGE_DEPLOYMENT_ADOPTION_RACE' 'deployment' `
            'the legacy runtime inventory changed during adoption verification' `
            'legacy exact adoption verification' @{
                expectedFileCount = $expected.Count
                observedFileCount = $observed.Count
            }
    }
    foreach ($path in $expected.Keys) {
        if (-not $observed.ContainsKey($path) -or $observed[$path] -ne $expected[$path]) {
            Throw-CausalFailure 'DEVBRIDGE_DEPLOYMENT_ADOPTION_RACE' 'deployment' `
                "legacy runtime file changed during adoption verification: $path" `
                'legacy exact adoption verification' @{ path = $path }
        }
    }
}


function Read-ArtifactState {
    if (-not (Test-Path -LiteralPath $artifactStatePath -PathType Leaf) -or
        $null -ne $script:DeploymentPendingPath -and
        (Test-Path -LiteralPath $script:DeploymentPendingPath -PathType Leaf)) {
        return $null
    }
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

function Write-PendingDeployment {
    param([Parameter(Mandatory = $true)][string]$ManifestPath)
    $script:DeploymentPendingPath = $ManifestPath + '.pending'
    $pending = [ordered]@{
        schemaVersion = 'devbridge-deployment-pending/v1'
        project = $Project
        deploymentRoot = $script:Report.deployment.targetRoot
        sourceFingerprint = $script:Report.sourceFingerprint
        transactionId = $script:Report.transactionId
    }
    [IO.File]::WriteAllText(
        $script:DeploymentPendingPath,
        ($pending | ConvertTo-Json -Depth 8 -Compress),
        [Text.UTF8Encoding]::new($false))
}

function Clear-PendingDeployment {
    if (-not [string]::IsNullOrWhiteSpace($script:DeploymentPendingPath) -and
        (Test-Path -LiteralPath $script:DeploymentPendingPath -PathType Leaf)) {
        Remove-Item -LiteralPath $script:DeploymentPendingPath -Force -ErrorAction SilentlyContinue
    }
}

function Write-ArtifactState {
    param([Parameter(Mandatory = $true)][int]$Generation,
        [Parameter(Mandatory = $true)][string]$DeployedHash,
        [Parameter(Mandatory = $true)][string]$PackageHash,
        [Parameter(Mandatory = $true)][string]$ManifestPath)
    $parentInfo = [IO.Directory]::GetParent($artifactStatePath)
    $parent = if ($null -eq $parentInfo) { $null } else { $parentInfo.FullName }
    Assert-Directory $parent 'artifact-state parent'
    Assert-NoReparsePath $artifactStatePath
    $temporary = Join-Path $parent ('.devbridge-artifact-' + $transactionId + '.tmp')
    $state = [ordered]@{
        schemaVersion = 'devbridge-artifact-state/v1'
        project = $Project
        deploymentTarget = [IO.Path]::GetFullPath($script:Report.deployment.targetPath)
        deploymentRoot = [IO.Path]::GetFullPath($script:Report.deployment.targetRoot)
        deployedArtifactSha256 = $DeployedHash
        deployedPackageSha256 = $PackageHash
        deploymentManifestPath = $ManifestPath
        generation = $Generation
        transactionId = $script:Report.transactionId
        workflowId = $script:Report.workflowId
        sourceFingerprint = $script:Report.sourceFingerprint
    }
    $json = $state | ConvertTo-Json -Depth 8 -Compress
    [IO.File]::WriteAllText($temporary, $json, [Text.UTF8Encoding]::new($false))
    Assert-NoReparsePath $temporary
    [IO.File]::Move($temporary, $artifactStatePath, $true)
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue }
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
        (Join-Path $coordinatorRoot 'Player.log')
    )
    if ($null -ne $script:Report.build) {
        $paths += @(
            $script:Report.build.rawStdoutPath,
            $script:Report.build.rawStderrPath,
            $script:Report.build.rawNativeStdoutPath,
            $script:Report.build.rawNativeStderrPath
        )
    }
    return @($paths | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        ForEach-Object { [IO.Path]::GetFullPath($_) } | Select-Object -Unique)
}

function Throw-CausalFailure {
    param([Parameter(Mandatory = $true)][string]$ErrorCode,
        [Parameter(Mandatory = $true)][string]$Phase,
        [Parameter(Mandatory = $true)][string]$Command,
        [Parameter(Mandatory = $true)][string]$Message,
        [hashtable]$Details = @{})
    $exception = [InvalidOperationException]::new($Message)
    $cause = [ordered]@{
        errorCode = $ErrorCode
        phase = $Phase
        command = $Command
        message = $Message
        exceptionType = $exception.GetType().FullName
    }
    foreach ($key in $Details.Keys) {
        $cause[$key] = $Details[$key]
    }
    $exception.Data['DevBridgeCause'] = $cause
    throw $exception
}

function Set-Failure {
    param([Parameter(Mandatory = $true)][string]$Stage,
        [Parameter(Mandatory = $true)][string]$NextAction,
        [string]$ErrorCode, [string]$Message, [string]$Command,
        [int]$ExitCode = 1, $Output, [bool]$KeepOwnership = $false,
        [bool]$OutputTruncated = $false)
    $script:Report.stage = $Stage
    $script:Report.nextAction = $NextAction
    $script:Report.exitCode = $ExitCode
    $script:Report.success = $false
    $failureOutput = if ($Stage -eq 'build') {
        Limit-BuildDiagnosticText ([string]$Output) -AlreadyTruncated:$OutputTruncated
    } else {
        [pscustomobject]@{ Text = Limit-Text ([string]$Output); Truncated = [bool]$OutputTruncated }
    }
    $script:Report.retrySafety = if ([bool]$script:Report.deploymentStarted) {
        if ([bool]$script:Report.deploymentCommitted) { 'COMMITTED_RECONCILE' } else { 'UNKNOWN_RECONCILE' }
    } else { 'SAFE_AFTER_REPAIR' }
    $script:Report.failure = [ordered]@{
        stage = $Stage
        command = $Command
        exitCode = $ExitCode
        errorCode = $ErrorCode
        message = Limit-Text $Message
        output = $failureOutput.Text
        outputTruncated = [bool]$failureOutput.Truncated
        causalDiagnostic = if ($Stage -eq 'build' -and $null -ne $script:Report.build) { $script:Report.build.causalDiagnostic } else { $null }
        diagnosticSignature = if ($Stage -eq 'build' -and $null -ne $script:Report.build) { $script:Report.build.diagnosticSignature } else { $null }
        ownership = if ($Stage -eq 'build' -and $null -ne $script:Report.build) { $script:Report.build.ownership } else { $null }
        causeErrorCode = $null
        cause = $null
        deploymentStarted = [bool]$script:Report.deploymentStarted
        deploymentCommitted = [bool]$script:Report.deploymentCommitted
        retrySafety = $script:Report.retrySafety
        evidence = @($script:TracePath)
        transactionId = $script:Report.transactionId
        workflowId = $script:Report.workflowId
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

if ($null -eq ('DevBridge.BoundedProcessRunner' -as [type])) {
    Add-Type -TypeDefinition @'
using System.IO;
using System;
using System.Diagnostics;
using System.Text;

namespace DevBridge
{
    public sealed class BoundedProcessResult
    {
        public int ExitCode { get; set; }
        public bool TimedOut { get; set; }
        public string StandardOutput { get; set; }
        public string StandardError { get; set; }
        public bool StandardOutputTruncated { get; set; }
        public bool StandardErrorTruncated { get; set; }
    }

    internal sealed class BoundedProcessText : IDisposable
    {
        private readonly object gate = new();
        private readonly int limit;
        private readonly StringBuilder value = new();
        private readonly StreamWriter raw;
        private bool hasLine;

        internal BoundedProcessText(int limit, string rawPath)
        {
            this.limit = Math.Max(1, limit);
            raw = new StreamWriter(rawPath, false, new UTF8Encoding(false))
            {
                AutoFlush = true
            };
        }

        internal bool Truncated { get; private set; }

        internal void AppendLine(string line)
        {
            if (line == null)
                return;

            lock (gate)
            {
                raw.WriteLine(line);
                if (Truncated)
                    return;

                string addition = hasLine ? "\n" + line : line;
                int remaining = limit - value.Length;
                if (addition.Length <= remaining)
                {
                    value.Append(addition);
                }
                else
                {
                    if (remaining > 0)
                        value.Append(addition.Substring(0, remaining));
                    Truncated = true;
                }
                hasLine = true;
            }
        }

        internal string GetText()
        {
            lock (gate)
                return value.Length == 0 ? null : value.ToString();
        }

        public void Dispose()
        {
            lock (gate)
                raw.Dispose();
        }
    }

    public static class BoundedProcessRunner
    {
        public static BoundedProcessResult Run(string executable, string[] arguments,
            string workingDirectory, int timeoutMilliseconds, int outputLimit,
            string rawStdoutPath, string rawStderrPath)
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workingDirectory
            };
            foreach (string argument in arguments ?? Array.Empty<string>())
                startInfo.ArgumentList.Add(argument ?? string.Empty);

            using BoundedProcessText standardOutput = new(outputLimit, rawStdoutPath);
            using BoundedProcessText standardError = new(outputLimit, rawStderrPath);
            using Process process = new() { StartInfo = startInfo };
            process.OutputDataReceived += (_, eventArgs) => standardOutput.AppendLine(eventArgs.Data);
            process.ErrorDataReceived += (_, eventArgs) => standardError.AppendLine(eventArgs.Data);
            if (!process.Start())
                throw new InvalidOperationException("the build process could not be started");

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            bool completed = process.WaitForExit(Math.Max(1, timeoutMilliseconds));
            bool timedOut = !completed;
            if (timedOut)
            {
                try { process.Kill(true); } catch { }
                try { process.WaitForExit(5000); } catch { }
            }
            else
            {
                // Drain the asynchronous reader callbacks, including a final unterminated line.
                process.WaitForExit();
            }

            return new BoundedProcessResult
            {
                ExitCode = timedOut ? 124 : process.ExitCode,
                TimedOut = timedOut,
                StandardOutput = standardOutput.GetText(),
                StandardError = standardError.GetText(),
                StandardOutputTruncated = standardOutput.Truncated,
                StandardErrorTruncated = standardError.Truncated
            };
        }
    }
}
'@
}

function Invoke-BoundedBuild {
    param([Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][int]$TimeoutSeconds,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [Parameter(Mandatory = $true)][string]$RawStdoutPath,
        [Parameter(Mandatory = $true)][string]$RawStderrPath)
    $dotnetCommand = @(Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1)
    if ($dotnetCommand.Count -eq 0) { throw 'dotnet executable could not be located' }
    $dotnet = [string]$dotnetCommand[0].Source
    $captured = [DevBridge.BoundedProcessRunner]::Run($dotnet, $Arguments,
        [IO.Path]::GetFullPath($WorkingDirectory), $TimeoutSeconds * 1000,
        [int]$script:BuildDiagnosticOutputLimit,
        [IO.Path]::GetFullPath($RawStdoutPath),
        [IO.Path]::GetFullPath($RawStderrPath))
    $combined = (@($captured.StandardOutput, $captured.StandardError) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join "`n"
    $bounded = Limit-BuildDiagnosticText $combined $script:BuildDiagnosticOutputLimit `
        -AlreadyTruncated:($captured.StandardOutputTruncated -or $captured.StandardErrorTruncated)
    $rawCombined = (@(
        if (Test-Path -LiteralPath $RawStdoutPath) { Get-Content -LiteralPath $RawStdoutPath -Raw }
        if (Test-Path -LiteralPath $RawStderrPath) { Get-Content -LiteralPath $RawStderrPath -Raw }
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join "`n"
    $diagnostics = Get-BuildDiagnosticSummary $rawCombined
    return [pscustomobject]@{
        ExitCode = $captured.ExitCode
        TimedOut = $captured.TimedOut
        Output = $bounded.Text
        OutputTruncated = $bounded.Truncated
        DiagnosticOutput = $diagnostics.Text
        DiagnosticOutputTruncated = $diagnostics.Truncated
        DiagnosticSignature = $diagnostics.Signature
        RawStdoutPath = [IO.Path]::GetFullPath($RawStdoutPath)
        RawStderrPath = [IO.Path]::GetFullPath($RawStderrPath)
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
    if ([string]$descriptor.deploymentRole -eq 'tooling-only') {
        Set-Failure 'preflight' 'use-tooling-build-command' `
            'DEVBRIDGE_TOOLING_ONLY_NOT_DEPLOYABLE' `
            'tooling-only projects are not eligible for RimWorld mod deployment' `
            'mod-test descriptor validation' 4 $null $false
    }

    $show = Invoke-BridgeJson (@('test', 'recipe', 'show', [string]$descriptor.testRecipe) + (Get-RecipeArguments $descriptor))
    Write-TransactionTrace 'planning' 'recipe show completed'
    $recipeInfo = Require-BridgeSuccess 'planning' 'fix-recipe-descriptor' 'recipe-show' $show
    $recipeProjects = @($recipeInfo.recipe.projects | ForEach-Object { [string]$_ })
    if ($recipeProjects -notcontains $Project) {
        Set-Failure 'planning' 'use-a-recipe-for-the-declared-project' 'DEVELOPMENT_RECIPE_PROJECT_MISMATCH' "recipe '$($descriptor.testRecipe)' does not request project '$Project'" $show.Command $show.ExitCode $show.Output $false
    }

    $projectPlanResult = Invoke-BridgeJson @('project', 'resolve', $Project)
    Write-TransactionTrace 'planning' 'project resolve completed'
    $projectPlan = Require-BridgeSuccess 'planning' 'fix-project-resolution' 'project-resolve' $projectPlanResult
    $recipePlanResult = Invoke-BridgeJson (@('test', 'recipe', 'plan', [string]$descriptor.testRecipe) + (Get-RecipeArguments $descriptor))
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
    $buildIntermediateRoot = Join-Path $transactionRoot 'obj'
    $buildPropsPath = Join-Path $scriptRoot 'mod-test-build.props'
    if (-not (Test-Path -LiteralPath $buildPropsPath -PathType Leaf)) {
        Set-Failure 'build' 'repair-owner-build-tooling' 'DEVELOPMENT_BUILD_CONFIGURATION_MISSING' "the owner build properties file is missing: $buildPropsPath" 'mod-test build setup' 1 $null $false
    }
    $descriptorBuildProperties = Get-DescriptorBuildProperties $descriptor
    $buildStatusResult = Invoke-BridgeJson @('status')
    Save-BridgeResult 'status-before-build' $buildStatusResult | Out-Null
    if ($null -eq $buildStatusResult.Response -or -not [bool]$buildStatusResult.Response.success) {
        Set-Failure 'build' 'configure-rimworld-build' 'RIMWORLD_DIR_UNRESOLVED' `
            'DevBridge2 could not resolve the RimWorld installation before the project build' `
            $buildStatusResult.Command 4 $buildStatusResult.Output $false
    }
    try {
        $rimWorldBuildDirectory = Resolve-RimWorldBuildDirectory $buildStatusResult.Response $descriptorBuildProperties
    } catch {
        Set-Failure 'build' 'configure-rimworld-build' 'RIMWORLD_DIR_UNRESOLVED' $_.Exception.Message `
            $buildStatusResult.Command 4 $buildStatusResult.Output $false
    }
    $buildPropertyArguments = Get-BuildPropertyArguments $descriptorBuildProperties $rimWorldBuildDirectory
    $buildArguments = @('build', $descriptor.ResolvedSource, '--configuration', [string]$descriptor.configuration,
        '--output', $stagingRoot, '--nologo',
        ('-p:CustomBeforeDirectoryBuildProps=' + $buildPropsPath),
        ('-p:DevBridgeModTestIntermediateRoot=' + $buildIntermediateRoot)) + $buildPropertyArguments
    $buildWorkingDirectory = [IO.Path]::GetDirectoryName($descriptor.ResolvedSource)
    if ([string]::IsNullOrWhiteSpace($buildWorkingDirectory)) {
        Set-Failure 'build' 'fix-build' 'DEVELOPMENT_BUILD_WORKING_DIRECTORY_INVALID' `
            'the declared project path has no working directory' `
            (Limit-BuildDiagnosticText (Format-Command (@('dotnet') + $buildArguments)) 4096).Text 1 $null $false
    }
    $controlledRawStdoutPath = Join-Path $transactionRoot 'controlled.stdout.log'
    $controlledRawStderrPath = Join-Path $transactionRoot 'controlled.stderr.log'
    $buildResult = Invoke-BoundedBuild $buildArguments $BuildTimeoutSeconds $buildWorkingDirectory `
        $controlledRawStdoutPath $controlledRawStderrPath
    Write-TransactionTrace 'build' ("exitCode=$($buildResult.ExitCode) timedOut=$($buildResult.TimedOut)") (Format-Command (@('dotnet') + $buildArguments))
    $buildExit = [int]$buildResult.ExitCode
    $buildCommand = (Limit-BuildDiagnosticText (Format-Command (@('dotnet') + $buildArguments)) 4096).Text
    $ownership = [ordered]@{
        orchestrator = 'DevBridge2'
        failureSurface = 'project-build'
        likelyOwner = 'unknown'
        confidence = 'unproven'
        basis = 'the causal build diagnostic is not yet proven'
    }
    $script:Report.build = [ordered]@{
        stage = 'build'
        command = $buildCommand
        exitCode = $buildExit
        output = $buildResult.Output
        outputTruncated = [bool]$buildResult.OutputTruncated
        causalDiagnostic = $buildResult.DiagnosticOutput
        causalDiagnosticTruncated = [bool]$buildResult.DiagnosticOutputTruncated
        diagnosticSignature = $buildResult.DiagnosticSignature
        rawStdoutPath = $buildResult.RawStdoutPath
        rawStderrPath = $buildResult.RawStderrPath
        rawNativeStdoutPath = $null
        rawNativeStderrPath = $null
        rimWorldDirectory = $rimWorldBuildDirectory
        buildProperties = $buildPropertyArguments
        stagingPath = $stagingRoot
        sourceProject = $descriptor.ResolvedSource
        timedOut = [bool]$buildResult.TimedOut
        transactionId = $transactionId
        workflowId = $WorkflowId
        errorCode = $null
        failureMessage = $null
        ownership = $ownership
    }
    if ($buildExit -ne 0) {
        $nativeBuild = $null
        $comparisonValid = $false
        $diagnosticFailure = $null
        if (-not $buildResult.TimedOut) {
            $nativeRoot = Join-Path $transactionRoot 'native'
            $nativeIntermediateRoot = Join-Path $nativeRoot 'obj'
            New-Item -ItemType Directory -Force -Path $nativeRoot, $nativeIntermediateRoot | Out-Null
            $nativeRestoreStdoutPath = Join-Path $transactionRoot 'native-restore.stdout.log'
            $nativeRestoreStderrPath = Join-Path $transactionRoot 'native-restore.stderr.log'
            $nativeRawStdoutPath = Join-Path $transactionRoot 'native.stdout.log'
            $nativeRawStderrPath = Join-Path $transactionRoot 'native.stderr.log'
            $nativePathArguments = @(
                ('-p:BaseIntermediateOutputPath=' + $nativeIntermediateRoot + [IO.Path]::DirectorySeparatorChar),
                ('-p:IntermediateOutputPath=' + $nativeIntermediateRoot + [IO.Path]::DirectorySeparatorChar),
                ('-p:MSBuildProjectExtensionsPath=' + $nativeIntermediateRoot + [IO.Path]::DirectorySeparatorChar),
                ('-p:OutputPath=' + $nativeRoot + [IO.Path]::DirectorySeparatorChar)
            )
            $nativeRestoreArguments = @('restore', $descriptor.ResolvedSource, '--nologo') +
                $nativePathArguments + $buildPropertyArguments
            $nativeRestore = Invoke-BoundedBuild $nativeRestoreArguments $BuildTimeoutSeconds $buildWorkingDirectory `
                $nativeRestoreStdoutPath $nativeRestoreStderrPath
            if ($nativeRestore.ExitCode -eq 0 -and -not $nativeRestore.TimedOut) {
                $nativeArguments = @('build', $descriptor.ResolvedSource, '--configuration', [string]$descriptor.configuration,
                    '--output', $nativeRoot, '--nologo', '--no-restore') +
                    $nativePathArguments + $buildPropertyArguments
                $nativeResult = Invoke-BoundedBuild $nativeArguments $BuildTimeoutSeconds $buildWorkingDirectory `
                    $nativeRawStdoutPath $nativeRawStderrPath
                $nativeValid = -not $nativeResult.TimedOut
                $nativeRawResult = $nativeResult
                if (-not $nativeValid) {
                    $diagnosticFailure = [ordered]@{
                        code = 'DEVELOPMENT_DIAGNOSTIC_COMPARISON_FAILED'
                        stage = 'native-build'
                        message = 'the native diagnostic build exceeded its bounded timeout'
                        restoreExitCode = [int]$nativeRestore.ExitCode
                        restoreDiagnostic = $nativeRestore.DiagnosticOutput
                    }
                }
            } else {
                $nativeValid = $false
                $nativeRawResult = $nativeRestore
                $diagnosticFailure = [ordered]@{
                    code = 'DEVELOPMENT_DIAGNOSTIC_COMPARISON_FAILED'
                    stage = 'native-restore'
                    message = 'the native diagnostic build could not restore its isolated intermediate directory'
                    restoreExitCode = [int]$nativeRestore.ExitCode
                    restoreDiagnostic = $nativeRestore.DiagnosticOutput
                }
            }
            $sameDiagnostic = $nativeValid -and
                -not [string]::IsNullOrWhiteSpace($buildResult.DiagnosticSignature) -and
                [string]::Equals(
                    [string]$buildResult.DiagnosticSignature,
                    [string]$nativeRawResult.DiagnosticSignature,
                    [StringComparison]::OrdinalIgnoreCase)
            $nativeBuild = [ordered]@{
                valid = $nativeValid
                success = $nativeValid -and ([int]$nativeRawResult.ExitCode -eq 0)
                exitCode = [int]$nativeRawResult.ExitCode
                diagnosticSignature = $nativeRawResult.DiagnosticSignature
                causalDiagnostic = $nativeRawResult.DiagnosticOutput
                causalDiagnosticTruncated = [bool]$nativeRawResult.DiagnosticOutputTruncated
                rawStdoutPath = $nativeRawResult.RawStdoutPath
                rawStderrPath = $nativeRawResult.RawStderrPath
                restoreExitCode = [int]$nativeRestore.ExitCode
                restoreSuccess = ([int]$nativeRestore.ExitCode -eq 0 -and -not $nativeRestore.TimedOut)
                restoreDiagnosticSignature = $nativeRestore.DiagnosticSignature
            }
            if (-not $nativeValid) {
                $ownership.basis = 'DevBridge2 diagnostic comparison failed while restoring the isolated native build'
                $ownership.confidence = 'low'
            } elseif (-not $nativeBuild.success -and $sameDiagnostic) {
                $ownership.likelyOwner = 'project'
                $ownership.confidence = 'high'
                $ownership.basis = 'native build failed with the same causal diagnostic'
            } elseif ($nativeBuild.success) {
                $ownership.likelyOwner = 'DevBridge2'
                $ownership.confidence = 'high'
                $ownership.basis = 'native build passed while the DevBridge-controlled build failed'
            } elseif (-not [string]::IsNullOrWhiteSpace($buildResult.DiagnosticOutput)) {
                $ownership.confidence = 'low'
                $ownership.basis = 'controlled and native builds failed with different causal diagnostics'
            }
            $script:Report.build.rawNativeStdoutPath = $nativeRawResult.RawStdoutPath
            $script:Report.build.rawNativeStderrPath = $nativeRawResult.RawStderrPath
        } elseif (-not [string]::IsNullOrWhiteSpace($buildResult.DiagnosticOutput)) {
            $ownership.likelyOwner = 'project'
            $ownership.confidence = 'low'
            $ownership.basis = 'a causal diagnostic exists but the controlled build timed out before native comparison'
        }
        $script:Report.build.ownership = $ownership
        $script:Report.buildDiscrimination = [ordered]@{
            controlledBuild = [ordered]@{
                valid = -not $buildResult.TimedOut
                success = $false
                exitCode = $buildExit
                diagnosticSignature = $buildResult.DiagnosticSignature
            }
            nativeBuild = $nativeBuild
            comparisonValid = $comparisonValid -or ($null -ne $nativeBuild -and [bool]$nativeBuild.valid)
            diagnosticFailure = $diagnosticFailure
            diagnosticSignature = $buildResult.DiagnosticSignature
            likelyOwner = $ownership.likelyOwner
            ownershipConfidence = $ownership.confidence
            ownershipBasis = $ownership.basis
        }
        $buildCode = if ($buildResult.TimedOut) { 'DEVELOPMENT_BUILD_TIMEOUT' } else { 'DEVELOPMENT_BUILD_FAILED' }
        $buildMessage = if ($buildResult.TimedOut) { 'the declared project build exceeded its bounded timeout' } else { 'the declared project build failed' }
        $script:Report.build.errorCode = $buildCode
        $script:Report.build.failureMessage = $buildMessage
        $failureOutput = if (-not [string]::IsNullOrWhiteSpace($buildResult.DiagnosticOutput)) {
            $buildResult.DiagnosticOutput
        } else { $buildResult.Output }
        $failureOutputTruncated = if (-not [string]::IsNullOrWhiteSpace($buildResult.DiagnosticOutput)) {
            [bool]$buildResult.DiagnosticOutputTruncated
        } else { [bool]$buildResult.OutputTruncated }
        Set-Failure 'build' 'fix-build' $buildCode $buildMessage $script:Report.build.command $buildExit `
            $failureOutput $false $failureOutputTruncated
    }
    if (-not (Test-Path -LiteralPath $expectedArtifact -PathType Leaf)) {
        Set-Failure 'build' 'fix-build-artifact' 'DEVELOPMENT_ARTIFACT_MISSING' "expected build artifact was not produced: $($descriptor.SafeExpectedAssembly)" $script:Report.build.command 1 $script:Report.build.output $false
    }
    Assert-NoReparsePath $expectedArtifact
    $builtHash = Get-Hash $expectedArtifact
    $script:Report.stage = 'package'
    Write-TransactionTrace 'package' 'runtime package construction started'
    $contentPlan = Resolve-RuntimePackagePlan $descriptor
    $packageFiles = Prepare-RuntimePackage $descriptor $expectedArtifact $contentPlan
    $packageHash = Get-PackageIdentity $packageFiles
    $manifestPath = Get-DeploymentManifestPath
    Acquire-DeploymentMutationLock
    $script:DeploymentPendingPath = $manifestPath + '.pending'
    $previousManifest = Read-DeploymentManifest $manifestPath
    $previousOwned = @{}
    if ($null -ne $previousManifest) {
        if ([IO.Path]::GetFullPath([string]$previousManifest.deploymentRoot) -ne
            [IO.Path]::GetFullPath([string]$descriptor.ResolvedTargetRoot)) {
            Set-Failure 'deployment' 'inspect-deployment-manifest' 'DEVBRIDGE_DEPLOYMENT_IDENTITY_MISMATCH' `
                'the deployment manifest belongs to a different active runtime root' 'deployment manifest validation' 4 $manifestPath $false
        }
        foreach ($entry in @($previousManifest.files)) {
            $previousOwned[[string]$entry.path] = $entry
        }
    }
    $expectedPaths = @{}
    foreach ($file in $packageFiles) { $expectedPaths[[string]$file.TargetPath] = $file }
    $existing = @()
    if (Test-Path -LiteralPath $descriptor.ResolvedTargetRoot -PathType Container) {
        Assert-NoReparsePath $descriptor.ResolvedTargetRoot
        foreach ($path in [IO.Directory]::EnumerateFiles($descriptor.ResolvedTargetRoot, '*', [IO.SearchOption]::AllDirectories)) {
            Assert-NoReparsePath $path
            $existing += [pscustomobject]@{
                Path = $path
                RelativePath = [IO.Path]::GetRelativePath(
                    $descriptor.ResolvedTargetRoot, $path).Replace('\', '/')
            }
        }
    }
    $unknown = @($existing | Where-Object {
        -not $previousOwned.ContainsKey($_.RelativePath) -and
        -not $expectedPaths.ContainsKey($_.RelativePath)
    } | ForEach-Object { $_.RelativePath })
    $ownershipConflict = if ($null -ne $previousManifest) {
        @($existing | Where-Object {
            $entry = $previousOwned[$_.RelativePath]
            ($expectedPaths.ContainsKey($_.RelativePath) -and $null -eq $entry) -or
            ($null -ne $entry -and (Get-Hash $_.Path) -ne [string]$entry.sha256)
        } | ForEach-Object { $_.RelativePath })
    } else { @() }
    if ($ownershipConflict.Count -gt 0) {
        Set-Failure 'deployment' 'inspect-deployment-ownership' 'DEVBRIDGE_DEPLOYMENT_OWNERSHIP_AMBIGUOUS' `
            ('managed deployment paths were changed outside DevBridge2: ' + ($ownershipConflict -join ', ')) `
            'deployment ownership validation' 4 ($ownershipConflict -join "`n") $false
    }
    $staleManaged = @($previousOwned.Keys | Where-Object { -not $expectedPaths.ContainsKey($_) })
    $currentMatches = $true
    foreach ($file in $packageFiles) {
        $target = Join-Path $descriptor.ResolvedTargetRoot $file.TargetPath.Replace('/', '\')
        if ((Get-Hash $target) -ne [string]$file.Sha256) { $currentMatches = $false }
    }
    $legacyClassification = $null
    $legacyExactAdopted = $false
    $legacyReplacement = $false
    if ($null -eq $previousManifest -and $existing.Count -gt 0) {
        $legacyIdentity = Assert-LegacyRuntimeIdentity $descriptor
        if ($currentMatches -and $unknown.Count -eq 0) {
            $legacyClassification = 'LEGACY_EXACT'
            $legacyExactAdopted = $true
        } elseif ($unknown.Count -gt 0) {
            $legacyClassification = 'LEGACY_WITH_UNKNOWN_CONTENT'
            $legacyReplacement = $true
        } else {
            $legacyClassification = 'LEGACY_RECONCILABLE'
            $legacyReplacement = $true
        }
    }
    $deployedPackageBefore = if ($currentMatches) { $packageHash } else { $null }
    $deploymentChanged = $legacyReplacement -or
        ($null -eq $previousManifest -and -not $legacyExactAdopted) -or
        $packageHash -ne $deployedPackageBefore -or $staleManaged.Count -gt 0
    $script:Report.deployment = [ordered]@{
        targetRoot = [IO.Path]::GetFullPath($descriptor.ResolvedTargetRoot)
        targetPath = [IO.Path]::GetFullPath($descriptor.ResolvedTarget)
        manifestPath = $manifestPath
        ownershipReconciliation = if ([string]::IsNullOrWhiteSpace($legacyClassification)) {
            [ordered]@{
                classification = if ($null -ne $previousManifest) { 'MANAGED' } else { 'NEW_DEPLOYMENT' }
                mode = 'NORMAL_DEVBRIDGE_DEPLOYMENT'
            }
        } else {
            [ordered]@{
                classification = $legacyClassification
                mode = if ($legacyExactAdopted) { 'LEGACY_EXACT_ADOPTION' } else { 'CONTROLLED_REPLACEMENT' }
                sourceRoot = $legacyIdentity.sourceRoot
                packageId = $legacyIdentity.packageId
                unknownFiles = $unknown
            }
        }
        managedFileCount = $packageFiles.Count
        managedFiles = @($packageFiles | ForEach-Object { [string]$_.TargetPath })
        unknownFiles = $unknown
        staleManagedFiles = $staleManaged
        deployedSha256Before = Get-Hash $descriptor.ResolvedTarget
        deployedPackageSha256Before = $deployedPackageBefore
        builtSha256 = $builtHash
        stagedSha256 = $builtHash
        packageSha256 = $packageHash
        changed = $deploymentChanged
        atomicReplacement = $false
        deployedSha256After = Get-Hash $descriptor.ResolvedTarget
        deployedPackageSha256After = $deployedPackageBefore
        stagingPath = $stagingRoot
        rollbackPath = $null
        rollbackState = $null
    }
    $script:Report.artifactFreshness.builtArtifactSha256 = $builtHash
    $script:Report.artifactFreshness.deployedArtifactSha256 = $script:Report.deployment.deployedSha256Before
    $script:Report.artifactFreshness.builtPackageSha256 = $packageHash
    $script:Report.artifactFreshness.deployedPackageSha256 = $deployedPackageBefore
    $script:Report.artifactFreshness.deploymentManifestPath = $manifestPath
    $script:Report.artifactFreshness.deploymentDecision = if ($deploymentChanged) { 'deployed' } else { 'unchanged' }
    $artifactState = Read-ArtifactState
    $statusBefore = Invoke-BridgeJson @('status')
    $statusBeforeResponse = Require-BridgeSuccess 'planning' 'inspect-runtime-status' 'status-before-registration' $statusBefore
    $generationBefore = [int]$statusBeforeResponse.generation
    $script:Report.runtime.generationBefore = $generationBefore
    $script:Report.artifactFreshness.generationBefore = $generationBefore
    $profileIncludesProject = @($statusBeforeResponse.requestedProjects | ForEach-Object { [string]$_ }) -contains $Project
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

    $postPlanResult = Invoke-BridgeJson (@('test', 'recipe', 'plan', [string]$descriptor.testRecipe) + (Get-RecipeArguments $descriptor))
    $postPlan = Require-BridgeSuccess 'planning' 'fix-recipe-plan' 'recipe-plan-after-registration' $postPlanResult
    if ($legacyExactAdopted) {
        Confirm-LegacyExactUnchanged $descriptor $packageFiles
    }
    $artifactStateMatches = $null -ne $artifactState -and
        [string]$artifactState.project -eq $Project -and
        [string]$artifactState.deploymentRoot -eq [string]$descriptor.ResolvedTargetRoot -and
        [string]$artifactState.deployedArtifactSha256 -eq $builtHash -and
        [string]$artifactState.deployedPackageSha256 -eq $packageHash -and
        [int]$artifactState.generation -eq $generationBefore
    $noOp = (-not [bool]$script:Report.deployment.changed) -and
        ($legacyExactAdopted -or
            ([bool]$postPlan.alreadySatisfied -and $artifactStateMatches))
    if (-not $noOp) {
        $script:Report.deploymentStarted = $true
        Write-PendingDeployment $manifestPath
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

        if ($legacyReplacement) {
            try {
                Move-LegacyRuntimeToRollback $descriptor.ResolvedTargetRoot
                $script:Report.deployment.rollbackState = 'retained-until-success'
                $script:Report.deployment.rollbackPath = $script:LegacyBackupPath
            } catch {
                Set-Failure 'deployment' 'inspect-maintenance-evidence' 'DEVBRIDGE_DEPLOYMENT_ADOPTION_UNSAFE' `
                    $_.Exception.Message 'legacy runtime directory replacement' 4 $null $true
            }
        }
        foreach ($stalePath in $staleManaged) {
            $staleTarget = Join-Path $descriptor.ResolvedTargetRoot $stalePath.Replace('/', '\')
            if (Test-Path -LiteralPath $staleTarget -PathType Leaf) {
                Remove-Item -LiteralPath $staleTarget -Force
            }
        }
        foreach ($file in $packageFiles) {
            $target = Join-Path $descriptor.ResolvedTargetRoot $file.TargetPath.Replace('/', '\')
            $parentInfo = [IO.Directory]::GetParent($target)
            $parent = if ($null -eq $parentInfo) { $null } else { $parentInfo.FullName }
            New-Item -ItemType Directory -Force -Path $parent | Out-Null
            try {
                Copy-AtomicFile $file.SourcePath $target
            } catch {
                Set-Failure 'deployment' 'repair-deployment-then-ensure-ready' 'DEVELOPMENT_DEPLOYMENT_FAILED' $_.Exception.Message 'atomic package deployment' 4 $null $true
            }
            if ((Get-Hash $target) -ne [string]$file.Sha256) {
                Set-Failure 'deployment' 'repair-deployment-then-ensure-ready' 'DEVELOPMENT_DEPLOYMENT_HASH_MISMATCH' `
                    "deployed package file does not match staged content: $($file.TargetPath)" `
                    'atomic package deployment' 4 $target $true
            }
        }
        $script:Report.deployment.deployedSha256After = Get-Hash $descriptor.ResolvedTarget
        $script:Report.deployment.deployedPackageSha256After = $packageHash
        $script:Report.deployment.atomicReplacement = $true
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
        $script:Report.deployment.deployedSha256After = Get-Hash $descriptor.ResolvedTarget
        $script:Report.deployment.deployedPackageSha256After = $packageHash
        $generationAfter = $generationBefore
    }


    $script:Report.runtime.generationAfter = $generationAfter
    $script:Report.runtime.generation = $generationAfter
    $script:Report.artifactFreshness.deployedArtifactSha256 = $script:Report.deployment.deployedSha256After
    $script:Report.artifactFreshness.deployedPackageSha256 = $script:Report.deployment.deployedPackageSha256After
    $script:Report.artifactFreshness.generationAfter = $generationAfter
    $script:Report.artifactFreshness.generation = $generationAfter
    if ($script:Report.deployment.deployedPackageSha256After -ne $packageHash) {
        Set-Failure 'freshness' 'repair-deployment-then-ensure-ready' 'DEVBRIDGE_DEPLOYMENT_PACKAGE_MISMATCH' `
            'the active runtime package does not match the staged package identity' `
            'package freshness verification' 4 $script:Report.deployment.deployedPackageSha256After $false
    }
    if ($script:Report.deployment.changed -or $generationAfter -gt $generationBefore) {
        $script:Report.artifactFreshness.loadedArtifactFreshnessProven = $true
        $script:Report.artifactFreshness.proof = 'package-manifest-plus-new-owned-generation'
    } elseif ($artifactStateMatches -or $legacyExactAdopted) {
        $script:Report.artifactFreshness.loadedArtifactFreshnessProven = $true
        $script:Report.artifactFreshness.proof = if ($legacyExactAdopted) {
            'legacy-exact-adoption-plus-owned-generation-state'
        } else {
            'package-manifest-plus-owned-generation-state'
        }
    } else {
        Set-Failure 'freshness' 'rebuild-or-establish-artifact-state' 'DEVELOPMENT_ARTIFACT_FRESHNESS_UNKNOWN' 'the current generation has no matching DevBridge artifact state evidence' 'artifact freshness proof' 4 $null $false
    }
    $ownershipProvenance = 'NORMAL_DEVBRIDGE_DEPLOYMENT'
    if ($legacyExactAdopted) {
        $ownershipProvenance = 'LEGACY_EXACT_ADOPTION'
    } elseif ($null -ne $legacyClassification) {
        $ownershipProvenance = 'LEGACY_CONTROLLED_REPLACEMENT'
    }
    Write-DeploymentManifest $manifestPath $packageFiles $packageHash $generationAfter `
        $ownershipProvenance $legacyClassification
    Write-ArtifactState $generationAfter ([string]$script:Report.deployment.deployedSha256After) `
        $packageHash $manifestPath
    Clear-PendingDeployment
    $script:Report.deploymentCommitted = $true

    if (-not $SkipRecipe) {
        $renew = Invoke-BridgeJson @('test', 'renew', [string]$script:Report.runtime.leaseId)
        Require-BridgeSuccess 'lease' 'renew-or-end-lease' 'test-renew-before-recipe' $renew | Out-Null
        $recipeArguments = @('test', 'recipe', 'run', [string]$descriptor.testRecipe, '--lease', [string]$script:Report.runtime.leaseId) + (Get-RecipeArguments $descriptor)
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
        $exception = $_.Exception
        $cause = if ($exception.Data.Contains('DevBridgeCause')) {
            [ordered]@{} + $exception.Data['DevBridgeCause']
        } else {
            [ordered]@{
                errorCode = 'DEVBRIDGE_TRANSACTION_EXCEPTION'
                phase = $script:Report.stage
                command = 'mod-test.ps1'
                message = Limit-Text $exception.Message
                exceptionType = $exception.GetType().FullName
            }
        }
        $evidence = [System.Collections.Generic.List[string]]::new()
        [void]$evidence.Add($script:TracePath)
        if ($null -ne $script:Report.build) {
            [void]$evidence.Add($script:Report.build.rawStdoutPath)
            [void]$evidence.Add($script:Report.build.rawStderrPath)
        }
        $cause.evidence = @($evidence)
        $script:Report.stage = if ($script:MaintenanceEstablished) { 'deployment' } else { [string]$cause.phase }
        $script:Report.nextAction = if ($script:MaintenanceEstablished) { 'inspect-maintenance-evidence' } else { 'inspect-result' }
        $script:Report.exitCode = 1
        $script:Report.retrySafety = if ([bool]$script:Report.deploymentStarted) {
            if ([bool]$script:Report.deploymentCommitted) { 'COMMITTED_RECONCILE' } else { 'UNKNOWN_RECONCILE' }
        } else { 'SAFE_AFTER_REPAIR' }
        $script:Report.failure = [ordered]@{
            stage = $script:Report.stage
            command = if ($cause.Contains('command')) { [string]$cause.command } else { 'mod-test.ps1' }
            exitCode = 1
            errorCode = 'DEVELOPMENT_TRANSACTION_FAILED'
            message = Limit-Text $exception.Message
            output = Limit-Text $_.ScriptStackTrace
            outputTruncated = $false
            causalDiagnostic = $null
            diagnosticSignature = $null
            ownership = $null
            causeErrorCode = [string]$cause.errorCode
            cause = $cause
            deploymentStarted = [bool]$script:Report.deploymentStarted
            deploymentCommitted = [bool]$script:Report.deploymentCommitted
            retrySafety = $script:Report.retrySafety
            evidence = @($cause.evidence)
            transactionId = $script:Report.transactionId
            workflowId = $script:Report.workflowId
        }
        $script:Report.artifactFreshness.errorCode = 'DEVELOPMENT_TRANSACTION_FAILED'
        $script:Report.artifactFreshness.loadedArtifactFreshnessProven = $false
        $script:KeepOwnership = $script:MaintenanceEstablished
    }
}
finally {
    if ($script:LegacyRootMoved) {
        if (-not $script:Report.success -and $script:MaintenanceEstablished) {
            Restore-LegacyRuntimeFromRollback
        } elseif ($script:Report.success -and $null -ne $script:Report.deployment) {
            $script:Report.deployment.rollbackState = 'retained-after-success'
        } elseif ($null -ne $script:Report.deployment) {
            $script:Report.deployment.rollbackState = 'retained-recoverable'
        }
    }
    if ($script:Report.success -or (-not $script:KeepOwnership -and -not $script:MaintenanceEstablished)) {
        Release-OwnedResources
    } else {
        $script:Report.cleanup.deferred = $true
    }
    Release-DeploymentMutationLock
    $script:Report.runtimeArtifacts = @(Get-ArtifactPaths)
    [Environment]::SetEnvironmentVariable('DEVBRIDGE_AGENT', $script:OldAgent, 'Process')
    [Environment]::SetEnvironmentVariable('DEVBRIDGE_SESSION', $script:OldSession, 'Process')
}

function Get-CompactJsonReport {
    $build = if ($null -eq $script:Report.build) {
        $null
    } else {
        [ordered]@{
            stage = [string]$script:Report.build.stage
            command = (Limit-BuildDiagnosticText ([string]$script:Report.build.command) 4096).Text
            exitCode = [int]$script:Report.build.exitCode
            output = [string]$script:Report.build.output
            outputTruncated = [bool]$script:Report.build.outputTruncated
            causalDiagnostic = [string]$script:Report.build.causalDiagnostic
            causalDiagnosticTruncated = [bool]$script:Report.build.causalDiagnosticTruncated
            diagnosticSignature = [string]$script:Report.build.diagnosticSignature
            rawStdoutPath = [string]$script:Report.build.rawStdoutPath
            rawStderrPath = [string]$script:Report.build.rawStderrPath
            rawNativeStdoutPath = [string]$script:Report.build.rawNativeStdoutPath
            rawNativeStderrPath = [string]$script:Report.build.rawNativeStderrPath
            rimWorldDirectory = [string]$script:Report.build.rimWorldDirectory
            buildProperties = @($script:Report.build.buildProperties)
            ownership = $script:Report.build.ownership
            sourceProject = [string]$script:Report.build.sourceProject
            stagingPath = [string]$script:Report.build.stagingPath
            timedOut = [bool]$script:Report.build.timedOut
            workingDirectory = [string]$script:Report.build.workingDirectory
            configuration = [string]$script:Report.build.configuration
            transactionId = [string]$script:Report.build.transactionId
            workflowId = [string]$script:Report.build.workflowId
            builtSha256 = [string]$script:Report.build.builtSha256
            errorCode = [string]$script:Report.build.errorCode
            failureMessage = [string]$script:Report.build.failureMessage
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
            packageSha256 = [string]$script:Report.deployment.packageSha256
            deployedPackageSha256Before = [string]$script:Report.deployment.deployedPackageSha256Before
            deployedPackageSha256After = [string]$script:Report.deployment.deployedPackageSha256After
            manifestPath = [string]$script:Report.deployment.manifestPath
            ownershipReconciliation = $script:Report.deployment.ownershipReconciliation
            rollbackPath = [string]$script:Report.deployment.rollbackPath
            rollbackState = [string]$script:Report.deployment.rollbackState
            managedFileCount = [int]$script:Report.deployment.managedFileCount
            managedFiles = @($script:Report.deployment.managedFiles)
            staleManagedFiles = @($script:Report.deployment.staleManagedFiles)
            unknownFiles = @($script:Report.deployment.unknownFiles)
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
            command = (Limit-BuildDiagnosticText ([string]$script:Report.failure.command) 4096).Text
            exitCode = [int]$script:Report.failure.exitCode
            errorCode = [string]$script:Report.failure.errorCode
            message = Limit-Text ([string]$script:Report.failure.message) 1024
            output = [string]$script:Report.failure.output
            outputTruncated = [bool]$script:Report.failure.outputTruncated
            causalDiagnostic = [string]$script:Report.failure.causalDiagnostic
            diagnosticSignature = [string]$script:Report.failure.diagnosticSignature
            ownership = $script:Report.failure.ownership
            causeErrorCode = [string]$script:Report.failure.causeErrorCode
            cause = $script:Report.failure.cause
            deploymentStarted = [bool]$script:Report.failure.deploymentStarted
            deploymentCommitted = [bool]$script:Report.failure.deploymentCommitted
            retrySafety = [string]$script:Report.failure.retrySafety
            evidence = @($script:Report.failure.evidence)
            transactionId = [string]$script:Report.transactionId
            workflowId = [string]$script:Report.workflowId
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
        deploymentStarted = [bool]$script:Report.deploymentStarted
        deploymentCommitted = [bool]$script:Report.deploymentCommitted
        retrySafety = $script:Report.retrySafety
        buildDiscrimination = $script:Report.buildDiscrimination
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
