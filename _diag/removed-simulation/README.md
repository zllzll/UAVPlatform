# 已删除的「仿真」功能（按 m00868 第 2 条移除）

本目录保留 `SimulationFeed.cs` 原文（17,510 B）作为后路。**这个工作区不是 git 仓库**，
所以这份副本是唯一的代码备份。

## 为什么删

`User said (m00868) 原文`："1、只想改显示、不动配置；2、启动仿真和仿真参数相关的代码和界面都去掉，
后面我们使用真实设备出数据"

`SimulationFeed` 直接构造**已经解析好的对象**喂进管线（`manager.InjectSample(DeviceKind.X, 样本)`），
绕过了全部协议解析器与真实的串口 / TCP / UDP 链路。它只验证「解析之后」的一切（相对位置、坐标变换、
存储、SignalR 推送、三维渲染），现场改用真实设备出数据后留着它只会误导——比如 `raw_*.bin` 会建出来
但是 0 字节、看起来像落盘坏了。

## 删掉了什么（11 个文件）

后端
- `backend\src\UavPlatform.Api\Services\SimulationFeed.cs` —— 整个文件（副本见本目录）
- `backend\src\UavPlatform.Api\Services\PlatformService.cs` —— `Simulation` 属性、`SampleInjected`
  事件、`StartAsync/StopAsync/DisposeAsync` 里的三处调用
- `backend\src\UavPlatform.Api\Program.cs` —— `POST /api/simulation/start`、`POST /api/simulation/stop`、
  `GET /api/simulation` 三个端点
- `backend\src\UavPlatform.Api\Hubs\PlatformHub.cs` —— `StartSimulation` / `StopSimulation` /
  `SimulationRunning` 三个 Hub 方法
- `backend\src\UavPlatform.Api\Contracts\LiveContracts.cs` —— `SimulationOptions` 类
- `backend\src\UavPlatform.Api\Services\LiveBroadcaster.cs` —— `SampleInjected` 订阅（样本摘要推送）
- `backend\src\UavPlatform.Core\Devices\DeviceManager.cs` —— `SampleInjected` 事件与
  `InjectSample(DeviceKind, DeviceSample)` 方法（**保留** `_lastSampleTicks` 新鲜度回退：它不依赖仿真，
  是「没有连接实例时也能用」的防御分支）

前端
- `frontend\src\app\App.tsx` —— 顶栏「启动仿真 / 停止仿真 / 仿真参数」按钮、参数条、`SimField`
- `frontend\src\store\useStore.ts`、`frontend\src\types.ts`、`frontend\src\services\api.ts` ——
  仿真状态、`SimulationOptions` 类型、两个 REST 方法
- 注释同步：`frontend\src\index.css:102`、`frontend\src\services\live.ts:148`

工具与文档
- `frontend\scripts\hub-smoke.mjs` —— 不再调 `StartSimulation`；无数据源时只提示、不判失败
- `frontend\scripts\capture-fixtures.mjs` —— `ensureRunning()` 只启动采集
- `selftest.ps1` —— 8/8 步不再 `POST /api/simulation/start`
- `README.md`、`docs\相对位置参数详解.md` —— 相关章节与「相对位置触发源」说明

## 删完之后的预期行为

没接设备时：后端照常起来、`/api/health` 正常，但三维视图与解析面板停在「未采集 / 等待数据」，
`raw_*.bin` 只有文件头、`parsed_*.jsonl` 没有数据行——**这是预期，不是故障**。
数据只来自真实设备：基座走串口（`baseStation.serialPort`）、雷达走 TCP、无人机 GPS 走 UDP，
接线与参数见 `README.md`。

## 遗留

`_diag\verify-m06464.mjs`（上一轮会话的旧诊断脚本）里仍然 `POST /api/simulation/*`，现在会 404。
它留作历史证据，要复用得先改掉那几处。
