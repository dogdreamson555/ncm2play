[CmdletBinding()]
param(
    [string]$Msys2Root,
    [ValidateRange(1, 256)]
    [int]$Jobs = [Math]::Min(64, [Environment]::ProcessorCount),
    [string[]]$ExtraToolPath = @(),
    [string]$PythonPath
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactRoot = Join-Path $repoRoot 'artifacts'
$workRoot = Join-Path $artifactRoot 'native-build\win-x64'
$cacheRoot = Join-Path $workRoot 'sources'
$sourceRoot = Join-Path $workRoot 'source'
$outputRoot = Join-Path $artifactRoot 'native\win-x64'
if ($repoRoot -match '\s') {
    throw 'Build native media in a checkout whose absolute path contains no whitespace; the FFmpeg makefiles require this.'
}

function Assert-NoReparsePathAncestors {
    param([Parameter(Mandatory)][string]$Path)

    $currentPath = [System.IO.Path]::GetFullPath($Path)
    while (-not [string]::IsNullOrWhiteSpace($currentPath)) {
        if (Test-Path -LiteralPath $currentPath) {
            $item = Get-Item -LiteralPath $currentPath -Force
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing a build path containing a reparse point: $currentPath"
            }
        }
        $parent = [System.IO.Directory]::GetParent($currentPath)
        if ($null -eq $parent) { break }
        $currentPath = $parent.FullName
    }
}

foreach ($directory in @($repoRoot, $artifactRoot, $workRoot, $cacheRoot, $sourceRoot, $outputRoot)) {
    Assert-NoReparsePathAncestors -Path $directory
}

$sourceSpecs = @(
    [pscustomobject]@{
        Name = 'ffmpeg-9.0.2'
        Archive = 'ffmpeg-9.0.2.tar.xz'
        Url = 'https://ffmpeg.org/releases/ffmpeg-9.0.2.tar.xz'
        Sha256 = '8c3850283eb25fa026482078a04051e0be17347b09ef81a0849bec15a96e002e'
    }
    [pscustomobject]@{
        Name = 'lame-3.100'
        Archive = 'lame-3.100.tar.gz'
        Url = 'https://downloads.sourceforge.net/project/lame/lame/3.100/lame-3.100.tar.gz'
        Sha256 = 'ddfe36cab873794038ae2c1210557ad34857a4b6bdc515785d1da9e175b1da1e'
    }
    [pscustomobject]@{
        Name = 'dav1d-1.5.3'
        Archive = 'dav1d-1.5.3.tar.xz'
        Url = 'https://downloads.videolan.org/pub/videolan/dav1d/1.5.3/dav1d-1.5.3.tar.xz'
        Sha256 = '732010aa5ef461fa93355ed2c6c5fedb48ddc4b74e697eaabe8907eaeb943011'
    }
)

function Resolve-Msys2Root {
    $candidates = [System.Collections.Generic.List[string]]::new()
    if (-not [string]::IsNullOrWhiteSpace($Msys2Root)) {
        $candidates.Add($Msys2Root)
    }
    if (-not [string]::IsNullOrWhiteSpace($env:MSYS2_ROOT)) {
        $candidates.Add($env:MSYS2_ROOT)
    }
    $candidates.Add('C:\msys64')
    if (-not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
        $candidates.Add((Join-Path $env:LOCALAPPDATA 'msys64'))
    }
    $gcc = Get-Command gcc.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $gcc) {
        $gccBin = Split-Path -Parent $gcc.Source
        if ((Split-Path -Leaf $gccBin) -eq 'bin' -and (Split-Path -Leaf (Split-Path -Parent $gccBin)) -eq 'ucrt64') {
            $candidates.Add((Split-Path -Parent (Split-Path -Parent $gccBin)))
        }
    }

    foreach ($candidate in $candidates | Select-Object -Unique) {
        if ([string]::IsNullOrWhiteSpace($candidate)) {
            continue
        }
        $fullPath = [System.IO.Path]::GetFullPath($candidate)
        if ((Test-Path -LiteralPath (Join-Path $fullPath 'usr\bin\bash.exe')) -and
            (Test-Path -LiteralPath (Join-Path $fullPath 'ucrt64\bin\gcc.exe'))) {
            return $fullPath
        }
    }

    throw 'MSYS2 UCRT64 was not found. Pass -Msys2Root with its install directory; this script does not install or upgrade MSYS2.'
}

function Remove-ContainedDirectory {
    param(
        [Parameter(Mandatory)][string]$Target,
        [Parameter(Mandatory)][string]$Parent
    )

    $parentPath = [System.IO.Path]::GetFullPath($Parent).TrimEnd('\') + '\'
    $targetPath = [System.IO.Path]::GetFullPath($Target)
    Assert-NoReparsePathAncestors -Path $parentPath
    Assert-NoReparsePathAncestors -Path $targetPath
    if (-not $targetPath.StartsWith($parentPath, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove a path outside the build area: $targetPath"
    }
    if (Test-Path -LiteralPath $targetPath) {
        Remove-Item -LiteralPath $targetPath -Recurse -Force
    }
}

function Get-Sha256 {
    param([Parameter(Mandatory)][string]$Path)
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Ensure-SourceArchive {
    param([Parameter(Mandatory)]$Spec)

    $archivePath = Join-Path $cacheRoot $Spec.Archive
    if (Test-Path -LiteralPath $archivePath) {
        if ((Get-Sha256 $archivePath) -eq $Spec.Sha256) {
            return $archivePath
        }
        Remove-Item -LiteralPath $archivePath -Force
    }

    $partialPath = "$archivePath.$([guid]::NewGuid().ToString('N')).download"
    try {
        Write-Output "Downloading $($Spec.Archive)" | Out-Host
        & curl.exe --fail --location --retry 2 --connect-timeout 20 --max-time 180 --silent --show-error --output $partialPath $Spec.Url
        if ($LASTEXITCODE -ne 0) {
            throw "Download failed for $($Spec.Archive) with exit code $LASTEXITCODE"
        }
        $actualHash = Get-Sha256 $partialPath
        if ($actualHash -ne $Spec.Sha256) {
            throw "SHA-256 mismatch for $($Spec.Archive): expected $($Spec.Sha256), received $actualHash"
        }
        Move-Item -LiteralPath $partialPath -Destination $archivePath -Force
        return $archivePath
    }
    finally {
        if (Test-Path -LiteralPath $partialPath) {
            Remove-Item -LiteralPath $partialPath -Force
        }
    }
}

function Expand-VerifiedSource {
    param(
        [Parameter(Mandatory)]$Spec,
        [Parameter(Mandatory)][string]$ArchivePath,
        [Parameter(Mandatory)][string]$TarPath
    )

    $temporaryRoot = Join-Path $sourceRoot ".$($Spec.Name).extract-$([guid]::NewGuid().ToString('N'))"
    $destination = Join-Path $sourceRoot $Spec.Name
    New-Item -ItemType Directory -Force -Path $temporaryRoot | Out-Null
    $extractionPath = $env:PATH
    try {
        $env:PATH = (Split-Path -Parent $TarPath) + [System.IO.Path]::PathSeparator + $extractionPath
        $cygpath = Join-Path (Split-Path -Parent $TarPath) 'cygpath.exe'
        $msysArchive = & $cygpath -u -- $ArchivePath
        if ($LASTEXITCODE -ne 0) { throw 'Could not convert the source archive path for MSYS2.' }
        $msysDestination = & $cygpath -u -- $temporaryRoot
        if ($LASTEXITCODE -ne 0) { throw 'Could not convert the extraction path for MSYS2.' }
        & $TarPath -xf $msysArchive -C $msysDestination
        if ($LASTEXITCODE -ne 0) {
            throw "tar failed to extract $($Spec.Archive) with exit code $LASTEXITCODE"
        }
        $extractedRoot = Join-Path $temporaryRoot $Spec.Name
        if (-not (Test-Path -LiteralPath $extractedRoot -PathType Container)) {
            throw "Unexpected top-level directory in $($Spec.Archive)"
        }
        Remove-ContainedDirectory -Target $destination -Parent $sourceRoot
        Move-Item -LiteralPath $extractedRoot -Destination $destination
    }
    finally {
        $env:PATH = $extractionPath
        Remove-ContainedDirectory -Target $temporaryRoot -Parent $sourceRoot
    }
}

$msysRootResolved = Resolve-Msys2Root
$bashPath = Join-Path $msysRootResolved 'usr\bin\bash.exe'
$tarPath = Join-Path $msysRootResolved 'usr\bin\tar.exe'
if (-not (Test-Path -LiteralPath $tarPath)) {
    throw "MSYS2 tar was not found at $tarPath"
}

New-Item -ItemType Directory -Force -Path $cacheRoot, $sourceRoot | Out-Null
foreach ($spec in $sourceSpecs) {
    $archivePath = Ensure-SourceArchive $spec
    Expand-VerifiedSource -Spec $spec -ArchivePath $archivePath -TarPath $tarPath
}

$extraPaths = [System.Collections.Generic.List[string]]::new()
foreach ($path in $ExtraToolPath) {
    if ([string]::IsNullOrWhiteSpace($path)) {
        continue
    }
    if (-not (Test-Path -LiteralPath $path -PathType Container)) {
        throw "Extra tool path does not exist: $path"
    }
    $extraPaths.Add((Resolve-Path -LiteralPath $path).Path)
}

$oldPath = $env:PATH
$oldMsystem = $env:MSYSTEM
$oldMsys2PathType = $env:MSYS2_PATH_TYPE
$oldChereInvoking = $env:CHERE_INVOKING
$oldPythonPath = $env:PYTHONPATH
$oldExtraToolPath = $env:NATIVE_EXTRA_TOOL_PATH
try {
    if ($extraPaths.Count -gt 0) {
        $env:PATH = (($extraPaths.ToArray() -join [System.IO.Path]::PathSeparator) + [System.IO.Path]::PathSeparator + $oldPath)
        $env:NATIVE_EXTRA_TOOL_PATH = $extraPaths.ToArray() -join [System.IO.Path]::PathSeparator
    }
    if (-not [string]::IsNullOrWhiteSpace($PythonPath)) {
        if ([string]::IsNullOrWhiteSpace($oldPythonPath)) {
            $env:PYTHONPATH = $PythonPath
        }
        elseif (($oldPythonPath -split [System.IO.Path]::PathSeparator) -notcontains $PythonPath) {
            $env:PYTHONPATH = $PythonPath + [System.IO.Path]::PathSeparator + $oldPythonPath
        }
    }
    $env:MSYSTEM = 'UCRT64'
    $env:MSYS2_PATH_TYPE = 'inherit'
    $env:CHERE_INVOKING = '1'

    Push-Location $repoRoot
    try {
        & $bashPath --login -c "bash ./tools/native/Build-Ffmpeg.sh $Jobs"
        if ($LASTEXITCODE -ne 0) {
            throw "Native FFmpeg build failed with exit code $LASTEXITCODE"
        }
    }
    finally {
        Pop-Location
    }
}
finally {
    $env:PATH = $oldPath
    if ($null -eq $oldMsystem) { Remove-Item Env:MSYSTEM -ErrorAction SilentlyContinue } else { $env:MSYSTEM = $oldMsystem }
    if ($null -eq $oldMsys2PathType) { Remove-Item Env:MSYS2_PATH_TYPE -ErrorAction SilentlyContinue } else { $env:MSYS2_PATH_TYPE = $oldMsys2PathType }
    if ($null -eq $oldChereInvoking) { Remove-Item Env:CHERE_INVOKING -ErrorAction SilentlyContinue } else { $env:CHERE_INVOKING = $oldChereInvoking }
    if ($null -eq $oldPythonPath) { Remove-Item Env:PYTHONPATH -ErrorAction SilentlyContinue } else { $env:PYTHONPATH = $oldPythonPath }
    if ($null -eq $oldExtraToolPath) { Remove-Item Env:NATIVE_EXTRA_TOOL_PATH -ErrorAction SilentlyContinue } else { $env:NATIVE_EXTRA_TOOL_PATH = $oldExtraToolPath }
}

$expectedDlls = @(
    'avcodec-63.dll',
    'avformat-63.dll',
    'avutil-61.dll',
    'swresample-7.dll',
    'swscale-10.dll'
)
foreach ($dll in $expectedDlls) {
    $dllPath = Join-Path $outputRoot $dll
    if (-not (Test-Path -LiteralPath $dllPath -PathType Leaf)) {
        throw "Expected FFmpeg ABI library is missing: $dll"
    }
    $versionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($dllPath)
    $expectedMajor = [int]([regex]::Match($dll, '-(\d+)\.dll$').Groups[1].Value)
    if ($versionInfo.ProductName -ne 'FFmpeg' -or
        $versionInfo.CompanyName -ne 'FFmpeg Project' -or
        $versionInfo.ProductVersion -ne '9.0.2' -or
        $versionInfo.FileMajorPart -ne $expectedMajor) {
        throw "FFmpeg version resource does not match the built library: $dll"
    }
}
$actualDlls = @(Get-ChildItem -LiteralPath $outputRoot -Filter '*.dll' -File | ForEach-Object Name | Sort-Object)
if (($actualDlls.Count -ne $expectedDlls.Count) -or
    (Compare-Object -ReferenceObject @($expectedDlls | Sort-Object) -DifferenceObject $actualDlls)) {
    throw ('Unexpected DLL set in {0}: {1}' -f $outputRoot, ($actualDlls -join ', '))
}

$buildRoot = Join-Path $workRoot 'build'
$configArgumentsPath = Join-Path $buildRoot 'ffmpeg-configure-args.txt'
$toolchainVersionsPath = Join-Path $buildRoot 'toolchain-versions.txt'
$runtimeAuditPath = Join-Path $buildRoot 'external-runtime-dlls.txt'
foreach ($path in @($configArgumentsPath, $toolchainVersionsPath, $runtimeAuditPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Build metadata input is missing: $path"
    }
}

$configureArguments = @(Get-Content -LiteralPath $configArgumentsPath)
$rootSlash = $repoRoot.Replace('\', '/')
foreach ($argument in $configureArguments) {
    if ($argument.IndexOf($repoRoot, [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $argument.IndexOf($rootSlash, [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $argument.Contains('/private/artifacts')) {
        throw 'FFmpeg configure arguments contain a private absolute build path'
    }
}

$toolchain = @{}
foreach ($line in Get-Content -LiteralPath $toolchainVersionsPath) {
    $separator = $line.IndexOf('=')
    if ($separator -gt 0) {
        $toolchain[$line.Substring(0, $separator)] = $line.Substring($separator + 1)
    }
}
$externalRuntimeImports = @(
    Get-Content -LiteralPath $runtimeAuditPath |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
)
if ($externalRuntimeImports.Count -gt 0) {
    throw "Unexpected unbundled runtime DLL imports: $($externalRuntimeImports -join ', ')"
}

$metadata = [ordered]@{
    schemaVersion = 1
    target = 'win-x64'
    sources = @(
        foreach ($spec in $sourceSpecs) {
            [ordered]@{
                name = $spec.Name
                url = $spec.Url
                sha256 = $spec.Sha256
            }
        }
    )
    licenses = [ordered]@{
        ffmpeg = 'LGPL-2.1-or-later'
        lame = 'LGPL-2.0-or-later'
        dav1d = 'BSD-2-Clause'
    }
    staticDependencies = @('LAME 3.100', 'dav1d 1.5.3')
    lameConfigureArguments = @('--host=x86_64-w64-mingw32', '--prefix=/usr', '--disable-shared', '--enable-static', '--disable-frontend', '--disable-decoder')
    dav1dMesonArguments = @('--prefix=C:/usr', '--libdir=lib', '--buildtype=minsize', '--default-library=static', '-Denable_asm=true', '-Denable_tools=false', '-Denable_tests=false', '-Denable_examples=false')
    toolchain = $toolchain
    compileFlags = @(
        '-Os',
        '-g0',
        '-ffunction-sections',
        '-fdata-sections',
        '-ffile-prefix-map=<repository-root>=.',
        '-fdebug-prefix-map=<repository-root>=.'
    )
    linkFlags = @('-static-libgcc', '-Wl,--gc-sections')
    ffmpegConfigureArguments = $configureArguments
    ffmpegMakeArguments = @('HAVE_GNU_WINDRES=yes')
    sharedLibraries = $expectedDlls
    runtimeDllAudit = [ordered]@{
        passed = ($externalRuntimeImports.Count -eq 0)
        unbundledNonSystemImports = $externalRuntimeImports
    }
}
$metadataPath = Join-Path $outputRoot 'build-metadata.json'
$json = $metadata | ConvertTo-Json -Depth 8
[System.IO.File]::WriteAllText($metadataPath, $json + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
Write-Output "Native FFmpeg libraries and build metadata written to $outputRoot"
