param(
    [switch]$BuildOnly,
    [switch]$VerifyOnly
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

try {
    if ($env:OS -ne 'Windows_NT') { throw 'This launcher requires Windows.' }
    if ($BuildOnly -and $VerifyOnly) { throw 'Choose either -BuildOnly or -VerifyOnly, not both.' }

    $release = (Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -Name Release -ErrorAction SilentlyContinue).Release
    if ($null -eq $release -or $release -lt 528040) {
        throw '.NET Framework 4.8 is required. Install it from https://dotnet.microsoft.com/en-us/download/dotnet-framework/net48'
    }

    $project = Join-Path $root 'src\NewCardDemo\NewCardDemo.csproj'
    $exe = Join-Path $root 'NewCardDemo.exe'
    if (Test-Path -LiteralPath $project -PathType Leaf) {
        $output = Join-Path $root '.artifacts\windows-demo'
        $exe = Join-Path $output 'NewCardDemo.exe'
        $build = Join-Path $PSScriptRoot 'build-windows.ps1'
        if (-not (Test-Path -LiteralPath $build -PathType Leaf)) { throw 'Source package is incomplete: scripts/build-windows.ps1 is missing.' }
        if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
            throw 'Source builds require the .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0 . Alternatively use the Windows binary package.'
        }
        $running = @(Get-Process -Name NewCardDemo -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe })
        if ($running.Count -gt 0) { throw 'This build is currently running. Close its Demo window before rebuilding.' }

        Write-Host 'Source package: building and checking the offline Demo...'
        & $build -OutputDirectory $output
        # The build script throws on every failed external command. It never launches the daily-use window.
    } elseif (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
        throw 'No Demo executable or source project was found. Extract the complete package before launching.'
    }

    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'Build did not produce NewCardDemo.exe.' }
    if ($BuildOnly) {
        Write-Host "Ready: $exe"
        exit 0
    }

    $workingDirectory = Split-Path -Parent $exe
    if ($VerifyOnly) {
        $temporary = Join-Path ([IO.Path]::GetTempPath()) ('NewCardDemo-launch-check-' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $temporary | Out-Null
        try {
            $stdout = Join-Path $temporary 'stdout.txt'
            $stderr = Join-Path $temporary 'stderr.txt'
            $process = Start-Process -FilePath $exe -WorkingDirectory $workingDirectory -ArgumentList '--ui-smoke-test' -Wait -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
            if (Test-Path -LiteralPath $stdout) { Get-Content -LiteralPath $stdout }
            if (Test-Path -LiteralPath $stderr) { Get-Content -LiteralPath $stderr | ForEach-Object { [Console]::Error.WriteLine($_) } }
            $result = $process.ExitCode
        } finally {
            Remove-Item -LiteralPath $temporary -Recurse -Force
        }
        exit $result
    }

    $process = Start-Process -FilePath $exe -WorkingDirectory $workingDirectory -PassThru
    Write-Host ("Offline Demo started (PID {0})." -f $process.Id)
    exit 0
} catch {
    [Console]::Error.WriteLine('Demo startup failed: ' + $_.Exception.Message)
    exit 1
}
