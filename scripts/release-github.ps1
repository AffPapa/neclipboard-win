[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Draft', 'Publish')][string]$Mode,
    [Parameter(Mandatory)][ValidatePattern('^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$')][string]$Tag,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string]$Repository,
    [ValidatePattern('^[0-9a-f]{40}$')][string]$ExpectedCommit,
    [ValidatePattern('^[0-9a-fA-F]{64}$')][string]$VerifiedSha256,
    [string]$AssetDirectory = (Join-Path $PSScriptRoot '../artifacts/release')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$assetName = 'NeClipboardWin-win-x64.exe'
$licenseAssetName = 'NeClipboardWin-LICENSES.txt'
$expectedAssets = @($assetName, "$assetName.sha256", 'release-manifest.json', $licenseAssetName)

function Invoke-Gh([string[]]$Arguments) {
    $output = @(& gh @Arguments)
    if ($LASTEXITCODE -ne 0) { throw "GitHub CLI operation failed ($($Arguments[0])) with exit code $LASTEXITCODE." }
    return ($output -join "`n")
}
function Read-VerifiedManifest([string]$Directory) {
    $manifest = Get-Content -LiteralPath (Join-Path $Directory 'release-manifest.json') -Raw | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or $manifest.assetName -ne $assetName -or
        $manifest.tag -ne $Tag -or $manifest.version -ne $Tag.Substring(1) -or
        $manifest.target -ne 'win-x64' -or $manifest.sourceTreeDirty -ne $false -or
        $manifest.sourceCommit -notmatch '^[0-9a-f]{40}$' -or $manifest.sha256 -notmatch '^[0-9a-f]{64}$') {
        throw 'Release manifest is not a clean, committed Windows x64 release of the requested version.'
    }
    if ($manifest.licenseAssetName -ne $licenseAssetName -or $manifest.licenseSha256 -notmatch '^[0-9a-f]{64}$') {
        throw 'Release manifest must identify the distribution license bundle.'
    }
    $licensePath = Join-Path $Directory $licenseAssetName
    if ((Get-FileHash -LiteralPath $licensePath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $manifest.licenseSha256 -or
        (Get-Item -LiteralPath $licensePath).Length -ne $manifest.licenseSizeBytes) {
        throw 'Distribution license bundle hash or size differs from the manifest.'
    }
    $exe = Join-Path $Directory $assetName
    $actualHash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $manifest.sha256 -or (Get-Item -LiteralPath $exe).Length -ne $manifest.sizeBytes) {
        throw 'EXE hash or size differs from the release manifest.'
    }
    $checksum = ([IO.File]::ReadAllText("$exe.sha256")).Trim()
    if ($checksum -cne "$actualHash *$assetName") { throw 'Checksum file does not match the final EXE.' }
    $fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion
    if ($fileVersion -ne "$($manifest.version).0") { throw 'EXE file version differs from requested release.' }
    $signatureStatus = [string](Get-AuthenticodeSignature -LiteralPath $exe).Status
    if ($signatureStatus -notin @('NotSigned', 'Valid') -or $signatureStatus -ne $manifest.signatureStatus) {
        throw 'Authenticode status differs from the release manifest.'
    }
    return $manifest
}
function Assert-TagCommit([string]$Commit) {
    $refs = @(Invoke-Gh @('api', "repos/$Repository/git/matching-refs/tags/$Tag") | ConvertFrom-Json)
    $exact = @($refs | Where-Object { $_.ref -eq "refs/tags/$Tag" })
    if ($exact.Count -gt 0) {
        $resolved = Invoke-Gh @('api', "repos/$Repository/commits/$Tag", '--jq', '.sha')
        if ($resolved -ne $Commit) { throw 'Existing release tag points to a different commit. Never overwrite it.' }
    }
}
function Download-Draft([string]$Directory) {
    New-Item -ItemType Directory -Path $Directory -Force | Out-Null
    foreach ($name in $expectedAssets) {
        $null = Invoke-Gh @('release', 'download', $Tag, '--repo', $Repository, '--pattern', $name, '--dir', $Directory)
    }
}

Push-Location $repositoryRoot
try {
    if ($Mode -eq 'Draft') {
        if (-not $ExpectedCommit) { throw 'Draft creation requires ExpectedCommit.' }
        $manifest = Read-VerifiedManifest $AssetDirectory
        if ($manifest.sourceCommit -ne $ExpectedCommit) { throw 'Assets were not built from the selected workflow commit.' }
        $existingTags = (Invoke-Gh @('api', '--paginate', "repos/$Repository/releases", '--jq', '.[].tag_name')) -split "`n"
        if ($existingTags -contains $Tag) { throw 'This release already exists. Inspect it; do not replace its files automatically.' }
        Assert-TagCommit $ExpectedCommit
        $notes = Join-Path $AssetDirectory 'RELEASE-NOTES.md'
        if (-not (Test-Path -LiteralPath $notes -PathType Leaf)) { throw 'Version-specific release notes are required.' }
        $assets = @($expectedAssets | ForEach-Object { Join-Path $AssetDirectory $_ })
        $null = Invoke-Gh (@('release', 'create', $Tag) + $assets + @('--repo', $Repository,
            '--draft', '--latest=false', '--target', $ExpectedCommit, '--title', "NeClipboard Win $Tag", '--notes-file', $notes))
    }
    elseif (-not $VerifiedSha256) { throw 'Publishing requires the SHA-256 of the EXE accepted by desktop and upgrade testing.' }

    $release = Invoke-Gh @('release', 'view', $Tag, '--repo', $Repository, '--json', 'isDraft,targetCommitish,tagName,assets,url') | ConvertFrom-Json
    if (-not $release.isDraft -or $release.tagName -ne $Tag) { throw 'Only an existing draft can be processed. Published releases are immutable here.' }
    $actualNames = @($release.assets | ForEach-Object { $_.name } | Sort-Object)
    if (($actualNames -join '|') -ne (($expectedAssets | Sort-Object) -join '|')) { throw 'Draft contains missing or unexpected assets.' }
    $downloadDir = Join-Path $repositoryRoot ('artifacts/download-check-' + [guid]::NewGuid().ToString('N'))
    Download-Draft $downloadDir
    $downloadedManifest = Read-VerifiedManifest $downloadDir
    if ($release.targetCommitish -ne $downloadedManifest.sourceCommit) { throw 'Draft target differs from the manifest commit.' }
    Assert-TagCommit $downloadedManifest.sourceCommit
    if ($Mode -eq 'Draft') {
        if ($downloadedManifest.sha256 -ne $manifest.sha256) { throw 'Uploaded asset differs from the locally tested file.' }
        Write-Host "PASS Draft uploaded and downloaded unchanged: $($release.url)"
        Write-Host "Desktop acceptance must use this exact SHA-256: $($downloadedManifest.sha256)"
        return
    }
    if ($downloadedManifest.sha256 -ne $VerifiedSha256.ToLowerInvariant()) { throw 'Draft EXE differs from the accepted desktop-test file.' }
    & ./tests/run-isolated-smoke.ps1 -ExecutablePath (Join-Path $downloadDir $assetName)
    $null = Invoke-Gh @('release', 'edit', $Tag, '--repo', $Repository, '--draft=false', '--latest')
    $published = Invoke-Gh @('release', 'view', $Tag, '--repo', $Repository, '--json', 'isDraft,url') | ConvertFrom-Json
    if ($published.isDraft) { throw 'GitHub still reports a draft after the publish operation.' }
    $tagCommit = Invoke-Gh @('api', "repos/$Repository/commits/$Tag", '--jq', '.sha')
    if ($tagCommit -ne $downloadedManifest.sourceCommit) { throw 'Published tag does not resolve to the tested commit.' }

    # This HTTP request has no token. A private or inaccessible download must fail.
    $publicUrl = "https://github.com/$Repository/releases/download/$Tag/$assetName"
    $publicExe = Join-Path $downloadDir 'anonymous-download.exe'
    Invoke-WebRequest -Uri $publicUrl -OutFile $publicExe -MaximumRedirection 5
    if ((Get-FileHash -LiteralPath $publicExe -Algorithm SHA256).Hash -ne $VerifiedSha256) {
        throw 'Anonymous download hash differs from the accepted EXE. Inspect the public release immediately.'
    }
    $publicLicenseUrl = "https://github.com/$Repository/releases/download/$Tag/$licenseAssetName"
    $publicLicense = Join-Path $downloadDir 'anonymous-licenses.txt'
    Invoke-WebRequest -Uri $publicLicenseUrl -OutFile $publicLicense -MaximumRedirection 5
    if ((Get-FileHash -LiteralPath $publicLicense -Algorithm SHA256).Hash.ToLowerInvariant() -ne $downloadedManifest.licenseSha256 -or
        (Get-Item -LiteralPath $publicLicense).Length -ne $downloadedManifest.licenseSizeBytes) {
        throw 'Anonymous license bundle download differs from the release manifest.'
    }
    & ./tests/run-isolated-smoke.ps1 -ExecutablePath $publicExe
    Write-Host "PASS Published release and anonymous download verified: $($published.url)"
    Write-Host "Download: $publicUrl"
    Write-Host "Licenses: $publicLicenseUrl"
}
finally { Pop-Location }
