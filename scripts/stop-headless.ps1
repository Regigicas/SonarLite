# Stops headless Sonar cleanly: FakeGG sends EVENT_SHUTDOWN so Sonar runs its own
# shutdown stack, then FakeGG is stopped.
param([int]$Port = 6399)

try { Invoke-RestMethod -Method Post "http://127.0.0.1:$Port/_fakegg/shutdown" -TimeoutSec 2 | Out-Null } catch { }

# Sonar waits up to 10 s after its shutdown handlers before exiting.
$sonar = Get-Process SteelSeriesSonar -ErrorAction SilentlyContinue
if ($sonar -and -not $sonar.WaitForExit(15000)) {
    $sonar | Stop-Process -Force
}
Get-Process FakeGG -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item (Join-Path $env:LOCALAPPDATA 'SonarLite\sonar.json') -ErrorAction SilentlyContinue
'Headless Sonar stopped'
