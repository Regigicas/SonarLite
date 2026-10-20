# Goes back to the official SteelSeries GG, launched the same way as its Run-key autostart.
& (Join-Path $PSScriptRoot 'stop-headless.ps1')
Start-Process 'C:\Program Files\SteelSeries\GG\SteelSeriesGGEZ.exe' `
    -ArgumentList '-dataPath="C:\ProgramData\SteelSeries\GG"', '-dbEnv=production', '-auto=true'
'SteelSeries GG started'
