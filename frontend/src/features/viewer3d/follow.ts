/**
 * 「跟随无人机」的相机步进。单独成文件是为了能脱离 WebGL/R3F 直接跑单测
 * （`frontend/tests/viewer-follow.test.ts`），逻辑本身不依赖 three。
 */

/** 相机位置 / 注视点 / 无人机位置都只用到 x、y、z 三个分量（THREE.Vector3 天然满足）。 */
export interface Vec3Like {
  x: number
  y: number
  z: number
}

/** 一步跟随：按无人机的**位移增量**平移相机与注视点，用户自己拖出来的视角原样保留。
 *
 * 为什么不是「把注视点朝无人机插值」（v1 的写法）：那样用户平移出去之后，注视点每帧被拉回无人机，
 * 相机再跟着注视点一起回去，松手一秒内画面就滑回原处——现场反馈「平移完又回到原位置」
 * （m06173 第 6 点）。改为只跟增量后：无人机不动就什么都不动，无人机走多少相机跟多少。
 *
 * prev 为 null（刚勾上跟随 / 数据断流后重新拿到无人机位置）或无人机一步跳变超过 jumpLimitM 时，
 * 把注视点直接对到无人机上、相机保持原有相对位姿（视角方向与距离都不变），免得一次性把用户甩到几公里外。
 *
 * `camera` 与 `target` 会被**原地修改**；返回值是无人机当前位置的快照，调用方留着当下一帧的 prev。
 */
export function followStep(
  camera: Vec3Like,
  target: Vec3Like,
  at: Vec3Like,
  prev: Vec3Like | null,
  jumpLimitM = 500,
): Vec3Like {
  const dx = prev ? at.x - prev.x : 0
  const dy = prev ? at.y - prev.y : 0
  const dz = prev ? at.z - prev.z : 0
  const jumped = Math.sqrt(dx * dx + dy * dy + dz * dz) > jumpLimitM

  if (!prev || jumped) {
    // 重新对中：注视点落在无人机上，相机保持原有的相对偏移。
    const ox = camera.x - target.x
    const oy = camera.y - target.y
    const oz = camera.z - target.z
    target.x = at.x
    target.y = at.y
    target.z = at.z
    camera.x = at.x + ox
    camera.y = at.y + oy
    camera.z = at.z + oz
  } else {
    target.x += dx
    target.y += dy
    target.z += dz
    camera.x += dx
    camera.y += dy
    camera.z += dz
  }

  return { x: at.x, y: at.y, z: at.z }
}
