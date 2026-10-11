param(
    [Parameter(Mandatory=$true)][string]$DriverInstaller,
    [string]$Compiler,
    [switch]$SkipAppBuild
)
$ErrorActionPreference='Stop'
$taskRoot=$PSScriptRoot
$driver=Get-Item -LiteralPath $DriverInstaller
$expected='12EF3F0F517B2F00A7EBBA3DBC0CAC978E4A71ECE8599B31FFE853304DD147F0'
if ((Get-FileHash -LiteralPath $driver.FullName -Algorithm SHA256).Hash -ne $expected) {
    throw 'Expected the original signed Donner_UsbAudio_v5.0.0_2021-03-29_setup.exe (not the outer download wrapper).'
}
$signature=Get-AuthenticodeSignature -LiteralPath $driver.FullName
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notlike '*Thesycon*') {
    throw 'The original driver installer signature is invalid.'
}
if (!$Compiler) {
    $candidates=@(
        (Join-Path $taskRoot 'build\inno\compiler\ISCC.exe'),
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    )
    $Compiler=$candidates | Where-Object {Test-Path -LiteralPath $_} | Select-Object -First 1
    if (!$Compiler) { $Compiler=(Get-Command ISCC.exe -ErrorAction Stop).Source }
}
if (!$SkipAppBuild) { & (Join-Path $taskRoot 'build.ps1') }
$appFiles=Join-Path $taskRoot 'dist\DonnerControlPanel'
if (!(Test-Path -LiteralPath (Join-Path $appFiles 'DonnerControlPanel.exe'))) {throw 'Publish the application first.'}
& $Compiler ('/DAppFiles='+$appFiles) ('/DDriverInstaller='+$driver.FullName) ('/DSetupOutput='+(Join-Path $taskRoot 'dist')) (Join-Path $taskRoot 'installer\DonnerControlPanel.iss')
if ($LASTEXITCODE -ne 0) {throw 'Installer compilation failed.'}
$output=Join-Path $taskRoot 'dist\DonnerControlPanel-v1.16-Setup-win-x64.exe'
Get-FileHash -LiteralPath $output -Algorithm SHA256
