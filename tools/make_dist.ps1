# Packs the released OUAK mods into ModMenuSource\dist:
#   OUAK_Mods.zip               the OUAK Modpack: every released mod (what the API's updater installs)
#   <Mod>-<version>.zip         each mod on its own (the website's per-mod downloads), unzipped the same way
# The mods are built here from the source (Release, not installed to the game's Mods), so a release matches the
# source that's pushed with it. Dear ImGui (UserLibs) goes with the API.
$ErrorActionPreference = "Stop"
$game = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path   # the game folder: ModMenuSource sits inside it
$src = Join-Path $game "ModMenuSource"
$dist = Join-Path $src "dist"
$build = Join-Path $dist "build"

# The released mods, in modpack order. Song Selector isn't released yet.
$mods = @(
    @{ Name = "OUAKModAPI";      Title = "OUAK Modding API"; Text = "the base the other mods need (required)"; UserLibs = $true },
    @{ Name = "OUAKModMenu";     Title = "OUAK Mod Menu";    Text = "the mod menu (freebies, the stage clock, stages, unlocks, backups...)" },
    @{ Name = "OUAKMultiplayer"; Title = "OUAK Multiplayer"; Text = "online play (everyone in a session needs the same version)" },
    @{ Name = "OUAKDashBar";     Title = "OUAK Dash Bar";    Text = "your Prince Dash on the game's own dash gauge" }
)

# A mod's version, from its MelonInfo: a literal there, or the Version constant it names.
function Get-ModVersion($name) {
    $files = Get-ChildItem (Join-Path $src $name) -Recurse -Filter *.cs | Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' }
    foreach ($f in $files) {
        $m = Select-String -Path $f.FullName -Pattern 'MelonInfo\(typeof\([^)]*\),\s*"[^"]*",\s*("([^"]+)"|[\w.]+)' | Select-Object -First 1
        if (-not $m) { continue }
        if ($m.Matches[0].Groups[2].Success) { return $m.Matches[0].Groups[2].Value }
        foreach ($g in $files) {
            $c = Select-String -Path $g.FullName -Pattern 'const string Version\s*=\s*"([^"]+)"' | Select-Object -First 1
            if ($c) { return $c.Matches[0].Groups[1].Value }
        }
    }
    throw "no version found for $name"
}

if (Test-Path $dist) { Remove-Item -Recurse -Force $dist }
New-Item -ItemType Directory -Force -Path $build | Out-Null

foreach ($m in $mods) {
    $m.Version = Get-ModVersion $m.Name
    $out = Join-Path $build $m.Name
    $log = & dotnet build (Join-Path $src "$($m.Name)\$($m.Name).csproj") -c Release -p:NoInstall=true "-p:OutDir=$out\" -nologo -v:q 2>&1
    if ($LASTEXITCODE -ne 0) { $log | Select-String "error" | ForEach-Object { $_.Line }; throw "build of $($m.Name) failed" }
    $m.Dll = Join-Path $out "$($m.Name).dll"
    if (-not (Test-Path $m.Dll)) { throw "no $($m.Dll)" }
    "built $($m.Name) $($m.Version)"
}

function Write-Readme($dir, $mods, $heading) {
    $what = if ($mods | Where-Object { $_.UserLibs }) { "the Mods and UserLibs folders" } else { "the Mods folder" }
    $lines = @($heading, "", "Copy $what into the game folder",
        "(Steam > Once Upon a KATAMARI > Manage > Browse local files), replacing older copies.", "")
    foreach ($m in $mods) { $lines += ("  Mods\{0,-22} {1} {2}: {3}" -f "$($m.Name).dll", $m.Title, $m.Version, $m.Text) }
    if ($mods | Where-Object { $_.UserLibs }) { $lines += "  UserLibs\ImGui.NET.dll, UserLibs\cimgui.dll   Dear ImGui, which the API draws its windows with (required)" }
    else { $lines += "", "Needs the OUAK Modding API (Mods\OUAKModAPI.dll and its UserLibs)." }
    $lines += "", "Right Shift opens the mod window. More: https://nmlacker.github.io/OUAK-hub/"
    $lines | Set-Content -Path (Join-Path $dir "README.txt") -Encoding UTF8
}

function Pack($zipName, $mods, $heading) {
    $stage = Join-Path $dist ([IO.Path]::GetFileNameWithoutExtension($zipName))
    New-Item -ItemType Directory -Force -Path (Join-Path $stage "Mods") | Out-Null
    foreach ($m in $mods) {
        Copy-Item $m.Dll (Join-Path $stage "Mods\$($m.Name).dll")
        if ($m.UserLibs) {
            New-Item -ItemType Directory -Force -Path (Join-Path $stage "UserLibs") | Out-Null
            foreach ($dll in @("ImGui.NET.dll", "cimgui.dll")) { Copy-Item (Join-Path $game "UserLibs\$dll") (Join-Path $stage "UserLibs\$dll") }
        }
    }
    Write-Readme $stage $mods $heading
    $zip = Join-Path $dist $zipName
    Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip
    Get-Item $zip
}

$zips = @()
$zips += Pack "OUAK_Mods.zip" $mods "OUAK Modpack for Once Upon a KATAMARI (MelonLoader 0.7)"
foreach ($m in $mods) { $zips += Pack "$($m.Name)-$($m.Version).zip" @($m) "$($m.Title) $($m.Version) for Once Upon a KATAMARI (MelonLoader 0.7)" }
$zips | Select-Object Name, Length
