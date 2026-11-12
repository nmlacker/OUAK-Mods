# Packs the three mods for sharing (e.g. with a friend for online play): ModMenuSource\dist\OUAK_Mods.zip
$game = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path   # the game folder: ModMenuSource sits inside it
$dist = Join-Path $game "ModMenuSource\dist"
$stage = Join-Path $dist "OUAK_Mods"
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force -Path (Join-Path $stage "Mods") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $stage "UserLibs") | Out-Null
foreach ($dll in @("ImGui.NET.dll", "cimgui.dll")) {
    Copy-Item (Join-Path $game "UserLibs\$dll") (Join-Path $stage "UserLibs\$dll")
}
foreach ($dll in @("OUAKModAPI.dll", "OUAKModMenu.dll", "OUAKMultiplayer.dll")) {
    Copy-Item (Join-Path $game "Mods\$dll") (Join-Path $stage "Mods\$dll")
}
@"
OUAK mods for Once Upon a KATAMARI (MelonLoader 0.7)

Copy the Mods and UserLibs folders into the game folder
(Steam > Once Upon a KATAMARI > Manage > Browse local files), replacing older copies.

  OUAKModAPI.dll       the OUAK Modding API (required by the other two)
  UserLibs\ImGui.NET.dll, UserLibs\cimgui.dll   Dear ImGui, which the API draws its windows with (required)
  OUAKModMenu.dll      the mod menu (cheats, maps, backups...)
  OUAKMultiplayer.dll  online play (protocol v16: everyone in a session needs this same version)

Right Shift opens the mod window. Online play: the Online tab.
"@ | Set-Content -Path (Join-Path $stage "README.txt") -Encoding UTF8
$zip = Join-Path $dist "OUAK_Mods.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip
Get-Item $zip | Select-Object FullName, Length
