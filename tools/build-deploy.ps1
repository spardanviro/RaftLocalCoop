# ============================================================
# build-deploy.ps1
# Mirror LocalCoop (RML, primary) -> SplitScreen (UMM, mirror), build both,
# pack LocalCoop.rmod and deploy it to Raft\mods.
#
# Usage (from anywhere):
#   pwsh -File tools\build-deploy.ps1            # mirror + build + pack + deploy
#   pwsh -File tools\build-deploy.ps1 -NoDeploy  # mirror + build + pack only
#   pwsh -File tools\build-deploy.ps1 -NoMirror  # skip the mirror step
#
# The two trees are identical except for the entry layer ($entryFiles).
# Paths are resolved from this script's location, so the repo can be moved.
# (English-only on purpose: avoids PS5.1 GBK/UTF-8 mojibake on .ps1.)
# ============================================================
param(
    [switch]$NoDeploy,
    [switch]$NoMirror
)
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$lc   = Join-Path $root 'LocalCoop\LocalCoop\LocalCoop'
$ss   = Join-Path $root 'SplitScreen'
$rmod = Join-Path $root 'LocalCoop\LocalCoop\LocalCoop.rmod'
$mods = Join-Path ${env:ProgramFiles(x86)} 'Steam\steamapps\common\Raft\mods'

# Entry-layer files: each tree keeps its own version, never mirrored.
$entryFiles = @('Main.cs', 'Main.State.cs', 'LocalCoop.cs', 'UmmCompat.cs')
$skipDirs   = @('bin', 'obj', 'Properties', '.vs')

function Get-SourceFiles([string]$base) {
    Get-ChildItem $base -Recurse -File -Filter *.cs | Where-Object {
        $rel = $_.FullName.Substring($base.Length + 1)
        $top = $rel.Split('\')[0]
        $skipDirs -notcontains $top
    }
}

# ---------- 1) mirror LocalCoop -> SplitScreen ----------
if (-not $NoMirror) {
    Write-Host '=== 1) Mirror shared .cs (LocalCoop -> SplitScreen) ===' -ForegroundColor Cyan
    $copied = 0
    foreach ($f in Get-SourceFiles $lc) {
        if ($entryFiles -contains $f.Name) { continue }
        $rel = $f.FullName.Substring($lc.Length + 1)
        $dst = Join-Path $ss $rel
        $same = (Test-Path $dst) -and ((Get-FileHash $f.FullName).Hash -eq (Get-FileHash $dst).Hash)
        if (-not $same) {
            $dir = Split-Path $dst -Parent
            if (-not (Test-Path $dir)) { [IO.Directory]::CreateDirectory($dir) | Out-Null }
            [IO.File]::Copy($f.FullName, $dst, $true)
            $copied++
            Write-Host "  copied $rel"
        }
    }
    Write-Host "  $copied file(s) updated"

    # Report drift the mirror cannot fix on its own.
    $lcRel = Get-SourceFiles $lc | ForEach-Object { $_.FullName.Substring($lc.Length + 1) }
    $ssRel = Get-SourceFiles $ss | ForEach-Object { $_.FullName.Substring($ss.Length + 1) }
    $onlySs = $ssRel | Where-Object { $lcRel -notcontains $_ }
    if ($onlySs) { Write-Host "  WARNING: only in SplitScreen: $($onlySs -join ', ')" -ForegroundColor Yellow }

    foreach ($pair in @(@($lc, 'LocalCoop.csproj'), @($ss, 'SplitScreen.csproj'))) {
        $text = [IO.File]::ReadAllText((Join-Path $pair[0] $pair[1]))
        $inc  = [regex]::Matches($text, '<Compile Include="([^"]+)"') | ForEach-Object { $_.Groups[1].Value }
        $disk = Get-ChildItem $pair[0] -Recurse -File -Filter *.cs |
                Where-Object { $_.FullName.Substring($pair[0].Length + 1).Split('\')[0] -notin @('bin', 'obj', '.vs') } |
                ForEach-Object { $_.FullName.Substring($pair[0].Length + 1) }
        $missing = $disk | Where-Object { $inc -notcontains $_ }
        if ($missing) { throw "$($pair[1]) is missing <Compile Include> for: $($missing -join ', ')" }
    }
}

# ---------- 2) build both trees (compile check only) ----------
Write-Host '=== 2) Build ===' -ForegroundColor Cyan
foreach ($pair in @(@($lc, 'LocalCoop.csproj'), @($ss, 'SplitScreen.csproj'))) {
    Push-Location $pair[0]
    try { $out = & dotnet build $pair[1] -c Release '-p:PostBuildEvent=' 2>&1 }
    finally { Pop-Location }
    $bad = $out | Where-Object { $_ -match ' error | warning CS' } | Sort-Object -Unique
    if ($LASTEXITCODE -ne 0 -or $bad) {
        $bad | Select-Object -First 20 | ForEach-Object { Write-Host $_ -ForegroundColor Red }
        throw "$($pair[1]) did not build cleanly"
    }
    Write-Host "  $($pair[1]) OK"
}

# ---------- 3) pack LocalCoop.rmod (a zip of the sources; RML compiles it at load) ----------
Write-Host '=== 3) Pack .rmod ===' -ForegroundColor Cyan
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
if (Test-Path $rmod) { [IO.File]::Delete($rmod) }
$zip = [System.IO.Compression.ZipFile]::Open($rmod, [System.IO.Compression.ZipArchiveMode]::Create)
$count = 0
try {
    foreach ($f in Get-ChildItem $lc -Recurse -File) {
        $rel = $f.FullName.Substring($lc.Length + 1)
        if ($skipDirs -contains $rel.Split('\')[0]) { continue }
        if ($f.Extension -in @('.csproj', '.user', '.rmod')) { continue }
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $f.FullName, $rel.Replace('\', '/')) | Out-Null
        $count++
    }
}
finally { $zip.Dispose() }
Write-Host "  $count entries -> $rmod"

# ---------- 4) deploy ----------
if ($NoDeploy) { Write-Host 'Skipped deploy (-NoDeploy).'; return }
Write-Host '=== 4) Deploy ===' -ForegroundColor Cyan
if (-not (Test-Path $mods)) { throw "Raft mods folder not found: $mods" }
$target = Join-Path $mods 'LocalCoop.rmod'
[IO.File]::Copy($rmod, $target, $true)
$ok = (Get-FileHash $rmod).Hash -eq (Get-FileHash $target).Hash
Write-Host "  $target (hash match: $ok)"
if (Get-Process -Name 'Raft' -ErrorAction SilentlyContinue) {
    Write-Host '  Raft is running: restart it (or reload the mod in RML) to pick up the change.' -ForegroundColor Yellow
}
