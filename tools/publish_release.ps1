param(
    [Parameter(Mandatory = $true)][string]$Tag,   # e.g. 0.1.1
    [string]$Notes = ""                            # release notes (Markdown)
)
# Publishes a release of the OUAK mods on GitHub (nmlacker/OUAK-Mods, public): builds dist\ from the source
# (tools\make_dist.ps1) and uploads OUAK_Mods.zip (the modpack) and one <Mod>-<version>.zip per mod; GitHub adds the
# source archives itself. The website (nmlacker/OUAK-hub) shows the modpack and each mod's zip of the latest release,
# and every player's OUAK Modding API downloads OUAK_Mods.zip at its next launch, installs it silently and asks for a
# restart (OUAKModAPI\Platform\Updater.cs). It is public: push the matching source first.
$ErrorActionPreference = "Stop"
$src = Split-Path $PSScriptRoot -Parent
& (Join-Path $PSScriptRoot "make_dist.ps1")
$dist = Join-Path $src "dist"
$zips = @(Get-ChildItem $dist -Filter *.zip | Sort-Object { $_.Name -ne "OUAK_Mods.zip" }, Name)
if (-not ($zips | Where-Object Name -eq "OUAK_Mods.zip")) { throw "no OUAK_Mods.zip in $dist" }
if ($Notes -eq "") { $Notes = "OUAK mods $Tag." }
$Notes += "`n`nSHA-256:"
foreach ($z in $zips) { $Notes += "`n- ``$($z.Name)``: $((Get-FileHash $z.FullName -Algorithm SHA256).Hash)" }
gh release create $Tag @($zips.FullName) --repo nmlacker/OUAK-Mods --title "OUAK mods $Tag" --notes $Notes
if ($LASTEXITCODE -ne 0) { throw "gh release create failed" }
"published $Tag"
