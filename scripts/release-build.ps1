[CmdletBinding()]
param(
    [string]$Tag,
    [string]$SourceCommit = 'uncommitted',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/release'),
    [string]$DotnetPath = 'dotnet',
    [switch]$RequireReleaseNotes
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $repositoryRoot
try {
    function Invoke-Dotnet([string[]]$Arguments) {
        & $DotnetPath @Arguments
        if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments[0]) failed with exit code $LASTEXITCODE." }
    }
    [xml]$project = Get-Content -LiteralPath ./NeClipboardWin.csproj -Raw
    $version = [string]($project.Project.PropertyGroup.Version | Select-Object -First 1)
    if (-not $Tag) { $Tag = "v$version" }
    if ($Tag -notmatch '^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$' -or $Tag.Substring(1) -ne $version) {
        throw 'Tag must be vX.Y.Z and match the project version.'
    }
    if ($SourceCommit -ne 'uncommitted' -and $SourceCommit -notmatch '^[0-9a-f]{40}$') {
        throw 'SourceCommit must be a full Git commit SHA or uncommitted for local builds.'
    }
    $dirty = @(& git status --porcelain --untracked-files=normal)
    if ($LASTEXITCODE -ne 0) { throw 'Could not inspect source tree state.' }
    if ($RequireReleaseNotes -and ($SourceCommit -eq 'uncommitted' -or $dirty.Count -ne 0)) {
        throw 'Public release assets require a committed, clean source tree.'
    }
    if ($SourceCommit -ne 'uncommitted') {
        $headCommit = & git rev-parse HEAD
        if ($LASTEXITCODE -ne 0 -or $headCommit -ne $SourceCommit) { throw 'SourceCommit does not match checked-out HEAD.' }
    }
    $notesPath = Join-Path $repositoryRoot "docs/releases/$Tag.md"
    if ($RequireReleaseNotes -and -not (Test-Path -LiteralPath $notesPath)) { throw "Version-specific notes are missing: docs/releases/$Tag.md" }
    $releaseDir = [IO.Path]::GetFullPath($OutputDirectory)
    if (Test-Path -LiteralPath $releaseDir) {
        if (@(Get-ChildItem -LiteralPath $releaseDir -Force).Count -gt 0) { throw 'Output directory must be empty; never overwrite tested release assets.' }
    }
    New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null
    $publishDir = Join-Path $repositoryRoot ("artifacts/build-" + [guid]::NewGuid().ToString('N'))

    Invoke-Dotnet @('build', './NeClipboardWin.csproj', '-c', 'Release', '-warnaserror', '-p:RestoreLockedMode=true')
    Invoke-Dotnet @('build', './tests/NeClipboardWin.Verification/NeClipboardWin.Verification.csproj', '-c', 'Release', '-warnaserror')
    Invoke-Dotnet @('run', '--project', './tests/NeClipboardWin.Verification/NeClipboardWin.Verification.csproj', '-c', 'Release', '--no-build')
    Invoke-Dotnet @('publish', './NeClipboardWin.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:PublishTrimmed=false',
        '-p:DebugType=None', '-p:DebugSymbols=false', '-p:ContinuousIntegrationBuild=true', '-p:RestoreLockedMode=true', '-o', $publishDir)

    $assetName = 'NeClipboardWin-win-x64.exe'
    $assetPath = Join-Path $releaseDir $assetName
    Copy-Item -LiteralPath (Join-Path $publishDir 'NeClipboardWin.exe') -Destination $assetPath
    $fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($assetPath).FileVersion
    if ($fileVersion -ne "$version.0") { throw 'Published executable file version differs from project version.' }
    & ./tests/run-isolated-smoke.ps1 -ExecutablePath $assetPath
    $signature = Get-AuthenticodeSignature -LiteralPath $assetPath
    if ([string]$signature.Status -notin @('NotSigned', 'Valid')) { throw "Unexpected executable signature status: $($signature.Status)" }
    $sha256 = (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText("$assetPath.sha256", "$sha256 *$assetName`n", [Text.UTF8Encoding]::new($false))
    $licenses = & ./scripts/release-notices.ps1 -OutputDirectory $releaseDir
    $sdk = & $DotnetPath --version
    if ($LASTEXITCODE -ne 0) { throw 'Could not determine .NET SDK version.' }
    $manifest = [ordered]@{
        schemaVersion = 1
        version = $version
        tag = $Tag
        sourceCommit = $SourceCommit
        sourceTreeDirty = ($dirty.Count -ne 0)
        builtAtUtc = [DateTime]::UtcNow.ToString('o')
        publishedAtUtc = $null
        target = 'win-x64'
        requiredOs = 'Windows 11 x64'
        targetFramework = [string]$project.Project.PropertyGroup.TargetFramework
        sdkVersion = [string]$sdk
        assetName = $assetName
        sizeBytes = (Get-Item -LiteralPath $assetPath).Length
        sha256 = $sha256
        signatureStatus = [string]$signature.Status
        licenseAssetName = $licenses.assetName
        licenseSha256 = $licenses.sha256
        licenseSizeBytes = $licenses.sizeBytes
        siteUrl = 'https://neclipboard-win.mail782861.chatgpt.site'
        changelogUrl = 'https://neclipboard-win.mail782861.chatgpt.site/changelog'
    }
    [IO.File]::WriteAllText((Join-Path $releaseDir 'release-manifest.json'), ($manifest | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
    $notes = if (Test-Path -LiteralPath $notesPath) { [IO.File]::ReadAllText($notesPath) } else { "# NeClipboard Win $Tag`n`nLocal verification build; not a public release." }
    $signatureNote = if ($signature.Status -eq 'Valid') { 'EXE имеет действительную подпись Authenticode.' } else { 'EXE не подписан сертификатом издателя; Windows может показать предупреждение SmartScreen. SHA-256 не заменяет подпись издателя.' }
    $notes += "`n`n---`nWindows 11 x64. Один self-contained EXE, установка .NET не требуется.`n`n$signatureNote`n`nSHA-256: $sha256`n`nSource commit: $SourceCommit`n`nСайт: https://neclipboard-win.mail782861.chatgpt.site`n"
    [IO.File]::WriteAllText((Join-Path $releaseDir 'RELEASE-NOTES.md'), $notes, [Text.UTF8Encoding]::new($false))
    Write-Host "PASS Release assets: $releaseDir"
    Write-Host "SHA-256: $sha256"
    Write-Host 'Smoke covers isolated startup only. Desktop, upgrade and visual acceptance remain required before publication.'
}
finally { Pop-Location }
