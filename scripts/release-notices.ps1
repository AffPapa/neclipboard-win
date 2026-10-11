[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$outputRoot = (Resolve-Path -LiteralPath $OutputDirectory).Path
$assetName = 'NeClipboardWin-LICENSES.txt'
$sources = @(
    'LICENSE',
    'THIRD-PARTY-NOTICES.md',
    'docs/legal/DOTNET-RUNTIME-LICENSE.txt',
    'docs/legal/DOTNET-RUNTIME-THIRD-PARTY-NOTICES.txt',
    'docs/legal/WINDOWSDESKTOP-LICENSE.txt',
    'docs/legal/WPF-THIRD-PARTY-NOTICES.txt',
    'docs/legal/WINFORMS-THIRD-PARTY-NOTICES.txt'
)
$text = [Text.StringBuilder]::new()
$null = $text.Append("NeClipboard Win — distribution licenses and notices`n")
foreach ($relativePath in $sources) {
    $sourcePath = Join-Path $repositoryRoot $relativePath
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        throw "Required distribution notice is missing: $relativePath"
    }
    $null = $text.Append("`n----- $relativePath -----`n`n")
    $null = $text.Append([IO.File]::ReadAllText($sourcePath).Replace("`r`n", "`n").TrimEnd())
    $null = $text.Append("`n")
}
$assetPath = Join-Path $outputRoot $assetName
$content = $text.ToString()
if (Test-Path -LiteralPath $assetPath) {
    if ([IO.File]::ReadAllText($assetPath) -cne $content) {
        throw 'Existing distribution notices differ; do not overwrite accepted release assets.'
    }
}
else {
    [IO.File]::WriteAllText($assetPath, $content, [Text.UTF8Encoding]::new($false))
}
[pscustomobject]@{
    assetName = $assetName
    sha256 = (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash.ToLowerInvariant()
    sizeBytes = (Get-Item -LiteralPath $assetPath).Length
}
