#requires -Version 7.4

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
$archivePath = Join-Path $releaseSourceRoot 'third-party-sources.zip'
$checksumPath = "$archivePath.sha256"
$fixedZipTime = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.Formats.Tar

$nativeSources = @(
    [pscustomobject]@{
        Name = 'FFmpeg'
        Version = '9.0.2'
        Archive = 'ffmpeg-9.0.2.tar.xz'
        Url = 'https://ffmpeg.org/releases/ffmpeg-9.0.2.tar.xz'
        Sha256 = '8c3850283eb25fa026482078a04051e0be17347b09ef81a0849bec15a96e002e'
        License = 'LGPL-2.1-or-later'
    }
    [pscustomobject]@{
        Name = 'LAME'
        Version = '3.100'
        Archive = 'lame-3.100.tar.gz'
        Url = 'https://downloads.sourceforge.net/project/lame/lame/3.100/lame-3.100.tar.gz'
        Sha256 = 'ddfe36cab873794038ae2c1210557ad34857a4b6bdc515785d1da9e175b1da1e'
        License = 'LGPL-2.0-or-later'
    }
    [pscustomobject]@{
        Name = 'dav1d'
        Version = '1.5.3'
        Archive = 'dav1d-1.5.3.tar.xz'
        Url = 'https://downloads.videolan.org/pub/videolan/dav1d/1.5.3/dav1d-1.5.3.tar.xz'
        Sha256 = '732010aa5ef461fa93355ed2c6c5fedb48ddc4b74e697eaabe8907eaeb943011'
        License = 'BSD-2-Clause'
    }
)

$tagLibSource = [pscustomobject]@{
    Name = 'TagLibSharp'
    Version = '2.3.0'
    Archive = 'taglib-sharp-2.3.0.tar.gz'
    SlimArchive = 'taglib-sharp-2.3.0-source.zip'
    Url = 'https://codeload.github.com/mono/taglib-sharp/tar.gz/b5ae84f2e84087bf160bb0471420200dd2b5d809'
    Commit = 'b5ae84f2e84087bf160bb0471420200dd2b5d809'
    Sha256 = '2e54eb7382991caeafd2ac414ca5ab6ca2a4d2b5fe9bba4d8abff3fc1b308195'
    License = 'LGPL-2.1-only'
}

$tagLibExcludedPaths = @('examples/', 'src/Debug/', 'src/TaglibSharp.Tests/', 'tests/')

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
        if (-not $item.PSIsContainer -or (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)) {
            throw "Expected a regular directory: $Path"
        }
        return
    }

    [System.IO.Directory]::CreateDirectory($Path) | Out-Null
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
    $entry.LastWriteTime = $fixedZipTime
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
    $entry.LastWriteTime = $fixedZipTime
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
    try {
        return [System.Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($entryStream)).ToLowerInvariant()
    }
    finally {
        $entryStream.Dispose()
    }
}

function New-TagLibSourceArchive {
    param(
        [Parameter(Mandatory = $true)][string]$SourcePath,
        [Parameter(Mandatory = $true)][string]$DestinationPath
    )

    $sourceStream = $null
    $gzipStream = $null
    $tarReader = $null
    $destinationStream = $null
    $zipArchive = $null
    $rootName = $null
    $excludedFound = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $includedNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $completed = $false

    try {
        $sourceStream = [System.IO.File]::OpenRead($SourcePath)
        $gzipStream = [System.IO.Compression.GZipStream]::new($sourceStream, [System.IO.Compression.CompressionMode]::Decompress, $true)
        $tarReader = [System.Formats.Tar.TarReader]::new($gzipStream, $true)
        $destinationStream = [System.IO.File]::Open($DestinationPath, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
        $zipArchive = [System.IO.Compression.ZipArchive]::new($destinationStream, [System.IO.Compression.ZipArchiveMode]::Create, $true)

        while ($null -ne ($entry = $tarReader.GetNextEntry($false))) {
            $entryType = [string]$entry.EntryType
            if (@('GlobalExtendedAttributes', 'ExtendedAttributes', 'LongLink', 'LongPath') -contains $entryType) {
                continue
            }

            $entryName = [string]$entry.Name
            if ($entryName.StartsWith('/', [System.StringComparison]::Ordinal) -or
                $entryName.StartsWith('\', [System.StringComparison]::Ordinal) -or
                $entryName -match '^[A-Za-z]:') {
                throw "Unsafe path in the verified TagLib source archive: $entryName"
            }
            $entryPath = $entryName.Replace('\', '/').TrimEnd('/')
            if ([string]::IsNullOrWhiteSpace($entryPath) -or $entryPath -match '(^|/)\.\.?(/|$)') {
                throw "Unsafe path in the verified TagLib source archive: $($entry.Name)"
            }

            $segments = $entryPath.Split([char[]]@('/'), [System.StringSplitOptions]::RemoveEmptyEntries)
            if ($null -eq $rootName) {
                if ($segments.Count -ne 1 -or $entryType -ne 'Directory') {
                    throw 'The TagLib source archive does not start with a single top-level directory.'
                }
                $rootName = $segments[0]
            }
            elseif ($segments[0] -ne $rootName) {
                throw "Unexpected top-level path in the TagLib source archive: $entryPath"
            }

            $relativePath = if ($entryPath -eq $rootName) { '' } else { $entryPath.Substring($rootName.Length + 1) }
            $excludedPrefix = $null
            foreach ($prefix in $tagLibExcludedPaths) {
                $directoryPath = $prefix.TrimEnd('/')
                if ($relativePath -eq $directoryPath -or $relativePath.StartsWith($prefix, [System.StringComparison]::Ordinal)) {
                    $excludedPrefix = $prefix
                    break
                }
            }
            if ($null -ne $excludedPrefix) {
                $excludedFound.Add($excludedPrefix) | Out-Null
                if ($null -ne $entry.DataStream) {
                    $entry.DataStream.CopyTo([System.IO.Stream]::Null)
                }
                continue
            }

            $zipEntryPath = $entryPath
            if ($entryType -eq 'Directory') {
                $zipEntryPath += '/'
            }
            elseif (@('RegularFile', 'V7RegularFile', 'ContiguousFile') -notcontains $entryType) {
                throw "Unsupported entry type in the TagLib source archive: $entryType ($entryPath)"
            }
            if (-not $includedNames.Add($zipEntryPath)) {
                throw "Duplicate path in the TagLib source archive: $zipEntryPath"
            }

            $zipEntry = $zipArchive.CreateEntry($zipEntryPath, [System.IO.Compression.CompressionLevel]::Optimal)
            $zipEntry.LastWriteTime = $fixedZipTime
            if ($entryType -ne 'Directory' -and $null -ne $entry.DataStream) {
                $entryStream = $zipEntry.Open()
                try {
                    $entry.DataStream.CopyTo($entryStream)
                }
                finally {
                    $entryStream.Dispose()
                }
            }
        }
        $zipArchive.Dispose()
        $zipArchive = $null
        $destinationStream.Dispose()
        $destinationStream = $null
        $tarReader.Dispose()
        $tarReader = $null
        $gzipStream.Dispose()
        $gzipStream = $null
        $sourceStream.Dispose()
        $sourceStream = $null

        if ([string]::IsNullOrWhiteSpace($rootName)) {
            throw 'The TagLib source archive was empty.'
        }

        foreach ($requiredPath in @('COPYING', 'AUTHORS', 'Directory.Build.props', 'Directory.Build.targets', 'taglib-sharp.snk', 'src/TaglibSharp/TaglibSharp.csproj')) {
            if (-not $includedNames.Contains("$rootName/$requiredPath")) {
                throw "The reduced TagLib source archive is missing required build or license material: $requiredPath"
            }
        }

        foreach ($excludedPrefix in $tagLibExcludedPaths) {
            $hasExcludedEntry = @($includedNames | Where-Object { $_.StartsWith("$rootName/$excludedPrefix", [System.StringComparison]::Ordinal) }).Count -gt 0
            if ($hasExcludedEntry) {
                throw "The reduced TagLib source archive still contains excluded content: $excludedPrefix"
            }
        }

        $archiveHash = Get-Sha256 -Path $DestinationPath
        $completed = $true
        return [pscustomobject]@{
            ExcludedPaths = @($excludedFound | Sort-Object -CaseSensitive)
            Sha256 = $archiveHash
        }
    }
    finally {
        if ($null -ne $zipArchive) { $zipArchive.Dispose() }
        if ($null -ne $destinationStream) { $destinationStream.Dispose() }
        if ($null -ne $tarReader) { $tarReader.Dispose() }
        if ($null -ne $gzipStream) { $gzipStream.Dispose() }
        if ($null -ne $sourceStream) { $sourceStream.Dispose() }
        if (-not $completed -and (Test-Path -LiteralPath $DestinationPath)) {
            $destinationItem = Get-Item -LiteralPath $DestinationPath -Force
            if ($destinationItem.PSIsContainer -or (($destinationItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)) {
                throw "Refusing to remove an unexpected TagLib temporary archive path: $DestinationPath"
            }
            Remove-Item -LiteralPath $DestinationPath -Force
        }
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
$tagLibTemporaryPath = Join-Path $releaseSourceRoot ('.' + $tagLibSource.SlimArchive + '.' + [guid]::NewGuid().ToString('N') + '.tmp')
$tagLibArchiveInfo = New-TagLibSourceArchive -SourcePath $tagLibArchivePath -DestinationPath $tagLibTemporaryPath
$tagLibArchivePathInZip = "third-party/$($tagLibSource.SlimArchive)"

Assert-RegularFile -Path $nativeMetadataPath
$nativeMetadataText = [System.IO.File]::ReadAllText($nativeMetadataPath)
Assert-NoLocalPathsInMetadata -Text $nativeMetadataText
$null = $nativeMetadataText | ConvertFrom-Json -ErrorAction Stop

$gitRevisionLines = @(& git -c ("safe.directory={0}" -f $repoRoot) -C $repoRoot rev-parse HEAD)
if ($LASTEXITCODE -ne 0 -or $gitRevisionLines.Count -eq 0) {
    throw 'git rev-parse failed while recording the project source revision.'
}
$gitRevision = ([string]$gitRevisionLines[0]).Trim()
if ($gitRevision -notmatch '^[0-9a-fA-F]{40,64}$') {
    throw 'git rev-parse returned an invalid project source revision.'
}

$manifest = [ordered]@{
    schemaVersion = 2
    project = 'NcmConverter'
    target = 'win-x64'
    sourceRevision = $gitRevision.ToLowerInvariant()
    thirdPartySources = @(
        foreach ($item in $nativeArchiveSpecs) {
            [ordered]@{
                name = $item.Spec.Name
                version = $item.Spec.Version
                archive = $item.ArchivePath
                sourceUrl = $item.Spec.Url
                upstreamSha256 = $item.Spec.Sha256
                archiveSha256 = $item.Spec.Sha256
                sha256 = $item.Spec.Sha256
                license = $item.Spec.License
            }
        }
        [ordered]@{
            name = $tagLibSource.Name
            version = $tagLibSource.Version
            archive = $tagLibArchivePathInZip
            upstreamArchive = $tagLibSource.Archive
            sourceUrl = $tagLibSource.Url
            commit = $tagLibSource.Commit
            upstreamSha256 = $tagLibSource.Sha256
            archiveSha256 = $tagLibArchiveInfo.Sha256
            sha256 = $tagLibArchiveInfo.Sha256
            excludedPaths = $tagLibArchiveInfo.ExcludedPaths
            license = $tagLibSource.License
        }
    )
    nativeBuild = [ordered]@{
        metadata = 'build/native-build-metadata.json'
        metadataSha256 = Get-Sha256 -Path $nativeMetadataPath
    }
}
$manifestJson = ($manifest | ConvertTo-Json -Depth 8) + [Environment]::NewLine

foreach ($path in @($archivePath, $checksumPath)) {
    if (Test-Path -LiteralPath $path) {
        $item = Get-Item -LiteralPath $path -Force
        if ($item.PSIsContainer -or (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)) {
            throw "Refusing to replace a non-regular release archive path: $path"
        }
    }
}

$temporaryArchivePath = "$archivePath.$([guid]::NewGuid().ToString('N')).tmp"
$temporaryChecksumPath = "$checksumPath.$([guid]::NewGuid().ToString('N')).tmp"
$ownedTemporaryPaths = [System.Collections.Generic.List[string]]::new()
$ownedTemporaryPaths.Add($tagLibTemporaryPath)

try {
    $fileStream = [System.IO.File]::Open($temporaryArchivePath, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
    $ownedTemporaryPaths.Add($temporaryArchivePath)
    $zipArchive = [System.IO.Compression.ZipArchive]::new($fileStream, [System.IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        foreach ($item in $nativeArchiveSpecs) {
            Add-FileToArchive -Archive $zipArchive -SourcePath $item.FullPath -EntryPath $item.ArchivePath -CompressionLevel ([System.IO.Compression.CompressionLevel]::NoCompression)
        }
        Add-FileToArchive -Archive $zipArchive -SourcePath $tagLibTemporaryPath -EntryPath $tagLibArchivePathInZip -CompressionLevel ([System.IO.Compression.CompressionLevel]::NoCompression)
        Add-TextToArchive -Archive $zipArchive -EntryPath 'build/native-build-metadata.json' -Text ($nativeMetadataText.TrimEnd() + [Environment]::NewLine)
        Add-TextToArchive -Archive $zipArchive -EntryPath 'build/build-metadata.json' -Text $manifestJson
    }
    finally {
        $zipArchive.Dispose()
        $fileStream.Dispose()
    }

    $verifyStream = [System.IO.File]::OpenRead($temporaryArchivePath)
    $verifyArchive = [System.IO.Compression.ZipArchive]::new($verifyStream, [System.IO.Compression.ZipArchiveMode]::Read, $true)
    try {
        $expectedEntryNames = @(
            @($nativeArchiveSpecs | ForEach-Object ArchivePath) +
            @($tagLibArchivePathInZip, 'build/native-build-metadata.json', 'build/build-metadata.json')
        ) | Sort-Object -CaseSensitive
        $entryNames = @($verifyArchive.Entries | ForEach-Object FullName)
        $actualEntryNames = @($entryNames | Sort-Object -CaseSensitive)
        if (($entryNames | Select-Object -Unique).Count -ne $entryNames.Count -or
            $actualEntryNames.Count -ne $expectedEntryNames.Count -or
            (Compare-Object -ReferenceObject $expectedEntryNames -DifferenceObject $actualEntryNames)) {
            throw 'The third-party source archive contains duplicate, missing, or unexpected ZIP entries.'
        }

        foreach ($item in $nativeArchiveSpecs) {
            $entry = $verifyArchive.GetEntry($item.ArchivePath)
            if ((Get-ZipEntrySha256 -Entry $entry) -ne $item.Spec.Sha256) {
                throw "SHA-256 mismatch inside the source archive for $($item.Spec.Archive)."
            }
        }
        if ((Get-ZipEntrySha256 -Entry $verifyArchive.GetEntry($tagLibArchivePathInZip)) -ne $tagLibArchiveInfo.Sha256) {
            throw 'SHA-256 mismatch inside the source archive for the reduced TagLib source archive.'
        }
        if ((Get-ZipEntrySha256 -Entry $verifyArchive.GetEntry('build/native-build-metadata.json')) -ne (Get-Sha256 -Path $nativeMetadataPath)) {
            throw 'Native build metadata changed while creating the source archive.'
        }
    }
    finally {
        $verifyArchive.Dispose()
        $verifyStream.Dispose()
    }

    $archiveHash = Get-Sha256 -Path $temporaryArchivePath
    $checksumText = "$archiveHash *third-party-sources.zip$([Environment]::NewLine)"
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
    Write-Output ("Reduced TagLib source archive SHA256: {0}" -f $tagLibArchiveInfo.Sha256)
    Write-Output ("TagLib excluded paths: {0}" -f ($tagLibArchiveInfo.ExcludedPaths -join ', '))
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
