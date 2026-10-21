[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = Split-Path -Parent $PSScriptRoot
$output = [IO.Path]::GetFullPath($OutputDirectory)
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repository '.artifacts')).TrimEnd('\') + '\'
if (-not $output.StartsWith($artifactRoot, [StringComparison]::OrdinalIgnoreCase))
{
    throw 'Notices output must be inside the repository artifact directory.'
}

New-Item -ItemType Directory -Path $output -Force | Out-Null
Copy-Item (Join-Path $repository 'LICENSE') (Join-Path $output 'SqlBackup-MIT.txt')
Copy-Item (Join-Path $repository 'licenses\*') $output
Copy-Item (Join-Path $repository 'THIRD-PARTY-NOTICES.md') $output
$inventory = [Collections.Generic.SortedDictionary[string, object]]::new([StringComparer]::Ordinal)
foreach ($project in @('Desktop', 'Service', 'Setup'))
{
    $assetsPath = Join-Path $repository "src\SqlBackup.$project\obj\project.assets.json"
    $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json -AsHashtable
    foreach ($entry in $assets.libraries.GetEnumerator())
    {
        if ($entry.Value.type -ne 'package')
        {
            continue
        }

        $packageDirectory = $null
        foreach ($cache in $assets.packageFolders.Keys)
        {
            $candidate = Join-Path $cache $entry.Value.path
            if (Test-Path -LiteralPath $candidate -PathType Container)
            {
                $packageDirectory = $candidate
                break
            }
        }
        if (-not $packageDirectory)
        {
            throw "Resolved package is missing: $($entry.Key)"
        }

        $specification = Get-ChildItem -LiteralPath $packageDirectory -Filter '*.nuspec' | Select-Object -First 1
        [xml]$metadataDocument = Get-Content -LiteralPath $specification.FullName -Raw
        $metadata = $metadataDocument.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]')
        $license = $metadata.SelectSingleNode('*[local-name()="license"]')
        if (-not $license)
        {
            throw "License review required for $($entry.Key)"
        }
        if ($license.type -eq 'expression' -and $license.InnerText -notin @('MIT', 'Apache-2.0'))
        {
            throw "New license review required for $($entry.Key)"
        }
        if ($license.type -eq 'file' -and $entry.Key -notlike 'Microsoft.Data.SqlClient.SNI.runtime/*')
        {
            throw "New file license review required for $($entry.Key)"
        }

        $packageOutput = Join-Path $output ($entry.Key.Replace('/', '-'))
        New-Item -ItemType Directory -Path $packageOutput -Force | Out-Null
        Copy-Item -LiteralPath $specification.FullName -Destination $packageOutput
        if ($license.type -eq 'file')
        {
            $licensePath = [IO.Path]::GetFullPath((Join-Path $packageDirectory $license.InnerText))
            if (-not $licensePath.StartsWith($packageDirectory.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase))
            {
                throw 'Package license path escapes its package directory.'
            }
            Copy-Item -LiteralPath $licensePath -Destination $packageOutput
        }

        foreach ($notice in Get-ChildItem -LiteralPath $packageDirectory -File -Recurse |
            Where-Object { $_.Name -match '(?i)(license|notice|copying)' })
        {
            $relative = [IO.Path]::GetRelativePath($packageDirectory, $notice.FullName)
            $noticeTarget = Join-Path $packageOutput $relative
            New-Item -ItemType Directory -Path (Split-Path -Parent $noticeTarget) -Force | Out-Null
            Copy-Item -LiteralPath $notice.FullName -Destination $noticeTarget
        }
        $copyright = $metadata.SelectSingleNode('*[local-name()="copyright"]')
        $inventory[$entry.Key] = [ordered]@{
            package = $entry.Key
            license = $license.InnerText
            licenseType = $license.type
            copyright = $(if ($copyright) { $copyright.InnerText } else { '' })
        }
    }
}

foreach ($project in @('Desktop', 'Service'))
{
    $dependencyPath = Join-Path (Split-Path -Parent $output) "$project\SqlBackup.$project.deps.json"
    if (-not (Test-Path -LiteralPath $dependencyPath))
    {
        throw "Published dependency manifest is missing for $project. Generate notices after publishing."
    }
    $dependencies = Get-Content -LiteralPath $dependencyPath -Raw | ConvertFrom-Json -AsHashtable
    foreach ($entry in $dependencies.libraries.GetEnumerator())
    {
        if ($entry.Value.type -ne 'runtimepack')
        {
            continue
        }
        $identity = $entry.Key -replace '^runtimepack\.', ''
        $runtimeDirectory = $null
        foreach ($cache in $assets.packageFolders.Keys)
        {
            $candidate = Join-Path $cache $identity.ToLowerInvariant()
            if (Test-Path -LiteralPath $candidate -PathType Container)
            {
                $runtimeDirectory = $candidate
                break
            }
        }
        if (-not $runtimeDirectory)
        {
            throw "Runtime pack notices are missing: $identity"
        }
        $runtimeOutput = Join-Path $output ($identity.Replace('/', '-'))
        New-Item -ItemType Directory -Path $runtimeOutput -Force | Out-Null
        foreach ($notice in Get-ChildItem -LiteralPath $runtimeDirectory -File |
            Where-Object { $_.Name -match '(?i)(license|notice|\.nuspec$)' })
        {
            Copy-Item -LiteralPath $notice.FullName -Destination $runtimeOutput
        }
        $inventory[$identity] = [ordered]@{
            package = $identity
            license = 'See included runtime LICENSE and notices'
            licenseType = 'runtimepack'
            copyright = 'See upstream notices'
        }
    }
}

$inventory.Values | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $output 'packages.json')
$terms = (Get-Content (Join-Path $repository 'LICENSE') -Raw) + "`n`nBundled native SQL client terms follow. These apply to that component, not the SqlBackup source.`n`n" +
    (Get-Content (Join-Path $repository 'licenses\SqlClient-SNI.txt') -Raw)
$terms | Set-Content (Join-Path $output 'Distribution-Terms.txt')
Write-Host "Wrote notices for $($inventory.Count) resolved packages."
