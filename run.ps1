# 无人机 + RTK 平台上位机 —— 一键启动脚本
#
# 用法：
#   .\run.ps1              生产模式：构建前端到后端 wwwroot，启动后端，打开浏览器（默认 http://localhost:5080）
#   .\run.ps1 -Dev         开发模式：同时启动后端与 Vite 开发服务器（前端热更新），打开 http://localhost:5173
#   .\run.ps1 -SkipBuild   跳过构建，直接用已有产物启动
#   .\run.ps1 -NoBrowser   启动但不自动打开浏览器
#   .\run.ps1 -SelfTest    不启动平台，改为跑一遍无硬件自检（见 selftest.ps1）：
#                          没接任何设备时，用它确认每一层代码都是对的。
#
# 说明：
#   * 生产模式下前端产物被后端静态托管，只需要一个端口、一个进程，交给现场用户最省事。
#   * 开发模式需要 frontend\node_modules 已存在（本机离线，无法 npm install）。
#   * 后端监听地址在 backend\src\UavPlatform.Api\appsettings.json 的 "Urls" 里改。

[CmdletBinding()]
param(
    [switch]$Dev,
    [switch]$SkipBuild,
    [switch]$NoBrowser
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$apiProject = Join-Path $root 'backend\src\UavPlatform.Api\UavPlatform.Api.csproj'
$solution = Join-Path $root 'backend\UavPlatform.slnx'
$frontend = Join-Path $root 'frontend'
$backendUrl = 'http://localhost:5080'
$devUrl = 'http://localhost:5173'

function Write-Step([string]$text) {
    Write-Host ''
    Write-Host "==> $text" -ForegroundColor Cyan
}

function Assert-Tool([string]$name, [string]$hint) {
    if (-not (Get-Command $name -ErrorAction SilentlyContinue)) {
        throw "未找到 $name。$hint"
    }
}

Assert-Tool 'dotnet' '请安装 .NET 9 SDK（本机实测 9.0.301）。'

if (-not (Test-Path $solution)) {
    throw "未找到解决方案文件：$solution"
}

if (-not $Dev) {
    # ── 生产模式 ────────────────────────────────────────────────────────────
    $wwwroot = Join-Path $root 'backend\src\UavPlatform.Api\wwwroot\index.html'
    $releaseAssembly = Join-Path $root 'backend\src\UavPlatform.Api\bin\Release\net9.0\UavPlatform.Api.dll'

    if (-not $SkipBuild) {
        Assert-Tool 'npm' '请安装 Node.js（本机实测 v24.19.0）。'

        Write-Step '构建前端（产物输出到 backend\src\UavPlatform.Api\wwwroot）'
        if (-not (Test-Path (Join-Path $frontend 'node_modules'))) {
            throw "缺少 $frontend\node_modules。本机离线无法 npm install，请从参考项目复制该目录。"
        }
        Push-Location $frontend
        try {
            & npm run build
            if ($LASTEXITCODE -ne 0) { throw "前端构建失败（npm run build 退出码 $LASTEXITCODE）。" }
        }
        finally {
            Pop-Location
        }

        Write-Step '构建后端'
        & dotnet build $solution -c Release --nologo
        if ($LASTEXITCODE -ne 0) { throw "后端构建失败（dotnet build 退出码 $LASTEXITCODE）。" }
    }
    else {
        Write-Host '已跳过前端构建（-SkipBuild）。' -ForegroundColor Yellow

        # 下面用的是 dotnet run --no-build，产物不存在时报错很难看懂，这里提前补上
        if (-not (Test-Path $releaseAssembly)) {
            Write-Host '未找到 Release 后端产物，先补一次后端构建…' -ForegroundColor Yellow
            & dotnet build $solution -c Release --nologo
            if ($LASTEXITCODE -ne 0) { throw "后端构建失败（dotnet build 退出码 $LASTEXITCODE）。" }
        }

        if (-not (Test-Path $wwwroot)) {
            Write-Warning "未找到前端产物 $wwwroot，页面会 404。去掉 -SkipBuild 跑一次完整构建。"
        }
    }

    Write-Step "启动后端：$backendUrl"
    Write-Host '按 Ctrl+C 停止。' -ForegroundColor DarkGray

    if (-not $NoBrowser) {
        # 等后端把端口监听起来再打开浏览器，避免用户看到空白页
        Start-Job -ScriptBlock {
            param($url)
            for ($i = 0; $i -lt 60; $i++) {
                Start-Sleep -Milliseconds 500
                try {
                    $response = Invoke-WebRequest -Uri "$url/api/health" -UseBasicParsing -TimeoutSec 2
                    if ($response.StatusCode -eq 200) {
                        Start-Process $url
                        return
                    }
                }
                catch {
                    # 还没起来，继续等
                }
            }
            Write-Warning "后端在 30 秒内未响应 $url/api/health，请手动打开该地址。"
        } -ArgumentList $backendUrl | Out-Null
    }

    & dotnet run --project $apiProject -c Release --no-build
}
else {
    # ── 开发模式 ────────────────────────────────────────────────────────────
    Assert-Tool 'npm' '请安装 Node.js（本机实测 v24.19.0）。'

    if (-not $SkipBuild) {
        Write-Step '构建后端（开发模式使用 dotnet watch 之外的最简方式：先构建一次）'
        & dotnet build $solution --nologo
        if ($LASTEXITCODE -ne 0) { throw "后端构建失败（dotnet build 退出码 $LASTEXITCODE）。" }
    }

    if (-not (Test-Path (Join-Path $frontend 'node_modules'))) {
        throw "缺少 $frontend\node_modules。本机离线无法 npm install，请从参考项目复制该目录。"
    }

    Write-Step "启动后端：$backendUrl"
    $backend = Start-Process -FilePath 'dotnet' `
        -ArgumentList @('run', '--project', $apiProject, '--no-build') `
        -WorkingDirectory $root -PassThru -NoNewWindow

    Write-Step "启动前端开发服务器：$devUrl"
    $front = Start-Process -FilePath 'npm' `
        -ArgumentList @('run', 'dev') `
        -WorkingDirectory $frontend -PassThru -NoNewWindow

    if (-not $NoBrowser) {
        Start-Sleep -Seconds 4
        Start-Process $devUrl
    }

    Write-Host ''
    Write-Host '开发模式已启动。按任意键停止两个进程…' -ForegroundColor DarkGray
    try {
        $null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
    }
    finally {
        foreach ($proc in @($front, $backend)) {
            if ($proc -and -not $proc.HasExited) {
                Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
            }
        }
        # npm 会派生子进程，按名字兜底清理
        Get-Process -Name 'node' -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -like '*node.exe' } |
            ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
    }
}
