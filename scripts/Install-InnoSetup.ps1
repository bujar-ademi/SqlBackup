[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$compiler = Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe'
if (Test-Path -LiteralPath $compiler)
{
    $version = (& $compiler --version | Out-String).Trim()
    if ($LASTEXITCODE -eq 0 -and $version -eq '7.1.0')
    {
        Write-Output $compiler
        return
    }
}

$repository = Split-Path -Parent $PSScriptRoot
$downloadDirectory = Join-Path $repository '.artifacts\tools'
New-Item -ItemType Directory -Path $downloadDirectory -Force | Out-Null
$download = Join-Path $downloadDirectory 'innosetup-7.1.0-x64.exe'
$url = 'https://github.com/jrsoftware/issrc/releases/download/is-7_1_0/innosetup-7.1.0-x64.exe'
$expectedHash = '0362a383ed217d4c4239b5933866dd96d3eb2102737da92f80f6057a4b40df2f'
Invoke-WebRequest -Uri $url -OutFile $download
if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedHash)
{
    throw 'The pinned Inno Setup download failed checksum verification.'
}

$signature = Get-AuthenticodeSignature -LiteralPath $download
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '(^|,\s*)CN=Pyrsys B\.V\.(,|$)')
{
    throw 'The Inno Setup publisher signature could not be verified.'
}

$installation = Start-Process -FilePath $download -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-') -Wait -PassThru -WindowStyle Hidden
if ($installation.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $compiler))
{
    throw 'Inno Setup installation failed on the build runner.'
}

Write-Output $compiler
