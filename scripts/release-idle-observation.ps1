[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Executable,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$ExpectedSha256,
    [ValidateSet('Isolated', 'Desktop')][string]$QaMode = 'Isolated',
    [ValidateRange(30, 3600)][int]$IdleSeconds = 600,
    [Parameter(Mandatory)][string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$executablePath = (Resolve-Path -LiteralPath $Executable).Path
$actualHash = (Get-FileHash -LiteralPath $executablePath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualHash -ne $ExpectedSha256.ToLowerInvariant()) { throw 'Executable hash does not match the approved test artifact.' }
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $outputPath) { throw 'Use a new output directory for each observation.' }
New-Item -ItemType Directory -Path $outputPath | Out-Null
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
$profilePath = Join-Path $tempRoot ('NeClipboardWin-QA-idle-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $profilePath | Out-Null
$ownedProcess = $null
$points = [Collections.Generic.List[object]]::new()
$logicalProcessors = [Environment]::ProcessorCount

function Save-Json([string]$Name, $Value) {
    [IO.File]::WriteAllText((Join-Path $outputPath $Name), ($Value | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
}

function Read-Resources([Diagnostics.Process]$Process, [double]$ElapsedSeconds) {
    $Process.Refresh()
    if ($Process.HasExited) { throw 'The owned QA process exited during idle observation.' }
    [ordered]@{
        elapsedSeconds = $ElapsedSeconds
        privateBytes = $Process.PrivateMemorySize64
        workingSetBytes = $Process.WorkingSet64
        handles = $Process.HandleCount
        threads = $Process.Threads.Count
        cpuMilliseconds = $Process.TotalProcessorTime.TotalMilliseconds
    }
}

try {
    $os = Get-CimInstance Win32_OperatingSystem
    $cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
    Save-Json 'environment.json' ([ordered]@{
        startedAtUtc = [DateTime]::UtcNow.ToString('o')
        windowsVersion = $os.Version
        windowsBuild = $os.BuildNumber
        cpu = $cpu.Name.Trim()
        logicalProcessors = $logicalProcessors
        physicalMemoryBytes = (Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory
        executableSha256 = $actualHash
        executableVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($executablePath).FileVersion
        executableBytes = (Get-Item -LiteralPath $executablePath).Length
        qaMode = $QaMode
        requestedSeconds = $IdleSeconds
        method = 'One owned process; Start-Process Hidden; empty disposable profile; five-second warmup; process counters every five seconds. No UI automation, input, clipboard read/write or capture fixture from this observer.'
        limits = 'QA mode is not production acceptance. Desktop attempts real listener/hotkey/tray initialization but this counter observer does not independently verify successful registration; an existing hotkey owner may cause a conflict. Capture is gated to synthetic fixture content and startup registry writes are disabled. No useful interaction, capture load, sleep/resume, GDI/USER counters, IO events, wakeups, managed heap or long-term leak claim. Other workstation activity is uncontrolled. WPF may show its QA window despite the Hidden startup hint. Process is forcibly terminated at observation end; graceful shutdown is not tested.'
    })
    $modeArgument = if ($QaMode -eq 'Desktop') { '--qa-desktop' } else { '--qa-isolated' }
    $ownedProcess = Start-Process -FilePath $executablePath -ArgumentList @($modeArgument, "`"$profilePath`"", '--qa-interactive', '--qa-theme', 'dark') -WindowStyle Hidden -PassThru
    Save-Json 'running.json' ([ordered]@{ processId = $ownedProcess.Id; profilePath = $profilePath; state = 'observing'; executableSha256 = $actualHash })
    Write-Host "Owned idle process PID $($ownedProcess.Id); mode $QaMode; profile $profilePath; output $outputPath"
    if (-not $ownedProcess.WaitForInputIdle(10000)) { throw 'Owned process did not become input-idle within ten seconds.' }
    Start-Sleep -Seconds 5
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $lastCheckpoint = -60.0
    do {
        $points.Add((Read-Resources $ownedProcess $watch.Elapsed.TotalSeconds))
        if ($watch.Elapsed.TotalSeconds - $lastCheckpoint -ge 60) {
            Save-Json 'idle-samples.json' @($points.ToArray())
            Write-Host "Idle observation live: $([Math]::Floor($watch.Elapsed.TotalSeconds))/$IdleSeconds seconds; PID $($ownedProcess.Id)."
            $lastCheckpoint = $watch.Elapsed.TotalSeconds
        }
        if ($watch.Elapsed.TotalSeconds -ge $IdleSeconds) { break }
        Start-Sleep -Seconds 5
    } while ($true)
    Save-Json 'idle-samples.json' @($points.ToArray())
    $first = $points[0]
    $last = $points[$points.Count - 1]
    $elapsedMs = 1000 * ($last.elapsedSeconds - $first.elapsedSeconds)
    $cpuMs = $last.cpuMilliseconds - $first.cpuMilliseconds
    $summary = [ordered]@{
        elapsedSeconds = $elapsedMs / 1000
        observations = $points.Count
        cpuMilliseconds = $cpuMs
        cpuPercentOneCore = 100 * $cpuMs / $elapsedMs
        cpuPercentAllLogicalProcessors = 100 * $cpuMs / $elapsedMs / $logicalProcessors
        first = $first
        last = $last
        minPrivateBytes = ($points.privateBytes | Measure-Object -Minimum).Minimum
        maxPrivateBytes = ($points.privateBytes | Measure-Object -Maximum).Maximum
        maxWorkingSetBytes = ($points.workingSetBytes | Measure-Object -Maximum).Maximum
        minHandles = ($points.handles | Measure-Object -Minimum).Minimum
        maxHandles = ($points.handles | Measure-Object -Maximum).Maximum
        minThreads = ($points.threads | Measure-Object -Minimum).Minimum
        maxThreads = ($points.threads | Measure-Object -Maximum).Maximum
        diagnosticFilePresent = [bool](Get-ChildItem -LiteralPath $profilePath -File -Filter '*error*' -ErrorAction SilentlyContinue)
        profileFileCount = @(Get-ChildItem -LiteralPath $profilePath -File -Recurse).Count
        executableHashUnchanged = ((Get-FileHash -LiteralPath $executablePath -Algorithm SHA256).Hash.ToLowerInvariant() -eq $actualHash)
    }
    Save-Json 'idle-summary.json' $summary
    Write-Host ($summary | ConvertTo-Json -Depth 5)
    Write-Host 'PASS: Idle observation completed; not a general leak-free or desktop E2E claim.'
}
finally {
    if ($ownedProcess) {
        try {
            if (-not $ownedProcess.HasExited) { $ownedProcess.Kill(); $ownedProcess.WaitForExit() }
            Save-Json 'stopped.json' ([ordered]@{ processId = $ownedProcess.Id; state = 'stopped'; atUtc = [DateTime]::UtcNow.ToString('o') })
        }
        finally { $ownedProcess.Dispose() }
    }
    $resolved = [IO.Path]::GetFullPath($profilePath)
    if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -notmatch '^NeClipboardWin-QA-idle-[0-9a-f]{32}$') { throw 'Unsafe QA profile cleanup path.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
