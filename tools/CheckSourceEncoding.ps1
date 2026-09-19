param(
    [string]$Root = "SplitScreen"
)

$ErrorActionPreference = "Stop"

$utf8Strict = [System.Text.UTF8Encoding]::new($false, $true)
$suspiciousChars = @(
    [char]0xFFFD, # replacement char
    [char]0x00C3, # A with tilde, common UTF-8-as-Latin1 marker
    [char]0x00C2  # A with circumflex, common UTF-8-as-Latin1 marker
)
$suspiciousStrings = @(
    [string]([char]0x00E2) + [string]([char]0x20AC),
    [string]([char]0x9234) + [string]([char]0xA5),
    [string]([char]0x6DBF) + [string]([char]0x95C6),
    [string]([char]0x934F) + [string]([char]0x5B58),
    [string]([char]0x95C5) + [string]([char]0x5C4F),
    [string]([char]0x9359) + [string]([char]0x56DE)
)
$bad = New-Object System.Collections.Generic.List[string]

Get-ChildItem -Path $Root -Recurse -File -Include *.cs | ForEach-Object {
    $path = $_.FullName
    try {
        $bytes = [System.IO.File]::ReadAllBytes($path)
        $text = $utf8Strict.GetString($bytes)
    }
    catch {
        $bad.Add("$path : invalid UTF-8 ($($_.Exception.Message))")
        return
    }

    $lines = $text -split "`r?`n"
    for ($i = 0; $i -lt $lines.Length; $i++) {
        $line = $lines[$i]
        $hit = $false
        foreach ($ch in $suspiciousChars) {
            if ($line.IndexOf($ch) -ge 0) { $hit = $true; break }
        }
        if (-not $hit) {
            foreach ($s in $suspiciousStrings) {
                if ($line.Contains($s)) { $hit = $true; break }
            }
        }
        if ($hit) {
            $bad.Add("${path}:$($i + 1) : possible mojibake: $($lines[$i].Trim())")
        }
    }
}

if ($bad.Count -gt 0) {
    $bad | ForEach-Object { Write-Host $_ }
    throw "Source encoding check failed: $($bad.Count) suspicious line(s)."
}

Write-Host "Source encoding check passed."
