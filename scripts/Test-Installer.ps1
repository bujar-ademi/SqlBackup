[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$InstallerPath,
    [Parameter(Mandatory)]
    [string]$UpgradeInstallerPath,
    [switch]$AllowInstallation
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))
{
    throw 'Installer acceptance checks require an elevated disposable Windows machine.'
}
if ($env:GITHUB_ACTIONS -ne 'true' -and -not $AllowInstallation)
{
    throw 'Use a disposable VM and pass -AllowInstallation for explicit local installation testing.'
}

$repository = Split-Path -Parent $PSScriptRoot
$installDirectory = Join-Path $env:ProgramFiles 'SqlBackup'
$dataDirectory = Join-Path $env:ProgramData 'SqlBackup'
if ((Get-Service -Name AbaSqlBackup -ErrorAction SilentlyContinue) -or (Test-Path -LiteralPath $dataDirectory) -or (Test-Path -LiteralPath $installDirectory))
{
    throw 'This smoke harness refuses an existing SqlBackup installation, data directory or service.'
}

$InstallerPath = (Resolve-Path -LiteralPath $InstallerPath).Path
$UpgradeInstallerPath = (Resolve-Path -LiteralPath $UpgradeInstallerPath).Path
$logDirectory = Join-Path $repository '.artifacts\installer-logs'
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
$env:SQLBACKUP_INSTALLER_SMOKE = '1'
$testProject = Join-Path $repository 'tests\SqlBackup.Tests\SqlBackup.Tests.csproj'
$testAssembly = Join-Path $repository 'tests\SqlBackup.Tests\bin\Release\net10.0-windows\SqlBackup.Tests.dll'
if (-not (Test-Path -LiteralPath $testAssembly))
{
    throw 'Build SqlBackup.slnx --configuration Release before running installer acceptance.'
}

function Invoke-Installer([string]$Executable, [string]$LogName)
{
    $log = Join-Path $logDirectory $LogName
    $process = Start-Process -FilePath $Executable -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', "/LOG=`"$log`"") -Wait -PassThru -WindowStyle Hidden
    if ($process.ExitCode -ne 0)
    {
        throw "Installer/uninstaller failed with exit code $($process.ExitCode). See $log."
    }
}

function Invoke-Fixture([string]$Operation)
{
    & dotnet run --project $testProject --configuration Release --no-build -- --installed-service $Operation
    if ($LASTEXITCODE -ne 0)
    {
        throw "Installed service fixture failed: $Operation."
    }
}

function Assert-Removed
{
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while (Get-Service -Name AbaSqlBackup -ErrorAction SilentlyContinue)
    {
        if ([DateTime]::UtcNow -gt $deadline)
        {
            throw 'Uninstall did not remove AbaSqlBackup.'
        }
        Start-Sleep -Milliseconds 250
    }
    if (-not (Test-Path -LiteralPath (Join-Path $dataDirectory 'configuration.db')))
    {
        throw 'Uninstall removed retained configuration.'
    }
    if (Test-Path -LiteralPath (Join-Path $installDirectory 'Service\SqlBackup.Service.exe'))
    {
        throw 'The service executable remains after uninstall.'
    }
}

Invoke-Installer $InstallerPath 'clean-install.log'
Invoke-Fixture 'seed'
Restart-Service -Name AbaSqlBackup
(Get-Service -Name AbaSqlBackup).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
Invoke-Fixture 'check'
Write-Host 'PASS: Clean installation and service restart preserve LocalSystem-protected credentials.'

# Run recovery from a copied helper, never from a directory that rollback replaces.
$helperDirectory = Join-Path ([IO.Path]::GetTempPath()) ('SqlBackup.SetupSmoke.' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $helperDirectory | Out-Null
$helper = Join-Path $helperDirectory 'SqlBackup.Setup.exe'
Copy-Item -LiteralPath (Join-Path $installDirectory 'Setup\SqlBackup.Setup.exe') -Destination $helper
$version = (Get-Content -LiteralPath (Join-Path $dataDirectory 'installation.json') -Raw | ConvertFrom-Json).Version
$fixtureFile = Join-Path $installDirectory 'Service\rollback-fixture.txt'
[IO.File]::WriteAllText($fixtureFile, 'Original payload')
$resultFile = Join-Path $helperDirectory 'result.txt'
& $helper prepare $installDirectory $version $resultFile
if ($LASTEXITCODE -ne 0)
{
    throw 'Rollback fixture could not prepare a maintenance snapshot.'
}
[IO.File]::WriteAllText($fixtureFile, 'Simulated failed update')
Invoke-Fixture 'corrupt-offline'
& $helper rollback $installDirectory $version $resultFile
if ($LASTEXITCODE -ne 0 -or [IO.File]::ReadAllText($fixtureFile) -ne 'Original payload')
{
    throw 'Application/database rollback did not restore its original payload.'
}
Invoke-Fixture 'check'
Remove-Item -LiteralPath $fixtureFile
Write-Host 'PASS: Explicit rollback restores application payload and a deliberately changed schema snapshot.'

Invoke-Installer $UpgradeInstallerPath 'upgrade.log'
Invoke-Fixture 'check'
Write-Host 'PASS: Upgrade preserves the saved job and decryptable SQL/NAS credentials.'

$uninstaller = Join-Path $installDirectory 'unins000.exe'
Invoke-Installer $uninstaller 'uninstall.log'
Assert-Removed
Write-Host 'PASS: Uninstall removes the service/program while preserving configuration and recovery evidence.'

Invoke-Installer $UpgradeInstallerPath 'reinstall.log'
Invoke-Fixture 'check'
Invoke-Installer $uninstaller 'final-uninstall.log'
Assert-Removed
Write-Host 'PASS: Reinstall under the same LocalSystem identity restores access to retained job credentials.'
Write-Host 'No SQL/NAS server was contacted. Retained synthetic state is left for inspection on this disposable machine.'
