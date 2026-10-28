param(
    [string]$Label = 'motion',
    [int]$Cycles = 5,
    [int]$Work = 2500,
    [double]$Scale = 4,
    [int]$Priority = 5,
    [int]$Repeats = 2,
    [string]$Plugin,
    [string]$ReferencePlugin,
    [switch]$IncludeLiveMotion,
    [switch]$Capture
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$results = Join-Path $repo 'artifacts\notification-perf'
if (-not $Plugin) { $Plugin = Join-Path $repo 'source\bin\Release' }
$exe = Join-Path $PSScriptRoot 'bin\TemplateBench.exe'
New-Item -ItemType Directory -Force $results | Out-Null
$cases = @(@{ Name = 'candidate'; Plugin = $Plugin; Args = @() })
if ($ReferencePlugin) { $cases = @(@{ Name = 'reference'; Plugin = $ReferencePlugin; Args = @() }) + $cases }
if ($IncludeLiveMotion) { $cases += @{ Name = 'live'; Plugin = $Plugin; Args = @('--live-motion') } }
for ($repeat = 1; $repeat -le $Repeats; $repeat++) {
    # Alternate order to reduce warm-up and thermal bias. Never run GPU trials concurrently.
    $ordered = @($cases)
    if ($repeat % 2 -eq 0) { [array]::Reverse($ordered) }
    foreach ($trial in $ordered) {
        $scaleText = $Scale.ToString([Globalization.CultureInfo]::InvariantCulture)
        $arguments = @('--plugin', $trial.Plugin, '--game', "$Work", '--gpu-priority', "$Priority",
            '--card-scale', $scaleText, '--cycles', "$Cycles", '--hold', '3') + $trial.Args
        if (-not $Capture) { $arguments += '--no-capture' }
        $log = Join-Path $results ($Label + '-' + $trial.Name + '-' + $repeat + '.txt')
        & $exe @arguments | Tee-Object -FilePath $log
        if ($LASTEXITCODE -ne 0) { throw "Trial failed: $($trial.Name)" }
    }
}
