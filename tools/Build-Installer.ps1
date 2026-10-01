[CmdletBinding()]
param(
    [string]$SourceDirectory,
    [string]$OutputDirectory,
    [string]$NativeMediaDirectory,
    [string]$Version,
    [string]$IsccPath,
    [switch]$PublishTrimmed,
    [switch]$NoPublishTrimmed,
    [switch]$PublishAot,
    [switch]$NoPublishAot,
    [switch]$SkipPublish
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:ExpectedNativeMediaDlls = @(
    'avcodec-63.dll',
    'avformat-63.dll',
    'avutil-61.dll',
    'swresample-7.dll',
    'swscale-10.dll'
)

function Get-FullPathFromBase {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$BasePath
    )

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $BasePath $Path))
}

function Assert-NoReparsePathAncestors {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Description
    )

    $currentPath = [System.IO.Path]::GetFullPath($Path)
    while (-not [string]::IsNullOrWhiteSpace($currentPath)) {
        if (Test-Path -LiteralPath $currentPath) {
            $item = Get-Item -LiteralPath $currentPath -Force
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing $Description because this path or an ancestor is a reparse point: $currentPath"
            }
        }

        $parent = [System.IO.Directory]::GetParent($currentPath)
        if ($null -eq $parent) {
            break
        }
        $currentPath = $parent.FullName
    }
}

function Get-TrimmedDirectoryPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $pathRoot = [System.IO.Path]::GetPathRoot($fullPath)
    if ([string]::Equals($fullPath, $pathRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $pathRoot
    }

    $trimmed = $fullPath.TrimEnd([char[]]@( '\', '/' ))
    if ([string]::IsNullOrEmpty($trimmed)) {
        return [System.IO.Path]::GetPathRoot($fullPath)
    }

    return $trimmed
}

function Test-PathContains {
    param(
        [Parameter(Mandatory = $true)][string]$ParentPath,
        [Parameter(Mandatory = $true)][string]$ChildPath
    )

    $parent = Get-TrimmedDirectoryPath $ParentPath
    $child = Get-TrimmedDirectoryPath $ChildPath
    if ([string]::Equals($parent, $child, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $true
    }

    $prefix = $parent
    if (-not $prefix.EndsWith('\') -and -not $prefix.EndsWith('/')) {
        $prefix += [System.IO.Path]::DirectorySeparatorChar
    }

    return $child.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)
}

function Test-PathsOverlap {
    param(
        [Parameter(Mandatory = $true)][string]$FirstPath,
        [Parameter(Mandatory = $true)][string]$SecondPath
    )

    return (Test-PathContains -ParentPath $FirstPath -ChildPath $SecondPath) -or
        (Test-PathContains -ParentPath $SecondPath -ChildPath $FirstPath)
}

function Assert-ReplaceableRegularFile {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    $item = Get-Item -LiteralPath $Path -Force
    if ($item.PSIsContainer) {
        throw "Expected a file but found a directory: $Path"
    }
    if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing to replace a reparse point: $Path"
    }
    if (($item.Attributes -band [System.IO.FileAttributes]::ReadOnly) -ne 0) {
        throw "Refusing to replace a read-only file: $Path"
    }
}

function Resolve-IsccExecutable {
    param([string]$RequestedPath, [string]$RepositoryRoot)

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $command = Get-Command -Name $RequestedPath -CommandType Application -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($null -ne $command) {
            return $command.Source
        }

        $candidate = Get-FullPathFromBase -Path $RequestedPath -BasePath $RepositoryRoot
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return $candidate
        }

        throw "The requested ISCC.exe was not found: $RequestedPath"
    }

    $programRoots = @(
        $env:ProgramFiles,
        ${env:ProgramFiles(x86)},
        $env:ProgramW6432
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique

    foreach ($programRoot in $programRoots) {
        $candidate = Join-Path $programRoot 'Inno Setup 7\ISCC.exe'
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return $candidate
        }
    }

    $command = Get-Command -Name 'ISCC.exe' -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -ne $command) {
        return $command.Source
    }

    throw 'ISCC.exe was not found. Install Inno Setup 7.0.2 or later or pass its path with -IsccPath; this script does not download build tools.'
}

function Get-ValidatedIsccVersion {
    param([Parameter(Mandatory = $true)][string]$CompilerPath)

    $versionOutput = & $CompilerPath '--version'
    if ($LASTEXITCODE -ne 0) {
        throw "Could not query the ISCC version from $CompilerPath (exit code $LASTEXITCODE). Install Inno Setup 7.0.2 or later."
    }

    $versionMatch = [regex]::Match((@($versionOutput) -join ' '), '(?<version>\d+\.\d+\.\d+)')
    if (-not $versionMatch.Success) {
        throw "Could not parse the ISCC version from its --version output. Install Inno Setup 7.0.2 or later. Output: $(@($versionOutput) -join ' ')"
    }

    $version = [version]$versionMatch.Groups['version'].Value
    if ($version -lt [version]'7.0.2') {
        throw "ISCC $version is too old. Inno Setup 7.0.2 or later is required."
    }

    return $version
}

function Get-MsBuildProperty {
    param(
        [Parameter(Mandatory = $true)][string]$DotnetPath,
        [Parameter(Mandatory = $true)][string]$ProjectPath,
        [Parameter(Mandatory = $true)][string]$PropertyName
    )

    $lines = & $DotnetPath msbuild $ProjectPath "-getProperty:$PropertyName" '-p:Configuration=Release' '-p:Platform=x64' '-p:RuntimeIdentifier=win-x64' -nologo
    if ($LASTEXITCODE -ne 0) {
        throw "Could not read the $PropertyName property from the app project."
    }

    $value = (@($lines) -join [Environment]::NewLine).Trim()
    if ($value.StartsWith('{')) {
        $json = $value | ConvertFrom-Json -ErrorAction Stop
        if ($null -ne $json.Properties) {
            $property = $json.Properties.PSObject.Properties[$PropertyName]
            if ($null -ne $property) {
                return [string]$property.Value
            }
        }
    }

    return $value
}

function Assert-X64PeExecutable {
    param([Parameter(Mandatory = $true)][string]$Path)

    $stream = [System.IO.File]::OpenRead($Path)
    $reader = [System.IO.BinaryReader]::new($stream)
    try {
        if ($reader.ReadUInt16() -ne 0x5A4D) {
            throw "The app executable is not a valid PE file: $Path"
        }

        $stream.Position = 0x3C
        $peOffset = $reader.ReadInt32()
        if ($peOffset -lt 0 -or $peOffset -gt ($stream.Length - 6)) {
            throw "The app executable has an invalid PE header: $Path"
        }

        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) {
            throw "The app executable has an invalid PE signature: $Path"
        }
        if ($reader.ReadUInt16() -ne 0x8664) {
            throw "The published app executable is not x64: $Path"
        }
    }
    finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}

function Assert-SelfContainedPublish {
    param(
        [Parameter(Mandatory = $true)][string]$PublishDirectory,
        [Parameter(Mandatory = $true)][bool]$IsAot
    )

    $appExecutable = Join-Path $PublishDirectory 'Ncm.App.exe'
    if (-not (Test-Path -LiteralPath $appExecutable -PathType Leaf)) {
        throw "Publish output is missing the normal app entry point: $appExecutable"
    }
    if ((Get-Item -LiteralPath $appExecutable).Length -le 0) {
        throw "The app executable is empty: $appExecutable"
    }
    Assert-X64PeExecutable -Path $appExecutable

    $runtimeConfigPath = Join-Path $PublishDirectory 'Ncm.App.runtimeconfig.json'
    if (-not (Test-Path -LiteralPath $runtimeConfigPath -PathType Leaf)) {
        if (-not $IsAot) {
            throw 'The self-contained publish output is missing Ncm.App.runtimeconfig.json.'
        }
    }
    else {
        $runtimeConfig = Get-Content -LiteralPath $runtimeConfigPath -Raw | ConvertFrom-Json -ErrorAction Stop
        $runtimeOptions = $runtimeConfig.runtimeOptions
        if ($null -eq $runtimeOptions) {
            throw 'Ncm.App.runtimeconfig.json has no runtimeOptions object.'
        }

        $runtimeOptionNames = @($runtimeOptions.PSObject.Properties | ForEach-Object { $_.Name })
        if ($runtimeOptionNames -contains 'framework' -or $runtimeOptionNames -contains 'frameworks') {
            throw 'The publish output requests a shared .NET runtime and is not self-contained.'
        }
    }

    if (-not $IsAot) {
        foreach ($runtimeFile in @('coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll')) {
            if (-not (Test-Path -LiteralPath (Join-Path $PublishDirectory $runtimeFile) -PathType Leaf)) {
                throw "The self-contained publish output is missing $runtimeFile."
            }
        }
    }

    foreach ($requiredRuntimeFile in @('Ncm.App.pri', 'App.xbf', 'MainWindow.xbf', 'Microsoft.UI.Xaml.dll')) {
        $runtimePath = Join-Path $PublishDirectory $requiredRuntimeFile
        if (-not (Test-Path -LiteralPath $runtimePath -PathType Leaf)) {
            throw "Publish output is missing required WinUI runtime content: $requiredRuntimeFile"
        }
        if ((Get-Item -LiteralPath $runtimePath).Length -le 0) {
            throw "Required WinUI runtime content is empty: $requiredRuntimeFile"
        }
    }

    $licenseDirectory = Join-Path $PublishDirectory 'licenses'
    if (-not (Test-Path -LiteralPath $licenseDirectory -PathType Container)) {
        throw 'The publish output is missing the third-party licenses directory.'
    }
    $licenseFile = Get-ChildItem -LiteralPath $licenseDirectory -File -Recurse | Select-Object -First 1
    if ($null -eq $licenseFile) {
        throw 'The third-party licenses directory is empty.'
    }

    foreach ($documentName in @('LICENSE', 'NOTICE')) {
        $publishedDocument = Join-Path $PublishDirectory $documentName
        if (-not (Test-Path -LiteralPath $publishedDocument -PathType Leaf)) {
            throw "Publish output is missing the root $documentName file: $publishedDocument"
        }
        if ((Get-Item -LiteralPath $publishedDocument).Length -le 0) {
            throw "The published $documentName file is empty: $publishedDocument"
        }
    }
}

function Assert-NativeMediaDirectory {
    param([Parameter(Mandatory = $true)][string]$Directory)

    if (-not (Test-Path -LiteralPath $Directory -PathType Container)) {
        throw "NativeMediaDirectory was not found. Run Build-NativeMedia.ps1 first: $Directory"
    }
    $directoryItem = Get-Item -LiteralPath $Directory -Force
    if (($directoryItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing to package NativeMediaDirectory through a reparse point: $Directory"
    }

    $expected = @($script:ExpectedNativeMediaDlls | Sort-Object)
    $actual = @(Get-ChildItem -LiteralPath $Directory -Filter '*.dll' -File | ForEach-Object { $_.Name } | Sort-Object)
    $dllDifference = @(Compare-Object -ReferenceObject $expected -DifferenceObject $actual)
    if ($dllDifference.Count -gt 0) {
        throw "NativeMediaDirectory must contain exactly the five supported FFmpeg DLLs. Found: $($actual -join ', ')"
    }
    foreach ($dllName in $script:ExpectedNativeMediaDlls) {
        $dllPath = Join-Path $Directory $dllName
        $dllItem = Get-Item -LiteralPath $dllPath -Force
        if (($dllItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing to package a reparse point as a native FFmpeg DLL: $dllPath"
        }
        if ($dllItem.Length -le 0) {
            throw "Native FFmpeg DLL is empty: $dllPath"
        }
    }

    $metadataPath = Join-Path $Directory 'build-metadata.json'
    if (-not (Test-Path -LiteralPath $metadataPath -PathType Leaf)) {
        throw "NativeMediaDirectory is missing build-metadata.json: $metadataPath"
    }
    $metadataItem = Get-Item -LiteralPath $metadataPath -Force
    if (($metadataItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Refusing to package build-metadata.json through a reparse point.'
    }
    $metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json -ErrorAction Stop
    if ($metadata.schemaVersion -ne 1 -or $metadata.target -ne 'win-x64') {
        throw 'NativeMedia build metadata must declare schemaVersion 1 and target win-x64.'
    }

    $metadataDlls = @($metadata.sharedLibraries | ForEach-Object { [string]$_ } | Sort-Object)
    $metadataDifference = @(Compare-Object -ReferenceObject $expected -DifferenceObject $metadataDlls)
    if ($metadataDifference.Count -gt 0) {
        throw 'NativeMedia build metadata sharedLibraries does not match the five DLLs in the directory.'
    }
    if ($metadata.runtimeDllAudit.passed -ne $true -or @($metadata.runtimeDllAudit.unbundledNonSystemImports).Count -ne 0) {
        throw 'NativeMedia metadata reports unbundled non-system runtime DLL dependencies.'
    }
}

function Copy-PublishDirectoryContents {
    param(
        [Parameter(Mandatory = $true)][string]$SourceDirectory,
        [Parameter(Mandatory = $true)][string]$DestinationDirectory
    )

    $sourceEntries = @(Get-ChildItem -LiteralPath $SourceDirectory -Force -Recurse)
    $reparseEntry = $sourceEntries | Where-Object {
        ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0
    } | Select-Object -First 1
    if ($null -ne $reparseEntry) {
        throw "Refusing to copy a publish tree containing a reparse point: $($reparseEntry.FullName)"
    }

    foreach ($entry in Get-ChildItem -LiteralPath $SourceDirectory -Force) {
        Copy-Item -LiteralPath $entry.FullName -Destination $DestinationDirectory -Recurse -Force
    }
}

function Install-NativeMediaIntoPublishDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$NativeMediaDirectory,
        [Parameter(Mandatory = $true)][string]$PublishDirectory
    )

    Assert-NativeMediaDirectory -Directory $NativeMediaDirectory
    foreach ($dllName in $script:ExpectedNativeMediaDlls) {
        Copy-Item -LiteralPath (Join-Path $NativeMediaDirectory $dllName) -Destination $PublishDirectory -Force
    }
    Copy-Item -LiteralPath (Join-Path $NativeMediaDirectory 'build-metadata.json') -Destination $PublishDirectory -Force

    $obsoleteFfmpegNames = @('avdevice-63.dll', 'avfilter-12.dll')
    $obsoleteFfmpegFiles = @(
        Get-ChildItem -LiteralPath $PublishDirectory -File -Recurse |
            Where-Object { $_.Name -match '^av(?:device|filter)-\d+\.dll$' }
    )
    foreach ($obsoleteFile in $obsoleteFfmpegFiles) {
        if ($obsoleteFfmpegNames -notcontains $obsoleteFile.Name) {
            throw "Unexpected bundled FFmpeg auxiliary DLL version: $($obsoleteFile.FullName)"
        }
        Remove-Item -LiteralPath $obsoleteFile.FullName -Force
    }

    $ffmpegDllPattern = '^(?:avcodec|avformat|avutil|avdevice|avfilter|postproc|swresample|swscale)-\d+\.dll$'
    $packagedFfmpegDlls = @(
        Get-ChildItem -LiteralPath $PublishDirectory -File -Recurse |
            Where-Object { $_.Name -match $ffmpegDllPattern } |
            ForEach-Object { $_.Name } |
            Sort-Object
    )
    $expectedFfmpegDlls = @($script:ExpectedNativeMediaDlls | Sort-Object)
    $packagedDifference = @(Compare-Object -ReferenceObject $expectedFfmpegDlls -DifferenceObject $packagedFfmpegDlls)
    if ($packagedDifference.Count -gt 0) {
        throw "Published FFmpeg DLL set is not minimal: $($packagedFfmpegDlls -join ', ')"
    }

    foreach ($dllName in $script:ExpectedNativeMediaDlls) {
        $nativeHash = (Get-FileHash -LiteralPath (Join-Path $NativeMediaDirectory $dllName) -Algorithm SHA256).Hash
        $publishHash = (Get-FileHash -LiteralPath (Join-Path $PublishDirectory $dllName) -Algorithm SHA256).Hash
        if ($nativeHash -ne $publishHash) {
            throw "Packaged FFmpeg DLL hash does not match NativeMediaDirectory: $dllName"
        }
    }
    $metadataHash = (Get-FileHash -LiteralPath (Join-Path $NativeMediaDirectory 'build-metadata.json') -Algorithm SHA256).Hash
    $publishedMetadataHash = (Get-FileHash -LiteralPath (Join-Path $PublishDirectory 'build-metadata.json') -Algorithm SHA256).Hash
    if ($metadataHash -ne $publishedMetadataHash) {
        throw 'Packaged build-metadata.json does not match NativeMediaDirectory.'
    }
}

$scriptRoot = [System.IO.Path]::GetFullPath($PSScriptRoot)
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $scriptRoot '..'))
$artifactRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
$projectPath = Join-Path $repositoryRoot 'src\Ncm.App\Ncm.App.csproj'
$publishProfilePath = Join-Path $repositoryRoot 'src\Ncm.App\Properties\PublishProfiles\Installer.pubxml'
$installerScriptPath = Join-Path $repositoryRoot 'installer\Ncm.iss'

if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    throw "App project was not found: $projectPath"
}
if (-not (Test-Path -LiteralPath $publishProfilePath -PathType Leaf)) {
    throw "Installer publish profile was not found: $publishProfilePath"
}
if (-not (Test-Path -LiteralPath $installerScriptPath -PathType Leaf)) {
    throw "Inno Setup script was not found: $installerScriptPath"
}

if ($PublishTrimmed.IsPresent -and $NoPublishTrimmed.IsPresent) {
    throw 'Use only one of -PublishTrimmed and -NoPublishTrimmed.'
}
if ($PublishAot.IsPresent -and $NoPublishAot.IsPresent) {
    throw 'Use only one of -PublishAot and -NoPublishAot.'
}
if ($PublishAot.IsPresent -and $NoPublishTrimmed.IsPresent) {
    throw 'NativeAOT requires trimming; -NoPublishTrimmed cannot be combined with -PublishAot.'
}

$dotnetPath = $null
if (-not $SkipPublish.IsPresent -or [string]::IsNullOrWhiteSpace($Version)) {
    $dotnetCommand = Get-Command -Name 'dotnet' -CommandType Application -ErrorAction Stop |
        Select-Object -First 1
    $dotnetPath = $dotnetCommand.Source
}
$resolvedIsccPath = Resolve-IsccExecutable -RequestedPath $IsccPath -RepositoryRoot $repositoryRoot
$resolvedIsccVersion = Get-ValidatedIsccVersion -CompilerPath $resolvedIsccPath
$includeChineseSimplified = 'no'
$chineseSimplifiedFile = Join-Path (Split-Path -Parent $resolvedIsccPath) 'Languages\ChineseSimplified.isl'
if (Test-Path -LiteralPath $chineseSimplifiedFile -PathType Leaf) {
    $includeChineseSimplified = 'yes'
}
else {
    Write-Warning "Official Simplified Chinese language file was not found next to ISCC $resolvedIsccVersion; built-in installer messages will use English. No translation was downloaded."
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = Get-MsBuildProperty -DotnetPath $dotnetPath -ProjectPath $projectPath -PropertyName 'AssemblyVersion'
    if ([string]::IsNullOrWhiteSpace($Version)) {
        $Version = Get-MsBuildProperty -DotnetPath $dotnetPath -ProjectPath $projectPath -PropertyName 'Version'
    }
}

$Version = $Version.Trim()
$versionMatch = [regex]::Match($Version, '^(?<core>\d+(?:\.\d+){0,3})(?:[-+][0-9A-Za-z.-]+)?$')
if (-not $versionMatch.Success) {
    throw "Version must be numeric with up to four components, optionally followed by a prerelease or build suffix: $Version"
}
$fileVersion = $versionMatch.Groups['core'].Value
foreach ($component in $fileVersion.Split('.')) {
    $componentValue = 0
    if (-not [int]::TryParse($component, [ref]$componentValue) -or $componentValue -gt 65535) {
        throw "Version components must be between 0 and 65535: $Version"
    }
}

$publishAotEnabled = -not $NoPublishAot.IsPresent
if ($publishAotEnabled -and $NoPublishTrimmed.IsPresent) {
    throw 'NativeAOT requires trimming; -NoPublishTrimmed cannot be combined with the installer AOT profile.'
}

$outputWasProvided = -not [string]::IsNullOrWhiteSpace($OutputDirectory)
if ($outputWasProvided) {
    $resolvedOutputDirectory = Get-FullPathFromBase -Path $OutputDirectory -BasePath $repositoryRoot
}
else {
    $resolvedOutputDirectory = Join-Path $artifactRoot 'installer'
}

$nativeDirectoryWasProvided = -not [string]::IsNullOrWhiteSpace($NativeMediaDirectory)
if ($nativeDirectoryWasProvided) {
    $resolvedNativeMediaDirectory = Get-FullPathFromBase -Path $NativeMediaDirectory -BasePath $repositoryRoot
}
else {
    $resolvedNativeMediaDirectory = Join-Path $artifactRoot 'native\win-x64'
}

$sourceWasProvided = -not [string]::IsNullOrWhiteSpace($SourceDirectory)
$ownsTemporarySource = -not $sourceWasProvided -or $SkipPublish.IsPresent
$temporarySourceMarker = $null
if ($SkipPublish.IsPresent -and -not $sourceWasProvided) {
    throw '-SkipPublish requires -SourceDirectory to point to an existing publish directory.'
}
if ($sourceWasProvided) {
    $requestedSourceDirectory = Get-FullPathFromBase -Path $SourceDirectory -BasePath $repositoryRoot
}
else {
    $requestedSourceDirectory = $null
}

if ($SkipPublish.IsPresent) {
    $stageId = [guid]::NewGuid().ToString('N')
    $stageName = ".ncm-installer-publish-$stageId"
    $resolvedSourceDirectory = Join-Path $artifactRoot $stageName
    $temporarySourceMarker = Join-Path $artifactRoot "$stageName.owner"
    if ((Test-Path -LiteralPath $resolvedSourceDirectory) -or (Test-Path -LiteralPath $temporarySourceMarker)) {
        throw 'The generated publish staging path already exists; refusing to reuse it.'
    }
}
elseif ($sourceWasProvided) {
    $resolvedSourceDirectory = $requestedSourceDirectory
}
else {
    $stageId = [guid]::NewGuid().ToString('N')
    $stageName = ".ncm-installer-publish-$stageId"
    $resolvedSourceDirectory = Join-Path $artifactRoot $stageName
    $temporarySourceMarker = Join-Path $artifactRoot "$stageName.owner"
    if ((Test-Path -LiteralPath $resolvedSourceDirectory) -or (Test-Path -LiteralPath $temporarySourceMarker)) {
        throw 'The generated publish staging path already exists; refusing to reuse it.'
    }
}

if (Test-PathsOverlap -FirstPath $resolvedSourceDirectory -SecondPath $resolvedOutputDirectory) {
    throw 'SourceDirectory and OutputDirectory must not be the same directory or contain one another.'
}
if ($sourceWasProvided -and (Test-PathsOverlap -FirstPath $requestedSourceDirectory -SecondPath $resolvedOutputDirectory)) {
    throw 'SourceDirectory and OutputDirectory must not be the same directory or contain one another.'
}
if ($SkipPublish.IsPresent -and (Test-PathsOverlap -FirstPath $requestedSourceDirectory -SecondPath $resolvedSourceDirectory)) {
    throw 'The requested source and owned packaging stage must not be the same directory or contain one another.'
}
if (Test-PathsOverlap -FirstPath $resolvedNativeMediaDirectory -SecondPath $resolvedOutputDirectory) {
    throw 'NativeMediaDirectory and OutputDirectory must not be the same directory or contain one another.'
}
if (Test-PathsOverlap -FirstPath $resolvedNativeMediaDirectory -SecondPath $resolvedSourceDirectory) {
    throw 'NativeMediaDirectory and the publish directory must not be the same directory or contain one another.'
}
if ($SkipPublish.IsPresent -and (Test-PathsOverlap -FirstPath $resolvedNativeMediaDirectory -SecondPath $requestedSourceDirectory)) {
    throw 'NativeMediaDirectory and the requested source directory must not be the same directory or contain one another.'
}

Assert-NoReparsePathAncestors -Path $repositoryRoot -Description 'use the repository root'
Assert-NoReparsePathAncestors -Path $artifactRoot -Description 'use the artifact root'
Assert-NoReparsePathAncestors -Path $resolvedSourceDirectory -Description 'use the publish directory'
if ($sourceWasProvided) {
    Assert-NoReparsePathAncestors -Path $requestedSourceDirectory -Description 'read the requested source directory'
}
Assert-NoReparsePathAncestors -Path $resolvedNativeMediaDirectory -Description 'read NativeMediaDirectory'
Assert-NoReparsePathAncestors -Path $resolvedOutputDirectory -Description 'write OutputDirectory'

$licenseDocument = Join-Path $repositoryRoot 'LICENSE'
$noticeFile = Join-Path $repositoryRoot 'NOTICE'
foreach ($requiredDocument in @($licenseDocument, $noticeFile)) {
    if (-not (Test-Path -LiteralPath $requiredDocument -PathType Leaf)) {
        throw "Required application document was not found: $requiredDocument"
    }
}

Assert-NativeMediaDirectory -Directory $resolvedNativeMediaDirectory

if ($SkipPublish.IsPresent) {
    if (-not (Test-Path -LiteralPath $requestedSourceDirectory -PathType Container)) {
        throw "-SkipPublish requires an existing publish directory: $requestedSourceDirectory"
    }
    $sourceItem = Get-Item -LiteralPath $requestedSourceDirectory -Force
    if (($sourceItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing to package a reparse point: $requestedSourceDirectory"
    }
}
elseif (Test-Path -LiteralPath $resolvedSourceDirectory) {
    $sourceItem = Get-Item -LiteralPath $resolvedSourceDirectory -Force
    if (-not $sourceItem.PSIsContainer) {
        throw "SourceDirectory is a file: $resolvedSourceDirectory"
    }
    if (($sourceItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing to publish into a reparse point: $resolvedSourceDirectory"
    }
    if ([System.IO.Directory]::EnumerateFileSystemEntries($resolvedSourceDirectory).GetEnumerator().MoveNext()) {
        throw "SourceDirectory must be empty so stale files cannot enter the installer: $resolvedSourceDirectory"
    }
}

if (Test-Path -LiteralPath $resolvedOutputDirectory) {
    $outputItem = Get-Item -LiteralPath $resolvedOutputDirectory -Force
    if (-not $outputItem.PSIsContainer) {
        throw "OutputDirectory is a file: $resolvedOutputDirectory"
    }
    if (($outputItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing to write installer outputs through a reparse point: $resolvedOutputDirectory"
    }
}

$setupFileName = 'setup.exe'
$checksumFileName = "$setupFileName.sha256"
$finalSetupPath = Join-Path $resolvedOutputDirectory $setupFileName
$finalChecksumPath = Join-Path $resolvedOutputDirectory $checksumFileName
Assert-ReplaceableRegularFile -Path $finalSetupPath
Assert-ReplaceableRegularFile -Path $finalChecksumPath

$temporarySetupBaseName = 'NcmConverter-Setup-build-' + [guid]::NewGuid().ToString('N')
$temporarySetupPath = Join-Path $resolvedOutputDirectory "$temporarySetupBaseName.exe"
$temporaryChecksumPath = "$finalChecksumPath.$([guid]::NewGuid().ToString('N')).tmp"

try {
    [System.IO.Directory]::CreateDirectory($artifactRoot) | Out-Null
    [System.IO.Directory]::CreateDirectory($resolvedOutputDirectory) | Out-Null

    if ($ownsTemporarySource) {
        [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($resolvedSourceDirectory)) | Out-Null
        Set-Content -LiteralPath $temporarySourceMarker -Value $resolvedSourceDirectory -NoNewline -Encoding UTF8
        [System.IO.Directory]::CreateDirectory($resolvedSourceDirectory) | Out-Null
    }
    elseif (-not $SkipPublish.IsPresent) {
        [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($resolvedSourceDirectory)) | Out-Null
        [System.IO.Directory]::CreateDirectory($resolvedSourceDirectory) | Out-Null
    }

    if ($SkipPublish.IsPresent) {
        Copy-PublishDirectoryContents -SourceDirectory $requestedSourceDirectory -DestinationDirectory $resolvedSourceDirectory
    }
    else {
        $publishArguments = @(
            'publish',
            $projectPath,
            "-p:PublishProfileFullPath=$publishProfilePath",
            '--configuration', 'Release',
            '--runtime', 'win-x64',
            '--self-contained', 'true',
            '--output', $resolvedSourceDirectory,
            '-p:Platform=x64',
            '-p:SelfContained=true',
            '-p:UseAppHost=true'
        )
        if ($PublishTrimmed.IsPresent) {
            $publishArguments += '-p:PublishTrimmed=true'
        }
        elseif ($NoPublishTrimmed.IsPresent) {
            $publishArguments += '-p:PublishTrimmed=false'
        }
        if ($PublishAot.IsPresent) {
            $publishArguments += '-p:PublishAot=true'
        }
        elseif ($NoPublishAot.IsPresent) {
            $publishArguments += '-p:PublishAot=false'
        }

        & $dotnetPath @publishArguments
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish failed with exit code $LASTEXITCODE."
        }
    }

    Assert-SelfContainedPublish -PublishDirectory $resolvedSourceDirectory -IsAot:$publishAotEnabled
    Install-NativeMediaIntoPublishDirectory -NativeMediaDirectory $resolvedNativeMediaDirectory -PublishDirectory $resolvedSourceDirectory

    $innoDefines = @(
        "/DAppSourceDir=$resolvedSourceDirectory",
        "/DAppVersion=$Version",
        "/DFileVersion=$fileVersion",
        "/DIncludeChineseSimplified=$includeChineseSimplified"
    )
    $compilerArguments = @(
        '/Qp',
        "/O$resolvedOutputDirectory",
        "/F$temporarySetupBaseName"
    ) + $innoDefines + @($installerScriptPath)

    & $resolvedIsccPath @compilerArguments
    if ($LASTEXITCODE -ne 0) {
        throw "ISCC.exe failed with exit code $LASTEXITCODE."
    }
    if (-not (Test-Path -LiteralPath $temporarySetupPath -PathType Leaf)) {
        throw "ISCC.exe reported success but did not create the expected installer: $temporarySetupPath"
    }
    if ((Get-Item -LiteralPath $temporarySetupPath).Length -le 0) {
        throw 'ISCC.exe produced an empty setup.exe.'
    }

    $setupHash = (Get-FileHash -LiteralPath $temporarySetupPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksumContents = "$setupHash *$setupFileName`r`n"
    $utf8WithoutBom = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($temporaryChecksumPath, $checksumContents, $utf8WithoutBom)

    Move-Item -LiteralPath $temporarySetupPath -Destination $finalSetupPath -Force
    Move-Item -LiteralPath $temporaryChecksumPath -Destination $finalChecksumPath -Force

    $finalHash = (Get-FileHash -LiteralPath $finalSetupPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($finalHash -ne $setupHash) {
        throw 'The setup.exe SHA-256 changed while finalizing the output.'
    }
    Write-Output "Installer: $finalSetupPath"
    Write-Output "SHA256: $finalHash"
    Write-Output "Checksum: $finalChecksumPath"
}
finally {
    if (Test-Path -LiteralPath $temporarySetupPath -PathType Leaf) {
        Remove-Item -LiteralPath $temporarySetupPath -Force
    }
    if (Test-Path -LiteralPath $temporaryChecksumPath -PathType Leaf) {
        Remove-Item -LiteralPath $temporaryChecksumPath -Force
    }

    if ($ownsTemporarySource -and
        (Test-Path -LiteralPath $temporarySourceMarker -PathType Leaf) -and
        (Test-Path -LiteralPath $resolvedSourceDirectory -PathType Container)) {
        $sourceLeaf = Split-Path -Leaf $resolvedSourceDirectory
        $sourceParent = Get-TrimmedDirectoryPath (Split-Path -Parent $resolvedSourceDirectory)
        $artifactRootTrimmed = Get-TrimmedDirectoryPath $artifactRoot
        $markerSource = (Get-Content -LiteralPath $temporarySourceMarker -Raw).Trim()
        $sourceItem = Get-Item -LiteralPath $resolvedSourceDirectory -Force
        $markerItem = Get-Item -LiteralPath $temporarySourceMarker -Force
        $safeOwnedStage =
            [string]::Equals($sourceParent, $artifactRootTrimmed, [System.StringComparison]::OrdinalIgnoreCase) -and
            $sourceLeaf.StartsWith('.ncm-installer-publish-', [System.StringComparison]::OrdinalIgnoreCase) -and
            [string]::Equals($markerSource, $resolvedSourceDirectory, [System.StringComparison]::OrdinalIgnoreCase) -and
            (($sourceItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0) -and
            (($markerItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0)

        if ($safeOwnedStage) {
            Remove-Item -LiteralPath $resolvedSourceDirectory -Recurse -Force
            Remove-Item -LiteralPath $temporarySourceMarker -Force
        }
        else {
            Write-Warning "The temporary publish directory failed its ownership check and was left in place: $resolvedSourceDirectory"
        }
    }
}
