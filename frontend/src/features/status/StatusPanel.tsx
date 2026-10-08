/**
 * 链路与存储状态面板（只读）。
 *
 * 设计要点：
 * - 数据全部来自 store.status；唯一的写操作是「强制相对位置 / 重算原点 / 清空轨迹」，
 *   它们都经 store.runAction 走统一的通知与 busy 流程，组件里不自己 try/catch。
 * - 后端的枚举是 PascalCase 字符串（'Connected'），配置里却是 camelCase，两种写法都可能出现，
 *   因此一律先过 toCamel() 再查表，查不到就原样显示，绝不因未知枚举把整张卡片渲染崩掉。
 * - 0 与 null 语义不同（0 ms 是「刚刚有数据」，null 是「不知道」），所以格式化函数把 null 单独显示成「—」。
 * - 存储区块只回答一个问题：「我的文件到底存在哪个文件夹」——所以不列历史会话，只列绝对路径。
 * - 「雷达探测 vs 无人机 RTK」是这张面板上唯一的高频读数：相对位置帧 10 Hz，按项目约定只存
 *   services/live.ts（进 state 会让三维视图掉帧），因此单独用 useLiveFrame 取样。
 */
import { useEffect, useState } from 'react'
import { api } from '../../services/api'
import { live } from '../../services/live'
import { useStore } from '../../store/useStore'
import { fixQualityLabel, toCamel } from '../../types'
import type { DeviceStatus, DroneRadarComparison, RelativeFrame, RelativeNode, StorageStatus } from '../../types'

/** 链路状态 → 中文。 */
const STATE_LABEL: Record<string, string> = {
  disabled: '已禁用',
  disconnected: '未连接',
  connecting: '连接中',
  connected: '已连接',
  faulted: '故障',
}

/** 链路状态 → chip 配色。disabled/disconnected 都是「不报错但也没数据」，用同一个 idle。 */
const STATE_CHIP: Record<string, string> = {
  disabled: 'chip chip--idle',
  disconnected: 'chip chip--idle',
  connecting: 'chip chip--warn',
  connected: 'chip chip--ok',
  faulted: 'chip chip--bad',
}

/** 字节数 → 人类可读。存储量级横跨 B~GB，固定单位会让读数没法一眼比较。 */
function formatBytes(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes <= 0) return '0 B'
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let value = bytes
  let unit = 0
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024
    unit += 1
  }
  if (unit === 0) return `${Math.round(value)} B`
  return `${value >= 100 ? value.toFixed(0) : value.toFixed(1)} ${units[unit]}`
}

/** 毫秒 → 「12 ms」/「3.4 s」；null 显示破折号。 */
function formatMs(value: number | null): string {
  if (value == null || !Number.isFinite(value)) return '—'
  if (Math.abs(value) < 1000) return `${Math.round(value)} ms`
  return `${(value / 1000).toFixed(1)} s`
}

/** 定点小数；null / NaN 显示破折号。 */
function formatNumber(value: number | null | undefined, digits: number): string {
  if (value == null || !Number.isFinite(value)) return '—'
  return value.toFixed(digits)
}

/** 带符号的定点小数：偏差要看得出「比真值大还是小」，所以正数显式带 +。 */
function formatSigned(value: number | null | undefined, digits: number): string {
  if (value == null || !Number.isFinite(value)) return '—'
  const text = value.toFixed(digits)
  // 极小的负数会被 toFixed 抹成「-0.00」；它其实等于 0，去掉负号免得看着像方向反了。
  if (Number(text) === 0) return text.startsWith('-') ? text.slice(1) : text
  return value > 0 ? `+${text}` : text
}

/** kv 容器里的一对键值（容器由调用方给出，这里只负责一对，方便外面用 grid 排版）。 */
function Kv({ label, children }: { label: string; children: React.ReactNode }): React.ReactElement {
  return (
    <>
      <span className="kv__k">{label}</span>
      <span className="kv__v">{children}</span>
    </>
  )
}

/** 链路状态标记。 */
function StateChip({ state }: { state: string }): React.ReactElement {
  const key = toCamel(state)
  return <span className={STATE_CHIP[key] ?? 'chip chip--idle'}>{STATE_LABEL[key] ?? state}</span>
}

/** 单台设备的链路卡片。 */
function DeviceCard({
  device,
  onReconnect,
}: {
  device: DeviceStatus
  onReconnect: (kind: string) => void
}): React.ReactElement {
  return (
    <div className="panel__section">
      <div className="row">
        <strong>{device.name}</strong>
        <span className="hint">{device.kindLabel}</span>
        <StateChip state={device.state} />
        <button type="button" className="btn" onClick={() => onReconnect(device.kind)}>
          重连
        </button>
      </div>

      <div className="kv">
        <Kv label="链路">{device.transportDescription}</Kv>
        <Kv label="协议">{device.protocol}</Kv>
        <Kv label="对端">{device.remoteEndPoint ?? '—'}</Kv>
        <Kv label="空闲">{formatMs(device.idleMs)}</Kv>
        <Kv label="收到字节">{formatBytes(device.bytesReceived)}</Kv>
        <Kv label="收到帧数">{device.framesReceived}</Kv>
        <Kv label="解析样本">{device.samples}</Kv>
        <Kv label="解析错误">{device.parseErrors}</Kv>
        <Kv label="丢弃字节">{device.skippedBytes}</Kv>
        <Kv label="目标数">{device.targetCount}</Kv>
        <Kv label="重连次数">{device.reconnects}</Kv>
        <Kv label="落盘">
          {device.saveRaw || device.saveParsed
            ? `${device.saveRaw ? '原始' : ''}${device.saveRaw && device.saveParsed ? ' + ' : ''}${
                device.saveParsed ? '解析' : ''
              }`
            : '未开启'}
        </Kv>
      </div>

      {/* 契约里没有「红色文字」类名，用 chip--bad 承载错误态，正文仍走 hint 以免长文本撑破布局 */}
      {device.lastError ? (
        <p className="hint">
          <span className="chip chip--bad">错误</span> {device.lastError}
        </p>
      ) : null}
    </div>
  )
}

/** 相对位置节点（基座 / 雷达 / 无人机）的关键字段。 */
function NodeCard({ title, node }: { title: string; node: RelativeNode | null }): React.ReactElement {
  if (!node) {
    return (
      <div className="panel__section">
        <div className="row">
          <strong>{title}</strong>
          <span className="chip chip--idle">暂无数据</span>
        </div>
      </div>
    )
  }

  return (
    <div className="panel__section">
      <div className="row">
        <strong>{title}</strong>
        <span className="hint">{node.name}</span>
        <span className={node.online ? 'chip chip--ok' : 'chip chip--idle'}>{node.online ? '在线' : '离线'}</span>
        <span className={node.fresh ? 'chip chip--ok' : 'chip chip--warn'}>{node.fresh ? '数据新鲜' : '数据陈旧'}</span>
      </div>

      <div className="kv">
        <Kv label="纬度">{formatNumber(node.latitude, 6)}</Kv>
        <Kv label="经度">{formatNumber(node.longitude, 6)}</Kv>
        <Kv label="海拔（米）">{formatNumber(node.altitudeM, 6)}</Kv>
        <Kv label="航向（度）">{formatNumber(node.headingDeg, 1)}</Kv>
        <Kv label="定位质量">{fixQualityLabel(node.fixQuality)}</Kv>
        <Kv label="卫星数">{node.satellites ?? '—'}</Kv>
        <Kv label="数据年龄">{formatMs(node.ageMs)}</Kv>
        <Kv label="备注">{node.note ?? '—'}</Kv>
      </div>
    </div>
  )
}

/* ── 雷达探测 vs 无人机 RTK ─────────────────────────────────────────────── */

/** 取样间隔：相对位置帧 10 Hz，读数用不着跟满，250 ms 已经比人眼快。 */
const LIVE_SAMPLE_MS = 250

/** 超过这么久没有新帧就提示停更——冻结的读数长得和实时的一模一样，不提示会被当成当前精度。 */
const LIVE_STALE_MS = 2000

interface LiveReadout {
  frame: RelativeFrame | null
  /** 距最近一帧的秒数；未超过 LIVE_STALE_MS 时为 0（正常跟帧，不提示）。 */
  ageSec: number
  /** 取样签名：签名不变就不写 state。 */
  key: string
}

/** 读一次 services/live.ts 里的最新相对位置帧。 */
function sampleLiveFrame(): LiveReadout {
  const frame = live.frame
  if (!frame) return { frame: null, ageSec: 0, key: '' }
  const ageMs = live.frameAt > 0 ? performance.now() - live.frameAt : 0
  const ageSec = ageMs > LIVE_STALE_MS ? Math.min(999, Math.floor(ageMs / 1000)) : 0
  // 帧序号每来一帧就变；平台停下之后靠 ageSec 每秒推一次，既知道「还活着」也知道「停了多久」。
  return { frame, ageSec, key: `${frame.sequence}|${ageSec}` }
}

/**
 * 实时相对位置帧（rAF 取样，不进 zustand）。
 *
 * 相对位置帧 10 Hz 且按项目约定只存 services/live.ts，所以这里自己取样；签名不变时
 * setReadout 直接返回原对象，React 会跳过这次重渲染，面板停更时也不会每秒空转。
 */
function useLiveFrame(): LiveReadout {
  const [readout, setReadout] = useState<LiveReadout>(() => sampleLiveFrame())

  useEffect(() => {
    let raf = 0
    let last = 0
    const tick = (now: number): void => {
      raf = requestAnimationFrame(tick)
      if (now - last < LIVE_SAMPLE_MS) return
      last = now
      const next = sampleLiveFrame()
      setReadout((prev) => (prev.key === next.key ? prev : next))
    }
    raf = requestAnimationFrame(tick)
    return () => cancelAnimationFrame(raf)
  }, [])

  return readout
}

/**
 * 雷达朝向的来源说明：区分「双天线基线推算」与「手动绝对角」；THS 未定向时基线航向为 null。
 * fromBaseline 为 undefined 说明这一帧压根没带这个键（旧版后端或早期录制的帧），
 * 这时不能默认说成「手动绝对角」，如实写来源未知。
 */
function boresightSourceText(
  fromBaseline: boolean | undefined,
  baselineHeadingDeg: number | null | undefined,
): string {
  if (fromBaseline === undefined) return '来源未知（该帧未带朝向来源标记）'
  if (!fromBaseline) return '手动绝对角'
  if (baselineHeadingDeg == null || !Number.isFinite(baselineHeadingDeg)) {
    return '由双天线基线推算（未收到 THS 双天线定向，基线航向未知）'
  }
  return `由双天线基线推算（基线航向 ${baselineHeadingDeg.toFixed(1)}° + 安装夹角）`
}

/** 东/北/天三元组，位置读数用（不显式带 + 号）。 */
function formatEnu(east: number, north: number, up: number): string {
  return `东 ${formatNumber(east, 2)} · 北 ${formatNumber(north, 2)} · 天 ${formatNumber(up, 2)}`
}

/** 匹配状态那一句：命中报目标号，未命中要写清「最近目标」有多远、匹配半径多大。 */
function matchText(comparison: DroneRadarComparison): string {
  const radius = formatNumber(comparison.matchRadiusM, 0)
  const nearest = formatNumber(comparison.deltaDistanceM, 1)
  const target = `#${formatNumber(comparison.targetId, 0)}`
  if (comparison.matched) {
    const type = comparison.targetTypeName ? `（${comparison.targetTypeName}）` : ''
    return `本帧雷达探到无人机：目标 ${target}${type} 距 RTK 位置 ${nearest} m，在匹配半径 ${radius} m 内。`
  }
  return `本帧雷达未探到无人机：最近目标 ${target} 距离 ${nearest} m > 匹配半径 ${radius} m。下面这些数都属于这个最近目标，不是雷达对无人机的探测。`
}

/** 极坐标对照表的一行。 */
interface PolarRow {
  label: string
  digits: number
  radar: number
  rtk: number
  delta: number
}

/** 极坐标三项：雷达实测、RTK 换算到雷达本体的真值、两者之差。 */
function polarRows(comparison: DroneRadarComparison): PolarRow[] {
  return [
    {
      label: '距离（m）',
      digits: 2,
      radar: comparison.radarRangeM,
      rtk: comparison.rtkRangeM,
      delta: comparison.deltaRangeM,
    },
    {
      label: '方位（°）',
      digits: 2,
      radar: comparison.radarAzimuthDeg,
      rtk: comparison.rtkAzimuthDeg,
      delta: comparison.deltaAzimuthDeg,
    },
    {
      label: '俯仰（°）',
      digits: 2,
      radar: comparison.radarElevationDeg,
      rtk: comparison.rtkElevationDeg,
      delta: comparison.deltaElevationDeg,
    },
  ]
}

/**
 * 「雷达探测 vs 无人机 RTK」读数卡。
 *
 * 无人机本身就是天上那个运动目标：雷达探到的位置与它自己 RTK 报的位置一比，就能直接读出
 * 雷达探测误差。偏差一律是「雷达 − RTK」；未命中时那几个数来自离无人机最近的雷达目标，
 * 不是雷达对无人机的探测，所以文案里点明目标号。
 */
function DroneComparisonCard({ readout }: { readout: LiveReadout }): React.ReactElement {
  const { frame, ageSec } = readout

  if (!frame) {
    return (
      <div className="panel__section">
        <div className="row">
          <strong>雷达探测 vs 无人机 RTK</strong>
          <span className="chip chip--idle">尚无相对位置帧</span>
        </div>
        <p className="hint">还没收到相对位置帧，收到后这里会显示雷达朝向与探测偏差。</p>
      </div>
    )
  }

  // 老版本后端 / 早期录制的帧里根本没有 droneComparison 这个键，取出来是 undefined 而不是 null，
  // 所以在这里统一归成 null，后面只判断一种「没有」。
  const comparison = frame.droneComparison ?? null

  return (
    <div className="panel__section">
      <div className="row">
        <strong>雷达探测 vs 无人机 RTK</strong>
        <span className="chip chip--idle">帧 #{formatNumber(frame.sequence, 0)}</span>
        {/* 停更提示：没有新帧时读数会原地冻结，和实时的长得一模一样。 */}
        {ageSec > 0 ? <span className="chip chip--warn">已 {ageSec} s 无新帧</span> : null}
        {comparison === null ? null : comparison.matched ? (
          <span className="chip chip--ok">命中 目标 #{formatNumber(comparison.targetId, 0)}</span>
        ) : (
          <span className="chip chip--warn">未命中</span>
        )}
      </div>

      <div className="kv">
        <Kv label="雷达朝向">
          <span className="mono">{formatNumber(frame.radarBoresightDeg, 1)}°</span>{' '}
          <span className="hint">
            {boresightSourceText(frame.radarBoresightFromBaseline, frame.radarBaselineHeadingDeg)}
          </span>
        </Kv>
      </div>

      {comparison === null ? (
        <p className="hint">
          未开启对比，或无人机 / 雷达暂无数据。（要出读数需要：配置里打开无人机对比、原点已解算、无人机有
          RTK 定位，且本帧雷达有目标。）
        </p>
      ) : (
        <>
          <p className="hint">{matchText(comparison)}</p>

          <div className="kv">
            {/* 未命中时 deltaDistanceM 是「最近目标到无人机的距离」，不是探测误差，标签跟着换。 */}
            <Kv label={comparison.matched ? '三维 Δ（米）' : '最近目标（米）'}>
              <span className="mono">{formatNumber(comparison.deltaDistanceM, 2)}</span>
            </Kv>
            <Kv label="水平 Δ（米）">
              <span className="mono">{formatNumber(comparison.deltaHorizontalM, 2)}</span>
            </Kv>
            <Kv label="东向 Δ（米）">
              <span className="mono">{formatSigned(comparison.deltaEastM, 2)}</span>
            </Kv>
            <Kv label="北向 Δ（米）">
              <span className="mono">{formatSigned(comparison.deltaNorthM, 2)}</span>
            </Kv>
            <Kv label="天向 Δ（米）">
              <span className="mono">{formatSigned(comparison.deltaUpM, 2)}</span>
            </Kv>
            <Kv label="雷达位置（米）">
              <span className="mono">
                {formatEnu(comparison.radarEast, comparison.radarNorth, comparison.radarUp)}
              </span>
            </Kv>
            <Kv label="RTK 位置（米）">
              <span className="mono">{formatEnu(comparison.rtkEast, comparison.rtkNorth, comparison.rtkUp)}</span>
            </Kv>
          </div>

          <table className="table">
            <thead>
              <tr>
                <th>极坐标</th>
                <th>雷达实测</th>
                <th>RTK 真值</th>
                <th>偏差（雷达 − RTK）</th>
              </tr>
            </thead>
            <tbody>
              {polarRows(comparison).map((row) => (
                <tr key={row.label}>
                  <td>{row.label}</td>
                  <td className="mono">{formatNumber(row.radar, row.digits)}</td>
                  <td className="mono">{formatNumber(row.rtk, row.digits)}</td>
                  <td className="mono">{formatSigned(row.delta, row.digits)}</td>
                </tr>
              ))}
            </tbody>
          </table>

          <p className="hint">
            极坐标都在雷达本体系：方位以雷达正前方为 0°、向右为正（后端归一化到 0~360°），俯仰向上为正；
            方位差按 ±180° 归一化，跨 0° / 360° 时不会出现 ±359° 的假大偏差。
          </p>
        </>
      )}
    </div>
  )
}

/**
 * 存储卡片。
 *
 * 这里只回答一个问题：「我的数据存在哪个文件夹」。所以不列历史会话，只把会话目录（完整绝对路径）
 * 和每台设备当前正在写的文件原样摆出来——路径一律不截断、不省略成文件名，可选中、可一键复制，
 * 方便直接粘进资源管理器。
 */
function StorageCard({ storage }: { storage: StorageStatus }): React.ReactElement {
  const [copied, setCopied] = useState(false)
  const directory = storage.sessionDirectory.trim()

  async function copyDirectory(): Promise<void> {
    try {
      await navigator.clipboard.writeText(directory)
      setCopied(true)
      window.setTimeout(() => setCopied(false), 2000)
    } catch {
      // 非安全上下文（http）或用户拒绝授权时 navigator.clipboard 不可用：降级成手动复制，
      // 不做无声失败——否则用户点了按钮没反应，会以为路径已经复制走了。
      window.prompt('浏览器不允许自动复制，请手动复制这个路径：', directory)
    }
  }

  return (
    <div className="panel__section">
      <div className="row">
        <strong>存储位置</strong>
        <span className={storage.enabled ? 'chip chip--ok' : 'chip chip--warn'}>
          {storage.enabled ? '存储已启用' : '存储未启用'}
        </span>
        {directory ? (
          <button type="button" className="btn btn--ghost" onClick={() => void copyDirectory()}>
            {copied ? '已复制' : '复制路径'}
          </button>
        ) : null}
      </div>

      {storage.enabled ? null : (
        <p className="hint">
          <span className="chip chip--warn">未启用</span> 存储未启用，数据不会落盘，本次运行不会产生任何文件。
        </p>
      )}

      {directory ? (
        <>
          <p className="hint">本次采集的全部文件都写在这个文件夹里，每台设备一个子文件夹：</p>
          <p className="mono">{directory}</p>
        </>
      ) : (
        <p className="hint">尚未开始采集，开始采集后这里会显示本次会话的文件夹。</p>
      )}

      <div className="kv">
        <Kv label="原始记录">{storage.rawRecords}</Kv>
        <Kv label="解析记录">{storage.parsedRecords}</Kv>
        <Kv label="雷达转基座系">{storage.radarBaseRecords}</Kv>
        <Kv label="三设备同帧">{storage.frameRecords}</Kv>
        <Kv label="已落盘">{formatBytes(storage.totalBytes)}</Kv>
      </div>

      {storage.files.length === 0 ? (
        <p className="hint">本次会话还没有写出文件。</p>
      ) : (
        <>
          <p className="hint">
            各设备当前正在写的文件，按「原始 → 解析」列出；只显示一个路径表示该项只有一种文件
            （例如「雷达转基座系」「三设备同帧」都只有一个结果文件）。
          </p>
          <p className="hint">
            文件名前缀 raw_ / parsed_ / radar_base_ / frame_ 分别对应原始数据、解析数据、雷达转基座系结果、三设备同帧结果。
          </p>
          <div className="kv">
            {storage.files.map((file) => (
              <Kv key={`${file.device}|${file.rawPath}|${file.parsedPath}`} label={file.device}>
                {file.rawPath || file.parsedPath ? (
                  <>
                    {file.rawPath ? (
                      <>
                        {/* 只有「原始 + 解析」都写出时才贴「原始 / 解析」标签：
                            相对位置数据的 rawPath 其实是相对位置结果文件，贴「原始」会误导。 */}
                        {file.parsedPath ? <span className="hint">原始 </span> : null}
                        <span className="mono">{file.rawPath}</span>
                      </>
                    ) : null}
                    {file.rawPath && file.parsedPath ? <span className="hint"> → 解析 </span> : null}
                    {/* parsedPath 对「相对位置数据」是空串（它只有相对位置结果这一个文件）。
                        空串时既不画箭头，也不把它当路径处理，避免渲染出「→ 」空指向。 */}
                    {file.parsedPath ? <span className="mono">{file.parsedPath}</span> : null}
                  </>
                ) : (
                  <span className="hint">本次会话尚未写出文件</span>
                )}
              </Kv>
            ))}
          </div>
        </>
      )}
    </div>
  )
}

export function StatusPanel(): React.ReactElement {
  const status = useStore((s) => s.status)
  const readout = useLiveFrame()
  const clearTracks = useStore((s) => s.clearTracks)
  const resetReference = useStore((s) => s.resetReference)
  const runAction = useStore((s) => s.runAction)
  const reconnect = useStore((s) => s.reconnect)

  return (
    <div className="panel">
      <div className="panel__title">链路与存储状态{status ? ` · ${status.name}` : ''}</div>

      {status ? (
        <div className="panel__actions">
          <span className={status.running ? 'chip chip--ok' : 'chip chip--idle'}>
            {status.running ? '平台运行中' : '平台已停止'}
          </span>
          <span className="chip chip--idle">原点 {status.originName}</span>
          <span className={status.referenceResolved ? 'chip chip--ok' : 'chip chip--warn'}>
            {status.referenceResolved ? '原点已解算' : '原点未解算'}
          </span>
          <span className="chip chip--idle">相对位置帧 {status.relativeFrames}</span>
          <span className={status.droppedRelativeFrames > 0 ? 'chip chip--warn' : 'chip chip--idle'}>
            丢弃 {status.droppedRelativeFrames}
          </span>
        </div>
      ) : null}

      <div className="panel__body">
        {status === null ? (
          <p className="hint">等待后端状态…（若长时间无响应，请检查后端 /api/snapshot 是否可达）</p>
        ) : (
          <>
            {status.devices.length === 0 ? <p className="hint">配置里没有启用任何设备。</p> : null}
            {status.devices.map((device) => (
              <DeviceCard
                key={device.kind}
                device={device}
                onReconnect={(kind) => {
                  void reconnect(kind)
                }}
              />
            ))}

            <StorageCard storage={status.storage} />

            <div className="panel__section">
              <div className="panel__title">基座坐标系几何</div>
              <div className="kv">
                <Kv label="无人机 ↔ 基座（米）">{formatNumber(status.droneDistanceToBaseM, 3)}</Kv>
                <Kv label="无人机相对基座高（米）">{formatNumber(status.droneHeightAboveBaseM, 3)}</Kv>
                <Kv label="无人机 ↔ 雷达（米）">{formatNumber(status.droneDistanceToRadarM, 3)}</Kv>
                <Kv label="无人机轨迹点">{status.droneTrackPoints}</Kv>
                <Kv label="在跟踪目标">{status.trackedTargets}</Kv>
              </div>
            </div>

            <DroneComparisonCard readout={readout} />

            <NodeCard title="基座" node={status.baseStation} />
            <NodeCard title="雷达" node={status.radar} />
            <NodeCard title="无人机" node={status.drone} />
          </>
        )}
      </div>

      {/* 操作行始终可见：resetReference / clearTracks 在 store 里已经各自包过 runAction，
          返回 Promise<void>，无法再塞进 runAction 的 {ok,message} 契约里，故直接调用；
          只有 api.forceRelative 需要就地包一层。 */}
      <div className="panel__actions">
        <button
          type="button"
          className="btn btn--ghost"
          onClick={() => void runAction('强制重建一帧', () => api.forceRelative())}
        >
          强制重建一帧
        </button>
        <button type="button" className="btn btn--ghost" onClick={() => void resetReference()}>
          重算原点
        </button>
        <button type="button" className="btn btn--danger" onClick={() => void clearTracks()}>
          清空轨迹
        </button>
      </div>
    </div>
  )
}
