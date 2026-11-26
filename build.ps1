param([switch]$SkipTests)
$ErrorActionPreference='Stop'
$taskRoot=$PSScriptRoot
$sdk=Join-Path $taskRoot 'build\dotnet\dotnet.exe'
if (!(Test-Path -LiteralPath $sdk)) { $sdk=(Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$distRoot=[IO.Path]::GetFullPath((Join-Path $taskRoot 'dist'))
$outputDir=Join-Path $distRoot 'DonnerControlPanel'
$buildId=[guid]::NewGuid().ToString('N')
$stagingDir=Join-Path $distRoot ('.publish-'+$buildId)
$previousDir=Join-Path $distRoot ('.previous-'+$buildId)

function Remove-BuildDirectory([string]$directory) {
    if (!(Test-Path -LiteralPath $directory)) {return}
    $path=[IO.Path]::GetFullPath($directory)
    if ([IO.Path]::GetDirectoryName($path) -ne $distRoot -or $path -notin @($stagingDir,$previousDir)) {
        throw "Unexpected build directory: $path"
    }
    # Inspect ordinary directories individually so Windows PowerShell never traverses a junction.
    $pending=[Collections.Generic.Stack[IO.DirectoryInfo]]::new()
    $pending.Push([IO.DirectoryInfo]::new($path))
    while ($pending.Count -gt 0) {
        $entry=$pending.Pop()
        if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) {throw "Retaining linked build directory: $path"}
        foreach ($child in $entry.EnumerateFileSystemInfos()) {
            if ($child.Attributes -band [IO.FileAttributes]::ReparsePoint) {throw "Retaining linked build entry: $($child.FullName)"}
            if ($child -is [IO.DirectoryInfo]) {$pending.Push($child)}
        }
    }
    Remove-Item -LiteralPath $path -Recurse -Force
}

Push-Location $taskRoot
try {
    if (!$SkipTests) {
        & $sdk run --project tests\Donner.Tests -c Release
        if ($LASTEXITCODE -ne 0) {throw 'Tests failed.'}
        & $sdk run --project tests\Donner.Uninstall.Tests -c Release
        if ($LASTEXITCODE -ne 0) {throw 'Uninstall shutdown tests failed.'}
        & $sdk run --project tests\Donner.Panel.Tests -c Release
        if ($LASTEXITCODE -ne 0) {throw 'Panel regression tests failed.'}
    }
    [IO.Directory]::CreateDirectory($distRoot) | Out-Null
    if ((Get-Item -LiteralPath $distRoot).Attributes -band [IO.FileAttributes]::ReparsePoint) {throw "Linked dist directory: $distRoot"}
    # Start with an empty output even when obj/ was cleared or this build removed a resource.
    [IO.Directory]::CreateDirectory($stagingDir) | Out-Null
    & $sdk publish src\Donner.Panel -c Release -r win-x64 --self-contained true -o $stagingDir -v minimal
    if ($LASTEXITCODE -ne 0) {throw 'Panel publish failed.'}
    & $sdk publish src\Donner.Host -c Release -r win-x64 --self-contained true -o $stagingDir -v minimal
    if ($LASTEXITCODE -ne 0) {throw 'Host publish failed.'}
    # Referenced projects can supply their own PDB even when the main publish disables symbols.
    Get-ChildItem -LiteralPath $stagingDir -File -Filter '*.pdb' | Remove-Item
    Copy-Item -LiteralPath README.md -Destination (Join-Path $stagingDir '使用说明.md')
    Copy-Item -LiteralPath THIRD_PARTY_NOTICES.txt -Destination $stagingDir

    $publishLock=[IO.File]::Open((Join-Path $distRoot '.publish.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
    try {
        if ([IO.Directory]::Exists($outputDir)) {
            if ((Get-Item -LiteralPath $outputDir).Attributes -band [IO.FileAttributes]::ReparsePoint) {throw "Linked output directory: $outputDir"}
            [IO.Directory]::Move($outputDir,$previousDir)
        }
        try {
            [IO.Directory]::Move($stagingDir,$outputDir)
        } catch {
            if ([IO.Directory]::Exists($previousDir)) {[IO.Directory]::Move($previousDir,$outputDir)}
            throw
        }
        try {Remove-BuildDirectory $previousDir} catch {Write-Warning $_}
    } finally {$publishLock.Dispose()}
    Write-Output (Join-Path $outputDir 'DonnerControlPanel.exe')
} finally {
    try {Remove-BuildDirectory $stagingDir} finally {Pop-Location}
}
