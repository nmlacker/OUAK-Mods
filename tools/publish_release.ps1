param(
    [Parameter(Mandatory = $true)][string]$Tag,   # e.g. 0.1.0
    [string]$Notes = ""                            # release notes (Markdown)
)
# Publishes a release of the OUAK mods on GitHub (nmlacker/OUAK-Mods, public): rebuilds dist\OUAK_Mods.zip from the
# installed mods (tools\make_dist.ps1) and uploads it as the release asset; GitHub adds the source archives itself.
# The website (nmlacker/OUAK-hub) links to the latest release here, and every player's OUAK Modding API downloads it at
# its next launch, installs it silently and asks for a restart (OUAKModAPI\Platform\Updater.cs). It is public.
$ErrorActionPreference = "Stop"
$src = Split-Path $PSScriptRoot -Parent
& (Join-Path $PSScriptRoot "make_dist.ps1")
$zip = Join-Path $src "dist\OUAK_Mods.zip"
if (-not (Test-Path $zip)) { throw "no $zip" }
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash
if ($Notes -eq "") { $Notes = "OUAK mods $Tag." }
$Notes += "`n`nOUAK_Mods.zip SHA-256: $hash"
gh release create $Tag $zip --repo nmlacker/OUAK-Mods --title "OUAK mods $Tag" --notes $Notes
if ($LASTEXITCODE -ne 0) { throw "gh release create failed" }
"published $Tag ($hash)"
