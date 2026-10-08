/**
 * 高频实时数据的**非响应式**存放处。
 *
 * 设计依据（沿用参考项目 UAV4DSystem 的实测结论）：相对位置帧以 10 Hz 到达，若每帧都写进
 * React state / zustand，整棵组件树每 100 ms 重渲染一次，三维视图会明显掉帧。
 * 因此这里用普通模块级对象保存「最新一帧」与轨迹，由 requestAnimationFrame 直接读取；
 * 只有低频信息（状态、配置、样本表、日志）才进 zustand。
 */
import type { RelativeFrame, RelativeNode, RelativeTarget, TrackPoint } from '../types'

/** 一条轨迹点（与后端 TrackPoint 对齐，但用扁平数组存储以便直接喂给 BufferGeometry）。 */
export interface LiveTrack {
  /** 扁平 [e,n,u, e,n,u, ...] */
  positions: Float32Array
  /**
   * 每个点的本地时刻（ms，performance.now() 基准），与 positions 一一对应。
   *
   * 为什么要逐点存时间：只有 track.updatedAt 的话，只知道「最后一个点是什么时候到的」，
   * 判断不出前面哪些点已经过期——而「轨迹超时后从最久远的点开始慢慢减少」正是逐点判定。
   */
  times: Float64Array
  count: number
  /** 首点时间戳（ms） */
  startedAt: number
  updatedAt: number
  /**
   * 时序断点：每个元素是「新一段起点」的点序号。
   *
   * 目标离开雷达视场、或数据流中断后再回来时，前后两点之间并没有真实轨迹；
   * 若按点序直接连线，图上会出现一条横穿基座的假直线，所以渲染时必须在这里断开。
   */
  breaks: number[]
}

const MAX_TRACK_POINTS = 20000
const MAX_TARGET_TRACKS = 400
/**
 * 相邻两点时间差超过该值即视为轨迹中断。
 * 后端出帧约 100 ms，1 s 足以区分「网络抖动」与「真的没有数据」。
 */
const TRACK_GAP_MS = 1000
/**
 * 从后端轨迹快照灌历史点时用的假想间隔。
 *
 * 快照里的 TrackPoint 不带时间戳，而轨迹过期判定要靠时间：全给「现在」的话，
 * 几分钟的历史会一起到期、整条突然消失。按后端出帧节奏（约 100 ms）倒推，
 * 历史点就是「越老越先消失」，与实时段的表现一致。
 */
const SNAPSHOT_STEP_MS = 100

class LiveStore {
  /** 最新相对位置帧（可能为 null）。写入方：SignalR 'relative' 事件 / 快照初始化。 */
  frame: RelativeFrame | null = null
  /** 上一帧，用于插值与变化检测。 */
  previous: RelativeFrame | null = null
  /** 最近一次收到相对位置帧的本地时刻（performance.now()）。 */
  frameAt = 0
  /** 最近一次收到任何数据的本地时刻。 */
  dataAt = 0

  /** 无人机轨迹。 */
  readonly droneTrack: LiveTrack = makeTrack(4096)
  /** 目标轨迹，key = 目标 ID。 */
  readonly targetTracks = new Map<number, LiveTrack>()

  /** 目标闪烁用的「消失时间」：id → performance.now()。 */
  private readonly targetGone = new Map<number, number>()

  update(frame: RelativeFrame): void {
    this.previous = this.frame
    this.frame = frame
    const now = performance.now()
    this.frameAt = now
    this.dataAt = now

    // 陈旧节点每一帧都会把上一份旧位置再报一遍：继续追加只会画出一团原地堆叠的点，
    // 而且轨迹永远「有更新」、过期判定形同虚设。数据不新鲜就不往轨迹里写。
    if (!frame.isPointCloud && hasFreshData(frame.drone)) {
      const p = frame.drone.position
      appendTrack(this.droneTrack, p.east, p.north, p.up, now)
    }
    if (!hasFreshData(frame.radar)) return

    for (const target of frame.targets) {
      let track = this.targetTracks.get(target.id)
      if (!track) {
        if (this.targetTracks.size >= MAX_TARGET_TRACKS) {
          // 淘汰最久未更新的目标轨迹
          let oldestId = -1
          let oldestAt = Number.POSITIVE_INFINITY
          for (const [id, t] of this.targetTracks) {
            if (t.updatedAt < oldestAt) {
              oldestAt = t.updatedAt
              oldestId = id
            }
          }
          if (oldestId >= 0) this.targetTracks.delete(oldestId)
        }
        track = makeTrack(512, now)
        this.targetTracks.set(target.id, track)
      }
      appendTrack(track, target.east, target.north, target.up, now)
      this.targetGone.delete(target.id)
    }
  }

  /** 三角网 / 点云用：把当前帧目标位置写进预分配 Float32Array，避免每帧新建数组。 */
  fillTargetPositions(buffer: Float32Array): number {
    const targets: RelativeTarget[] = this.frame?.targets ?? []
    let n = 0
    const limit = Math.min(targets.length, Math.floor(buffer.length / 3))
    for (let i = 0; i < limit; i++) {
      const t = targets[i]
      buffer[n * 3] = t.east
      buffer[n * 3 + 1] = t.up
      buffer[n * 3 + 2] = t.north
      n++
    }
    return n
  }

  /** 标记某目标已消失（用于淡出）。 */
  markGone(ids: Iterable<number>, at: number): void {
    for (const id of ids) this.targetGone.set(id, at)
  }

  goneSince(id: number): number | undefined {
    return this.targetGone.get(id)
  }

  /** 清理超过 keepMs 未更新的目标轨迹。 */
  prune(maxAgeMs: number): void {
    const now = performance.now()
    for (const [id, track] of this.targetTracks) {
      if (now - track.updatedAt > maxAgeMs) this.targetTracks.delete(id)
    }
    for (const [id, at] of this.targetGone) {
      if (now - at > 3000) this.targetGone.delete(id)
    }
  }

  /**
   * 按时间过期轨迹：由渲染循环（rAF）每帧调用一次。
   *
   * 两个时间参数对应两种不同的「没有数据」：
   * - `trailMs`：**单点寿命**。超过它的点从最久远的一端开始逐个消失，于是轨迹是「慢慢缩短」，
   *   而不是整条突然不见；无人机绕圈时看到的就是尾迹一段段淡出。
   * - `staleMs`：**整条轨迹的寿命**。某条轨迹这么久没有新点（设备掉线、停止采集、后端停了），
   *   就整条清掉——留一条不再更新的线会让人以为数据还在来。
   *
   * 放在 rAF 里而不是 setInterval：页面不可见时 rAF 自动停，回到前台一次性按真实时间算准，
   * 不必为「后台标签页里空转的定时器」写额外逻辑。
   */
  tick(trailMs: number, staleMs: number): void {
    const now = performance.now()
    const drone = this.droneTrack
    if (drone.updatedAt > 0 && now - drone.updatedAt > staleMs) resetTrack(drone)
    else expireTrack(drone, now, trailMs)

    this.prune(staleMs)
    for (const track of this.targetTracks.values()) expireTrack(track, now, trailMs)
  }

  clear(): void {
    this.frame = null
    this.previous = null
    this.frameAt = 0
    resetTrack(this.droneTrack)
    this.targetTracks.clear()
    this.targetGone.clear()
  }
}

/** 节点这一帧有没有新数据。`online` 是链路、`fresh` 是后端判定的数据是否过期。 */
function hasFreshData(node: RelativeNode): boolean {
  return node.online && node.fresh
}

function makeTrack(capacity: number, at = 0): LiveTrack {
  return {
    positions: new Float32Array(3 * capacity),
    times: new Float64Array(capacity),
    count: 0,
    startedAt: at,
    updatedAt: at,
    breaks: [],
  }
}

function appendTrack(track: LiveTrack, east: number, north: number, up: number, at: number): void {
  if (track.count * 3 + 3 > track.positions.length) {
    // 容量翻倍；为免无限增长，达到上限后整体左移一半，丢掉最老的点。
    const grown = new Float32Array(Math.min(track.positions.length * 2, MAX_TRACK_POINTS * 3))
    const grownTimes = new Float64Array(grown.length / 3)
    if (grown.length === track.positions.length) {
      const keep = Math.floor(track.count / 2)
      const drop = track.count - keep
      grown.copyWithin(0, drop * 3)
      grownTimes.set(track.times.subarray(drop, track.count))
      track.count = keep
      // 断点是「点序号」，左移后必须同步位移；落在被丢弃区间里的断点直接作废。
      const kept: number[] = []
      for (const b of track.breaks) {
        const moved = b - drop
        if (moved > 0) kept.push(moved)
      }
      track.breaks = kept
    } else {
      grown.set(track.positions.subarray(0, track.count * 3))
      grownTimes.set(track.times.subarray(0, track.count))
    }
    track.positions = grown
    track.times = grownTimes
  }
  // 与上一点时间差过大 → 中间这段没有数据，记一个断点，渲染时不要连过去。
  if (track.count > 0 && at - track.updatedAt > TRACK_GAP_MS) track.breaks.push(track.count)
  const i = track.count * 3
  track.positions[i] = east
  track.positions[i + 1] = up
  track.positions[i + 2] = north
  track.times[track.count] = at
  track.count++
  track.updatedAt = at
  if (track.startedAt === 0) track.startedAt = at
}

/**
 * 把超过 maxAgeMs 的点从**最久远的一端**丢掉。
 *
 * 时间戳是递增的，所以从队首线性扫到第一个「还活着」的点即可；
 * 丢弃量是累积的，均摊下来每次调用只搬一次数组，不会成为每帧的开销。
 */
function expireTrack(track: LiveTrack, now: number, maxAgeMs: number): void {
  if (track.count === 0) return
  const cutoff = now - maxAgeMs
  let drop = 0
  while (drop < track.count && track.times[drop] < cutoff) drop++
  if (drop === 0) return
  if (drop >= track.count) {
    resetTrack(track)
    return
  }
  track.positions.copyWithin(0, drop * 3, track.count * 3)
  track.times.copyWithin(0, drop, track.count)
  track.count -= drop
  const kept: number[] = []
  for (const b of track.breaks) {
    const moved = b - drop
    if (moved > 0) kept.push(moved)
  }
  track.breaks = kept
  track.startedAt = track.times[0]
}

function resetTrack(track: LiveTrack): void {
  track.count = 0
  track.startedAt = 0
  track.updatedAt = 0
  track.breaks = []
}

/** 从 REST 轨迹快照灌入（页面初次加载 / 重连后补齐历史）。 */
export function loadTracks(drone: TrackPoint[], targets: Record<string, TrackPoint[]>): void {
  const now = performance.now()
  resetTrack(live.droneTrack)
  live.targetTracks.clear()
  fillFromSnapshot(live.droneTrack, drone, now)
  for (const [idText, points] of Object.entries(targets)) {
    const id = Number(idText)
    if (!Number.isFinite(id)) continue
    const track = makeTrack(512, now)
    fillFromSnapshot(track, points, now)
    live.targetTracks.set(id, track)
  }
}

/** 历史点按固定间隔倒退着给时间戳，这样它们会「越老越先到期」。 */
function fillFromSnapshot(track: LiveTrack, points: TrackPoint[], now: number): void {
  const count = points.length
  for (let i = 0; i < count; i++) {
    const p = points[i]
    appendTrack(track, p.east, p.north, p.up, now - (count - 1 - i) * SNAPSHOT_STEP_MS)
  }
}

export const live = new LiveStore()
