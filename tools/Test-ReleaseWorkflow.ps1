#requires -Version 7.4

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$workflow = Get-Content -LiteralPath (Join-Path $repositoryRoot '.github/workflows/release.yml') -Raw

function Get-WorkflowScript {
    param([string]$StepName)
    $pattern = '(?ms)^      - name: ' + [regex]::Escape($StepName) + '\r?\n(?<step>.*?)(?=^      - name: |\z)'
    $step = [regex]::Match($workflow, $pattern)
    $match = [regex]::Match($step.Groups['step'].Value, '(?ms)^        run: \|\r?\n(?<script>.*)\z')
    if (-not $match.Success) { throw "Workflow step not found: $StepName" }
    return [scriptblock]::Create($match.Groups['script'].Value)
}

function Assert-Fails {
    param([scriptblock]$Action, [string]$ExpectedMessage)
    try { & $Action }
    catch {
        if ($_.Exception.Message -like $ExpectedMessage) { return }
        throw
    }
    throw "Expected failure: $ExpectedMessage"
}

function gh {
    $global:LASTEXITCODE = 0
    if ($args[0] -eq 'api') { return }
    if ($args[0] -eq 'release' -and $args[1] -eq 'create') {
        $script:publishedArguments = @($args)
        return
    }
    throw "Unexpected gh command: $args"
}

function git {
    if ($args[0] -ne 'ls-remote') { throw "Unexpected git command: $args" }
    $global:LASTEXITCODE = 0
}

$prepareNotes = Get-WorkflowScript 'Prepare release notes'
$publishRelease = Get-WorkflowScript 'Publish GitHub Release'
$testRoot = Join-Path $repositoryRoot "artifacts/release-workflow-check-$([guid]::NewGuid().ToString('N'))"
$savedEnvironment = @{}
foreach ($name in @('APP_VERSION', 'GITHUB_REPOSITORY', 'GITHUB_SHA', 'RUNNER_TEMP')) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
}

Push-Location $repositoryRoot
try {
    [xml]$project = Get-Content -LiteralPath 'src/Ncm.App/Ncm.App.csproj' -Raw
    $env:APP_VERSION = $project.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
    $env:GITHUB_REPOSITORY = 'example/ncm-test'
    $env:GITHUB_SHA = '0000000000000000000000000000000000000000'
    $env:RUNNER_TEMP = Join-Path $testRoot 'notes'
    $installerName = "NcmConverter-$env:APP_VERSION-win-x64-setup.exe"
    $installerScript = Get-Content -LiteralPath 'installer/Ncm.iss' -Raw
    if ($installerScript -notmatch '(?m)^OutputBaseFilename=NcmConverter-\{#AppVersion\}-win-x64-setup\r?$') {
        throw 'The Inno output filename does not include the application version.'
    }
    $buildTokens = $null
    $buildErrors = $null
    $buildAst = [Management.Automation.Language.Parser]::ParseFile((Join-Path $repositoryRoot 'tools/Build-Installer.ps1'), [ref]$buildTokens, [ref]$buildErrors)
    if ($buildErrors.Count -gt 0) { throw ($buildErrors.Message -join [Environment]::NewLine) }
    $filenameAssignment = $buildAst.Find({ param($node) $node -is [Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -eq '$setupFileName' }, $true)
    $actualName = & { $Version = $env:APP_VERSION; Invoke-Expression $filenameAssignment.Extent.Text; $setupFileName }
    if ($actualName -ne $installerName) { throw 'The build script and release workflow disagree on the installer filename.' }

    foreach ($directory in @('notes', 'docs/releases', 'artifacts/installer', 'artifacts/release-sources')) {
        [IO.Directory]::CreateDirectory((Join-Path $testRoot $directory)) | Out-Null
    }
    Copy-Item -LiteralPath "docs/releases/$env:APP_VERSION.md" -Destination (Join-Path $testRoot "docs/releases/$env:APP_VERSION.md")
    Set-Location $testRoot
    & $prepareNotes
    $notes = Get-Content -LiteralPath (Join-Path $env:RUNNER_TEMP 'release-notes.md') -Raw -Encoding utf8
    $downloadUrl = "https://github.com/example/ncm-test/releases/download/v$env:APP_VERSION/$installerName"
    if (-not $notes.Contains($downloadUrl) -or -not $notes.Contains('## 本次更新')) { throw 'Release notes have no versioned installer link or update section.' }

    $assets = @("artifacts/installer/$installerName", 'artifacts/release-sources/third-party-sources.zip')
    foreach ($asset in $assets) {
        Set-Content -LiteralPath $asset -Value 'release workflow test' -Encoding utf8
        $hash = (Get-FileHash -LiteralPath $asset -Algorithm SHA256).Hash.ToLowerInvariant()
        Set-Content -LiteralPath "$asset.sha256" -Value "$hash *$(Split-Path -Leaf $asset)" -Encoding utf8
    }
    $script:publishedArguments = @()
    & $publishRelease
    foreach ($asset in @($assets[0], "$($assets[0]).sha256", $assets[1], "$($assets[1]).sha256")) {
        if ($script:publishedArguments -notcontains $asset) { throw "Release omitted an asset: $asset" }
    }
    if ($script:publishedArguments -notcontains "v$env:APP_VERSION" -or $script:publishedArguments -notcontains ('NCM 转换器 ' + $env:APP_VERSION)) {
        throw 'Release tag or title has the wrong version.'
    }

    $script:publishedArguments = @()
    Set-Content -LiteralPath "$($assets[0]).sha256" -Value 'invalid checksum' -Encoding utf8
    Assert-Fails $publishRelease 'Release checksum does not match:*'
    if ($script:publishedArguments.Count -ne 0) { throw 'A corrupt asset was published.' }
    Remove-Item -LiteralPath "$($assets[0]).sha256"
    Assert-Fails $publishRelease 'Release asset is missing or empty:*'
    [IO.File]::WriteAllText((Join-Path $testRoot "docs/releases/$env:APP_VERSION.md"), '')
    Assert-Fails $prepareNotes 'Release changes are empty:*'
    Remove-Item -LiteralPath "docs/releases/$env:APP_VERSION.md"
    Assert-Fails $prepareNotes 'Write the release changes*'
    Write-Output 'Release workflow checks passed: notes, filenames, assets, corrupt checksums, missing files.'
}
finally {
    Pop-Location
    foreach ($name in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name]) }
    if (Test-Path -LiteralPath $testRoot) {
        $resolvedTestRoot = (Resolve-Path -LiteralPath $testRoot).Path
        $artifactRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
        if ([IO.Path]::GetDirectoryName($resolvedTestRoot) -ne $artifactRoot -or
            -not [IO.Path]::GetFileName($resolvedTestRoot).StartsWith('release-workflow-check-')) {
            throw "Refusing to remove an unexpected test directory: $resolvedTestRoot"
        }
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}
