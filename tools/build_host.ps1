#Requires -Version 5.1
<#
.SYNOPSIS
    构建 WinUI 3 宿主，并把产物放进 Python 包内，供 wheel 打包分发。

.DESCRIPTION
    pywinui3 的 Python 侧只是一层协议客户端，真正渲染界面的是 host/PyWinUI.Host。
    `pip install` 拿到的 wheel 里如果缺少这个 exe，import 能过、一开窗口就报
    「找不到 PyWinUI.Host.exe」。所以发布前必须先跑本脚本：

        tools/build_host.ps1
        pip install python/

    产物落在 python/pywinui3/runtime/PyWinUI.Host/，由 MANIFEST.in 与
    pyproject.toml 的 include-package-data 收进 wheel。

.PARAMETER Platform
    x64（默认）或 ARM64。ARM64 需要本机具备对应的 .NET 桌面运行时。

.PARAMETER Configuration
    默认 Release。Debug 产物很大，不要用于分发。

.PARAMETER SkipRestore
    跳过 NuGet 还原（本机已有 obj/project.assets.json 时可用）。
#>
[CmdletBinding()]
param(
    [ValidateSet('x64', 'ARM64')]
    [string]$Platform = 'x64',

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$SkipRestore
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'host/PyWinUI.Host/PyWinUI.Host.csproj'
$destination = Join-Path $root 'python/pywinui3/runtime/PyWinUI.Host'

if (-not (Test-Path $project)) {
    throw "找不到宿主工程：$project（脚本应位于 <repo>/tools/ 下）"
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '没有找到 dotnet。请先安装 .NET 8 SDK：https://dotnet.microsoft.com/download/dotnet/8.0'
}

$rid = if ($Platform -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
$publishDir = Join-Path $root "host/PyWinUI.Host/bin/$Configuration/publish-$rid"

Write-Host "[build_host] 配置=$Configuration 平台=$Platform RID=$rid"

$arguments = @(
    'publish', $project,
    '-c', $Configuration,
    '-r', $rid,
    '--self-contained', 'false',
    "-p:Platform=$Platform",
    '-o', $publishDir
)
if ($SkipRestore) { $arguments += '--no-restore' }

& dotnet @arguments
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish 失败（退出码 $LASTEXITCODE）"
}

# 整个目录替换，避免上一次构建的旧 DLL 混进 wheel
if (Test-Path $destination) {
    Remove-Item $destination -Recurse -Force
}
New-Item -ItemType Directory -Path $destination -Force | Out-Null
Copy-Item (Join-Path $publishDir '*') $destination -Recurse -Force

$exe = Join-Path $destination 'PyWinUI.Host.exe'
if (-not (Test-Path $exe)) {
    throw "产物里没有 PyWinUI.Host.exe，wheel 装完会无法启动：$destination"
}

$bytes = (Get-ChildItem $destination -Recurse -File | Measure-Object -Property Length -Sum).Sum
$sizeMb = [math]::Round($bytes / 1MB, 1)
Write-Host "[build_host] 完成：$destination（$sizeMb MB）"
Write-Host '[build_host] 下一步：pip install python/'
