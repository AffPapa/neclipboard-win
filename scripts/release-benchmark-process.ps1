[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BaselineExecutable,
    [Parameter(Mandatory)][string]$CandidateExecutable,
    [ValidateRange(1, 100)][int]$Samples = 30,
    [ValidateRange(30, 1800)][int]$IdleSeconds = 300,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/process-benchmark')
)

$ErrorActionPreference = 'Stop'
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
$logicalProcessors = [Environment]::ProcessorCount
$fixtures = [Collections.Generic.List[string]]::new()
$ownedProcesses = [Collections.Generic.List[Diagnostics.Process]]::new()
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$targets = @(
    [pscustomobject]@{ Name = 'baseline'; Path = (Resolve-Path -LiteralPath $BaselineExecutable).Path },
    [pscustomobject]@{ Name = 'candidate'; Path = (Resolve-Path -LiteralPath $CandidateExecutable).Path }
)

function Save-Json([string]$Name, $Value) {
    [IO.File]::WriteAllText((Join-Path $outputPath $Name), ($Value | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
}
function Start-Fixture($Target) {
    $directory = Join-Path $tempRoot ('NeClipboardWin-QA-benchmark-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $directory | Out-Null
    $fixtures.Add($directory)
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $process = Start-Process -FilePath $Target.Path -ArgumentList @('--qa-isolated', "`"$directory`"", '--qa-interactive', '--qa-theme', 'dark') -WindowStyle Hidden -PassThru
    $ownedProcesses.Add($process)
    $idleMs = $null
    $handleMs = $null
    while ($watch.ElapsedMilliseconds -lt 10000) {
        if ($process.HasExited) { throw "$($Target.Name) exited before readiness observation." }
        $process.Refresh()
        if ($null -eq $handleMs -and $process.MainWindowHandle -ne [IntPtr]::Zero) { $handleMs = $watch.Elapsed.TotalMilliseconds }
        if ($null -eq $idleMs -and $process.WaitForInputIdle(1)) { $idleMs = $watch.Elapsed.TotalMilliseconds }
        if ($null -ne $idleMs -and ($null -ne $handleMs -or $watch.Elapsed.TotalMilliseconds -gt $idleMs + 250)) { break }
        Start-Sleep -Milliseconds 10
    }
    if ($null -eq $idleMs) { throw "$($Target.Name) did not become input-idle within 10 seconds." }
    return [pscustomobject]@{ Process = $process; InputIdleMs = $idleMs; WindowHandleMs = $handleMs; Profile = $directory }
}
function Read-Resources([Diagnostics.Process]$Process) {
    $Process.Refresh()
    return [pscustomobject]@{
        privateBytes = $Process.PrivateMemorySize64
        workingSetBytes = $Process.WorkingSet64
        peakWorkingSetBytes = $Process.PeakWorkingSet64
        handles = $Process.HandleCount
        threads = $Process.Threads.Count
        cpuMilliseconds = $Process.TotalProcessorTime.TotalMilliseconds
    }
}
function Stop-Fixture($Fixture) {
    if (-not $Fixture.Process.HasExited) { $Fixture.Process.Kill(); $Fixture.Process.WaitForExit() }
    $Fixture.Process.Dispose()
    $null = $ownedProcesses.Remove($Fixture.Process)
}
function Percentile([double[]]$Values, [double]$Percentile) {
    $ordered = @($Values | Sort-Object)
    return $ordered[[Math]::Max(0, [Math]::Ceiling($Percentile * $ordered.Count) - 1)]
}

try {
    $os = Get-CimInstance Win32_OperatingSystem
    $cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
    $memory = (Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory
    $metadata = [ordered]@{
        measuredAtUtc = [DateTime]::UtcNow.ToString('o')
        windowsVersion = $os.Version
        windowsBuild = $os.BuildNumber
        cpu = $cpu.Name.Trim()
        physicalCores = $cpu.NumberOfCores
        logicalProcessors = $logicalProcessors
        physicalMemoryBytes = $memory
        profile = 'empty, isolated, interactive QA, forced dark palette; clipboard/hotkey/tray/startup integrations disabled'
        methodology = 'Start-Process Hidden; observe process MainWindowHandle and WaitForInputIdle without UI Automation/input. WPF may still show its QA window. First observed launch separate; two warmups, then paired sequential samples. No cache flushing or AV changes. Idle pair sampled concurrently every five seconds.'
        limitations = 'Readiness observables do not prove first useful input, capture, hotkey or full desktop latency. Warm samples are not cold startup. Other desktop/build activity is uncontrolled. Intermediate EXEs, not final acceptance hashes.'
        executables = @($targets | ForEach-Object { [ordered]@{ label = $_.Name; version = [Diagnostics.FileVersionInfo]::GetVersionInfo($_.Path).FileVersion; bytes = (Get-Item -LiteralPath $_.Path).Length; sha256 = (Get-FileHash -LiteralPath $_.Path -Algorithm SHA256).Hash.ToLowerInvariant() } })
    }
    Save-Json 'environment.json' $metadata
    $rows = [Collections.Generic.List[object]]::new()
    for ($iteration = -2; $iteration -lt $Samples; $iteration++) {
        foreach ($target in $targets) {
            $fixture = Start-Fixture $target
            Start-Sleep -Milliseconds 100
            $resources = Read-Resources $fixture.Process
            $rows.Add([pscustomobject]@{ label = $target.Name; sample = $iteration; inputIdleMs = $fixture.InputIdleMs; windowHandleMs = $fixture.WindowHandleMs; resources = $resources })
            Stop-Fixture $fixture
        }
        if ($iteration -ge 0 -and ($iteration + 1) % 10 -eq 0) { Write-Host "Measured $($iteration + 1)/$Samples paired startup observations." }
    }
    Save-Json 'startup-samples.json' @($rows.ToArray())
    $summaries = @($targets | ForEach-Object {
        $name = $_.Name
        $measured = @($rows | Where-Object { $_.label -eq $name -and $_.sample -ge 0 })
        $handles = @($measured | Where-Object { $null -ne $_.windowHandleMs } | ForEach-Object { $_.windowHandleMs })
        [ordered]@{
            label = $name; samples = $measured.Count
            inputIdleP50Ms = Percentile @($measured.inputIdleMs) 0.5
            inputIdleP95Ms = Percentile @($measured.inputIdleMs) 0.95
            windowHandleSamples = $handles.Count
            windowHandleP50Ms = if ($handles.Count) { Percentile $handles 0.5 } else { $null }
            windowHandleP95Ms = if ($handles.Count) { Percentile $handles 0.95 } else { $null }
        }
    })
    Save-Json 'startup-summary.json' $summaries
    Write-Host ($summaries | ConvertTo-Json -Depth 4)

    $idleFixtures = @($targets | ForEach-Object {
        $fixture = Start-Fixture $_
        Write-Host "Idle process: $($_.Name), PID $($fixture.Process.Id), own isolated QA profile."
        [pscustomobject]@{ Name = $_.Name; Fixture = $fixture }
    })
    Start-Sleep -Seconds 5
    $idleWatch = [Diagnostics.Stopwatch]::StartNew()
    $idleRows = [Collections.Generic.List[object]]::new()
    while ($idleWatch.Elapsed.TotalSeconds -lt $IdleSeconds) {
        foreach ($target in $idleFixtures) {
            $idleRows.Add([pscustomobject]@{ label = $target.Name; elapsedSeconds = $idleWatch.Elapsed.TotalSeconds; resources = (Read-Resources $target.Fixture.Process) })
        }
        Start-Sleep -Seconds 5
    }
    foreach ($target in $idleFixtures) {
        $idleRows.Add([pscustomobject]@{ label = $target.Name; elapsedSeconds = $idleWatch.Elapsed.TotalSeconds; resources = (Read-Resources $target.Fixture.Process) })
    }
    Save-Json 'idle-samples.json' @($idleRows.ToArray())
    $idleSummary = @($targets | ForEach-Object {
        $name = $_.Name
        $points = @($idleRows | Where-Object { $_.label -eq $name })
        $first = $points[0]; $last = $points[-1]
        $elapsedMs = ($last.elapsedSeconds - $first.elapsedSeconds) * 1000
        $cpuMs = $last.resources.cpuMilliseconds - $first.resources.cpuMilliseconds
        [ordered]@{
            label = $name; elapsedSeconds = ($elapsedMs / 1000); observations = $points.Count
            cpuMilliseconds = $cpuMs
            cpuPercentOneCore = 100 * $cpuMs / $elapsedMs
            cpuPercentAllLogicalProcessors = 100 * $cpuMs / $elapsedMs / $logicalProcessors
            first = $first.resources; last = $last.resources
            maximumPrivateBytes = ($points.resources.privateBytes | Measure-Object -Maximum).Maximum
            maximumWorkingSetBytes = ($points.resources.workingSetBytes | Measure-Object -Maximum).Maximum
        }
    })
    Save-Json 'idle-summary.json' $idleSummary
    Write-Host ($idleSummary | ConvertTo-Json -Depth 5)
    Write-Host "PASS Process observability benchmark completed. Results: $outputPath"
}
finally {
    foreach ($process in $ownedProcesses) {
        try { if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() }; $process.Dispose() } catch { }
    }
    foreach ($directory in $fixtures) {
        $resolved = [IO.Path]::GetFullPath($directory)
        if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
            [IO.Path]::GetFileName($resolved) -notmatch '^NeClipboardWin-QA-benchmark-[0-9a-f]{32}$') { throw 'Unsafe benchmark fixture cleanup path.' }
        if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
    }
}
