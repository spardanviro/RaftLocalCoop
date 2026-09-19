# ============================================================
# sync-to-rml.ps1
# One-way sync UMM(SplitScreen) -> RML(LocalCoop) shared logic,
# fix LocalCoop.csproj compile list, pack .rmod via build.bat, deploy to Raft\mods.
#
# Usage (after editing SplitScreen mainline):
#   powershell -ExecutionPolicy Bypass -File .\sync-to-rml.ps1
#
# Two projects are 99% identical; only the entry layer differs (see $skipFiles).
# Shared logic (Patches/Runtime/Main.P2*/Main.Rendering ...) is copied UMM -> RML.
# Pack uses Start-Process build.bat (Huorong allows it once its folder is trusted).
# (English-only on purpose: avoids PS5.1 GBK/UTF-8 mojibake on .ps1.)
# ============================================================
$ErrorActionPreference = 'Stop'
$bs = [char]92

$srcRoot   = 'C:\Users\Nero\Desktop\RaftMod\SplitScreen'
$dstRoot   = 'C:\Users\Nero\Desktop\RaftMod\LocalCoop\LocalCoop\LocalCoop'
$solDir    = 'C:\Users\Nero\Desktop\RaftMod\LocalCoop\LocalCoop'
$dstCsproj = Join-Path $dstRoot 'LocalCoop.csproj'
$buildBat  = Join-Path $solDir 'build.bat'
$rmodPath  = Join-Path $solDir 'LocalCoop.rmod'
$modsDir   = Join-Path ${env:ProgramFiles(x86)} 'Steam\steamapps\common\Raft\mods'
$deployTgt = Join-Path $modsDir 'LocalCoop.rmod'

# Entry-layer/RML-specific files that must keep LocalCoop's own version: never overwrite or prune
$skipFiles   = @('Main.cs','Main.State.cs','LocalCoop.cs','UmmCompat.cs')
# Directories to exclude from sync/scan
$excludeDirs = @('bin','obj','Properties','.vs')

function Test-PathSegments($full, $dirs) {
    $segs = $full.Split($bs)
    foreach ($d in $dirs) { if ($segs -contains $d) { return $true } }
    return $false
}

Write-Host '=== 1) Sync shared .cs (SplitScreen -> LocalCoop) ===' -ForegroundColor Cyan
$synced = 0; $unchanged = 0
$srcBase = $srcRoot.Length + 1
Get-ChildItem $srcRoot -Recurse -File -Filter *.cs |
    Where-Object { -not (Test-PathSegments $_.FullName $excludeDirs) } |
    ForEach-Object {
        $rel = $_.FullName.Substring($srcBase)
        if ($skipFiles -contains $_.Name) { return }
        $dstPath = Join-Path $dstRoot $rel
        $dstDir  = Split-Path $dstPath -Parent
        if (-not (Test-Path $dstDir)) { New-Item -ItemType Directory -Path $dstDir -Force | Out-Null }
        $needCopy = $true
        if (Test-Path $dstPath) {
            if ((Get-FileHash $_.FullName -Algorithm MD5).Hash -eq (Get-FileHash $dstPath -Algorithm MD5).Hash) { $needCopy = $false }
        }
        if ($needCopy) { Copy-Item $_.FullName $dstPath -Force; $synced++; Write-Host ('  + ' + $rel) -ForegroundColor Green }
        else { $unchanged++ }
    }
Write-Host ('  synced {0}, unchanged {1}; skipped entry files: {2}' -f $synced, $unchanged, ($skipFiles -join ', ')) -ForegroundColor Yellow

Write-Host '=== 1b) Prune stale shared .cs removed from SplitScreen ===' -ForegroundColor Cyan
$removed = 0
$dstBase = $dstRoot.Length + 1
Get-ChildItem $dstRoot -Recurse -File -Filter *.cs |
    Where-Object { -not (Test-PathSegments $_.FullName $excludeDirs) } |
    ForEach-Object {
        $rel = $_.FullName.Substring($dstBase)
        if ($skipFiles -contains $_.Name) { return }
        $srcPath = Join-Path $srcRoot $rel
        if (-not (Test-Path $srcPath)) {
            Remove-Item $_.FullName -Force
            $removed++
            Write-Host ('  - ' + $rel) -ForegroundColor DarkYellow
        }
    }
Write-Host ('  removed stale shared files: {0}' -f $removed) -ForegroundColor Yellow

Write-Host '=== 2) Fix LocalCoop.csproj compile list ===' -ForegroundColor Cyan
$csproj = [IO.File]::ReadAllText($dstCsproj)
$dstBase = $dstRoot.Length + 1
$compileMatches = [regex]::Matches($csproj, '^\s*<Compile Include="([^"]+\.cs)" />\s*\r?$', 'Multiline')
$removedCompile = 0
foreach ($cm in $compileMatches) {
    $rel = $cm.Groups[1].Value
    if (-not (Test-Path (Join-Path $dstRoot $rel))) {
        $csproj = $csproj.Replace($cm.Value, '')
        $removedCompile++
    }
}
if ($removedCompile -gt 0) {
    [IO.File]::WriteAllText($dstCsproj, $csproj, (New-Object System.Text.UTF8Encoding($true)))
    Write-Host ('  removed {0} stale Compile entries' -f $removedCompile) -ForegroundColor DarkYellow
}
$missing = @()
Get-ChildItem $dstRoot -Recurse -File -Filter *.cs |
    Where-Object { -not (Test-PathSegments $_.FullName $excludeDirs) } |
    ForEach-Object {
        $rel = $_.FullName.Substring($dstBase)
        if (-not $csproj.Contains('Include="' + $rel + '"')) { $missing += $rel }
    }
if ($missing.Count -gt 0) {
    $insert = ($missing | ForEach-Object { '    <Compile Include="' + $_ + '" />' }) -join "`r`n"
    $m = [regex]::Match($csproj, '    <Compile Include="[^"]+" />')
    if ($m.Success) {
        $csproj = $csproj.Insert($m.Index, $insert + "`r`n")
        [IO.File]::WriteAllText($dstCsproj, $csproj, (New-Object System.Text.UTF8Encoding($true)))
        Write-Host ('  added {0} Compile entries' -f $missing.Count) -ForegroundColor Green
    } else { Write-Host '  !! Compile anchor not found' -ForegroundColor Red }
} else { Write-Host '  csproj already up to date' -ForegroundColor Yellow }

Write-Host '=== 3) Pack .rmod via build.bat ===' -ForegroundColor Cyan
if (Test-Path $rmodPath) { Remove-Item $rmodPath -Force }
Start-Process -FilePath $buildBat -WorkingDirectory $solDir -Wait -NoNewWindow -RedirectStandardOutput "$env:TEMP\bb_out.txt" -RedirectStandardError "$env:TEMP\bb_err.txt"
Start-Sleep -Seconds 3
if (Test-Path $rmodPath) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $z = [System.IO.Compression.ZipFile]::OpenRead($rmodPath)
    $hasBee = [bool]($z.Entries | Where-Object { $_.FullName -like '*BeeHive*' })
    $cs = ($z.Entries | Where-Object { $_.FullName -like '*.cs' }).Count
    $z.Dispose()
    Write-Host ('  packed -> {0} bytes, {1} .cs, BeeHive={2}' -f (Get-Item $rmodPath).Length, $cs, $hasBee) -ForegroundColor Green
} else { Write-Host '  !! build.bat did not produce .rmod (Huorong may have blocked it)' -ForegroundColor Red; exit 1 }

Write-Host '=== 4) Deploy to Raft\mods ===' -ForegroundColor Cyan
$raft = Get-Process -Name '*Raft*' -ErrorAction SilentlyContinue
if ($raft) {
    Write-Host '  !! Raft is running, exit it and re-run to deploy.' -ForegroundColor Red
} else {
    Copy-Item $rmodPath $deployTgt -Force
    $ok = (Test-Path $deployTgt) -and ((Get-FileHash $rmodPath -Algorithm MD5).Hash -eq (Get-FileHash $deployTgt -Algorithm MD5).Hash)
    Write-Host ('  deployed -> {0}  (MD5 match: {1})' -f $deployTgt, $ok) -ForegroundColor Green
}

Write-Host '=== Done. Restart Raft for changes to take effect. ===' -ForegroundColor Cyan
