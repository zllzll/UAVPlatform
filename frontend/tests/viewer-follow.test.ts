/**
 * 「跟随无人机」的相机步进单测（m06173 第 6 点）。
 *
 * 现场反馈「三维视图平移完又回到原位置」，根因是 v1 的跟随写法把注视点每帧朝无人机插值
 * （0.08 的 lerp），用户平移出去之后注视点被拉回无人机，相机跟着一起回去。
 * 这里把判据钉在「用户自己挪出来的相机位置会不会被跟随改掉」上。
 */
import { describe, expect, it } from 'vitest'
import { followStep } from '../src/features/viewer3d/follow'

const v = (x: number, y: number, z: number) => ({ x, y, z })

describe('followStep（跟随无人机只按位移增量）', () => {
  it('无人机不动时，相机与注视点都不动——平移松手后不会被拉回原处', () => {
    const camera = v(150, 130, 190)
    const target = v(40, 0, -30) // 用户自己拖出去的位置
    const at = v(73.55, 40, -94.8)
    const next = followStep(camera, target, at, v(73.55, 40, -94.8))
    expect(camera).toEqual(v(150, 130, 190))
    expect(target).toEqual(v(40, 0, -30))
    expect(next).toEqual(at)
  })

  it('无人机平移多少，相机与注视点就平移多少，相对位姿不变', () => {
    const camera = v(150, 130, 190)
    const target = v(40, 0, -30)
    followStep(camera, target, v(83, 40, -94), v(73, 40, -94)) // 往东 10 米
    expect(camera).toEqual(v(160, 130, 190))
    expect(target).toEqual(v(50, 0, -30))
  })

  it('刚勾上跟随（prev 为 null）时把注视点对到无人机，相机保持原有相对位姿', () => {
    const camera = v(150, 130, 190)
    const target = v(0, 0, 0)
    const at = v(100, 40, -50)
    followStep(camera, target, at, null)
    expect(target).toEqual(at)
    expect(camera).toEqual(v(250, 170, 140))
  })

  it('无人机一步跳变超过 500 米时重新对中，而不是把相机甩出几公里', () => {
    const camera = v(150, 130, 190)
    const target = v(0, 0, 0)
    const at = v(3000, 0, 0)
    followStep(camera, target, at, v(0, 0, 0))
    expect(target).toEqual(at)
    expect(camera).toEqual(v(3150, 130, 190))
  })

  it('返回的是无人机位置快照，外部改 at 不会污染下一帧的 prev', () => {
    const at = v(10, 20, 30)
    const next = followStep(v(1, 2, 3), v(0, 0, 0), at, null)
    at.x = 999
    expect(next).toEqual(v(10, 20, 30))
  })
})
