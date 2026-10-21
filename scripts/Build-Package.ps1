[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')]
    [string]$Version = '0.1.0',
    [string]$InnoCompiler
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = Split-Path -Parent $PSScriptRoot
$packageRoot = Join-Path $repository '.artifacts\package'
$outputRoot = Join-Path $repository '.artifacts\installer'
$numericVersion = $Version.Split('-')[0]

if (-not $InnoCompiler)
{
    $InnoCompiler = Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe'
}

if (-not (Test-Path -LiteralPath $InnoCompiler -PathType Leaf))
{
    throw 'Install Inno Setup 7 or pass -InnoCompiler with its ISCC.exe path.'
}

$compilerVersion = (& $InnoCompiler --version | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $compilerVersion -notmatch '^7\.')
{
    throw 'The package build requires Inno Setup 7.'
}

foreach ($folder in @('Desktop', 'Service', 'Setup', 'Docs', 'Legal'))
{
    $destination = [IO.Path]::GetFullPath((Join-Path $packageRoot $folder))
    $expectedRoot = [IO.Path]::GetFullPath($packageRoot).TrimEnd('\') + '\'
    if (-not $destination.StartsWith($expectedRoot, [StringComparison]::OrdinalIgnoreCase))
    {
        throw 'Package output is outside the expected artifact directory.'
    }

    if (Test-Path -LiteralPath $destination)
    {
        Remove-Item -LiteralPath $destination -Recurse -Force
    }
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
}

foreach ($project in @('Desktop', 'Service', 'Setup'))
{
    $projectPath = Join-Path $repository "src\SqlBackup.$project\SqlBackup.$project.csproj"
    $destination = Join-Path $packageRoot $project
    $arguments = @('publish', $projectPath, '--configuration', 'Release', '--runtime', 'win-x64',
        '--self-contained', 'true', '--output', $destination, "-p:Version=$Version", "-p:FileVersion=$numericVersion.0",
        '-p:PublishTrimmed=false', '-p:DebugType=None', '-p:DebugSymbols=false')
    if ($project -eq 'Setup')
    {
        $arguments += @('-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true')
    }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0)
    {
        throw "Publish failed for $project."
    }
}

$helper = Join-Path $packageRoot 'Setup\SqlBackup.Setup.exe'
& (Join-Path $PSScriptRoot 'Write-PackageNotices.ps1') -OutputDirectory (Join-Path $packageRoot 'Legal')

& $helper --verify-payload
if ($LASTEXITCODE -ne 0)
{
    throw 'The published setup helper could not load its bundled runtime/SQLite payload.'
}

New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
& $InnoCompiler --quiet-progress "--define=AppVersion=$numericVersion" "--define=DisplayVersion=$Version" "--define=PackageRoot=$packageRoot" "--define=OutputRoot=$outputRoot" (Join-Path $repository 'installer\SqlBackup.iss')
if ($LASTEXITCODE -ne 0)
{
    throw 'Inno Setup compilation failed.'
}

$installer = Join-Path $outputRoot "SqlBackup-Setup-$Version-win-x64.exe"
$checksum = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($installer + '.sha256', "$checksum  $([IO.Path]::GetFileName($installer))`n")
Write-Host "Installer: $installer"

