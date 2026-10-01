[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactRoot = Join-Path $repoRoot 'artifacts'
$releaseSourceRoot = Join-Path $artifactRoot 'release-sources'
$nativeSourceRoot = Join-Path $artifactRoot 'native-build\win-x64\sources'
$nativeMetadataPath = Join-Path $artifactRoot 'native\win-x64\build-metadata.json'
$archivePath = Join-Path $releaseSourceRoot 'ncm-sources.zip'
$checksumPath = "$archivePath.sha256"
Add-Type -AssemblyName System.IO.Compression

$nativeSources = @(
    [pscustomobject]@{
        Name = 'FFmpeg 9.0.2'
        Archive = 'ffmpeg-9.0.2.tar.xz'
        Url = 'https://ffmpeg.org/releases/ffmpeg-9.0.2.tar.xz'
        Sha256 = '8c3850283eb25fa026482078a04051e0be17347b09ef81a0849bec15a96e002e'
        License = 'LGPL-2.1-or-later'
    }
    [pscustomobject]@{
        Name = 'LAME 3.100'
        Archive = 'lame-3.100.tar.gz'
        Url = 'https://downloads.sourceforge.net/project/lame/lame/3.100/lame-3.100.tar.gz'
        Sha256 = 'ddfe36cab873794038ae2c1210557ad34857a4b6bdc515785d1da9e175b1da1e'
        License = 'LGPL-2.0-or-later'
    }
    [pscustomobject]@{
        Name = 'dav1d 1.5.3'
        Archive = 'dav1d-1.5.3.tar.xz'
        Url = 'https://downloads.videolan.org/pub/videolan/dav1d/1.5.3/dav1d-1.5.3.tar.xz'
        Sha256 = '732010aa5ef461fa93355ed2c6c5fedb48ddc4b74e697eaabe8907eaeb943011'
        License = 'BSD-2-Clause'
    }
)

$tagLibSource = [pscustomobject]@{
    Name = 'TagLibSharp 2.3.0'
    Archive = 'taglib-sharp-2.3.0.tar.gz'
    Url = 'https://codeload.github.com/mono/taglib-sharp/tar.gz/b5ae84f2e84087bf160bb0471420200dd2b5d809'
    Commit = 'b5ae84f2e84087bf160bb0471420200dd2b5d809'
    Tag = 'TaglibSharp-2.3.0.0'
    Sha256 = '2e54eb7382991caeafd2ac414ca5ab6ca2a4d2b5fe9bba4d8abff3fc1b308195'
    License = 'LGPL-2.1-only'
}

function Get-Sha256 {
    param([Parameter(Mandatory = $true)][string]$Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Assert-RegularFile {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required file was not found: $Path"
    }

    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing to read a reparse point: $Path"
    }
}

function Assert-SafeDirectory {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (Test-Path -LiteralPath $Path) {
        $item = Get-Item -LiteralPath $Path -Force
        if (-not $item.PSIsContainer) {
            throw "Expected a directory: $Path"
        }
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing to write through a reparse point: $Path"
        }
        return
    }

    [System.IO.Directory]::CreateDirectory($Path) | Out-Null
    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing to write through a reparse point: $Path"
    }
}

function Ensure-TagLibArchive {
    param([Parameter(Mandatory = $true)]$Spec)

    $path = Join-Path $releaseSourceRoot $Spec.Archive
    if (Test-Path -LiteralPath $path) {
        Assert-RegularFile -Path $path
        $actualHash = Get-Sha256 -Path $path
        if ($actualHash -ne $Spec.Sha256) {
            throw "SHA-256 mismatch for cached $($Spec.Archive): expected $($Spec.Sha256), received $actualHash"
        }
        return $path
    }

    $partialPath = Join-Path $releaseSourceRoot ('.' + $Spec.Archive + '.' + [guid]::NewGuid().ToString('N') + '.download')
    try {
        $requestParameters = @{
            Uri = $Spec.Url
            OutFile = $partialPath
            MaximumRedirection = 10
            ErrorAction = 'Stop'
        }
        if ((Get-Command Invoke-WebRequest).Parameters.ContainsKey('UseBasicParsing')) {
            $requestParameters.UseBasicParsing = $true
        }
        $null = Invoke-WebRequest @requestParameters

        $actualHash = Get-Sha256 -Path $partialPath
        if ($actualHash -ne $Spec.Sha256) {
            throw "SHA-256 mismatch for downloaded $($Spec.Archive): expected $($Spec.Sha256), received $actualHash"
        }
        if (Test-Path -LiteralPath $path) {
            throw "The TagLib source cache appeared while downloading; refusing to replace it: $path"
        }

        Move-Item -LiteralPath $partialPath -Destination $path | Out-Null
        return $path
    }
    finally {
        if (Test-Path -LiteralPath $partialPath) {
            $partialItem = Get-Item -LiteralPath $partialPath -Force
            if ($partialItem.PSIsContainer -or (($partialItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)) {
                throw "Refusing to remove an unexpected download path: $partialPath"
            }
            Remove-Item -LiteralPath $partialPath -Force
        }
    }
}

function Get-ApplicationSourceFiles {
    $allowedPrefixes = @('src/', 'tests/', 'tools/', 'installer/', 'licenses/', '.github/')
    $allowedRootFiles = @('Ncm.sln', 'Directory.Build.props', 'README', 'README.md', 'LICENSE', 'NOTICE', '.gitignore')
    $excludedDirectoryPattern = '(?i)(^|/)(?:\.git|\.private|private|cache|bin|obj|samples|credentials?)(?:/|$)'
    $sensitiveLeafPattern = '(?i)^(?:\.env(?:\..*)?|credentials?(?:\..*)?|secrets?(?:\..*)?|id_(?:rsa|ed25519)|.+\.(?:pfx|p12|pem|key|cer))$'
    $gitOutput = @(& git -c ("safe.directory={0}" -f $repoRoot) -C $repoRoot ls-files --cached --others --exclude-standard -z)
    if ($LASTEXITCODE -ne 0) {
        throw 'git ls-files failed while collecting the public source working tree.'
    }

    $joinedOutput = [string]::Join('', [string[]]$gitOutput)
    $paths = $joinedOutput.Split([char[]]@([char]0), [System.StringSplitOptions]::RemoveEmptyEntries)
    $files = [System.Collections.Generic.List[object]]::new()
    $excludedCount = 0
    $rootPrefix = $repoRoot.TrimEnd([char[]]@('\', '/')) + [System.IO.Path]::DirectorySeparatorChar

    foreach ($gitPath in $paths) {
        $relativePath = $gitPath.Replace('\', '/')
        if ([System.IO.Path]::IsPathRooted($relativePath) -or $relativePath -match '(^|/)\.\.?(/|$)') {
            throw "git returned a non-relative source path: $relativePath"
        }

        $isAllowed = $allowedRootFiles -contains $relativePath
        if (-not $isAllowed) {
            foreach ($prefix in $allowedPrefixes) {
                if ($relativePath.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
                    $isAllowed = $true
                    break
                }
            }
        }
        if (-not $isAllowed) {
            continue
        }

        $leafName = [System.IO.Path]::GetFileName($relativePath)
        if ($relativePath -match $excludedDirectoryPattern -or
            ($leafName -match $sensitiveLeafPattern -and $leafName -ne '.env.example')) {
            $excludedCount++
            continue
        }

        $fullPath = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $relativePath))
        if (-not $fullPath.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Source path escaped the repository root: $relativePath"
        }
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            continue
        }

        $relativeSegments = $relativePath.Split([char[]]@('/'))
        $probePath = $repoRoot
        foreach ($segment in $relativeSegments) {
            $probePath = Join-Path $probePath $segment
            $probeItem = Get-Item -LiteralPath $probePath -Force
            if (($probeItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing to archive a path through a reparse point: $relativePath"
            }
        }
        if ((Get-Item -LiteralPath $fullPath -Force).PSIsContainer) {
            continue
        }

        $files.Add([pscustomobject]@{
            FullPath = $fullPath
            RelativePath = $relativePath
            ArchivePath = "application/$relativePath"
        })
    }

    if ($excludedCount -gt 0) {
        Write-Verbose ("Filtered {0} sensitive or generated source paths." -f $excludedCount)
    }
    if ($files.Count -eq 0) {
        throw 'No public application source files were found for the source archive.'
    }

    return @($files | Sort-Object RelativePath -CaseSensitive)
}

function Assert-NoLocalPathsInMetadata {
    param([Parameter(Mandatory = $true)][string]$Text)

    $personalRoots = @($repoRoot, $env:USERPROFILE, $env:HOME) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    foreach ($personalRoot in $personalRoots) {
        if ($Text.IndexOf($personalRoot, [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $Text.IndexOf($personalRoot.Replace('\', '/'), [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            throw 'Native build metadata contains a local absolute path.'
        }
    }
    if ($Text -match '(?i)(?:[A-Z]:[/\\]Users[/\\][^/\\\r\n]+[/\\]|/[a-z]/Users/[^/\r\n]+/|/Users/[^/\r\n]+/|/home/[^/\r\n]+/)') {
        throw 'Native build metadata contains a local absolute path.'
    }
}

function Add-FileToArchive {
    param(
        [Parameter(Mandatory = $true)][System.IO.Compression.ZipArchive]$Archive,
        [Parameter(Mandatory = $true)][string]$SourcePath,
        [Parameter(Mandatory = $true)][string]$EntryPath,
        [Parameter(Mandatory = $true)][System.IO.Compression.CompressionLevel]$CompressionLevel
    )

    $entry = $Archive.CreateEntry($EntryPath, $CompressionLevel)
    $entry.LastWriteTime = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
    $sourceStream = [System.IO.File]::OpenRead($SourcePath)
    $entryStream = $entry.Open()
    try {
        $sourceStream.CopyTo($entryStream)
    }
    finally {
        $entryStream.Dispose()
        $sourceStream.Dispose()
    }
}

function Add-TextToArchive {
    param(
        [Parameter(Mandatory = $true)][System.IO.Compression.ZipArchive]$Archive,
        [Parameter(Mandatory = $true)][string]$EntryPath,
        [Parameter(Mandatory = $true)][string]$Text
    )

    $entry = $Archive.CreateEntry($EntryPath, [System.IO.Compression.CompressionLevel]::Optimal)
    $entry.LastWriteTime = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
    $entryStream = $entry.Open()
    $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($Text)
    try {
        $entryStream.Write($bytes, 0, $bytes.Length)
    }
    finally {
        $entryStream.Dispose()
    }
}

function Get-ZipEntrySha256 {
    param([Parameter(Mandatory = $true)][System.IO.Compression.ZipArchiveEntry]$Entry)

    $entryStream = $Entry.Open()
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha256.ComputeHash($entryStream)
        return ([System.BitConverter]::ToString($hash)).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
        $entryStream.Dispose()
    }
}

Assert-SafeDirectory -Path $artifactRoot
Assert-SafeDirectory -Path $releaseSourceRoot

$nativeArchiveSpecs = [System.Collections.Generic.List[object]]::new()
foreach ($spec in $nativeSources) {
    $path = Join-Path $nativeSourceRoot $spec.Archive
    Assert-RegularFile -Path $path
    $actualHash = Get-Sha256 -Path $path
    if ($actualHash -ne $spec.Sha256) {
        throw "SHA-256 mismatch for cached $($spec.Archive): expected $($spec.Sha256), received $actualHash"
    }
    $nativeArchiveSpecs.Add([pscustomobject]@{
        Spec = $spec
        FullPath = $path
        ArchivePath = "third-party/$($spec.Archive)"
    })
}

$tagLibArchivePath = Ensure-TagLibArchive -Spec $tagLibSource
$tagLibArchive = [pscustomobject]@{
    Spec = $tagLibSource
    FullPath = $tagLibArchivePath
    ArchivePath = "third-party/$($tagLibSource.Archive)"
}

Assert-RegularFile -Path $nativeMetadataPath
$nativeMetadataText = [System.IO.File]::ReadAllText($nativeMetadataPath)
Assert-NoLocalPathsInMetadata -Text $nativeMetadataText
$null = $nativeMetadataText | ConvertFrom-Json -ErrorAction Stop

$applicationFiles = @(Get-ApplicationSourceFiles)
$applicationManifest = @(
    foreach ($file in $applicationFiles) {
        $sourceItem = Get-Item -LiteralPath $file.FullPath
        $sourceHash = Get-Sha256 -Path $file.FullPath
        $file | Add-Member -NotePropertyName Size -NotePropertyValue $sourceItem.Length -Force
        $file | Add-Member -NotePropertyName Sha256 -NotePropertyValue $sourceHash -Force
        [ordered]@{
            path = $file.RelativePath
            size = $file.Size
            sha256 = $file.Sha256
        }
    }
)

$gitRevisionLines = @(& git -c ("safe.directory={0}" -f $repoRoot) -C $repoRoot rev-parse HEAD)
if ($LASTEXITCODE -ne 0 -or $gitRevisionLines.Count -eq 0) {
    throw 'git rev-parse failed while recording the source revision.'
}
$gitRevision = ([string]$gitRevisionLines[0]).Trim()
if ($gitRevision -notmatch '^[0-9a-fA-F]{40,64}$') {
    throw 'git rev-parse returned an invalid source revision.'
}

$metadata = [ordered]@{
    schemaVersion = 1
    project = 'NcmConverter'
    target = 'win-x64'
    generatedUtc = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ', [Globalization.CultureInfo]::InvariantCulture)
    sourceRevision = $gitRevision.ToLowerInvariant()
    sourceSnapshot = 'public working tree, including staged and unstaged changes'
    applicationFileCount = $applicationManifest.Count
    applicationFiles = $applicationManifest
    thirdPartySources = @(
        foreach ($item in $nativeArchiveSpecs) {
            [ordered]@{
                name = $item.Spec.Name
                archive = $item.ArchivePath
                url = $item.Spec.Url
                sha256 = $item.Spec.Sha256
                license = $item.Spec.License
            }
        }
        [ordered]@{
            name = $tagLibSource.Name
            archive = $tagLibArchive.ArchivePath
            url = $tagLibSource.Url
            commit = $tagLibSource.Commit
            tag = $tagLibSource.Tag
            sha256 = $tagLibSource.Sha256
            license = $tagLibSource.License
        }
    )
    nativeBuild = [ordered]@{
        metadata = 'build/native-build-metadata.json'
        metadataSha256 = Get-Sha256 -Path $nativeMetadataPath
        outputDirectory = 'artifacts/native/win-x64'
        sharedLibraries = @('avcodec-63.dll', 'avformat-63.dll', 'avutil-61.dll', 'swresample-7.dll', 'swscale-10.dll')
    }
}
$metadataJson = ($metadata | ConvertTo-Json -Depth 8) + [Environment]::NewLine

if (Test-Path -LiteralPath $archivePath) {
    $archiveItem = Get-Item -LiteralPath $archivePath -Force
    if ($archiveItem.PSIsContainer -or (($archiveItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)) {
        throw "Refusing to replace a non-regular source archive: $archivePath"
    }
}
if (Test-Path -LiteralPath $checksumPath) {
    $checksumItem = Get-Item -LiteralPath $checksumPath -Force
    if ($checksumItem.PSIsContainer -or (($checksumItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)) {
        throw "Refusing to replace a non-regular checksum file: $checksumPath"
    }
}

$temporaryArchivePath = "$archivePath.$([guid]::NewGuid().ToString('N')).tmp"
$temporaryChecksumPath = "$checksumPath.$([guid]::NewGuid().ToString('N')).tmp"
$ownedTemporaryPaths = [System.Collections.Generic.List[string]]::new()

try {
    $fileStream = [System.IO.File]::Open($temporaryArchivePath, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
    $ownedTemporaryPaths.Add($temporaryArchivePath)
    $zipArchive = [System.IO.Compression.ZipArchive]::new($fileStream, [System.IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        foreach ($file in $applicationFiles) {
            Add-FileToArchive -Archive $zipArchive -SourcePath $file.FullPath -EntryPath $file.ArchivePath -CompressionLevel ([System.IO.Compression.CompressionLevel]::Optimal)
        }
        foreach ($item in $nativeArchiveSpecs) {
            Add-FileToArchive -Archive $zipArchive -SourcePath $item.FullPath -EntryPath $item.ArchivePath -CompressionLevel ([System.IO.Compression.CompressionLevel]::NoCompression)
        }
        Add-FileToArchive -Archive $zipArchive -SourcePath $tagLibArchive.FullPath -EntryPath $tagLibArchive.ArchivePath -CompressionLevel ([System.IO.Compression.CompressionLevel]::NoCompression)
        Add-TextToArchive -Archive $zipArchive -EntryPath 'build/native-build-metadata.json' -Text ($nativeMetadataText.TrimEnd() + [Environment]::NewLine)
        Add-TextToArchive -Archive $zipArchive -EntryPath 'build/build-metadata.json' -Text $metadataJson
    }
    finally {
        $zipArchive.Dispose()
        $fileStream.Dispose()
    }

    $expectedThirdPartyEntries = @($nativeArchiveSpecs) + @($tagLibArchive)
    $verifyStream = [System.IO.File]::OpenRead($temporaryArchivePath)
    $verifyArchive = [System.IO.Compression.ZipArchive]::new($verifyStream, [System.IO.Compression.ZipArchiveMode]::Read, $true)
    try {
        $entryNames = @($verifyArchive.Entries | ForEach-Object FullName)
        if (($entryNames | Select-Object -Unique).Count -ne $entryNames.Count) {
            throw 'The source archive contains duplicate ZIP entry names.'
        }
        $expectedThirdPartyEntries = @($nativeArchiveSpecs) + @($tagLibArchive)
        $expectedEntryNames = @(
            @($applicationFiles | ForEach-Object ArchivePath) +
            @($expectedThirdPartyEntries | ForEach-Object ArchivePath) +
            @('build/native-build-metadata.json', 'build/build-metadata.json')
        ) | Sort-Object -CaseSensitive
        $actualEntryNames = @($entryNames | Sort-Object -CaseSensitive)
        if ($actualEntryNames.Count -ne $expectedEntryNames.Count -or
            (Compare-Object -ReferenceObject $expectedEntryNames -DifferenceObject $actualEntryNames)) {
            throw 'The source archive contains missing or unexpected ZIP entries.'
        }
        foreach ($file in $applicationFiles) {
            $entry = $verifyArchive.GetEntry($file.ArchivePath)
            if ($null -eq $entry) {
                throw "The source archive is missing an application file: $($file.RelativePath)"
            }
            $entryHash = Get-ZipEntrySha256 -Entry $entry
            if ($entryHash -ne $file.Sha256) {
                throw "SHA-256 mismatch inside the source archive for $($file.RelativePath)"
            }
        }
        foreach ($item in $expectedThirdPartyEntries) {
            $entry = $verifyArchive.GetEntry($item.ArchivePath)
            if ($null -eq $entry) {
                throw "The source archive is missing a third-party source archive: $($item.Spec.Archive)"
            }
            $entryHash = Get-ZipEntrySha256 -Entry $entry
            if ($entryHash -ne $item.Spec.Sha256) {
                throw "SHA-256 mismatch inside the source archive for $($item.Spec.Archive): expected $($item.Spec.Sha256), received $entryHash"
            }
        }
        $nativeMetadataEntry = $verifyArchive.GetEntry('build/native-build-metadata.json')
        if ((Get-ZipEntrySha256 -Entry $nativeMetadataEntry) -ne (Get-Sha256 -Path $nativeMetadataPath)) {
            throw 'Native build metadata changed while creating the source archive.'
        }
    }
    finally {
        $verifyArchive.Dispose()
        $verifyStream.Dispose()
    }

    $archiveHash = Get-Sha256 -Path $temporaryArchivePath
    $checksumText = "$archiveHash *ncm-sources.zip$([Environment]::NewLine)"
    $checksumStream = [System.IO.File]::Open($temporaryChecksumPath, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
    $ownedTemporaryPaths.Add($temporaryChecksumPath)
    try {
        $checksumBytes = [System.Text.UTF8Encoding]::new($false).GetBytes($checksumText)
        $checksumStream.Write($checksumBytes, 0, $checksumBytes.Length)
    }
    finally {
        $checksumStream.Dispose()
    }

    Move-Item -LiteralPath $temporaryArchivePath -Destination $archivePath -Force
    Move-Item -LiteralPath $temporaryChecksumPath -Destination $checksumPath -Force
    $finalHash = Get-Sha256 -Path $archivePath
    $finalChecksumText = [System.IO.File]::ReadAllText($checksumPath)
    if ($finalHash -ne $archiveHash -or $finalChecksumText -ne $checksumText) {
        throw 'The source archive SHA-256 changed while finalizing the release assets.'
    }

    Write-Output "Source archive: $archivePath"
    Write-Output "SHA256: $finalHash"
    Write-Output "Checksum: $checksumPath"
}
finally {
    foreach ($temporaryPath in $ownedTemporaryPaths) {
        if (Test-Path -LiteralPath $temporaryPath) {
            $temporaryItem = Get-Item -LiteralPath $temporaryPath -Force
            if ($temporaryItem.PSIsContainer -or (($temporaryItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)) {
                throw "Refusing to remove an unexpected temporary file: $temporaryPath"
            }
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }
}
