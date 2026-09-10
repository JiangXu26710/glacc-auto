# glacc-auto 一键打包脚本
# 用法：
#   .\package.ps1                # 发布 + 暂存 + zip（版本号读 GlaccAuto.Gui.csproj）
#   .\package.ps1 -Launch        # 打包后启动应用
#   .\package.ps1 -Version 0.2.0 # 覆盖版本号（同时写入程序集与 zip 文件名）
#   .\package.ps1 -BuildToolsRoot <路径>   # 手动指定生成工具安装目录
# 产物：dist\glacc-auto-win-x64\（5 文件平铺）+ dist\glacc-auto-v<版本>-win-x64.zip
# 工具链定位顺序：-BuildToolsRoot → 环境变量 GLACC_BUILDTOOLS → vswhere 探测 → 常见默认位置
[CmdletBinding()]
param(
    [string]$Version = "",
    [string]$BuildToolsRoot = "",
    [switch]$Launch
)

$ErrorActionPreference = "Stop"
$repo = $PSScriptRoot

# ── 0) 版本号：未显式指定时读 Gui 项目文件，与程序内关于行同源 ──
$guiProject = Join-Path $repo "src\GlaccAuto.Gui\GlaccAuto.Gui.csproj"
if (-not $Version) {
    $Version = ([xml](Get-Content $guiProject -Raw)).Project.PropertyGroup.Version |
        Where-Object { $_ } | Select-Object -First 1
}
if (-not $Version) { throw "未能在 GlaccAuto.Gui.csproj 中找到 <Version>" }

$stage = Join-Path $repo "dist\glacc-auto-v$Version-win-x64"
$zip = Join-Path $repo "dist\glacc-auto-v$Version-win-x64.zip"

# ── 1) 结束运行中的实例（打包需覆盖其 exe）──
Get-Process "GlaccAuto.Gui" -ErrorAction SilentlyContinue | Stop-Process -Force

# ── 2) 定位 MSVC（link.exe/lib.exe，取最新版本目录）与 Windows SDK ──
$programFilesX86 = ${env:ProgramFiles(x86)}
if (-not $programFilesX86) { $programFilesX86 = "C:\Program Files (x86)" }
$programFiles = $env:ProgramFiles
if (-not $programFiles) { $programFiles = "C:\Program Files" }

# 生成工具候选目录：显式参数 → 环境变量 → vswhere 探测 → 常见默认位置
$msvcRoots = @()
if ($BuildToolsRoot) { $msvcRoots += $BuildToolsRoot }
if ($env:GLACC_BUILDTOOLS) { $msvcRoots += $env:GLACC_BUILDTOOLS }

# vswhere 由安装器附带，可发现装在任意盘符或自定义目录下的 VS 与生成工具
$vswhere = Join-Path $programFilesX86 "Microsoft Visual Studio\Installer\vswhere.exe"
if (Test-Path $vswhere) {
    $detected = & $vswhere -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
        -property installationPath 2>$null
    if ($LASTEXITCODE -eq 0 -and $detected) { $msvcRoots += $detected }
}

$msvcRoots += @(
    (Join-Path $programFilesX86 "Microsoft Visual Studio\2022\BuildTools"),
    (Join-Path $programFiles "Microsoft Visual Studio\2022\BuildTools"),
    (Join-Path $programFilesX86 "Microsoft Visual Studio\2022\Community"),
    (Join-Path $programFiles "Microsoft Visual Studio\2022\Community")
)

$link = $null
foreach ($root in ($msvcRoots | Where-Object { $_ -and (Test-Path $_) } | Select-Object -Unique)) {
    $hit = Get-ChildItem (Join-Path $root "VC\Tools\MSVC\*\bin\Hostx64\x64\link.exe") -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1
    if ($hit) { $link = $hit; break }
}
if (-not $link) {
    throw "未找到 MSVC 工具链（link.exe）。请安装 Visual Studio 生成工具的 C++ 桌面开发组件，或用 -BuildToolsRoot 指定安装目录。"
}

$msvcRoot = $link.FullName -replace '\\bin\\Hostx64\\x64\\link\.exe$', ''
$libExe = Join-Path $msvcRoot "bin\Hostx64\x64\lib.exe"
$msvcLib = Join-Path $msvcRoot "lib\x64"

$sdkRoot = Join-Path $programFilesX86 "Windows Kits\10"
$sdkUm = Get-ChildItem "$sdkRoot\Lib\*\um\x64" -Directory -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending | Select-Object -First 1
if (-not $sdkUm) {
    throw "未找到 Windows SDK（$sdkRoot\Lib\*\um\x64）。请安装 Windows 11 SDK。"
}
$sdkUcrt = Join-Path (Split-Path (Split-Path $sdkUm.FullName) -Parent) "ucrt\x64"

Write-Host "MSVC  : $msvcRoot"
Write-Host "SDK   : $(Split-Path (Split-Path $sdkUm.FullName) -Parent)"

# ── 3) 补齐沙盒/精简终端缺失的环境变量（已设置的不覆盖）──
$userProfile = $env:USERPROFILE
if (-not $userProfile) { $userProfile = [Environment]::GetFolderPath("UserProfile") }
$envDefaults = [ordered]@{
    "APPDATA"            = Join-Path $userProfile "AppData\Roaming"
    "LOCALAPPDATA"       = Join-Path $userProfile "AppData\Local"
    "TEMP"               = Join-Path $userProfile "AppData\Local\Temp"
    "SystemRoot"         = "C:\Windows"
    "OS"                 = "Windows_NT"
    "ProgramFiles(x86)"  = "C:\Program Files (x86)"
}
foreach ($key in $envDefaults.Keys) {
    if ([string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable($key))) {
        Set-Item "Env:$key" $envDefaults[$key]
    }
}
$env:LIB = "$msvcLib;$($sdkUm.FullName);$sdkUcrt"

# ── 4) AOT 发布 ──
$publishArgs = @(
    "publish", (Join-Path $repo "src\GlaccAuto.Gui\GlaccAuto.Gui.csproj"),
    "-c", "Release",
    "-p:Version=$Version",
    "-p:IlcUseEnvironmentalTools=true",
    "-p:CppLinker=$($link.FullName)",
    "-p:CppLibCreator=$libExe"
)
Write-Host "dotnet publish ...（版本 $Version）"
# dotnet 不一定在 PATH（精简终端/沙盒），依次探测常见安装位置
$dotnetExe = (Get-Command "dotnet.exe" -ErrorAction SilentlyContinue | Select-Object -First 1).Source
if (-not $dotnetExe) {
    foreach ($probe in @("C:\Program Files\dotnet\dotnet.exe", (Join-Path $userProfile ".dotnet\dotnet.exe"))) {
        if (Test-Path $probe) { $dotnetExe = $probe; break }
    }
}
if (-not $dotnetExe) { throw "未找到 dotnet，请安装 .NET SDK 或将其加入 PATH" }
Write-Host "dotnet : $dotnetExe"
# EAP=Continue：native 命令写 stderr（编译警告）在 Stop 下会被当作 NativeCommandError 终止脚本
# 不放进管道：PS5.1 对管道中间的 & 调用可能报 CantActivateDocumentInPipeline
$ErrorActionPreference = "Continue"
& $dotnetExe @publishArgs
$publishExit = $LASTEXITCODE
$ErrorActionPreference = "Stop"
if ($publishExit -ne 0) { throw "dotnet publish 失败（exit $publishExit）" }
$publishDir = Join-Path $repo "src\GlaccAuto.Gui\bin\Release\net10.0\win-x64\publish"

# ── 5) 暂存：发布最小集 5 文件（剔 pdb 与运行时生成的 NVIDIA 目录）──
$files = @("GlaccAuto.Gui.exe", "av_libglesv2.dll", "libHarfBuzzSharp.dll", "libSkiaSharp.dll", "tls-client.dll")
if (-not (Test-Path (Join-Path $publishDir "tls-client.dll"))) {
    throw "发布目录缺少 tls-client.dll（CopyTlsClientToPublish 未生效？）"
}
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item $stage -ItemType Directory -Force | Out-Null
foreach ($f in $files) { Copy-Item (Join-Path $publishDir $f) $stage }

# ── 6) 打 zip：解压后是单个文件夹（内含 5 文件），避免解压炸开 ──
if (Test-Path $zip) { Remove-Item $zip -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip,
    [System.IO.Compression.CompressionLevel]::Optimal, $true)

$zipItem = Get-Item $zip
Write-Host ""
Write-Host ("打包完成：{0}（{1:N1} MB）" -f $zipItem.FullName, ($zipItem.Length / 1MB))
Write-Host ("暂存目录：{0}" -f $stage)

# ── 7) 可选：启动应用 ──
if ($Launch) {
    Start-Process (Join-Path $stage "GlaccAuto.Gui.exe")
    Write-Host "应用已启动。"
}
