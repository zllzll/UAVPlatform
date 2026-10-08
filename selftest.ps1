# 无人机 + RTK 平台上位机 —— 无硬件自检脚本
#
# 回答的是这个问题：「我现在没接任何设备，怎么知道代码是对的？」
#
# 它把**每一层验证**串起来跑一遍，最后打一张通过/失败汇总表。全部不需要真实设备。
#
# 用法：
#   .\selftest.ps1                完整自检（会构建）
#   .\selftest.ps1 -SkipBuild     跳过构建（产物已在，跑得快）
#   .\selftest.ps1 -KeepServer    自检结束后保留后端进程（自己再手工点页面看看）
#
# 它**不证明**什么（照实说，别误以为绿灯 = 现场一定通）：
#   * 真实雷达的应答时序、真实 UM982 的双天线解算 —— 没有硬件无法覆盖；
#   * 真实串口的电气层与驱动行为（本机只验证到「能枚举出串口名」）；
#   * 现场网段的连通性（防火墙、IP 规划、路由）。
#   这些必须接上真设备后按 README「现场接入三设备通讯参数」一节逐项确认。

[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [switch]$KeepServer
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$solution = Join-Path $root 'backend\UavPlatform.slnx'
$apiProject = Join-Path $root 'backend\src\UavPlatform.Api\UavPlatform.Api.csproj'
$frontend = Join-Path $root 'frontend'
$backendUrl = 'http://localhost:5080'

$script:results = New-Object System.Collections.ArrayList
$script:server = $null
$script:serverLog = Join-Path $env:TEMP "uavplatform-selftest-$PID.log"

# ── 小工具 ──────────────────────────────────────────────────────────────────

function Write-Head([string]$text) {
    Write-Host ''
    Write-Host "════ $text" -ForegroundColor Cyan
}

# 跑一步检查。
# 注意：$Body 里所有外部命令的输出都必须先接住再 Write-Host —— Write-Host 只写控制台、
# 不进管道，所以函数最终返回的就只剩退出码。若直接 `& dotnet build`，它的 stdout 会混进
# 返回值里，$ok 就变成字符串数组，判断全乱。
function Invoke-Step {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Proves,
        [Parameter(Mandatory)][scriptblock]$Body
    )

    Write-Host ''
    Write-Host "── $Name" -ForegroundColor White
    Write-Host "   证明：$Proves" -ForegroundColor DarkGray

    $code = 1
    try {
        $code = & $Body
        if ($null -eq $code) { $code = 0 }
    }
    catch {
        Write-Host "   异常：$($_.Exception.Message)" -ForegroundColor Red
        $code = 1
    }

    $ok = ([int]$code -eq 0)
    if ($ok) { Write-Host "   ✅ 通过" -ForegroundColor Green }
    else { Write-Host "   ❌ 失败（退出码 $code）" -ForegroundColor Red }

    $null = $script:results.Add([pscustomobject]@{
            '检查项'   = $Name
            '结果'     = $(if ($ok) { '通过' } else { '失败' })
            '证明什么' = $Proves
        })

    return $ok
}

function Test-BackendUp {
    try {
        $r = Invoke-WebRequest -Uri "$backendUrl/api/health" -UseBasicParsing -TimeoutSec 2
        return ($r.StatusCode -eq 200)
    }
    catch { return $false }
}

function Wait-Backend([int]$seconds = 40) {
    for ($i = 0; $i -lt ($seconds * 2); $i++) {
        if (Test-BackendUp) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

function Stop-Backend {
    if ($script:server -and -not $script:server.HasExited) {
        Stop-Process -Id $script:server.Id -Force -ErrorAction SilentlyContinue
    }
    # dotnet run 会派生子进程，按进程名兜底
    Get-Process -Name 'UavPlatform.Api' -ErrorAction SilentlyContinue |
        ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
    $script:server = $null
}

# 在 frontend 目录里跑一条 npm script，只回显输出并返回退出码
function Invoke-Npm {
    param([Parameter(Mandatory)][string[]]$Args)
    Push-Location $frontend
    $saved = $ErrorActionPreference
    try {
        # 原生程序往 stderr 写一行（node 的 console.error、工具自己的告警都会）在
        # $ErrorActionPreference='Stop' 下会变成终止错误：脚本自己的输出全被吞掉，
        # 只剩一句「异常：<那行 stderr>」，第一块是空行时连提示都是空的（踩过一次）。
        # 这里临时降级，让 stderr 进管道被捕获，判定只看退出码。
        $ErrorActionPreference = 'Continue'
        $out = & npm @Args 2>&1 | Out-String
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $saved
        Pop-Location
    }
    Write-Host $out
    return $code
}

# 在 frontend 目录里跑一条命令（同上：必须捕获 stderr，不能让它中断）
function Invoke-InFrontend {
    param([Parameter(Mandatory)][scriptblock]$Body)
    Push-Location $frontend
    $saved = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $out = & $Body 2>&1 | Out-String
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $saved
        Pop-Location
    }
    Write-Host $out
    return $code
}

# ── 开始 ────────────────────────────────────────────────────────────────────

Write-Host ''
Write-Host '无人机 + RTK 平台上位机 —— 无硬件自检' -ForegroundColor Cyan
Write-Host "项目根目录：$root"
Write-Host "后端地址：  $backendUrl"

if (-not (Test-Path $solution)) { throw "未找到解决方案文件：$solution" }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '未找到 dotnet，请安装 .NET 9 SDK。' }
if (-not (Get-Command npm -ErrorAction SilentlyContinue)) { throw '未找到 npm，请安装 Node.js。' }

# 构建前先放掉可能占着输出文件的旧后端进程，否则报 MSB3027 / MSB3021
if (Test-BackendUp) {
    Write-Host '检测到已有后端在运行，先停掉它再构建…' -ForegroundColor Yellow
    Stop-Backend
    Start-Sleep -Seconds 1
}

$allOk = $true

# ── 1/8 后端构建 ────────────────────────────────────────────────────────────
if (-not $SkipBuild) {
    $ok = Invoke-Step -Name '1/8 后端构建' -Proves '全部 C# 源码能编译通过，无语法或类型错误' -Body {
        $out = & dotnet build $solution -v q --nologo 2>&1 | Out-String
        $code = $LASTEXITCODE
        Write-Host $out
        $code
    }
    $allOk = $ok -and $allOk
}
else {
    Write-Host "`n── 1/8 后端构建（已跳过 -SkipBuild）" -ForegroundColor DarkGray
}

# ── 2/8 后端测试 ────────────────────────────────────────────────────────────
$ok = Invoke-Step -Name '2/8 后端测试' -Proves '协议解析用规格书真实字节向量逐字节喂入验证；相对位置数学、坐标换算、滚动落盘全部有断言' -Body {
    $out = & dotnet test $solution -v q --nologo 2>&1 | Out-String
    $code = $LASTEXITCODE
    # 只回显总结行与错误行，避免刷屏
    $out -split "`r?`n" |
        Where-Object { $_ -match '总计|通过|失败|error|Error' } |
        Select-Object -First 15 |
        ForEach-Object { Write-Host "   $_" -ForegroundColor DarkGray }
    $code
}
$allOk = $ok -and $allOk

# ── 3/8 前端类型检查 ────────────────────────────────────────────────────────
$ok = Invoke-Step -Name '3/8 前端类型检查' -Proves 'TypeScript 全量类型检查零错误' -Body {
    # 必须切到 frontend 目录再跑：tsc 装在 frontend\node_modules 下，
    # 在仓库根目录执行会退化成「找不到模块」（node:internal/modules/cjs/loader:1520）而报假失败。
    $code = Invoke-InFrontend { & node '.\node_modules\typescript\bin\tsc' --noEmit -p tsconfig.app.json }
    if ($code -eq 0) { Write-Host '   （退出码 0：零错误）' -ForegroundColor DarkGray }
    $code
}
$allOk = $ok -and $allOk

# ── 4/8 前端类名审计 ────────────────────────────────────────────────────────
$ok = Invoke-Step -Name '4/8 前端类名审计' -Proves '组件用到的每个 CSS 类名都有定义（曾因 .viewer3d 与 .viewer-wrap 不一致，导致三维画布塌成 HTML 默认 150px）' -Body {
    Invoke-InFrontend { & npm run --silent audit:css }
}
$allOk = $ok -and $allOk

# ── 5/8 前端构建 ────────────────────────────────────────────────────────────
if (-not $SkipBuild) {
    $ok = Invoke-Step -Name '5/8 前端构建' -Proves '前端能产出可被后端静态托管的产物（backend\src\UavPlatform.Api\wwwroot）' -Body {
        Invoke-InFrontend { & npm run --silent build }
    }
    $allOk = $ok -and $allOk
}
else {
    Write-Host "`n── 5/8 前端构建（已跳过 -SkipBuild）" -ForegroundColor DarkGray
}

# ── 6/8 启动后端并检查静态托管 ──────────────────────────────────────────────
$ok = Invoke-Step -Name '6/8 后端启动与页面托管' -Proves '后端能起来；/api/health 通；根路径返回前端页面；未知路径回落到 SPA（前端路由可直接刷新）' -Body {
    if (-not (Test-BackendUp)) {
        $script:server = Start-Process -FilePath 'dotnet' `
            -ArgumentList @('run', '--project', $apiProject, '--no-build') `
            -WorkingDirectory $root -PassThru -NoNewWindow `
            -RedirectStandardOutput $script:serverLog `
            -RedirectStandardError "$($script:serverLog).err"

        if (-not (Wait-Backend 40)) {
            Write-Host '   后端 40 秒内没起来，日志尾部：' -ForegroundColor Red
            if (Test-Path $script:serverLog) {
                Get-Content $script:serverLog -Tail 15 | ForEach-Object { Write-Host "     $_" -ForegroundColor DarkGray }
            }
            return 1
        }
        Write-Host '   后端已就绪。' -ForegroundColor DarkGray
    }
    else {
        Write-Host '   复用已在运行的后端。' -ForegroundColor DarkGray
    }

    # 变量不能叫 $home：PowerShell 的 $HOME 是只读自动变量，赋值会抛
    # 「Cannot overwrite variable HOME because it is read-only or constant.」
    $rootPage = Invoke-WebRequest -Uri "$backendUrl/" -UseBasicParsing -TimeoutSec 10
    $spa = Invoke-WebRequest -Uri "$backendUrl/some/spa/route" -UseBasicParsing -TimeoutSec 10
    $serial = Invoke-WebRequest -Uri "$backendUrl/api/serial-ports" -UseBasicParsing -TimeoutSec 10
    $hasRoot = $rootPage.Content -match 'id="root"'

    Write-Host ("   /                    → HTTP {0}，{1} 字节，含 #root：{2}" -f $rootPage.StatusCode, $rootPage.RawContentLength, $hasRoot) -ForegroundColor DarkGray
    Write-Host ("   /some/spa/route      → HTTP {0}（SPA 回落）" -f $spa.StatusCode) -ForegroundColor DarkGray
    Write-Host ("   /api/serial-ports    → {0}" -f $serial.Content.Trim()) -ForegroundColor DarkGray

    if ($rootPage.StatusCode -ne 200) { Write-Host '   根路径没返回 200，前端产物可能没构建。' -ForegroundColor Red; return 1 }
    if (-not $hasRoot) { Write-Host '   首页里没有 #root，index.html 可能被改动。' -ForegroundColor Red; return 1 }
    if ($spa.StatusCode -ne 200) { Write-Host '   SPA 回落失效，刷新子路由会 404。' -ForegroundColor Red; return 1 }
    return 0
}
$allOk = $ok -and $allOk

# ── 7/8 SignalR 冒烟 ────────────────────────────────────────────────────────
$ok = Invoke-Step -Name '7/8 实时推送链路冒烟' -Proves '用与前端完全相同的 SignalR 客户端库连上 Hub，真实收到 status / logs 推送；接了设备（有数据源）时额外断言 relative 帧与解析样本的字段完整性' -Body {
    Invoke-InFrontend { & npm run --silent smoke:hub }
}
$allOk = $ok -and $allOk

# ── 8/8 无头浏览器冒烟 ──────────────────────────────────────────────────────
$ok = Invoke-Step -Name '8/8 真实浏览器渲染冒烟' -Proves '真 Chrome 打开真页面：React 挂载、三维画布撑满容器、无未捕获异常（曾因 Canvas 外用 R3F hook 导致整页全黑）' -Body {
    # 先把平台启动起来，让页面有实时数据可画（否则三维是空的，测不到真实渲染路径）。
    # 内置仿真源已按要求删除：只有接了真实设备，三维与解析面板才会有数据。
    try {
        Invoke-WebRequest -Uri "$backendUrl/api/platform/start" -Method Post -UseBasicParsing -TimeoutSec 10 | Out-Null
        Write-Host '   已启动采集（没接设备时三维是空的，属预期；布局断言仍然有效）…' -ForegroundColor DarkGray
        Start-Sleep -Seconds 4
    }
    catch {
        Write-Host "   启动采集失败（不影响布局断言）：$($_.Exception.Message)" -ForegroundColor Yellow
    }
    Invoke-InFrontend { & npm run --silent smoke:ui }
}
$allOk = $ok -and $allOk

# ── 收尾 ────────────────────────────────────────────────────────────────────

if (-not $KeepServer) {
    Write-Host ''
    Write-Host '关闭后端…' -ForegroundColor DarkGray
    Stop-Backend
}
else {
    Write-Host ''
    Write-Host "后端仍在运行：$backendUrl（Ctrl+C 或 Stop-Process -Name UavPlatform.Api 结束）" -ForegroundColor Yellow
}

Write-Head '自检汇总'
$script:results | Format-Table -AutoSize -Wrap | Out-String -Width 200 | Write-Host

if ($allOk) {
    Write-Host '✅ 全部通过。' -ForegroundColor Green
    Write-Host '   注意这仍**不代表**现场一定通：真实硬件、串口电气层、现场网段没有被覆盖。' -ForegroundColor DarkGray
    Write-Host '   接上真设备后请按 README「现场接入三设备通讯参数」一节逐项确认。' -ForegroundColor DarkGray
    exit 0
}
else {
    $failed = ($script:results | Where-Object { $_.'结果' -eq '失败' } | ForEach-Object { $_.'检查项' }) -join '、'
    Write-Host "❌ 有检查项未通过：$failed" -ForegroundColor Red
    exit 1
}
