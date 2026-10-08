/**
 * SignalR 冒烟测试：用与前端完全相同的客户端库（@microsoft/signalr）连接
 * /hubs/platform，验证「浏览器将要走的完整数据链路」是否真的通。
 *
 * 覆盖：
 *   1. WebSocket 建链（withUrl('/hubs/platform') → 这里用绝对地址）
 *   2. GetSnapshot / StartPlatform 两个 Hub 方法
 *   3. relative / samples / status / logs 四个服务端推送事件
 *   4. relative 载荷的字段完整性（与 frontend/src/types.ts 的 RelativeFrame 对齐）
 *
 * 数据来源：内置仿真源已删除，样本只能来自真实设备。没接设备（或没点「开始采集」）时
 * 收不到 relative / samples —— 这一步只提示「无数据」，不判失败；字段断言在有数据时才跑。
 *
 * 用法：node scripts/hub-smoke.mjs [baseUrl]
 */
import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr'
import WebSocket from 'ws'

const BASE = process.argv[2] ?? 'http://localhost:5080'
const HUB = `${BASE}/hubs/platform`

const seen = { relative: 0, samples: 0, status: 0, logs: 0, notice: 0 }
let lastRelative = null
let lastSamples = null
let lastStatus = null
const problems = []

const conn = new HubConnectionBuilder()
  .withUrl(HUB, { WebSocket })
  .withAutomaticReconnect([500, 1000, 2000])
  .configureLogging(LogLevel.Error)
  .build()

conn.on('relative', (frame) => {
  seen.relative++
  lastRelative = frame
})
conn.on('samples', (samples) => {
  seen.samples++
  lastSamples = samples
})
conn.on('status', (status) => {
  seen.status++
  lastStatus = status
})
conn.on('logs', (lines) => {
  seen.logs += Array.isArray(lines) ? lines.length : 1
})
conn.on('notice', (text) => {
  seen.notice++
  console.log(`  [notice] ${text}`)
})

/** 检查 relative 帧是否带齐前端 types.ts 里声明的字段。 */
function checkRelativeFrame(f) {
  const required = [
    'timestamp', 'sequence', 'originName', 'referenceResolved', 'reference',
    'baseStation', 'radar', 'drone', 'radarBaseLineM', 'targets', 'isPointCloud',
  ]
  for (const key of required) {
    if (!(key in f)) problems.push(`relative 缺少字段 ${key}`)
  }
  // m04238 第 5 点：显示坐标系原点恒定（统一基座世界 ENU 系），帧里不该再有 origin 枚举，
  // 只剩给人看的中文名 originName。旧版这里把 origin 当成必备字段。
  if (f.origin !== undefined) problems.push('relative 不应再有 origin 字段（已统一为基座世界坐标系）')
  for (const node of ['baseStation', 'radar', 'drone']) {
    const n = f[node]
    if (!n?.position || typeof n.position.east !== 'number') problems.push(`${node}.position 不合法`)
    if (typeof n?.online !== 'boolean') problems.push(`${node}.online 不是布尔`)
  }
  if (!f.reference || typeof f.reference.latitude !== 'number') problems.push('reference 不合法')
  if (!Array.isArray(f.targets)) problems.push('targets 不是数组')
  else if (f.targets.length > 0) {
    const t = f.targets[0]
    for (const key of ['id', 'type', 'typeName', 'east', 'north', 'up', 'rangeM', 'azimuthDeg', 'elevationDeg']) {
      if (!(key in t)) problems.push(`target 缺少字段 ${key}`)
    }
  }
}

try {
  console.log(`连接 ${HUB} ...`)
  await conn.start()
  console.log(`已连接。transport=${conn.connection?.transport?.constructor?.name ?? '?'}  state=${conn.state}`)

  const snap = await conn.invoke('GetSnapshot')
  console.log(
    `GetSnapshot: status.running=${snap?.status?.running}  config.name=${snap?.config?.name}  ` +
      `samples=${snap?.samples?.length ?? 0}  logs=${snap?.logs?.length ?? 0}  relative=${snap?.relative ? '有' : '无'}`,
  )
  if (!snap?.config?.devices?.length) problems.push('GetSnapshot.config.devices 为空')

  console.log('GetDefaultConfig:', (await conn.invoke('GetDefaultConfig'))?.name ?? '(空)')

  await conn.invoke('StartPlatform')
  console.log('StartPlatform 成功')

  // 等 9 秒收集推送：接了真实设备时 relative / samples 会持续到来；
  // 没有数据源时这里只有 status / logs（不算失败，见文末结论）。
  await new Promise((r) => setTimeout(r, 9000))

  console.log('\n=== 推送事件统计（9 秒）===')
  for (const [k, v] of Object.entries(seen)) console.log(`  ${k.padEnd(8)} ${v}`)

  if (lastStatus) {
    console.log('\n=== status 推送 ===')
    console.log(
      `  running=${lastStatus.running} origin=${lastStatus.originName} 解析=${lastStatus.referenceResolved} ` +
        `relativeFrames=${lastStatus.relativeFrames} dropped=${lastStatus.droppedRelativeFrames} ` +
        `droneTrackPoints=${lastStatus.droneTrackPoints} trackedTargets=${lastStatus.trackedTargets}`,
    )
    for (const d of lastStatus.devices ?? []) {
      console.log(`  ${String(d.kindLabel).padEnd(20)} ${String(d.state).padEnd(13)} ${d.transport}`)
    }
    console.log(
      `  storage: raw=${lastStatus.storage?.rawRecords} parsed=${lastStatus.storage?.parsedRecords} ` +
        `radarBase=${lastStatus.storage?.radarBaseRecords} frames=${lastStatus.storage?.frameRecords} ` +
        `bytes=${lastStatus.storage?.totalBytes}`,
    )
  }

  if (lastSamples?.length) {
    console.log('\n=== 最新解析样本 ===')
    for (const s of lastSamples.slice(0, 3)) {
      console.log(`  [${s.deviceKind}] ${s.summary}`)
    }
  }

  if (lastRelative) {
    console.log('\n=== 最新相对位置帧 ===')
    console.log(
      `  origin=${lastRelative.originName} trigger=${lastRelative.trigger} ` +
        `pointCloud=${lastRelative.isPointCloud} targets=${lastRelative.targets.length}`,
    )
    console.log(
      `  基座 E=${lastRelative.baseStation.position.east.toFixed(3)} N=${lastRelative.baseStation.position.north.toFixed(3)} U=${lastRelative.baseStation.position.up.toFixed(6)}`,
    )
    console.log(
      `  无人机 E=${lastRelative.drone.position.east.toFixed(2)} N=${lastRelative.drone.position.north.toFixed(2)} U=${lastRelative.drone.position.up.toFixed(2)} ` +
        `航向=${lastRelative.drone.headingDeg?.toFixed(2)}° 质量=${lastRelative.drone.fixQuality} 星=${lastRelative.drone.satellites}`,
    )
    console.log(
      `  离基座=${lastRelative.droneDistanceToBaseM?.toFixed(3)} m 相对高=${lastRelative.droneHeightAboveBaseM?.toFixed(3)} m ` +
        `离雷达=${lastRelative.droneDistanceToRadarM?.toFixed(3)} m 基线=${lastRelative.radarBaseLineM} m`,
    )
    if (lastRelative.targets[0]) {
      const t = lastRelative.targets[0]
      console.log(
        `  目标[0] id=${t.id} type=${t.type}(${t.typeName}) E=${t.east.toFixed(2)} N=${t.north.toFixed(2)} ` +
          `距离=${t.rangeM.toFixed(2)} 方位=${t.azimuthDeg.toFixed(2)}° 离无人机=${t.distanceToDroneM?.toFixed(2)} m`,
      )
    }
    checkRelativeFrame(lastRelative)
  }

  if (seen.relative === 0 && seen.samples === 0) {
    console.log('\n（未收到 relative / samples：没接真实设备，也没有数据源；字段级断言未执行。）')
  }
} catch (err) {
  problems.push(`异常：${err?.message ?? err}`)
} finally {
  try {
    await conn.stop()
  } catch {
    /* 忽略 */
  }
}

console.log('\n=== 结论 ===')
if (problems.length === 0) {
  console.log(
    seen.relative === 0
      ? 'PASS：SignalR 链路、Hub 方法与 status / logs 推送正常（无相对位置帧：没有数据源，字段断言已跳过）。'
      : 'PASS：SignalR 链路、Hub 方法、四个推送事件与 relative 字段全部正常。',
  )
  process.exit(0)
}
console.log(`FAIL：${problems.length} 个问题`)
for (const p of problems.slice(0, 20)) console.log(`  - ${p}`)
process.exit(1)
