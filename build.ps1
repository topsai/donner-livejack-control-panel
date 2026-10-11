param([switch]$SkipTests)
$ErrorActionPreference='Stop'
$taskRoot=$PSScriptRoot
$sdk=Join-Path $taskRoot 'build\dotnet\dotnet.exe'
if (!(Test-Path -LiteralPath $sdk)) { $sdk=(Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
Push-Location $taskRoot
try {
    if (!$SkipTests) {
        & $sdk run --project tests\Donner.Tests -c Release
        if ($LASTEXITCODE -ne 0) {throw 'Tests failed.'}
    }
    & $sdk publish src\Donner.Panel -c Release -r win-x64 --self-contained true -o dist\DonnerControlPanel -v minimal
    if ($LASTEXITCODE -ne 0) {throw 'Panel publish failed.'}
    & $sdk publish src\Donner.Host -c Release -r win-x64 --self-contained true -o dist\DonnerControlPanel -v minimal
    if ($LASTEXITCODE -ne 0) {throw 'Host publish failed.'}
    Copy-Item -LiteralPath README.md -Destination dist\DonnerControlPanel\使用说明.md -Force
    Write-Output (Join-Path $taskRoot 'dist\DonnerControlPanel\DonnerControlPanel.exe')
} finally {Pop-Location}
