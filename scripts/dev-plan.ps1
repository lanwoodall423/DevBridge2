[CmdletBinding()]
param(
    [string]$ChangedSince,
    [Alias('ChangedFiles', 'Path')]
    [string[]]$ChangedFile,
    [switch]$Json
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Set-Location -LiteralPath $repoRoot

function Get-FullPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    return [System.IO.Path]::GetFullPath($Path)
}

function Get-RelativeRepositoryPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $candidate = $Path
    if ([System.IO.Path]::IsPathRooted($candidate)) {
        $candidate = [System.IO.Path]::GetRelativePath($repoRoot, (Get-FullPath $candidate))
    }
    $candidate = $candidate.Replace('\', '/').TrimStart('./')
    if ([string]::IsNullOrWhiteSpace($candidate) -or $candidate -eq '..' -or
        $candidate.StartsWith('../', [StringComparison]::Ordinal)) {
        throw "Changed file is outside the repository: $Path"
    }
    return $candidate
}

function Invoke-GitNames {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    $values = @(& git @Arguments)
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
    return @($values | ForEach-Object { ([string]$_).Trim() } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}

function Get-ChangedFiles {
    $files = [System.Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)

    if ($null -ne $ChangedFile -and $ChangedFile.Count -gt 0) {
        foreach ($value in $ChangedFile) {
            foreach ($file in [regex]::Split([string]$value, '[,;]') |
                    Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) {
                [void]$files.Add((Get-RelativeRepositoryPath $file.Trim()))
            }
        }
    }
    else {
        if ([string]::IsNullOrWhiteSpace($ChangedSince)) {
            $ChangedSince = 'HEAD'
        }

        # Include committed changes from the requested ref and the current
        # worktree. Untracked files are read separately because git diff does
        # not report them.
        foreach ($file in (Invoke-GitNames @('diff', '--name-only', '--diff-filter=ACMR',
                ($ChangedSince + '...HEAD')))) {
            [void]$files.Add((Get-RelativeRepositoryPath $file))
        }
        foreach ($file in (Invoke-GitNames @('diff', '--name-only', '--diff-filter=ACMR'))) {
            [void]$files.Add((Get-RelativeRepositoryPath $file))
        }
        foreach ($file in (Invoke-GitNames @('diff', '--cached', '--name-only',
                '--diff-filter=ACMR'))) {
            [void]$files.Add((Get-RelativeRepositoryPath $file))
        }
        foreach ($file in (Invoke-GitNames @('ls-files', '--others', '--exclude-standard'))) {
            [void]$files.Add((Get-RelativeRepositoryPath $file))
        }
    }

    return @($files | Sort-Object)
}

function Add-Unique {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.HashSet[string]]$Set,
        [Parameter(Mandatory = $true)][string]$Value
    )
    [void]$Set.Add($Value)
}

function Get-FileClassification {
    param([Parameter(Mandatory = $true)][string]$Path)

    $normalized = $Path.Replace('\', '/')
    $lower = $normalized.ToLowerInvariant()

    # Build outputs are deliberately ignored. A caller can still plan an
    # explicit source path, but copying a generated output must never make a
    # source-only plan rebuild or restart anything.
    if ($lower -match '^(runtime|coordinator|bridgetools|1\.6/assemblies)/' -or
        $lower -match '^source/.+/(bin|obj)/' -or $lower -match '^artifacts/') {
        return [pscustomobject]@{ Class = 'ignored-generated'; Reason = 'generated-artifact' }
    }

    if ($lower -match '^testrecipes/') {
        return [pscustomobject]@{ Class = 'test-recipes'; Reason = 'repository-owned-recipe' }
    }
    if ($lower -match '^source/coordinator\.core/') {
        $linkedToMod = $lower -in @(
            'source/coordinator.core/state/devbridgeschemaversions.cs',
            'source/coordinator.core/quicktestactivationcore.cs',
            'source/coordinator.core/quicktestfailureartifact.cs')
        return [pscustomobject]@{
            Class = 'coordinator-core'
            Reason = if ($linkedToMod) { 'coordinator-core-and-mod-linked-source' } else { 'coordinator-core-project' }
            LinkedMod = $linkedToMod
        }
    }
    if ($lower -match '^source/coordinator/') {
        return [pscustomobject]@{ Class = 'coordinator-host'; Reason = 'coordinator-host-project' }
    }
    if ($lower -match '^source/bridgetools/') {
        return [pscustomobject]@{ Class = 'BridgeTools'; Reason = 'canonical-companion-project' }
    }
    if ($lower -match '^source/mod/') {
        return [pscustomobject]@{ Class = 'RimWorld-mod-assembly'; Reason = 'rimworld-mod-project' }
    }
    if ($lower -match '^source/coordinator\.tests/') {
        return [pscustomobject]@{ Class = 'tests-only'; Reason = 'offline-test-project' }
    }

    if ($lower -match '(^|/)(about/|loadfolders\.xml$|1\.6/|patches/|defs/|languages/|textures/|sounds/)' -or
        $lower -match '\.(xml|patch|tex|png|jpg|jpeg|dds|wav|ogg)$') {
        return [pscustomobject]@{ Class = 'RimWorld-content/xml'; Reason = 'rimworld-content' }
    }
    if ($lower -match '(^|/)(docs?/|readme|start_here|maintenance|changelog|license|contributing)' -or
        $lower -match '\.(md|txt|rst)$') {
        return [pscustomobject]@{ Class = 'docs-only'; Reason = 'documentation' }
    }

    if ($lower -match '\.csproj$') {
        if ($lower -eq 'source/coordinator.core/devbridge.coordinator.core.csproj') {
            return [pscustomobject]@{ Class = 'coordinator-core'; Reason = 'coordinator-core-project-file' }
        }
        if ($lower -eq 'source/coordinator/devbridge.coordinator.csproj') {
            return [pscustomobject]@{ Class = 'coordinator-host'; Reason = 'coordinator-host-project-file' }
        }
        if ($lower -eq 'source/bridgetools/devbridge2.bridgetools.csproj') {
            return [pscustomobject]@{ Class = 'BridgeTools'; Reason = 'companion-project-file' }
        }
        if ($lower -eq 'source/mod/devbridge2.csproj') {
            return [pscustomobject]@{ Class = 'RimWorld-mod-assembly'; Reason = 'mod-project-file' }
        }
        if ($lower -eq 'source/coordinator.tests/devbridge.coordinator.tests.csproj') {
            return [pscustomobject]@{ Class = 'tests-only'; Reason = 'test-project-file' }
        }
    }

    if ($lower -match '(^|/)(directory\.build\.(props|targets)|directory\.packages\.props|global\.json|nuget\.config|packages\.lock\.json|[^/]+\.sln)$' -or
        $lower -match '(^|/)scripts/') {
        return [pscustomobject]@{ Class = 'build-infrastructure'; Reason = 'build-or-release-infrastructure' }
    }

    return [pscustomobject]@{ Class = 'build-infrastructure'; Reason = 'unclassified-repository-build-input' }
}

function Get-Plan {
    param([Parameter(Mandatory = $true)][string[]]$Files)

    $build = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $deploy = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $classes = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $ignored = [System.Collections.Generic.List[string]]::new()
    $coordinator = $false
    $bridgeTools = $false
    $mod = $false
    $content = $false
    $recipes = $false
    $buildInfra = $false
    $requiresAllBuilds = $false
    $fileRecords = [System.Collections.Generic.List[object]]::new()

    foreach ($file in $Files) {
        $classification = Get-FileClassification $file
        if ($classification.Class -eq 'ignored-generated') {
            [void]$ignored.Add($file)
            [void]$fileRecords.Add([ordered]@{
                path = $file; changeClass = $classification.Class; reason = $classification.Reason
            })
            continue
        }

        Add-Unique $classes $classification.Class
        [void]$fileRecords.Add([ordered]@{
            path = $file; changeClass = $classification.Class; reason = $classification.Reason
        })
        switch ($classification.Class) {
            'coordinator-core' {
                $coordinator = $true
                if ($classification.LinkedMod) { $mod = $true }
            }
            'coordinator-host' { $coordinator = $true }
            'BridgeTools' { $bridgeTools = $true }
            'RimWorld-mod-assembly' { $mod = $true }
            'RimWorld-content/xml' { $content = $true }
            'test-recipes' { $recipes = $true }
            'build-infrastructure' {
                $buildInfra = $true
                $lower = $file.ToLowerInvariant()
                if ($lower -match 'directory\.build\.(props|targets)|directory\.packages\.props|global\.json|nuget\.config|packages\.lock\.json|\.sln$') {
                    $requiresAllBuilds = $true
                }
            }
        }
    }

    if ($requiresAllBuilds) {
        $coordinator = $true
        $bridgeTools = $true
        $mod = $true
    }
    if ($coordinator) { Add-Unique $build 'coordinator'; Add-Unique $deploy 'coordinator' }
    if ($bridgeTools) { Add-Unique $build 'bridgeTools'; Add-Unique $deploy 'bridgeTools' }
    if ($mod) { Add-Unique $build 'rimworld-mod'; Add-Unique $deploy 'rimworld-mod' }
    if ($content) { Add-Unique $deploy 'rimworld-content' }
    if ($recipes) { Add-Unique $deploy 'test-recipes' }

    $meaningfulClasses = @($classes | Sort-Object)
    $overall = if ($meaningfulClasses.Count -eq 0) { 'none' }
        elseif ($meaningfulClasses.Count -eq 1) { $meaningfulClasses[0] }
        else { 'mixed' }

    $restartRequired = $false
    $restartKnown = $true
    $requiredRefresh = 'none'
    $refreshReason = 'No runtime artifact requires refresh.'
    if ($mod -or $content) {
        $restartRequired = $true
        $requiredRefresh = 'rimworld'
        $refreshReason = 'The mod assembly/content is loaded by RimWorld and cannot be proven live until RimWorld restarts.'
    }
    elseif ($bridgeTools) {
        $restartKnown = $false
        $requiredRefresh = 'unknown'
        $refreshReason = 'The canonical companion will be deployed, but this workflow cannot prove whether the live host reloads it without a RimWorld restart.'
    }
    elseif ($coordinator) {
        $requiredRefresh = 'coordinator'
        $refreshReason = 'The coordinator is refreshed by graceful shutdown; RimWorld remains running.'
    }

    $deployArtifacts = [System.Collections.Generic.List[string]]::new()
    foreach ($file in $Files) {
        $classification = Get-FileClassification $file
        if ($classification.Class -in @('RimWorld-content/xml', 'test-recipes')) {
            [void]$deployArtifacts.Add($file)
        }
    }
    if ($coordinator) {
        [void]$deployArtifacts.Add('Coordinator/DevBridge.Coordinator*.dll and runtime files')
    }
    if ($bridgeTools) { [void]$deployArtifacts.Add('BridgeTools/DevBridge2.BridgeTools.dll') }
    if ($mod) { [void]$deployArtifacts.Add('1.6/Assemblies/DevBridge2.dll') }

    $buildProjects = [System.Collections.Generic.List[string]]::new()
    if ($coordinator) { [void]$buildProjects.Add('Source/Coordinator/DevBridge.Coordinator.csproj (transitive Core)') }
    if ($bridgeTools) { [void]$buildProjects.Add('Source/BridgeTools/DevBridge2.BridgeTools.csproj') }
    if ($mod) { [void]$buildProjects.Add('Source/Mod/DevBridge2.csproj') }

    $changedSinceValue = if ([string]::IsNullOrWhiteSpace($ChangedSince)) { $null } else { $ChangedSince }
    return [ordered]@{
        schemaVersion = 'devbridge-build-plan/v1'
        repositoryRoot = $repoRoot
        changedSince = $changedSinceValue
        sourceChangedFiles = @($Files)
        ignoredGeneratedFiles = @($ignored | Sort-Object)
        fileClassifications = @($fileRecords | Sort-Object -Property path)
        changeClass = $overall
        changeClasses = @($classes | Sort-Object)
        build = @($build | Sort-Object)
        buildProjects = @($buildProjects)
        deploy = @($deploy | Sort-Object)
        deployArtifacts = @($deployArtifacts)
        coordinatorRefreshRequired = [bool]$coordinator
        rimWorldRestartRequired = if ($restartKnown) { [bool]$restartRequired } else { $null }
        requiredRefresh = $requiredRefresh
        refreshReason = $refreshReason
        bridgeToolsLiveReloadSupported = if ($bridgeTools) { $null } else { $false }
        bridgeToolsDeployment = if ($bridgeTools) { 'canonical sibling BridgeTools/<active-mod-folder>/DevBridge2.BridgeTools.dll' } else { $null }
        hashComparison = 'performed after each required build; identical bytes produce deployRequired=false'
        deployRequired = $null
        notes = @(
            'Coordinator build includes the transitive Coordinator.Core project.'
            'The three Coordinator.Core files linked directly by Source/Mod also require a mod build.'
            'A copied RimWorld assembly is never treated as loaded; restart state remains explicit or unknown.'
        )
    }
}

$files = Get-ChangedFiles
$plan = Get-Plan $files
$jsonText = ($plan | ConvertTo-Json -Depth 12)

if ($Json) {
    Write-Output $jsonText
    exit 0
}

Write-Host 'DevBridge development change plan'
Write-Host ('  Classification: ' + $plan.changeClass)
Write-Host ('  Changed files:  ' + $plan.sourceChangedFiles.Count)
Write-Host ('  Build:          ' + ($(if ($plan.build.Count) { $plan.build -join ', ' } else { 'none' })))
Write-Host ('  Deploy:         ' + ($(if ($plan.deploy.Count) { $plan.deploy -join ', ' } else { 'none' })))
Write-Host ('  Required refresh:' + $plan.requiredRefresh)
if ($null -eq $plan.rimWorldRestartRequired) {
    Write-Host '  RimWorld restart: unknown (live BridgeTools reload is not proven)'
} else {
    Write-Host ('  RimWorld restart: ' + $plan.rimWorldRestartRequired)
}
if ($plan.ignoredGeneratedFiles.Count -gt 0) {
    Write-Host ('  Ignored generated files: ' + ($plan.ignoredGeneratedFiles -join ', '))
}
Write-Host "`nMachine-readable plan:"
Write-Output $jsonText
