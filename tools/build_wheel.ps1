# 打出可发布的 wheel：构建宿主 → 打包 → 修正平台标签
#
# 为什么需要最后一步：setuptools 对纯 Python 工程默认给 py3-none-any，
# 但包内实际有 Windows 宿主 exe。none-any 会让 pip 在 Linux/macOS 上
# 判定兼容并安装成功，等到启动才炸。改成 win_amd64 后安装时就被拒绝。
#
# 用法：
#   ./tools/build_wheel.ps1                 # x64
#   ./tools/build_wheel.ps1 -Platform ARM64 # ARM64

param(
    [ValidateSet("x64", "ARM64")]
    [string]$Platform = "x64",
    [switch]$SkipHost
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

if (-not $SkipHost) {
    Write-Host "[wheel] 构建宿主 $Platform ..." -ForegroundColor Cyan
    & "$PSScriptRoot\build_host.ps1" -Platform $Platform -Configuration Release
    if ($LASTEXITCODE -ne 0) { throw "宿主构建失败" }
}

Write-Host "[wheel] 打包 Python wheel ..." -ForegroundColor Cyan
Push-Location "$root\python"
try {
    python -m build --wheel --outdir "$root\dist"
    if ($LASTEXITCODE -ne 0) { throw "python -m build 失败（确认已安装 build 包：pip install build）" }
}
finally {
    Pop-Location
}

# 只处理刚生成的那个 none-any wheel
$wheel = Get-ChildItem "$root\dist\pywinui3-*-py3-none-any.whl" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if (-not $wheel) {
    throw "dist\\ 下没有找到 pywinui3-*-py3-none-any.whl"
}

$tag = if ($Platform -eq "ARM64") { "win_arm64" } else { "win_amd64" }

Write-Host "[wheel] 修正平台标签 -> $tag ..." -ForegroundColor Cyan
python "$PSScriptRoot\retag_wheel.py" $wheel.FullName --platform $tag
if ($LASTEXITCODE -ne 0) { throw "平台标签修正失败" }

$final = Get-ChildItem "$root\dist\pywinui3-*-$tag.whl" | Sort-Object LastWriteTime -Descending | Select-Object -First 1

Write-Host ""
Write-Host "[wheel] 完成：$($final.Name)  ($([math]::Round($final.Length / 1MB, 1)) MB)" -ForegroundColor Green
Write-Host "[wheel] 安装：pip install `"$($final.FullName)`""
