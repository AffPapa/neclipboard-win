[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$scannerPath = Join-Path $PSScriptRoot 'public-preflight.ps1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('NeClipboard-preflight-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
    function Invoke-FixtureGit([string[]]$Arguments) {
        & git -C $testRoot -c user.name=Preflight-Test -c user.email=preflight@example.invalid @Arguments | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Fixture Git command failed.' }
    }
    function Write-Fixture([string]$Name, [string]$Content) {
        [IO.File]::WriteAllText((Join-Path $testRoot $Name), $Content, [Text.UTF8Encoding]::new($false))
    }
    function Expect-Rejection([string]$Name, [string]$ExpectedMessage) {
        try { & $scannerPath -RepositoryRoot $testRoot *> $null }
        catch {
            if ($_.Exception.Message -notlike "*$ExpectedMessage*") { throw "Wrong rejection for ${Name}: $($_.Exception.Message)" }
            Write-Host "PASS preflight regression: $Name"
            return
        }
        throw "Preflight accepted prohibited fixture: $Name"
    }
    Invoke-FixtureGit @('init', '-b', 'main', '--quiet')
    Write-Fixture 'safe.txt' 'Synthetic fixture; no private user data.'
    Invoke-FixtureGit @('add', 'safe.txt')
    Invoke-FixtureGit @('commit', '-m', 'Safe fixture', '--quiet')
    & $scannerPath -RepositoryRoot $testRoot

    # Construct a fake token at runtime; no credential-shaped literal is committed.
    $fakeToken = 'gh' + 'p_' + ('A' * 32)
    Write-Fixture 'untracked.txt' $fakeToken
    Expect-Rejection 'untracked credential-shaped content' 'working tree:untracked.txt'
    Remove-Item -LiteralPath (Join-Path $testRoot 'untracked.txt')
    Write-Fixture 'safe.txt' $fakeToken
    Invoke-FixtureGit @('add', 'safe.txt')
    Write-Fixture 'safe.txt' 'Worktree looks clean, index does not.'
    Expect-Rejection 'staged secret hidden by clean worktree' 'index:safe.txt'
    Invoke-FixtureGit @('add', 'safe.txt')

    Write-Fixture '.env.production' 'SYNTHETIC=true'
    Invoke-FixtureGit @('add', '--force', '.env.production')
    Expect-Rejection 'force-staged environment file' 'Forbidden public file'
    Invoke-FixtureGit @('rm', '--force', '.env.production')
    Write-Fixture 'download.exe' 'Synthetic file, not an executable.'
    Invoke-FixtureGit @('add', '--force', 'download.exe')
    Expect-Rejection 'binary outside output folders' 'Forbidden public file'
    Invoke-FixtureGit @('rm', '--force', 'download.exe')
    Write-Fixture 'settings.json.corrupt-fixture' 'Synthetic legacy settings, not private user data.'
    Invoke-FixtureGit @('add', '--force', 'settings.json.corrupt-fixture')
    Expect-Rejection 'quarantined legacy settings' 'Forbidden public file'
    Invoke-FixtureGit @('rm', '--force', 'settings.json.corrupt-fixture')

    Write-Fixture 'historic.txt' $fakeToken
    Invoke-FixtureGit @('add', 'historic.txt')
    Invoke-FixtureGit @('commit', '-m', 'Synthetic historical detector case', '--quiet')
    Invoke-FixtureGit @('rm', '--force', 'historic.txt')
    Invoke-FixtureGit @('commit', '-m', 'Remove historical fixture', '--quiet')
    Expect-Rejection 'removed secret still in reachable history' 'history:historic.txt'
    Write-Host 'PASS all public preflight regressions; only isolated synthetic repositories were changed.'
}
finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
        -not ([IO.Path]::GetFileName($resolved)).StartsWith('NeClipboard-preflight-')) {
        throw 'Refusing to remove a fixture outside the validated temporary test directory.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
