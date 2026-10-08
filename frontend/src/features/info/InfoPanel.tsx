/**
 * 右侧信息面板：四个页签（雷达解析 / 基座解析 / 无人机解析 / 运行日志）。
 *
 * 每页两段内容：
 * 1. 「最新一帧」结构化读数——由 requestAnimationFrame 直接改 textContent，
 *    不让 10 Hz 的相对位置帧进入 React state（否则整块面板每帧重渲染，
 *    滚动位置和页签交互都会被拖垮）。数据源 services/live.ts 的 live.frame。
 * 2. 该设备的滚动解析行——来自低频 store.samples（后端 SampleSummary.Summary 原文），
 *    最新在最上面。
 *
 * 数值一律取「显示坐标系」（原点 = 基座，x = 东，y = 天，z = 北），与三维视图一致。
 */
import { useEffect, useMemo, useRef, useState } from 'react'
import { live } from '../../services/live'
import { useStore } from '../../store/useStore'
import { fixQualityLabel, toCamel } from '../../types'
import type { EnuPoint, RelativeFrame, RelativeNode, SampleSummary } from '../../types'

export type InfoTab = 'radar' | 'base' | 'drone' | 'logs'

export const INFO_TABS: ReadonlyArray<{ id: InfoTab; label: string; kind?: string; hint: string }> = [
  { id: 'radar', label: '雷达解析', kind: 'radar', hint: '雷达（NSR）：本帧目标的本体观测量与世界坐标系读数。' },
  { id: 'base', label: '基座解析', kind: 'baseStation', hint: '基座（UM982）：定位、定向与坐标系原点来源。' },
  { id: 'drone', label: '无人机解析', kind: 'droneGps', hint: '无人机（UCM221）：GPS 读数与相对基座、雷达的位置。' },
  { id: 'logs', label: '运行日志', hint: '链路、解析与存储事件。' },
]

// ── 格式化 ──────────────────────────────────────────────────────────────────

function num(value: number | null | undefined, digits = 2, unit = ''): string {
  if (value == null || Number.isNaN(value)) return '—'
  return `${value.toFixed(digits)}${unit}`
}

function signed(value: number | null | undefined, digits = 2, unit = ''): string {
  if (value == null || Number.isNaN(value)) return '—'
  return `${value >= 0 ? '+' : ''}${value.toFixed(digits)}${unit}`
}

/** ISO 字符串 → 本机时刻（毫秒）。设备给的是 UTC，这里统一按浏览器本地时区显示。 */
function clock(iso: string | null | undefined): string {
  if (!iso) return '—'
  const date = new Date(iso)
  if (Number.isNaN(date.getTime())) return iso
  const pad = (n: number, width = 2) => String(n).padStart(width, '0')
  return `${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}.${pad(date.getMilliseconds(), 3)}`
}

function linkText(node: RelativeNode | null | undefined): string {
  if (!node) return '—'
  if (!node.online) return '离线（未连接或无数据）'
  const age = node.ageMs >= 1000 ? `${(node.ageMs / 1000).toFixed(1)} s` : `${Math.round(node.ageMs)} ms`
  return `${node.fresh ? '在线 · 数据新鲜' : '在线 · 数据陈旧'}（${age} 前）`
}

function linkTone(node: RelativeNode | null | undefined): 'ok' | 'warn' | 'bad' {
  if (!node || !node.online) return 'bad'
  return node.fresh ? 'ok' : 'warn'
}

/** 显示坐标系三轴读数（米）。 */
function enu(point: EnuPoint | null | undefined): string {
  if (!point) return '—'
  return `东 ${signed(point.east)} / 北 ${signed(point.north)} / 天 ${signed(point.up)} m`
}

function wgs84(node: RelativeNode): string {
  if (node.latitude == null || node.longitude == null) return '—（无定位）'
  return `${node.latitude.toFixed(7)}, ${node.longitude.toFixed(7)} · 海拔 ${num(node.altitudeM, 2, ' m')}`
}

function fixText(node: RelativeNode): string {
  return `${fixQualityLabel(node.fixQuality)} · 卫星 ${node.satellites ?? '—'}`
}

/** 雷达本帧里「最值得看」的那个目标：目标模式取最近，点云模式取 SNR 最高。 */
function pickTarget(frame: RelativeFrame) {
  if (frame.targets.length === 0) return null
  if (frame.isPointCloud) {
    return frame.targets.reduce((best, t) => (t.snr > best.snr ? t : best), frame.targets[0])
  }
  return frame.targets.reduce((best, t) => (t.rangeM < best.rangeM ? t : best), frame.targets[0])
}

// ── 最新一帧读数（rAF 直写 textContent）───────────────────────────────────────

interface LiveRow {
  label: string
  read: (frame: RelativeFrame) => string
  tone?: (frame: RelativeFrame) => 'ok' | 'warn' | 'bad' | null
}

/** 读数每 500 ms 至少重画一次：帧停了也能看到「x 秒前」在涨，陈旧一眼可见。 */
const REPAINT_MS = 500

function LiveBlock({ rows }: { rows: LiveRow[] }): React.ReactElement {
  const refs = useRef<Array<HTMLSpanElement | null>>([])

  useEffect(() => {
    let handle = 0
    let lastStamp = ''
    let lastPaint = 0
    const paint = () => {
      handle = requestAnimationFrame(paint)
      const frame = live.frame
      const now = performance.now()
      const stamp = frame ? `${frame.timestamp}|${frame.sequence}` : 'empty'
      if (stamp === lastStamp && now - lastPaint < REPAINT_MS) return
      lastStamp = stamp
      lastPaint = now
      rows.forEach((row, index) => {
        const element = refs.current[index]
        if (!element) return
        element.textContent = frame ? row.read(frame) : '尚无相对位置帧'
        const tone = frame && row.tone ? row.tone(frame) : null
        element.className = tone ? `infopanel__value infopanel__value--${tone}` : 'infopanel__value'
      })
    }
    handle = requestAnimationFrame(paint)
    return () => cancelAnimationFrame(handle)
  }, [rows])

  return (
    <div className="infopanel__block">
      {rows.map((row, index) => (
        <div className="infopanel__row" key={row.label}>
          <span className="infopanel__label">{row.label}</span>
          <span
            className="infopanel__value"
            ref={(element) => {
              refs.current[index] = element
            }}
          >
            —
          </span>
        </div>
      ))}
    </div>
  )
}

const HEAD_ROWS: LiveRow[] = [
  {
    label: '原点',
    read: (f) =>
      `${f.originName} · ${f.referenceResolved ? '已解算' : '未解算'}（来源 ${f.reference.source}）` +
      (f.reference.resolved ? ` · ${f.reference.latitude.toFixed(7)}, ${f.reference.longitude.toFixed(7)}` : ''),
    tone: (f) => (f.referenceResolved ? 'ok' : 'warn'),
  },
]

const RADAR_ROWS: LiveRow[] = [
  ...HEAD_ROWS,
  { label: '链路', read: (f) => linkText(f.radar), tone: (f) => linkTone(f.radar) },
  { label: '本机时间', read: (f) => clock(f.radar.pcTime) },
  { label: '设备时间', read: (f) => f.radar.deviceTime ?? '—（该协议不带时间戳）' },
  { label: '帧序号', read: (f) => `${f.radar.sequence}${f.trigger ? ` · 触发 ${f.trigger}` : ''}` },
  { label: '雷达位置', read: (f) => enu(f.radar.position) },
  { label: '距原点', read: (f) => `${num(f.radar.position.distance, 2, ' m')}（水平 ${num(f.radar.position.horizontalDistance, 2, ' m')}）` },
  {
    label: '正前方',
    read: (f) =>
      `${num(f.radarBoresightDeg, 1, '°')}（${f.radarBoresightFromBaseline ? '基座基线航向推算' : '手动绝对角'}）`,
    tone: (f) => (f.radarBoresightFromBaseline ? 'ok' : 'warn'),
  },
  {
    label: '基线航向',
    read: (f) =>
      f.radarBaselineHeadingDeg == null
        ? '—（尚未收到 THS 定向）'
        : `${num(f.radarBaselineHeadingDeg, 1, '°')} · 基线长 ${num(f.radarBaseLineM, 2, ' m')}`,
  },
  {
    label: '本帧内容',
    read: (f) => `${f.isPointCloud ? '点云' : '目标'} · ${f.targets.length} 个`,
  },
  {
    label: '最近目标',
    read: (f) => {
      const t = pickTarget(f)
      if (!t) return '—（本帧无目标）'
      return `#${t.id} ${t.typeName} · 距离 ${num(t.rangeM, 2, ' m')} · 方位 ${num(t.azimuthDeg, 1, '°')} · 俯仰 ${num(t.elevationDeg, 1, '°')} · SNR ${num(t.snr, 1)}`
    },
  },
  { label: '距无人机', read: (f) => num(f.droneDistanceToRadarM, 2, ' m') },
  {
    label: '对比偏差',
    read: (f) => {
      const c = f.droneComparison
      if (!c) return '—（未开启对比或缺数据）'
      if (!c.matched) return `未匹配到回波（匹配半径 ${num(c.matchRadiusM, 0, ' m')}）`
      return `雷达 − RTK：三维 ${num(c.deltaDistanceM, 2, ' m')} · 水平 ${num(c.deltaHorizontalM, 2, ' m')} · 斜距 ${signed(c.deltaRangeM, 2, ' m')} · 方位 ${signed(c.deltaAzimuthDeg, 2, '°')}`
    },
    tone: (f) => (f.droneComparison?.matched ? 'ok' : 'warn'),
  },
]

const BASE_ROWS: LiveRow[] = [
  ...HEAD_ROWS,
  { label: '链路', read: (f) => linkText(f.baseStation), tone: (f) => linkTone(f.baseStation) },
  { label: '本机时间', read: (f) => clock(f.baseStation.pcTime) },
  { label: '设备时间', read: (f) => f.baseStation.deviceTime ?? '—（该协议不带时间戳）' },
  { label: '帧序号', read: (f) => `${f.baseStation.sequence}` },
  { label: 'WGS84 位置', read: (f) => wgs84(f.baseStation) },
  { label: '定位质量', read: (f) => fixText(f.baseStation) },
  { label: '世界系位置', read: (f) => enu(f.baseStation.position) },
  {
    label: '基线航向',
    read: (f) =>
      f.baseStation.headingDeg == null
        ? '—（未收到真航向 THS）'
        : `${num(f.baseStation.headingDeg, 1, '°')}${f.radarBaselineHeadingDeg == null ? '（未用于雷达朝向）' : '（已用于雷达朝向）'}`,
    tone: (f) => (f.radarBaselineHeadingDeg == null ? 'warn' : 'ok'),
  },
  { label: '姿态', read: (f) => `俯仰 ${num(f.baseStation.pitchDeg, 2, '°')} · 横滚 ${num(f.baseStation.rollDeg, 2, '°')}` },
  { label: '距无人机', read: (f) => num(f.droneDistanceToBaseM, 2, ' m') },
  { label: '备注', read: (f) => f.baseStation.note ?? '—' },
]

const DRONE_ROWS: LiveRow[] = [
  ...HEAD_ROWS,
  { label: '链路', read: (f) => linkText(f.drone), tone: (f) => linkTone(f.drone) },
  { label: '本机时间', read: (f) => clock(f.drone.pcTime) },
  { label: '设备时间', read: (f) => f.drone.deviceTime ?? '—（该协议不带时间戳）' },
  { label: '帧序号', read: (f) => `${f.drone.sequence}` },
  { label: 'WGS84 位置', read: (f) => wgs84(f.drone) },
  { label: '定位质量', read: (f) => fixText(f.drone) },
  { label: '世界系位置', read: (f) => enu(f.drone.position) },
  { label: '距基座', read: (f) => `${num(f.droneDistanceToBaseM, 2, ' m')} · 高于基座 ${num(f.droneHeightAboveBaseM, 2, ' m')}` },
  { label: '距雷达', read: (f) => num(f.droneDistanceToRadarM, 2, ' m') },
  { label: '航向', read: (f) => num(f.drone.headingDeg, 1, '°') },
  {
    label: '帧对齐',
    read: (f) => (f.baseToDroneSkewMs == null ? '—' : `${num(f.baseToDroneSkewMs, 0, ' ms')}`),
  },
  { label: '备注', read: (f) => f.drone.note ?? '—' },
]

// ── 雷达目标板（1 Hz 刷新：目标在动，但不需要 10 Hz 的 DOM 更新）──────────────

const BOARD_LIMIT = 24

function TargetBoard(): React.ReactElement {
  const [, setTick] = useState(0)

  useEffect(() => {
    const timer = window.setInterval(() => setTick((value) => value + 1), 1000)
    return () => window.clearInterval(timer)
  }, [])

  const frame = live.frame
  if (!frame || frame.targets.length === 0) {
    return <p className="hint">本帧没有目标。雷达收到目标或点云后，这里会列出每个目标的本体观测量与世界坐标系读数。</p>
  }

  const sorted = [...frame.targets].sort((a, b) =>
    frame.isPointCloud ? b.snr - a.snr : a.rangeM - b.rangeM,
  )
  const shown = sorted.slice(0, BOARD_LIMIT)
  const world = frame.referenceResolved

  return (
    <>
      <p className="hint">
        本帧 {frame.isPointCloud ? '点云' : '目标'} {frame.targets.length} 个
        {frame.targets.length > shown.length ? `（按${frame.isPointCloud ? '信噪比' : '距离'}取前 ${shown.length} 个）` : ''}
        ；世界坐标系读数{world ? '原点已解算' : '需要先解算原点（当前显示 0 值）'}。
      </p>
      <div className="infopanel__tablewrap">
        <table className="infopanel__table">
          <thead>
            <tr>
              <th>目标</th>
              <th>距离 m</th>
              <th>方位 °</th>
              <th>俯仰 °</th>
              <th>SNR</th>
              <th>东 m</th>
              <th>北 m</th>
              <th>天 m</th>
              <th>离无人机 m</th>
            </tr>
          </thead>
          <tbody>
            {shown.map((t) => (
              <tr key={t.id}>
                <td className="infopanel__id">
                  #{t.id} <span className="infopanel__type">{t.typeName}</span>
                </td>
                <td>{num(t.rangeM, 1)}</td>
                <td>{num(t.azimuthDeg, 1)}</td>
                <td>{num(t.elevationDeg, 1)}</td>
                <td>{num(t.snr, 1)}</td>
                <td>{num(t.east, 1)}</td>
                <td>{num(t.north, 1)}</td>
                <td>{num(t.up, 1)}</td>
                <td>{num(t.distanceToDroneM, 1)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </>
  )
}

// ── 该设备的滚动解析行 ──────────────────────────────────────────────────────

const SAMPLE_LIMIT = 200

function SampleRows({ kind, empty }: { kind: string; empty: string }): React.ReactElement {
  const samples = useStore((s) => s.samples)
  const rows = useMemo(
    () => samples.filter((sample: SampleSummary) => toCamel(sample.deviceKind) === kind).slice(-SAMPLE_LIMIT).reverse(),
    [samples, kind],
  )

  if (rows.length === 0) return <p className="hint">{empty}</p>

  return (
    <div className="infopanel__list">
      {rows.map((sample) => (
        <div className="infopanel__sample" key={`${sample.device}-${sample.timestamp}-${sample.sequence}`}>
          <span className="mono infopanel__time">{clock(sample.timestamp)}</span>
          <span className="infopanel__summary">{sample.summary}</span>
        </div>
      ))}
      <p className="hint">
        共 {rows.length} 条（最多保留最近 {SAMPLE_LIMIT} 条；每条原文同时落盘在会话目录的 parsed 文件里）。
      </p>
    </div>
  )
}

function LogsPanel(): React.ReactElement {
  const logs = useStore((s) => s.logs)
  const boxRef = useRef<HTMLDivElement | null>(null)

  useEffect(() => {
    const box = boxRef.current
    if (box) box.scrollTop = box.scrollHeight
  }, [logs])

  return (
    <div className="logs" ref={boxRef}>
      {logs.length === 0 ? (
        <p className="hint">暂无日志。开始采集后这里会显示链路、解析与存储事件。</p>
      ) : (
        logs.map((line, index) => (
          <div className="logs__line" key={`${index}-${line.slice(0, 24)}`}>
            {line}
          </div>
        ))
      )}
    </div>
  )
}

// ── 面板 ────────────────────────────────────────────────────────────────────

export function InfoPanel({ tab }: { tab: InfoTab }): React.ReactElement {
  if (tab === 'logs') {
    return (
      <div className="panel">
        <div className="panel__title">
          <span className="panel__title-text">运行日志</span>
          <span className="hint">链路、解析与存储事件（最多保留最近 600 行）</span>
        </div>
        <LogsPanel />
      </div>
    )
  }

  const meta = INFO_TABS.find((item) => item.id === tab) ?? INFO_TABS[0]
  const rows = tab === 'radar' ? RADAR_ROWS : tab === 'base' ? BASE_ROWS : DRONE_ROWS
  const empty =
    tab === 'radar'
      ? '还没有解析到雷达数据。开始采集后，这里会逐帧列出目标命令、目标数与极坐标。'
      : tab === 'base'
        ? '还没有解析到基座数据。UM982 出 GGA/RMC/THS 语句后，这里会显示定位与定向结果。'
        : '还没有解析到无人机 GPS 数据。UCM221 上报后，这里会显示位置、速度与航向。'

  return (
    <div className="panel">
      <div className="panel__title">
        <span className="panel__title-text">{meta.label}</span>
        <span className="hint">{meta.hint}</span>
      </div>
      <div className="panel__body">
        <div className="panel__section">
          <h4>最新一帧（实时，10 Hz）</h4>
          <LiveBlock rows={rows} />
        </div>
        {tab === 'radar' && (
          <div className="panel__section">
            <h4>本帧目标（本体观测量 + 世界坐标系）</h4>
            <TargetBoard />
          </div>
        )}
        <div className="panel__section">
          <h4>解析原文（最新在上）</h4>
          <SampleRows kind={meta.kind ?? ''} empty={empty} />
        </div>
      </div>
    </div>
  )
}
