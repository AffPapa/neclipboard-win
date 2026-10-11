[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Join-Path $PSScriptRoot '..'),
    [switch]$SkipHistory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repositoryPath = (Resolve-Path -LiteralPath $RepositoryRoot).Path
Push-Location $repositoryPath
try {
    function Invoke-Git([string[]]$Arguments) {
        $result = @(& git -c core.quotepath=false @Arguments)
        if ($LASTEXITCODE -ne 0) { throw "Git preflight command failed ($($Arguments[0]))." }
        return $result
    }
    $actualRoot = @(Invoke-Git @('rev-parse', '--show-toplevel'))[0]
    if ([IO.Path]::GetFullPath($actualRoot) -ne $repositoryPath) {
        throw 'Preflight must target the application repository root.'
    }
    # Reject generated/private files anywhere in the tree, including force-added files.
    $forbiddenPath = '(?i)(^|/)(bin|obj|\.vs|\.idea|TestResults|artifacts|node_modules)/|(^|/)\.env($|\.)|\.(pfx|p12|pem|key|snk|kdbx|neclipboard-backup|dat|exe|dll|pdb|zip|7z|msi|msix|dmp|tmp|bak)$|\.(dat|json)\.corrupt-|(^|/)(startup-error\.log|restore\.pending\.json|credentials?\.json|secrets?\.(json|ya?ml)|appsettings\..*local\.json)$'
    $patterns = @(
        '-----BEGIN [A-Z ]*PRIVATE KEY-----',
        'gh[pousr]_[A-Za-z0-9_]{20,}',
        'github_pat_[A-Za-z0-9_]{20,}',
        'sk-(?:proj-|svcacct-)?[A-Za-z0-9_-]{20,}',
        'art_v1_[A-Za-z0-9]{20,}',
        '(?:AKIA|ASIA)[0-9A-Z]{16}',
        '(?i)(?:api[_-]?key|client[_-]?secret|access[_-]?token|password)\s*[=:]\s*["''][A-Za-z0-9_+/=-]{24,}["'']',
        'https?://[^\s/:]+:[^\s/@]+@'
    )
    $scanner = [regex]::new(($patterns -join '|'), [Text.RegularExpressions.RegexOptions]::CultureInvariant,
        [TimeSpan]::FromSeconds(5))
    $maxBlobBytes = 16MB
    $seenBlobs = [Collections.Generic.HashSet[string]]::new()
    $scannedBlobs = 0
    function Test-PathPolicy([string]$Path, [string]$Scope) {
        if ($Path -match $forbiddenPath) { throw "Forbidden public file: $Scope / $Path" }
        if ($Path -match '[\r\n\x00]') { throw 'Control characters in a repository filename are not allowed.' }
    }
    function Test-Content([string]$Content, [string]$Label) {
        # Only report the location; never echo the possible secret or matched line.
        if ($scanner.IsMatch($Content)) { throw "Possible credential in $Label. Review locally; secret value was not printed." }
    }
    function Test-Blob([string]$Oid, [string]$Label) {
        if (-not $seenBlobs.Add($Oid)) { return }
        $size = [long](@(Invoke-Git @('cat-file', '-s', $Oid))[0])
        if ($size -gt $maxBlobBytes) { throw "Blob requires separate review (over 16 MiB): $Label" }
        $content = (Invoke-Git @('cat-file', '-p', $Oid)) -join "`n"
        Test-Content $content $Label
        $script:scannedBlobs++
    }
    foreach ($line in @(Invoke-Git @('ls-files', '--stage'))) {
        if ($line -notmatch '^(\d+) ([0-9a-f]+) (\d)\t(.+)$') { throw 'Cannot parse Git index entry.' }
        $mode, $oid, $stage, $path = $Matches[1], $Matches[2], $Matches[3], $Matches[4]
        Test-PathPolicy $path 'index'
        if ($mode -notin @('100644', '100755') -or $stage -ne '0') { throw "Unsupported or unresolved index entry: $path" }
        Test-Blob $oid "index:$path"
    }
    $workingFiles = @(@(Invoke-Git @('ls-files', '--cached', '--others', '--exclude-standard')) | Select-Object -Unique)
    foreach ($path in $workingFiles) {
        Test-PathPolicy $path 'working tree'
        $fullPath = Join-Path $repositoryPath $path
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) { continue }
        $file = Get-Item -LiteralPath $fullPath
        if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Linked source file needs review: $path" }
        if ($file.Length -gt $maxBlobBytes) { throw "Working file requires separate review (over 16 MiB): $path" }
        Test-Content ([IO.File]::ReadAllText($fullPath)) "working tree:$path"
    }
    if (-not $SkipHistory) {
        foreach ($path in @(Invoke-Git @('log', '--all', '--pretty=format:', '--name-only', '--diff-filter=ACMR'))) {
            if ($path) { Test-PathPolicy $path 'reachable history path' }
        }
        foreach ($line in @(Invoke-Git @('rev-list', '--objects', '--all'))) {
            $parts = $line -split ' ', 2
            if ($parts.Count -lt 2) {
                Test-Blob $parts[0] "history metadata ($($parts[0].Substring(0, 12)))"
                continue
            }
            $oid, $path = $parts[0], $parts[1]
            Test-PathPolicy $path 'reachable history'
            $type = @(Invoke-Git @('cat-file', '-t', $oid))[0]
            if ($type -eq 'blob') { Test-Blob $oid "history:$path ($($oid.Substring(0, 12)))" }
        }
    }
    Write-Host "PASS Public preflight: exact index blobs, $($workingFiles.Count) working files, $scannedBlobs unique blobs; history skipped=$([bool]$SkipHistory)."
    Write-Host 'Pattern scan is a guardrail, not a guarantee that all possible secrets are absent.'
}
finally { Pop-Location }
