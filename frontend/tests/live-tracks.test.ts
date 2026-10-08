/**
 * 轨迹过期（m05133 第 8 点）的单元测试。
 *
 * 「尾迹按时间逐点消失、断流整条清空」是靠 `live.tick()` 在渲染循环里算的，
 * 只看截图证明不了它到底丢的是哪些点、丢完之后断点（breaks）有没有跟着错位。
 * 这里把 `performance.now()` 钉住，逐点核对：
 *   - 早于 trailMs 的点消失、新的还在，且丢弃量是累积的（断点序号同步左移）；
 *   - 某条轨迹超过 staleMs 没有新点 → 整条清空（不是留一小段）；
 *   - 节点不新鲜（后端判过期 / 掉线）时**不再往轨迹里追加点**，否则轨迹永远假装「有更新」；
 *   - 页面刷新后从快照灌进来的历史点，按固定间隔倒退打时间戳，于是越老越先到期。
 */
import { afterAll, beforeEach, describe, expect, it, vi } from 'vitest'
import { live, loadTracks } from '../src/services/live'
import type { RelativeFrame, RelativeNode } from '../src/types'

let clock = 1000
const spy = vi.spyOn(performance, 'now').mockImplementation(() => clock)
afterAll(() => spy.mockRestore())

function node(patch: Partial<RelativeNode> = {}): RelativeNode {
  return {
    name: 'node',
    online: true,
    fresh: true,
    position: { east: 0, north: 0, up: 0, horizontalDistance: 0, distance: 0 },
    ageMs: 0,
    sequence: 1,
    ...patch,
  } as RelativeNode
}

function frame(
  patch: {
    drone?: Partial<RelativeNode>
    radar?: Partial<RelativeNode>
    targets?: Array<{ id: number; east: number; north: number; up: number }>
    isPointCloud?: boolean
  } = {},
): RelativeFrame {
  return {
    timestamp: '2026-09-30T00:00:00.000Z',
    sequence: 1,
    originName: '基座',
    referenceResolved: true,
    baseStation: node({ name: 'base' }),
    radar: node({ name: 'radar', ...patch.radar }),
    drone: node({ name: 'drone', ...patch.drone }),
    radarBaseLineM: 1,
    radarBoresightDeg: 0,
    radarBoresightFromBaseline: true,
    targets: patch.targets ?? [],
    isPointCloud: patch.isPointCloud ?? false,
    trigger: 'timer',
  } as unknown as RelativeFrame
}

beforeEach(() => {
  signal(1000)
  live.clear()
})

function signal(at: number): void {
  clock = at
}

describe('轨迹按超时逐点消失（live.tick）', () => {
  it('早于 trailMs 的点消失、新的点留着，断点序号同步左移', () => {
    // 三个点：t=1000 / 1100 / 1200（间隔 100 ms < TRACK_GAP_MS，不产生断点）
    for (const [i, at] of [1000, 1100, 1200].entries()) {
      signal(at)
      live.update(frame({ drone: { position: enu(i) } }))
    }
    expect(live.droneTrack.count).toBe(3)

    // t=1400 时只剩「最近 250 ms」：1000 与 1100 到期，1200 留着
    signal(1400)
    live.tick(250, 60000)
    expect(live.droneTrack.count).toBe(1)
    expect(live.droneTrack.startedAt).toBe(1200)
    // 位置也一起搬对了：留下的是第 3 个点（east = 3）
    expect(live.droneTrack.positions[0]).toBe(3)

    // 再往后没有新点 → 连最后一点也到期，整条清空（resetTrack 把起始时间归零）
    signal(1600)
    live.tick(250, 60000)
    expect(live.droneTrack.count).toBe(0)
    expect(live.droneTrack.startedAt).toBe(0)
    expect(live.droneTrack.updatedAt).toBe(0)
  })

  it('丢弃点跨越断点时，断点跟着左移；落到被丢弃区间的断点作废', () => {
    // t=1000 一个点；t=3000 再来一个点：间隔 2000 > TRACK_GAP_MS(1000) → 记一个断点
    signal(1000)
    live.update(frame({ drone: { position: enu(0) } }))
    signal(3000)
    live.update(frame({ drone: { position: enu(1) } }))
    expect(live.droneTrack.breaks).toEqual([1])

    // t=3000、尾迹 1000 ms → cutoff = 2000，t=1000 的点到期；断点 1 左移成 0 后作废
    signal(3000)
    live.tick(1000, 60000)
    expect(live.droneTrack.count).toBe(1)
    expect(live.droneTrack.breaks).toEqual([])
  })

  it('断流超过 staleMs：整条轨迹清空，而不是剩一小段', () => {
    signal(1000)
    live.update(frame({ drone: { position: enu(0) } }))
    signal(1100)
    live.update(frame({ drone: { position: enu(1) } }))
    expect(live.droneTrack.count).toBe(2)

    // 5 s 没有新点，staleMs = 3000：即使 trailMs 给到 60 s，也必须整条清掉
    signal(6000)
    live.tick(60000, 3000)
    expect(live.droneTrack.count).toBe(0)
  })

  it('目标轨迹停止上报后按 staleMs 被清掉，消失标记也一并清', () => {
    signal(1000)
    live.update(frame({ targets: [{ id: 7, ...enu(0) }] }))
    expect(live.targetTracks.size).toBe(1)
    live.markGone([7], 1000)

    signal(2000)
    live.tick(60000, 5000)
    expect(live.targetTracks.size).toBe(1) // 才过 1 s，还在

    signal(7000)
    live.tick(60000, 5000)
    expect(live.targetTracks.size).toBe(0)
    // markGone 的标记本身有 3 s 寿命，7 s 后也应被清
    expect(live.goneSince(7)).toBeUndefined()
  })

  it('节点不新鲜时不再追加点：轨迹不会因为「每帧重报同一位置」而假装有新数据', () => {
    // 无人机数据过期 → 不写无人机轨迹
    signal(1000)
    live.update(frame({ drone: { fresh: false } }))
    expect(live.droneTrack.count).toBe(0)

    // 链路在线但雷达帧过期：目标点也不写（否则过期点会一直刷新 updatedAt）
    signal(1100)
    live.update(frame({ radar: { fresh: false }, targets: [{ id: 1, ...enu(1) }] }))
    expect(live.targetTracks.size).toBe(0)

    // 掉线同理：轨迹不再增长（无人机数据这道门在雷达之前，所以上面那次仍写了一个无人机点）
    const before = live.droneTrack.count
    signal(1200)
    live.update(frame({ drone: { online: false }, radar: { online: false }, targets: [{ id: 1, ...enu(2) }] }))
    expect(live.droneTrack.count).toBe(before)
    expect(live.targetTracks.size).toBe(0)
  })

  it('点云帧不写无人机轨迹（点云是雷达回波，不是无人机的位置）', () => {
    signal(1000)
    live.update(frame({ isPointCloud: true, targets: [{ id: 1, ...enu(0) }, { id: 2, ...enu(1) }] }))
    expect(live.droneTrack.count).toBe(0)
    expect(live.targetTracks.size).toBe(2)
  })

  it('从快照灌历史点时按固定间隔倒退打时间戳 → 越老越先到期', () => {
    signal(5000)
    loadTracks(
      [
        { east: 1, north: 0, up: 0 },
        { east: 2, north: 0, up: 0 },
        { east: 3, north: 0, up: 0 },
      ] as never,
      { 9: [{ east: 9, north: 0, up: 0 }] as never },
    )
    expect(live.droneTrack.count).toBe(3)
    expect(live.targetTracks.get(9)?.count).toBe(1)
    // 最后一点的时间戳就是「现在」，前面的各早 100 ms（SNAPSHOT_STEP_MS）
    expect(live.droneTrack.times[2]).toBe(5000)

    // 尾迹 150 ms → 只剩最后两个点
    live.tick(150, 60000)
    expect(live.droneTrack.count).toBe(2)
    expect(live.droneTrack.positions[0]).toBe(2)
  })
})

/** 造一个东向偏移的位置，便于核对丢弃后留下的是哪个点。 */
function enu(i: number): RelativeNode['position'] {
  return {
    east: i + 1,
    north: 0,
    up: 0,
    horizontalDistance: i + 1,
    distance: i + 1,
  }
}
