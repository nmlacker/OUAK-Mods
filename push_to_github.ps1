<#
  Pushes the OUAK mods source (this folder) to a PRIVATE GitHub repository.

  First run : signs you in to GitHub in your browser, creates the private repo, commits and pushes.
  Later runs: commits whatever changed and pushes it.

  Usage:  double-click push_to_github.cmd
     or:  powershell -ExecutionPolicy Bypass -File push_to_github.ps1 [-RepoName OUAK-Mods] [-Message "what changed"] [-Yes]

  Needs Git and the GitHub CLI (gh); offers to install them with winget if they are missing.
  No password or token is typed into this script: GitHub's own sign-in page handles it.
  It refuses to push to a repository that is not private.
#>
param(
    [string]$RepoName = 'OUAK-Mods',
    [string]$Message = '',
    [switch]$Yes
)

# Native tools (git, gh) report through exit codes; every call below is checked by hand.
$ErrorActionPreference = 'Continue'
$root = $PSScriptRoot

function Say($text) { Write-Host ''; Write-Host "== $text" -ForegroundColor Cyan }

function Fail($text) {
    Write-Host ''
    Write-Host "STOPPED: $text" -ForegroundColor Red
    exit 1
}

function Confirm-Step($question) {
    if ($Yes) { return $true }
    $answer = Read-Host "$question [Y/n]"
    return ($answer -eq '' -or $answer -match '^(y|yes)$')
}

function Need-Tool($exe, $wingetId, $url) {
    if (Get-Command $exe -ErrorAction SilentlyContinue) { return }
    Write-Host "$exe is not installed."
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) { Fail "Install it from $url, then run this again." }
    if (-not (Confirm-Step "Install it now with winget ($wingetId)?")) { Fail "Install it from $url, then run this again." }
    winget install --id $wingetId -e --source winget --accept-source-agreements --accept-package-agreements
    # A fresh install is on the machine's PATH but not yet on this window's.
    $env:Path = $env:Path + ';' + [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
    if (-not (Get-Command $exe -ErrorAction SilentlyContinue)) { Fail "$exe was installed but this window can't see it yet. Close the window and run this again." }
}

Set-Location -LiteralPath $root
if (-not (Test-Path -LiteralPath (Join-Path $root 'OUAK.sln'))) { Fail 'OUAK.sln is not next to this script: run it from the ModMenuSource folder.' }

# ---------------------------------------------------------------------------------------------
Say 'Checking tools'
Need-Tool 'git' 'Git.Git' 'https://git-scm.com/download/win'
Need-Tool 'gh' 'GitHub.cli' 'https://cli.github.com'
Write-Host 'git and gh are available.'

# ---------------------------------------------------------------------------------------------
Say 'GitHub sign-in'
gh auth status --hostname github.com 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host 'Your browser will open. Sign in there and type the one-time code shown below.'
    gh auth login --hostname github.com --git-protocol https --web
    if ($LASTEXITCODE -ne 0) { Fail 'GitHub sign-in did not complete.' }
}
# Lets git push with the account gh is signed in to.
gh auth setup-git --hostname github.com
$login = (gh api user --jq .login)
if ($LASTEXITCODE -ne 0 -or -not $login) { Fail 'Could not read your GitHub account name.' }
$login = "$login".Trim()
Write-Host "Signed in as $login"

# ---------------------------------------------------------------------------------------------
Say 'Preparing the local repository'
$fresh = -not (Test-Path -LiteralPath (Join-Path $root '.git'))
if ($fresh) {
    git init --quiet
    if ($LASTEXITCODE -ne 0) { Fail "git init failed. If Windows denied access, right-click push_to_github.cmd and choose 'Run as administrator'." }
}
# Git refuses folders owned by another Windows account ("dubious ownership"), which happens under Program Files.
$probe = (git rev-parse --git-dir 2>&1 | Out-String)
if ($LASTEXITCODE -ne 0) {
    if ($probe -match 'dubious ownership') {
        Write-Host 'Telling git this folder is safe to use (safe.directory).'
        git config --global --add safe.directory ($root -replace '\\', '/')
        git rev-parse --git-dir 2>&1 | Out-Null
    }
    if ($LASTEXITCODE -ne 0) { Fail "Git can't use this folder: $probe" }
}
if ($fresh) {
    git symbolic-ref HEAD refs/heads/main
    if ($LASTEXITCODE -ne 0) { Fail 'Could not name the branch main.' }
}
# A commit needs an author; use the GitHub account (its no-reply address) unless git already has one.
if (-not (git config user.name)) { git config user.name $login }
if (-not (git config user.email)) {
    $id = "$(gh api user --jq .id)".Trim()
    git config user.email "${id}+${login}@users.noreply.github.com"
}

# ---------------------------------------------------------------------------------------------
Say 'Collecting files'
git add -A
if ($LASTEXITCODE -ne 0) { Fail 'git add failed.' }
$tracked = @(git -c core.quotePath=false ls-files)
if ($tracked.Count -eq 0) { Fail 'No files to upload.' }

# Second line of defence behind .gitignore: nothing compiled, packaged or oversized goes up.
$bad = @($tracked | Where-Object {
    $_ -match '\.(dll|exe|pdb|zip)$' -or $_ -match '^(dist|Samples/ImGuiTest)/' -or $_ -match '(^|/)(bin|obj)/'
})
$big = @($tracked | Where-Object {
    $item = Get-Item -LiteralPath (Join-Path $root $_) -ErrorAction SilentlyContinue
    $item -and $item.Length -gt 50MB
})
if ($bad.Count -gt 0 -or $big.Count -gt 0) {
    ($bad + $big) | Select-Object -Unique | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
    Fail 'These files should not be uploaded (compiled, packaged, excluded, or over 50 MB). Fix .gitignore, run "git rm --cached <file>" for each, then run this again. Nothing was uploaded.'
}

$groups = $tracked | Group-Object { if ($_ -match '/') { ($_ -split '/')[0] + '/' } else { '(top level)' } } | Sort-Object Name
foreach ($g in $groups) { Write-Host ('  {0,5} files  {1}' -f $g.Count, $g.Name) }
Write-Host ('  {0,5} files in total' -f $tracked.Count)

git diff --cached --quiet
$hasChanges = ($LASTEXITCODE -ne 0)
git rev-parse --verify --quiet HEAD 2>$null | Out-Null
$hasCommit = ($LASTEXITCODE -eq 0)
if (-not $hasChanges -and -not $hasCommit) { Fail 'Nothing to commit.' }

if (-not (Confirm-Step "Upload these to the PRIVATE repository $RepoName on GitHub?")) { Fail 'Cancelled. Nothing was uploaded.' }

if ($hasChanges) {
    if (-not $Message) {
        if ($hasCommit) { $Message = 'Update ' + (Get-Date -Format 'yyyy-MM-dd HH:mm') }
        else { $Message = 'Initial commit: OUAK mods source' }
    }
    git commit --quiet -m $Message
    if ($LASTEXITCODE -ne 0) { Fail 'git commit failed.' }
    Write-Host "Committed: $Message"
} else {
    Write-Host 'No changes since the last commit.'
}
$branch = "$(git rev-parse --abbrev-ref HEAD)".Trim()

# ---------------------------------------------------------------------------------------------
Say 'GitHub repository'
$slug = "$login/$RepoName"
$origin = (git remote get-url origin 2>$null)
if ($LASTEXITCODE -eq 0 -and $origin) {
    if ("$origin" -match 'github\.com[:/](.+?)(\.git)?$') { $slug = $Matches[1] }
    else { Fail "The remote 'origin' ($origin) is not a GitHub repository." }
} else {
    gh repo view $slug --json name 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) {
        Write-Host "$slug already exists; using it."
    } else {
        gh repo create $slug --private --description 'MelonLoader mods for Once Upon a KATAMARI - OUAK Modding API, Mod Menu, Multiplayer, Dash Bar, Song Selector'
        if ($LASTEXITCODE -ne 0) { Fail 'Could not create the repository.' }
    }
    git remote add origin "https://github.com/${slug}.git"
    if ($LASTEXITCODE -ne 0) { Fail 'Could not set the remote.' }
}
# The repo was made public on purpose (2026-11-12: the source is published with the releases and the website).
# The .gitignore and the check below still keep compiled, packaged and oversized files out.
$visibility = "$(gh repo view $slug --json visibility --jq .visibility 2>$null)".Trim()
if ($LASTEXITCODE -ne 0 -or -not $visibility) { Fail "Can't see $slug with this GitHub account. Nothing was uploaded." }
Write-Host "$slug is $($visibility.ToLower()): everything pushed is visible to everyone who can see it."

# ---------------------------------------------------------------------------------------------
Say 'Uploading'
git push -u origin $branch
if ($LASTEXITCODE -ne 0) { Fail 'git push failed.' }

# ---------------------------------------------------------------------------------------------
Say 'Checking the upload'
$local = "$(git rev-parse HEAD)".Trim()
$remote = ("$(git ls-remote origin "refs/heads/$branch")" -split '\s+')[0]
if (-not $remote -or $local -ne $remote) { Fail "The upload could not be confirmed (this PC: $local, GitHub: $remote)." }
$url = "$(gh repo view $slug --json url --jq .url)".Trim()
Write-Host ''
Write-Host "Done. $($tracked.Count) files are in the private repository:" -ForegroundColor Green
Write-Host "  $url"
Write-Host 'Run this again whenever you want to upload new changes.'
