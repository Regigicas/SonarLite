# Runs SteelSeries Sonar without GG: stops the GG stack, starts FakeGG and launches
# SteelSeriesSonar.exe pointed at it. Sonar's REST address ends up in
# %LOCALAPPDATA%\SonarLite\sonar.json once it has registered.
param(
    [int]$Port = 6399,
    [string]$SonarDir = 'C:\Program Files\SteelSeries\GG\apps\sonar',
    [string]$SonarDataDir = 'C:\ProgramData\SteelSeries\GG\apps\sonar'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$logs = Join-Path $root 'logs'
$fakeGGExe = Join-Path $root 'src\FakeGG\bin\Release\net10.0\FakeGG.exe'
$discovery = Join-Path $env:LOCALAPPDATA 'SonarLite\sonar.json'

if (-not (Test-Path $fakeGGExe)) {
    dotnet build (Join-Path $root 'src\FakeGG') -c Release | Out-Null
}

# GGEZ supervises the others, so it goes first or it would respawn them.
foreach ($name in 'SteelSeriesGGEZ', 'SteelSeriesGG', 'SteelSeriesEngine', 'SteelSeriesPrism', 'SteelSeriesSonar', 'FakeGG') {
    Get-Process $name -ErrorAction SilentlyContinue | Stop-Process -Force
}
Start-Sleep -Milliseconds 1000
Remove-Item $discovery -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $logs | Out-Null

$fake = Start-Process $fakeGGExe -ArgumentList $Port -WindowStyle Hidden -PassThru `
    -RedirectStandardOutput "$logs\fakegg.log" -RedirectStandardError "$logs\fakegg.err.log"
$deadline = (Get-Date).AddSeconds(10)
while ($true) {
    try { Invoke-RestMethod "http://127.0.0.1:$Port/healthCheck" -TimeoutSec 1 | Out-Null; break }
    catch { if ((Get-Date) -gt $deadline) { throw "FakeGG did not start, see $logs\fakegg.err.log" }; Start-Sleep -Milliseconds 200 }
}

# Environment normally provided by GGEZ (see ApplicationConfigurationService).
# Plain http/ws: Sonar only validates GG's certificate on https.
$env:GG_SERVER_ADDRESS = "http://127.0.0.1:$Port"
$env:GG_WS_ENDPOINT = "ws://127.0.0.1:$Port/ws"
$env:APP_DATA_PATH = $SonarDataDir
$env:APP_INSTALL_PATH = $SonarDir
$env:ENVIRONMENT = 'production'
$env:GG_VERSION = (Get-Item (Join-Path $SonarDir '..\..\SteelSeriesGG.exe')).VersionInfo.ProductVersion

# Sonar resolves driver\apoDriverPackage and db-migrations relative to the working directory.
$sonar = Start-Process (Join-Path $SonarDir 'SteelSeriesSonar.exe') -WorkingDirectory $SonarDir -WindowStyle Hidden -PassThru `
    -RedirectStandardOutput "$logs\sonar.log" -RedirectStandardError "$logs\sonar.err.log"

$deadline = (Get-Date).AddSeconds(30)
while (-not ((Test-Path $discovery) -and (Get-Content $discovery | ConvertFrom-Json).sonarReady)) {
    if ($sonar.HasExited) { throw "Sonar exited with code $($sonar.ExitCode), see $logs\sonar.log" }
    if ((Get-Date) -gt $deadline) { throw "Sonar did not become ready, see $logs\sonar.log" }
    Start-Sleep -Milliseconds 300
}
$info = Get-Content $discovery | ConvertFrom-Json
"Sonar ready at $($info.sonarAddress) (FakeGG pid $($fake.Id), Sonar pid $($sonar.Id))"
