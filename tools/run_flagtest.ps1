param(
    [string]$FlagFile,                 # relative to UserData, e.g. OUAKModAPI\apitest.flag
    [string]$FlagText = "",
    [string]$DonePattern,              # e.g. '\[ApiTest\] DONE'
    [string]$Filter = 'Exception|Error|FAIL|Warning',
    [int]$TimeoutSec = 300,
    [string]$SaveLogAs = ""             # copy MelonLoader\Latest.log here when done
)
# Launches the game with a self-test flag and waits for it by reading MelonLoader's log only (no screen capture).
$game = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path   # the game folder: ModMenuSource sits inside it
$log = Join-Path $game "MelonLoader\Latest.log"
if (Get-Process -Name OnceUponaKATAMARI -ErrorAction SilentlyContinue) { "the game is already running; not launching"; exit 1 }
$flag = Join-Path $game ("UserData\" + $FlagFile)
New-Item -ItemType Directory -Force -Path (Split-Path $flag) | Out-Null
Set-Content -Path $flag -Value $FlagText -NoNewline
$start = Get-Date
Start-Process -FilePath (Join-Path $game "OnceUponaKATAMARI.exe") -WorkingDirectory $game
Start-Sleep -Seconds 20
$done = $false
while (((Get-Date) - $start).TotalSeconds -lt $TimeoutSec) {
    Start-Sleep -Seconds 3
    if (Select-String -Path $log -Pattern $DonePattern -Quiet -ErrorAction SilentlyContinue) { $done = $true; "DONE seen"; break }
    if (-not (Get-Process -Name OnceUponaKATAMARI -ErrorAction SilentlyContinue)) { "THE GAME EXITED before DONE"; break }
}
if (-not $done) { "no DONE (timeout or exit) after $([int]((Get-Date) - $start).TotalSeconds) s" }
Start-Sleep -Seconds 6
$p = Get-Process -Name OnceUponaKATAMARI -ErrorAction SilentlyContinue
if ($p) { "closing the game"; $p | Stop-Process -Force }
"game closed at $(Get-Date -Format HH:mm:ss)"
if ($SaveLogAs -ne "") { Copy-Item $log $SaveLogAs -Force; "log saved to $SaveLogAs" }
Get-Content $log | Select-String $Filter | ForEach-Object { $_.Line } | Select-Object -Last 150
"=== last lines"
Get-Content $log -Tail 4
