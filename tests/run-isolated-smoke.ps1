param(
    [string]$ExecutablePath = (Join-Path $PSScriptRoot "..\bin\Release\net10.0-windows\win-x64\publish\NeClipboardWin.exe"),
    [ValidateSet("light", "dark")]
    [string[]]$Themes = @("light", "dark")
)

$ErrorActionPreference = "Stop"

$resolvedExecutable = (Resolve-Path -LiteralPath $ExecutablePath -ErrorAction Stop).Path
$tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar

foreach ($theme in $Themes) {
    $process = $null
    $qaDirectory = Join-Path $tempRoot ("NeClipboardWin-QA-" + [guid]::NewGuid().ToString("N"))
    try {
        New-Item -ItemType Directory -Path $qaDirectory -Force | Out-Null
        $process = Start-Process -FilePath $resolvedExecutable -ArgumentList @("--qa-isolated", "`"$qaDirectory`"", "--qa-theme", $theme) -WindowStyle Hidden -PassThru
        if (-not $process.WaitForExit(10000)) {
            Stop-Process -Id $process.Id -Force
            throw "Isolated NeClipboard QA process did not exit within 10 seconds."
        }
        if ($process.ExitCode -ne 0) {
            throw "Isolated NeClipboard QA process exited with code $($process.ExitCode)."
        }
        Write-Output "PASS  isolated $theme startup: process $($process.Id) exited cleanly without desktop integrations."
    }
    finally {
        if ($null -ne $process -and -not $process.HasExited) {
            Stop-Process -Id $process.Id -Force
        }
        if ($null -ne $process) { $process.Dispose() }
        if (Test-Path -LiteralPath $qaDirectory) {
            $resolvedQaDirectory = (Resolve-Path -LiteralPath $qaDirectory).Path
            if (-not $resolvedQaDirectory.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
                [IO.Path]::GetFileName($resolvedQaDirectory) -notmatch '^NeClipboardWin-QA-[0-9a-f]{32}$') {
                throw 'Refusing to remove a QA folder outside the validated temporary test path.'
            }
            Remove-Item -LiteralPath $resolvedQaDirectory -Recurse -Force
        }
    }
}
