param([string]$Compiler)
$ErrorActionPreference='Stop'
$project=Split-Path $PSScriptRoot -Parent
if (!$Compiler) {$Compiler=Join-Path $project 'build/inno/compiler/ISCC.exe'}
$fixture=Join-Path $project ('build/uninstall-fixture-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
$registry='Software\DonnerControlPanel-UninstallTest-'+[guid]::NewGuid().ToString('N')
$template=@'
[Setup]
AppId=DonnerUninstallRegressionFixture
AppName=Donner Uninstall Test
AppVersion=1.0
DefaultDirName={#Fixture}\app
PrivilegesRequired=lowest
DisableDirPage=yes
DisableProgramGroupPage=yes
Uninstallable=yes
OutputDir={#Fixture}
OutputBaseFilename=setup
[Files]
Source: "{#Fixture}\payload.txt"; DestDir: "{app}"
[Code]
const
  StartupKey = '{#StartupRegistryKey}';
'@
$source=Get-Content (Join-Path $project 'installer/DonnerControlPanel.iss') -Raw
if ($source.Contains('#include "UserDataCleanup.iss"')) {
    $template+="`n#include `""+(Join-Path $project 'installer/UserDataCleanup.iss')+"`"`n"
} else {
    $template+="`n"+$source.Substring($source.IndexOf('procedure CurUninstallStepChanged'))
}
Set-Content -LiteralPath (Join-Path $fixture 'test.iss') -Value $template -Encoding utf8
Set-Content -LiteralPath (Join-Path $fixture 'payload.txt') -Value 'installed file'
& $Compiler ('/DFixture='+$fixture) ('/DUserDataDirectory='+$fixture+'\data') ('/DStartupRegistryKey='+$registry) ('/DStartupApprovalRegistryKey='+$registry+'\Approved') (Join-Path $fixture 'test.iss') | Out-Null
if ($LASTEXITCODE -ne 0) {throw 'Fixture compilation failed'}
function Run-Setup {
    $p=Start-Process -FilePath (Join-Path $fixture 'setup.exe') -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -WindowStyle Hidden -Wait -PassThru
    if ($p.ExitCode -ne 0) {throw "Fixture setup failed: $($p.ExitCode)"}
}
function Seed {
    New-Item -ItemType Directory -Path (Join-Path $fixture 'data/presets') -Force | Out-Null
    Set-Content (Join-Path $fixture 'data/presets/user.json') 'valuable preset'
    Set-Content (Join-Path $fixture 'data/mixer-session.json.bak') 'backup'
    Set-Content (Join-Path $fixture 'sibling.txt') 'unrelated'
    New-Item -Path "HKCU:\$registry" -Force | Out-Null
    Set-ItemProperty -LiteralPath "HKCU:\$registry" -Name DonnerControlPanel -Value ('"'+$fixture+'\app\DonnerControlPanel.exe" --startup')
    New-Item -Path "HKCU:\$registry\Approved" -Force | Out-Null
    Set-ItemProperty -LiteralPath "HKCU:\$registry\Approved" -Name DonnerControlPanel -Value 'startup approval fixture'
}
function Uninstall([string]$arguments) {
    $p=Start-Process -FilePath (Join-Path $fixture 'app/unins000.exe') -ArgumentList ('/VERYSILENT /SUPPRESSMSGBOXES /NORESTART '+$arguments) -WindowStyle Hidden -Wait -PassThru
    if ($p.ExitCode -ne 0) {throw "Fixture uninstall failed: $($p.ExitCode)"}
    # The uninstaller starts its own temporary copy; allow final directory removal.
    for($i=0;$i -lt 50 -and (Test-Path (Join-Path $fixture 'app/payload.txt'));$i++){Start-Sleep -Milliseconds 100}
    if (Test-Path (Join-Path $fixture 'app/payload.txt')) {throw 'Installed payload retained'}
    if (!(Test-Path (Join-Path $fixture 'sibling.txt'))) {throw 'Unrelated file deleted'}
}
try {
    Run-Setup; Seed
    Run-Setup
    if (!(Test-Path (Join-Path $fixture 'data/presets/user.json'))) {throw 'Upgrade lost settings'}
    Uninstall '/KEEPUSERDATA=1'
    if (!(Test-Path (Join-Path $fixture 'data/presets/user.json'))) {throw 'Keep lost settings'}
    if ((Get-ItemProperty "HKCU:\$registry" -ErrorAction SilentlyContinue).DonnerControlPanel) {throw 'Startup retained'}
    if ((Get-ItemProperty "HKCU:\$registry\Approved").DonnerControlPanel) {throw 'Startup approval retained'}
    Run-Setup; Seed
    Uninstall ''
    if (!(Test-Path (Join-Path $fixture 'data/presets/user.json'))) {throw 'Silent default lost settings'}
    Run-Setup; Seed
    Uninstall '/PURGEUSERDATA=1'
    if (Test-Path (Join-Path $fixture 'data')) {throw 'PURGE failed: user data and backups remain'}
    Run-Setup; Seed
    Set-ItemProperty -LiteralPath "HKCU:\$registry" -Name DonnerControlPanel -Value '"C:\OtherCopy\DonnerControlPanel.exe" --startup'
    Uninstall '/PURGEUSERDATA=1 /KEEPUSERDATA=1'
    if (!(Test-Path (Join-Path $fixture 'data/presets/user.json'))) {throw 'Explicit KEEP must take precedence'}
    if ((Get-ItemProperty "HKCU:\$registry").DonnerControlPanel -ne '"C:\OtherCopy\DonnerControlPanel.exe" --startup') {throw 'Another copy startup changed'}
    if (!(Get-ItemProperty "HKCU:\$registry\Approved").DonnerControlPanel) {throw 'Another copy startup approval changed'}
    Run-Setup; Seed
    New-Item -ItemType Directory -Path (Join-Path $fixture 'outside') | Out-Null
    Set-Content (Join-Path $fixture 'outside/valuable.txt') 'do not delete'
    New-Item -ItemType Junction -Path (Join-Path $fixture 'data/linked') -Target (Join-Path $fixture 'outside') | Out-Null
    Uninstall '/PURGEUSERDATA=1'
    if (!(Test-Path (Join-Path $fixture 'outside/valuable.txt'))) {throw 'Cleanup followed a junction'}
    if (Test-Path (Join-Path $fixture 'data/presets')) {throw 'Safe ordinary files not removed beside junction'}
    [pscustomobject]@{Success=$true;UpgradePreservesData=$true;KeepPreservesData=$true;SilentDefaultsToKeep=$true;PurgeRemovesData=$true;StartupAndApprovalRemoved=$true;OtherCopyStartupPreserved=$true;ExplicitKeepWins=$true;JunctionTargetPreserved=$true;UnrelatedFilesPreserved=$true;Fixture=$fixture} | ConvertTo-Json
} finally {
    Remove-Item -LiteralPath "HKCU:\$registry" -Recurse -Force -ErrorAction SilentlyContinue
}

