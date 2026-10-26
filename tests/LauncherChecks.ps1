param([string]$ArtifactDirectory = '')

$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw '启动器检查需要在 Windows 上运行。' }
$root = Split-Path -Parent $PSScriptRoot
if (-not $ArtifactDirectory) { $ArtifactDirectory = Join-Path $root '.artifacts\windows-demo' }
$ArtifactDirectory = [System.IO.Path]::GetFullPath($ArtifactDirectory)
$powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$wrapperName = '启动Demo.cmd'
$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('启动器 检查 中文 ' + [Guid]::NewGuid().ToString('N'))
$script:caseCount = 0
$script:interactiveExecutablePaths = @()

function Assert-Condition([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Invoke-Launcher([string]$Directory, [string[]]$Arguments, [switch]$ViaCommandFile, [switch]$NoOutputCapture) {
    $processInfo = New-Object System.Diagnostics.ProcessStartInfo
    $processInfo.UseShellExecute = $false
    $processInfo.CreateNoWindow = $true
    $processInfo.RedirectStandardOutput = -not $NoOutputCapture
    $processInfo.RedirectStandardError = -not $NoOutputCapture
    # Each check starts elsewhere, as it would when opened from Explorer or a shortcut.
    $processInfo.WorkingDirectory = $env:SystemRoot
    $quotedArguments = @($Arguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
    if ($ViaCommandFile) {
        $processInfo.FileName = $env:ComSpec
        $wrapperPath = Join-Path $Directory $wrapperName
        $processInfo.Arguments = '/d /s /c ""' + $wrapperPath + '" ' + $quotedArguments + '"'
    } else {
        $processInfo.FileName = $powershell
        $launcherPath = Join-Path $Directory 'scripts\start-demo.ps1'
        $processInfo.Arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $launcherPath + '" ' + $quotedArguments
    }
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $processInfo
    try {
        Assert-Condition ($process.Start()) '无法启动隔离检查进程。'
        if (-not $NoOutputCapture) {
            $outputTask = $process.StandardOutput.ReadToEndAsync()
            $errorTask = $process.StandardError.ReadToEndAsync()
        }
        if (-not $process.WaitForExit(180000)) {
            $process.Kill()
            throw ('启动器检查超过 180 秒：' + $Directory + ' ' + ($Arguments -join ' '))
        }
        $process.WaitForExit()
        $result = [PSCustomObject]@{
            ExitCode = $process.ExitCode
            Output = if ($NoOutputCapture) { '' } else { $outputTask.Result }
            Error = if ($NoOutputCapture) { '' } else { $errorTask.Result }
        }
        if ($result.Output) { Write-Host $result.Output.TrimEnd() }
        if ($result.Error) { Write-Host $result.Error.TrimEnd() }
        return $result
    } finally {
        $process.Dispose()
    }
}

function Get-OwnedDemoProcesses([string]$ExecutablePath) {
    foreach ($candidate in @(Get-Process -Name NewCardDemo -ErrorAction SilentlyContinue)) {
        try {
            if ($candidate.Path -eq $ExecutablePath) { $candidate }
        } catch {
            # An unrelated process may not expose its path; it is never acted on.
        }
    }
}

function Check-InteractiveLaunch([string]$Name, [string]$Directory, [string]$ExecutablePath) {
    # Open the normal offline window through the same no-argument entry used by double-click.
    # Do not redirect this invocation: a GUI child may inherit redirected pipe handles.
    $script:interactiveExecutablePaths += $ExecutablePath
    $normalLaunch = Invoke-Launcher $Directory @() -ViaCommandFile -NoOutputCapture
    Assert-Condition ($normalLaunch.ExitCode -eq 0) ($Name + '：无参数启动失败。')
    $windowTimer = [System.Diagnostics.Stopwatch]::StartNew()
    do {
        $windowProcesses = @(Get-OwnedDemoProcesses $ExecutablePath)
        Assert-Condition ($windowProcesses.Count -le 1) ($Name + '：隔离目录启动了多个 Demo 进程。')
        if ($windowProcesses.Count -eq 1 -and $windowProcesses[0].MainWindowHandle -ne [IntPtr]::Zero) { break }
        Start-Sleep -Milliseconds 200
    } while ($windowTimer.ElapsedMilliseconds -lt 20000)
    Assert-Condition ($windowProcesses.Count -eq 1) ($Name + '：没有启动隔离目录内的 Demo。')
    $windowProcess = $windowProcesses[0]
    Assert-Condition ($windowProcess.MainWindowTitle -eq '新卡四轴 Demo · 离线演示与回显诊断') ($Name + '：主窗体标题不符：' + $windowProcess.MainWindowTitle)
    Assert-Condition ($windowProcess.CloseMainWindow()) ($Name + '：无法请求关闭本次检查启动的 Demo 窗口。')
    Assert-Condition ($windowProcess.WaitForExit(10000)) ($Name + '：本次检查启动的 Demo 窗口未正常退出。')
    $windowProcess.Dispose()
    $script:caseCount++
    Write-Host ('PASS: ' + $Name + '无参数打开并正常关闭离线主窗体')
}

function Check-Result([string]$Name, $Result, [int]$ExpectedExitCode, [switch]$OfflineSmoke) {
    Assert-Condition ($Result.ExitCode -eq $ExpectedExitCode) ($Name + '：预期退出码 ' + $ExpectedExitCode + '，实际 ' + $Result.ExitCode)
    if ($OfflineSmoke) {
        Assert-Condition ($Result.Output -match 'PASS: \d+ offline UI checks; 0 echo probes; no hardware operations\.') ($Name + '：缺少真实离线界面检查通过记录。')
    }
    $script:caseCount++
    Write-Host ('PASS: ' + $Name)
}

function Check-Failure([string]$Name, $Result) {
    Assert-Condition ($Result.ExitCode -ne 0) ($Name + '：应失败但返回了 0。')
    $script:caseCount++
    Write-Host ('PASS: ' + $Name + ' (exit ' + $Result.ExitCode + ')')
}

function Copy-SourceFixture([string]$Destination) {
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    foreach ($directory in @('src', 'scripts', 'tests', 'docs')) {
        Get-ChildItem -LiteralPath (Join-Path $root $directory) -Recurse -File |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
            ForEach-Object {
                $relativePath = $_.FullName.Substring($root.Length + 1)
                $targetPath = Join-Path $Destination $relativePath
                New-Item -ItemType Directory -Path (Split-Path -Parent $targetPath) -Force | Out-Null
                Copy-Item -LiteralPath $_.FullName -Destination $targetPath
            }
    }
    Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $Destination
    Copy-Item -LiteralPath (Join-Path $root $wrapperName) -Destination $Destination
}

try {
    Assert-Condition (Test-Path -LiteralPath (Join-Path $ArtifactDirectory 'NewCardDemo.exe') -PathType Leaf) '请先运行 scripts/build-windows.ps1 生成真实 Windows 成品。'
    Assert-Condition (Test-Path -LiteralPath (Join-Path $ArtifactDirectory 'scripts\start-demo.ps1') -PathType Leaf) '成品缺少 scripts/start-demo.ps1。'
    Assert-Condition (Test-Path -LiteralPath (Join-Path $ArtifactDirectory $wrapperName) -PathType Leaf) '成品缺少一键启动文件。'
    New-Item -ItemType Directory -Path $temporaryRoot | Out-Null

    $packageDirectory = Join-Path $temporaryRoot '成品 有空格'
    Copy-Item -LiteralPath $ArtifactDirectory -Destination $packageDirectory -Recurse
    Check-Result '成品 BuildOnly' (Invoke-Launcher $packageDirectory @('-BuildOnly')) 0
    Check-Result '成品 VerifyOnly 真实离线界面' (Invoke-Launcher $packageDirectory @('-VerifyOnly')) 0 -OfflineSmoke
    Check-Result '成品一键入口 VerifyOnly 真实离线界面' (Invoke-Launcher $packageDirectory @('-VerifyOnly') -ViaCommandFile) 0 -OfflineSmoke

    Check-InteractiveLaunch '成品一键入口' $packageDirectory (Join-Path $packageDirectory 'NewCardDemo.exe')

    $sourceDirectory = Join-Path $temporaryRoot '源码 有空格'
    Copy-SourceFixture $sourceDirectory
    Check-Result '源码 BuildOnly' (Invoke-Launcher $sourceDirectory @('-BuildOnly')) 0
    Assert-Condition (Test-Path -LiteralPath (Join-Path $sourceDirectory '.artifacts\windows-demo\NewCardDemo.exe') -PathType Leaf) '源码 BuildOnly 未产生预期成品。'
    Check-Result '源码 VerifyOnly 真实离线界面' (Invoke-Launcher $sourceDirectory @('-VerifyOnly')) 0 -OfflineSmoke
    Check-InteractiveLaunch '源码一键入口' $sourceDirectory (Join-Path $sourceDirectory '.artifacts\windows-demo\NewCardDemo.exe')

    $missingExecutableDirectory = Join-Path $temporaryRoot '缺少 成品'
    Copy-Item -LiteralPath $ArtifactDirectory -Destination $missingExecutableDirectory -Recurse
    Remove-Item -LiteralPath (Join-Path $missingExecutableDirectory 'NewCardDemo.exe')
    Check-Failure '成品缺少 exe' (Invoke-Launcher $missingExecutableDirectory @('-VerifyOnly'))

    $missingProjectDirectory = Join-Path $temporaryRoot '缺少 源码项目'
    New-Item -ItemType Directory -Path (Join-Path $missingProjectDirectory 'scripts') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $missingProjectDirectory 'src\NewCardDemo') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $root 'scripts\start-demo.ps1') -Destination (Join-Path $missingProjectDirectory 'scripts')
    Copy-Item -LiteralPath (Join-Path $root $wrapperName) -Destination $missingProjectDirectory
    Check-Failure '源码缺少项目' (Invoke-Launcher $missingProjectDirectory @('-BuildOnly') -ViaCommandFile)

    $invalidProjectDirectory = Join-Path $temporaryRoot '无效 源码项目'
    Copy-SourceFixture $invalidProjectDirectory
    [System.IO.File]::WriteAllText((Join-Path $invalidProjectDirectory 'src\NewCardDemo\NewCardDemo.csproj'), '<Project>')
    Check-Failure '源码构建失败' (Invoke-Launcher $invalidProjectDirectory @('-BuildOnly'))
    Assert-Condition (-not (Test-Path -LiteralPath (Join-Path $invalidProjectDirectory '.artifacts\windows-demo\NewCardDemo.exe'))) '失败的源码构建留下了可误启动的成品。'

    # This fixture checks exit propagation only; real UI checks above always use the built demo.
    $compiler = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
        $compiler = Join-Path $env:SystemRoot 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
    }
    Assert-Condition (Test-Path -LiteralPath $compiler -PathType Leaf) '退出码检查需要 Windows 自带的 .NET Framework C# 编译器。'
    $exitFixtureDirectory = Join-Path $temporaryRoot '退出码 传播'
    Copy-Item -LiteralPath $ArtifactDirectory -Destination $exitFixtureDirectory -Recurse
    $exitFixtureSource = Join-Path $temporaryRoot 'ExitFixture.cs'
    [System.IO.File]::WriteAllText($exitFixtureSource, 'internal static class ExitFixture { private static int Main(string[] args) { return args.Length == 1 && args[0] == "--ui-smoke-test" ? 37 : 91; } }')
    & $compiler /nologo /target:exe ('/out:' + (Join-Path $exitFixtureDirectory 'NewCardDemo.exe')) $exitFixtureSource
    Assert-Condition ($LASTEXITCODE -eq 0) '退出码检查用的临时程序编译失败。'
    Check-Result '成品 BuildOnly 不运行程序' (Invoke-Launcher $exitFixtureDirectory @('-BuildOnly')) 0
    Check-Result 'VerifyOnly 传播程序退出码' (Invoke-Launcher $exitFixtureDirectory @('-VerifyOnly')) 37
    Check-Result '一键入口传播程序退出码' (Invoke-Launcher $exitFixtureDirectory @('-VerifyOnly') -ViaCommandFile) 37

    Write-Host ('PASS: ' + $script:caseCount + ' launcher checks; paths include spaces and Chinese; verification uses offline mode only.')
} finally {
    foreach ($interactiveExecutablePath in $script:interactiveExecutablePaths) {
        foreach ($ownedProcess in @(Get-OwnedDemoProcesses $interactiveExecutablePath)) {
            $ownedProcess.CloseMainWindow() | Out-Null
            if (-not $ownedProcess.WaitForExit(5000)) { $ownedProcess.Kill() }
            $ownedProcess.Dispose()
        }
    }
    if (Test-Path -LiteralPath $temporaryRoot) { Remove-Item -LiteralPath $temporaryRoot -Recurse -Force }
}
