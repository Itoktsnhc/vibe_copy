param([string]$AssemblyPath = (Join-Path $PSScriptRoot '../bin/Debug/net8.0-windows/VibeCopy.dll'))

# Run after dotnet build. Only disposable fixtures under the system temp directory are copied.
$ErrorActionPreference = 'Stop'
Add-Type -Path (Resolve-Path $AssemblyPath)
$script:checks = 0
function Assert-Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:checks++
}

$root = [IO.Path]::Combine([IO.Path]::GetTempPath(), 'vibecopy-checks-' + [Guid]::NewGuid().ToString('N'))
$none = [Threading.CancellationToken]::None
$onBytes = [Action[int]] { param($count) }
$all = [Collections.Generic.HashSet[string]]::new()
$date = [datetime]'2024-02-29T12:00:00'
$culture = [Globalization.CultureInfo]::CurrentCulture
try {
    [Globalization.CultureInfo]::CurrentCulture = [Globalization.CultureInfo]::GetCultureInfo('ar-SA')
    foreach ($pair in @(
        @('yyyy', '2024'), @('yyyy/MM', '2024\02'),
        @('yyyy/MM/dd', '2024\02\29'), @('yyyy-MM-dd', '2024-02-29'),
        @('yyyy\MM\dd', '2024\02\29')
    )) {
        Assert-Check ([VibeCopy.Copier]::FormatFolder($date, $pair[0]) -eq $pair[1]) "Wrong folder: $($pair[0])"
    }
    [Globalization.CultureInfo]::CurrentCulture = $culture
    foreach ($pattern in @('', '../yyyy', '/yyyy', 'yyyy//MM', 'yyyy/../dd', "'C:'/yyyy", "'CON'/yyyy", "'a?b'/yyyy")) {
        $errorMessage = ''
        Assert-Check (-not [VibeCopy.Copier]::TryValidateFolderPattern($pattern, [ref]$errorMessage)) "Accepted unsafe pattern: $pattern"
    }
    Assert-Check ([VibeCopy.Copier]::IsSameOrChild('C:\source\nested', 'C:\source\')) 'Nested path missed'
    Assert-Check ([VibeCopy.Copier]::IsSameOrChild('C:\source', 'C:\')) 'Drive-root containment missed'
    Assert-Check (-not [VibeCopy.Copier]::IsSameOrChild('C:\source-other', 'C:\source')) 'Sibling prefix mistaken for child'
    Assert-Check ([VibeCopy.Copier]::IsSameOrChild('\\server\share\nested', '\\server\share\')) 'UNC containment missed'

    $legacy = [System.Text.Json.JsonSerializer]::Deserialize('{"ScanDirs":"DCIM","Exts":".jpg","Conflict":"skip"}', [VibeCopy.Config])
    Assert-Check ($legacy.FolderPattern -eq 'yyyy-MM-dd' -and $legacy.ScanDirs -eq 'DCIM' -and $legacy.Exts -eq '.jpg') 'Legacy config defaults changed'
    $config = [VibeCopy.Config]::new()
    $config.SourceDirs = "C:\first`n\\server\share\second"
    $config.FolderPattern = 'yyyy/MM/dd'
    $json = [System.Text.Json.JsonSerializer]::Serialize($config, [VibeCopy.Config])
    $restored = [System.Text.Json.JsonSerializer]::Deserialize($json, [VibeCopy.Config])
    Assert-Check ($restored.SourceDirs -eq $config.SourceDirs -and $restored.FolderPattern -eq $config.FolderPattern) 'New settings did not round-trip'

    $source = [IO.Path]::Combine($root, 'source')
    $nested = [IO.Path]::Combine($source, 'nested')
    $target = [IO.Path]::Combine($source, 'archive')
    [IO.Directory]::CreateDirectory($nested) | Out-Null
    [IO.Directory]::CreateDirectory($target) | Out-Null
    $first = [IO.Path]::Combine($source, 'same.txt')
    $second = [IO.Path]::Combine($nested, 'same.txt')
    [IO.File]::WriteAllText($first, 'first source')
    [IO.File]::WriteAllText($second, 'second source')
    [IO.File]::WriteAllText([IO.Path]::Combine($source, 'README'), 'no extension')
    [IO.File]::WriteAllText([IO.Path]::Combine($target, 'existing.txt'), 'exclude this')
    foreach ($file in @($first, $second)) { [IO.File]::SetLastWriteTime($file, $date) }
    [VibeCopy.MediaFile[]]$files = @([VibeCopy.Copier]::Scan($source, $all, [string[]]@(), $target, $none, $null))
    Assert-Check ($files.Count -eq 3) 'Unfiltered scan missed files or included target'
    $filter = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $filter.Add('.txt') | Out-Null
    [VibeCopy.MediaFile[]]$filtered = @([VibeCopy.Copier]::Scan($source, $filter, [string[]]@(), $target, $none, $null))
    Assert-Check ($filtered.Count -eq 2) 'Extension filter failed'
    $subfiles = @([VibeCopy.Copier]::Scan($source, $all, [string[]]@('nested'), $target, $none, $null))
    Assert-Check ($subfiles.Count -eq 1 -and $subfiles[0].Src -eq $second) 'Relative subdirectory filter failed'

    $jobs = @([VibeCopy.Copier]::PlanCopies($filtered, $target, 'yyyy/MM/dd', $false, 'rename', $none))
    Assert-Check ($jobs.Count -eq 2 -and $jobs[0].Destination -ne $jobs[1].Destination) 'Batch collision was not renamed'
    foreach ($job in $jobs) {
        Assert-Check ($job.Destination.Contains('2024\02\29\')) 'Modified date was not used'
        Assert-Check ([VibeCopy.Copier]::CopyOne($job.File.Src, $job.Destination, $onBytes, $none, $false, 4096)) 'Copy failed'
        Assert-Check ([VibeCopy.Copier]::Sha1($job.File.Src) -eq [VibeCopy.Copier]::Sha1($job.Destination)) 'Copied bytes differ'
        Assert-Check ([IO.File]::GetLastWriteTime($job.Destination) -eq $date) 'Modification time was not preserved'
    }
    $rescanned = @([VibeCopy.Copier]::Scan($source, $all, [string[]]@(), $target, $none, $null))
    Assert-Check ($rescanned.Count -eq 3) 'A repeat scan included archived files'
    $skip = @([VibeCopy.Copier]::PlanCopies($filtered, $target, 'yyyy/MM/dd', $false, 'skip', $none))
    Assert-Check (@($skip | Where-Object Skip).Count -eq 2) 'Existing targets were not skipped'
    $skipNew = @([VibeCopy.Copier]::PlanCopies($filtered, [IO.Path]::Combine($root, 'new'), 'yyyy/MM/dd', $false, 'skip', $none))
    Assert-Check (-not $skipNew[0].Skip -and $skipNew[1].Skip) 'Same-batch skip failed'
    $again = @([VibeCopy.Copier]::PlanCopies($filtered, $target, 'yyyy/MM/dd', $false, 'rename', $none))
    Assert-Check (@($again | Where-Object { [IO.File]::Exists($_.Destination) }).Count -eq 0) 'Rename reused an existing target'
    Assert-Check ($again[0].Destination -ne $again[1].Destination) 'Rename reused a reserved name'

    $overwrite = @([VibeCopy.Copier]::PlanCopies($filtered, $target, 'yyyy/MM/dd', $false, 'overwrite', $none))
    foreach ($job in $overwrite) {
        Assert-Check ([VibeCopy.Copier]::CopyOne($job.File.Src, $job.Destination, $onBytes, $none, $true, 4096)) 'Overwrite failed'
    }
    Assert-Check ([IO.File]::ReadAllText($overwrite[-1].Destination) -eq [IO.File]::ReadAllText($overwrite[-1].File.Src)) 'Last overwrite did not win'
    $before = [IO.File]::ReadAllText($first)
    $rejected = $false
    try { [VibeCopy.Copier]::CopyOne($first, $first, $onBytes, $none, $true, 4096) | Out-Null }
    catch { $rejected = $true }
    Assert-Check ($rejected -and [IO.File]::ReadAllText($first) -eq $before) 'Self-copy damaged a source'
    $sentinel = $jobs[0].Destination + '.part'
    [IO.File]::WriteAllText($sentinel, 'keep existing part')
    [VibeCopy.Copier]::CopyOne($first, $jobs[0].Destination, $onBytes, $none, $true, 4096) | Out-Null
    Assert-Check ([IO.File]::ReadAllText($sentinel) -eq 'keep existing part') 'Temporary copy clobbered an unrelated .part file'

    $large = [IO.Path]::Combine($source, 'large.bin')
    [IO.File]::WriteAllBytes($large, [byte[]]::new(32768))
    $cancelDst = [IO.Path]::Combine($target, 'cancelled.bin')
    $cancel = [Threading.CancellationTokenSource]::new()
    try {
        $cancelBytes = [Action[int]] { param($count) $cancel.Cancel() }
        Assert-Check (-not [VibeCopy.Copier]::CopyOne($large, $cancelDst, $cancelBytes, $cancel.Token, $false, 4096)) 'Cancelled copy succeeded'
        Assert-Check (-not [IO.File]::Exists($cancelDst)) 'Cancelled destination was published'
        Assert-Check (@([IO.Directory]::GetFiles($target, '.vibecopy-*.part', [IO.SearchOption]::AllDirectories)).Count -eq 0) 'Temporary copy was not cleaned up'
    }
    finally { $cancel.Dispose() }
    Write-Output "PASS: $script:checks core checks"
}
finally {
    [Globalization.CultureInfo]::CurrentCulture = $culture
    $resolved = [IO.Path]::GetFullPath($root)
    $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if (-not $resolved.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase) -or
        -not [IO.Path]::GetFileName($resolved).StartsWith('vibecopy-checks-')) {
        throw "Refusing to clean an unexpected fixture path: $resolved"
    }
    if ([IO.Directory]::Exists($resolved)) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
