param([string]$OutputDirectory = "")
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root '.artifacts\windows-demo' }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '构建需要 .NET 8 SDK。已有成品时，直接运行 NewCardDemo.exe。' }
dotnet run --project (Join-Path $root 'tests\NewCard.Tests.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw '离线协议/模拟/回环测试失败。' }
dotnet build (Join-Path $root 'src\NewCardDemo\NewCardDemo.csproj') -c Release --nologo -o $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Windows 控制台构建失败。' }
$exe = Join-Path $OutputDirectory 'NewCardDemo.exe'
$screenshot = Join-Path $OutputDirectory 'offline-ui.png'
$process = Start-Process -FilePath $exe -ArgumentList @('--ui-smoke-test', '--screenshot', "`"$screenshot`"") -Wait -PassThru -RedirectStandardOutput (Join-Path $OutputDirectory 'ui-check.txt') -RedirectStandardError (Join-Path $OutputDirectory 'ui-error.txt')
Get-Content (Join-Path $OutputDirectory 'ui-check.txt')
if ($process.ExitCode -ne 0) { Get-Content (Join-Path $OutputDirectory 'ui-error.txt'); throw 'Windows 离线界面检查失败。' }
Copy-Item (Join-Path $root 'README.md') $OutputDirectory
Copy-Item (Join-Path $root 'docs\WINDOWS_QUICKSTART.md') $OutputDirectory
$launcherScripts = Join-Path $OutputDirectory 'scripts'
New-Item -ItemType Directory -Path $launcherScripts -Force | Out-Null
Copy-Item (Join-Path $root '启动Demo.cmd') $OutputDirectory
Copy-Item (Join-Path $PSScriptRoot 'start-demo.ps1') $launcherScripts
$outputDocs = Join-Path $OutputDirectory 'docs'
New-Item -ItemType Directory -Path $outputDocs -Force | Out-Null
Copy-Item (Join-Path $root 'docs\*') $outputDocs -Recurse -Force
$hashAlgorithm = [System.Security.Cryptography.SHA256]::Create()
$exeStream = [System.IO.File]::OpenRead($exe)
try {
    $exeHash = [System.BitConverter]::ToString($hashAlgorithm.ComputeHash($exeStream)).Replace('-', '').ToLowerInvariant()
} finally {
    $exeStream.Dispose()
    $hashAlgorithm.Dispose()
}
Write-Host ("SHA256: {0}  {1}" -f $exeHash, $exe)
Write-Host "构建与离线检查完成：$OutputDirectory"
