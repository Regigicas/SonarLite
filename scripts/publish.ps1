# Publishes SonarLite to a stable folder (the autostart entry points there), restarting it
# if it was running.
param(
    [string]$Destination = (Join-Path $env:LOCALAPPDATA 'Programs\SonarLite'),
    # Start the app after publishing even if it was not running.
    [switch]$Start
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $Destination 'SonarLite.exe'

# Build first, so a compile error never leaves you without the running app (and its audio).
dotnet build (Join-Path $root 'src\SonarLite') -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'build failed, running app left untouched' }

$wasRunning = [bool](Get-Process SonarLite -ErrorAction SilentlyContinue)
if ($wasRunning) {
    # Clean exit: stops Sonar through its own shutdown sequence.
    $running = (Get-Process SonarLite | Select-Object -First 1).Path
    Start-Process $running -ArgumentList '--exit' -Wait
    while (Get-Process SonarLite -ErrorAction SilentlyContinue) { Start-Sleep -Milliseconds 200 }
}

dotnet publish (Join-Path $root 'src\SonarLite') -c Release -o $Destination --nologo
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

if ($wasRunning -or $Start) {
    Start-Process $exe -ArgumentList '--minimized'
}
"Published to $exe"
