/**
 * 三维实时视图（React-Three-Fiber / WebGL）。
 *
 * 坐标约定（全项目统一，不可随意改）：
 *   显示坐标系 = 后端 ENU 切平面，原点就是「基准设备」（基座或雷达，由后端选择）。
 *   后端已保证：被选为基准的设备其 ENU 恒为 (0,0,0)，因此前端**不需要任何平移**。
 *   three.js 场景映射：x = East，y = Up，z = -North（即屏幕上「上 = 正北、右 = 正东」）。
 *
 * 性能策略：相对位置帧以 10 Hz 到达，**不写 React state**；所有高频对象在 useFrame 里
 * 直接从 services/live.ts 读取并原地更新 BufferGeometry / InstancedMesh。
 *
 * ⚠️ 只有 <Canvas> **内部**的组件才能调用 useFrame / useThree。渲染在画布之外的 DOM 叠加层
 * （如下方的 Hud）必须改用 requestAnimationFrame —— 一旦在 Canvas 外触发 R3F hook，
 * R3F 会抛「R3F: Hooks can only be used within the Canvas component!」，
 * 整棵 React 树随之卸载，页面只剩深色背景（用户看到的就是「整个网页都是黑的」）。
 *
 * 「雷达探测精度」相关的可视元素（本文件特有，改动时别认错）：
 *   1. 无人机 RTK 位置  = 洋红菱形（八面体线框）+ 水平环 +「RTK」标签，见 DroneLayer；
 *   2. 雷达探测点 ↔ RTK 点 的偏差连线 = 橙虚线（未匹配转暗橙点线），见 DroneComparisonLayer；
 *   3. 对比读数与雷达正前方朝向的具体数值写在 Hud 的两行 .viewer-hud__sub 上。
 * 注意 2 与 DroneLayer 里那两条受 ui.showLinks 控制的测距辅助线是两回事，别把开关串起来。
 */
import { Canvas, useFrame, useThree } from '@react-three/fiber'
import { Html, OrbitControls } from '@react-three/drei'
import { Fragment, useCallback, useEffect, useMemo, useRef } from 'react'
import * as THREE from 'three'
import type { ThreeEvent } from '@react-three/fiber'
import { live } from '../../services/live'
import { useStore } from '../../store/useStore'
import { usePersistentState } from '../common/usePersistentState'
import { followStep } from './follow'
import type { DroneRadarComparison, RelativeFrame, RelativeNode, RelativeTarget } from '../../types'

const MAX_TARGET_INSTANCES = 4000
const MAX_POINT_INSTANCES = 30000
const MAX_TRAIL_SEGMENTS = 40000

/** 点击拾取的屏幕空间半径（像素）。见 TargetsLayer 里自定义 raycast 的说明。 */
const PICK_RADIUS_PX = 12

/**
 * 场景里所有贴附文字的「距离缩放因子」，传给 drei <Html distanceFactor>。
 *
 * drei 逐帧按 `scale = distanceFactor / (2·tan(fov/2)·相机距离)` 缩放标签 DOM，
 * 也就是**拉远变小、拉近变大**——这是刻意的透视逻辑。六处标签（无人机 / RTK / 雷达 /
 * 基座 / 目标 / 方位刻度）必须共用这**同一个常量**，否则同一距离下大小会不一致。
 *
 * 取值依据：fov = 50 ⇒ 2·tan(25°) ≈ 0.9326，`scale = 因子 / (0.9326 × 相机到该标签的距离)`。
 * 默认自由机位在 (150, 130, 190)：原点的设备标签离相机 ≈ 275，而方位刻度在 ±0.55×gridSize
 * （默认 gridSize = 200 ⇒ ±110 m）处、离相机 ≈ 360 —— 两者不在同一个距离上，因子只能折中。
 * 取 320：方位刻度在默认机位下 scale ≈ 0.95（12px 的字渲染成 ~15px 行盒，跟「刚打开时」看到的
 * 自然字号基本一致，这是用户期望的观感），原点附近的设备标签 scale ≈ 1.25（11px 的字略放大到 ~14px）。
 * 早先用的是 90：默认机位只有 0.24~0.35 倍，而 drei 的**第一帧还没施加这个缩放**，于是
 * 「刚打开很清楚、一动视角字就变小」——那是标定不准，不是透视逻辑错。
 * 想整体调大调小就改这一个数，不要在调用处各写各的。
 */
const LABEL_DISTANCE_FACTOR = 320

/**
 * 「数据陈旧」时的统一配色（灰色）。
 * 设备断流后不能继续用实时配色渲染冻结位置——那样看起来和实时一模一样，
 * 现场根本发现不了「其实早就没数据了」。
 */
const STALE_COLOR = new THREE.Color('#61708a')
const STALE_EMISSIVE = new THREE.Color('#0f172a')

// 实时配色（与 JSX 里的初始值保持一致，便于 useFrame 里来回切换）
// 无人机图标是「贴图 × 材质颜色」：新鲜时不染色（白），陈旧时整张图压成灰色（见 tintDroneIcon）。
const DRONE_ICON_COLOR = new THREE.Color('#ffffff')
const RADAR_COLOR = new THREE.Color('#22d3ee')
const RADAR_EMISSIVE = new THREE.Color('#0891b2')
const BASE_COLOR = new THREE.Color('#a78bfa')
const BASE_EMISSIVE = new THREE.Color('#7c3aed')

/**
 * 「无人机 RTK 位置」标记的专属色（洋红）。
 *
 * 为什么不用青色：`#22d3ee` 已经是雷达本体 + 扇面的颜色，目标类型色里还有
 * 0x06b6d4 / 0x0ea5e9 / 0x14b8a6 一串青蓝 —— 青色标记混进目标堆里根本认不出来。
 * 洋红在整套配色里没有任何占用，和雷达目标的红/橙/绿/青、设备标记的白/紫都不撞。
 * （本文件不许改共享的 targetColors.ts，所以就地定义。）
 */
const RTK_HEX = '#f472b6'
const RTK_COLOR = new THREE.Color(RTK_HEX)

/**
 * 「雷达探测点 ↔ RTK 点」对比连线的两种配色：
 *   匹配   —— 亮橙虚线，醒目，表示「这帧探到了，偏差就这么大」；
 *   未匹配 —— 暗橙点线，表示「这帧没探到」，线只连到最近的那个目标上。
 * 橙色同样不与任何目标类型色、选中色（#fde047 黄）冲突。
 */
const COMPARE_MATCHED_HEX = '#fb923c'
const COMPARE_UNMATCHED_HEX = '#a16207'

/** ENU → three.js 场景坐标。 */
function sx(east: number): number {
  return east
}
function sy(up: number): number {
  return up
}
function sz(north: number): number {
  return -north
}

const TYPE_COLORS: Record<number, number> = {
  0: 0x94a3b8, // 未识别
  1: 0xef4444, // 人
  2: 0xf59e0b, // 车
  3: 0x22c55e, // 树
  4: 0x06b6d4, // 船
  // 5 = 空中目标。默认场景里雷达只探得到无人机，这条轨迹必须和无人机 GPS 轨迹
  //（#38bdf8 天蓝）明显区分，所以取品红；取值与 features/targetColors.ts 保持一致。
  5: 0xec4899,
  6: 0x14b8a6, // 小船
  7: 0x0ea5e9, // 中船
  8: 0x6366f1, // 大船
  0xffff: 0x475569, // 已删除
}

function targetColor(type: number): THREE.Color {
  return new THREE.Color(TYPE_COLORS[type] ?? TYPE_COLORS[0])
}

/**
 * 节点数据的「实际年龄」（毫秒）。
 *
 * 为什么不直接用 node.ageMs：ageMs 是**后端生成这一帧时**算出来的。如果整条数据流断掉
 * （不再有新帧到达），services/live.ts 里的 live.frame 会冻结，ageMs 也跟着冻结，
 * 界面就会永远停在「陈旧 3.2 s」，反而看不出已经断了多久。这里再加上「本帧到达之后
 * 本地又流逝的时间」，数据一停，秒数就继续往上走。
 */
function nodeAgeMs(node: RelativeNode): number {
  const drift = live.frameAt > 0 ? performance.now() - live.frameAt : 0
  const age = (Number.isFinite(node.ageMs) ? node.ageMs : 0) + (Number.isFinite(drift) ? drift : 0)
  return age > 0 ? age : 0
}

/**
 * 该节点是否按「陈旧」渲染。满足任一条件即为陈旧：
 *   1) online === false —— 链路断了；
 *   2) fresh === false  —— 后端判定这个节点的数据已过期；
 *   3) 实际年龄超过配置里的 relative.staleTimeoutMs（缺省 3000 ms）—— 覆盖「整个数据流
 *      停摆、后端连新帧都不再产生」的情况：那时 fresh 会一直停留在最后一帧的 true，
 *      只看 fresh 是发现不了的。
 */
function isNodeStale(node: RelativeNode): boolean {
  if (!node.online || !node.fresh) return true
  const timeout = useStore.getState().config?.relative.staleTimeoutMs ?? 3000
  return nodeAgeMs(node) > timeout
}

/** 陈旧标签后缀，如「（数据陈旧 3.2 s）」/「（离线 12.0 s）」。 */
function staleMark(node: RelativeNode): string {
  const seconds = (nodeAgeMs(node) / 1000).toFixed(1)
  return node.online ? `（数据陈旧 ${seconds} s）` : `（离线 ${seconds} s）`
}

/** 只在文本真的变化时才写 DOM，避免每帧做无意义的 DOM 写入。 */
function setText(el: HTMLElement | null, text: string): void {
  if (el && el.textContent !== text) el.textContent = text
}

/**
 * 与 setText 同理，只在颜色真的变化时才写 DOM。
 * 用 dataset 缓存上一次设的值：把 'rgb(251, 191, 36)' 反解回 '#fbbf24' 不值得，
 * 而 `el.style.color` 读回来是 CSSOM 序列化后的 rgb() 形式，直接比字符串会永远不等。
 */
function setColor(el: HTMLElement | null, color: string): void {
  if (!el || el.dataset.color === color) return
  el.dataset.color = color
  el.style.color = color
}

/**
 * 把「无人机 RTK 位置标记」的材质切到实时/陈旧配色。
 *
 * 这几个标记材质都是 MeshBasicMaterial（不受光照影响，暗场景里反而更醒目），
 * 没有 emissive 语义，所以不能直接用 tintNodeMaterial —— 它会把不透明度强制拉成 1，
 * 把外壳变成一坨实心洋红、把机体和白箭头整个盖住。
 */
function tintRtkMarker(material: THREE.MeshBasicMaterial | null, stale: boolean, litOpacity: number): void {
  if (!material) return
  material.color.copy(stale ? STALE_COLOR : RTK_COLOR)
  material.opacity = stale ? litOpacity * 0.3 : litOpacity
}

/**
 * 把设备节点材质切到「实时」或「陈旧」配色。
 * 陈旧 = 灰色 + 半透明 + 去掉自发光，表达「这是最后一次收到的位置，不是当前值」。
 */
function tintNodeMaterial(
  material: THREE.MeshStandardMaterial | null,
  freshColor: THREE.Color,
  freshEmissive: THREE.Color,
  stale: boolean,
  staleOpacity = 0.45,
): void {
  if (!material) return
  material.color.copy(stale ? STALE_COLOR : freshColor)
  material.emissive.copy(stale ? STALE_EMISSIVE : freshEmissive)
  material.opacity = stale ? staleOpacity : 1
}

/** 无人机图标的陈旧态：贴图本身是彩色的，靠材质颜色整体压灰 + 降不透明度。 */
function tintDroneIcon(material: THREE.SpriteMaterial | null, stale: boolean): void {
  if (!material) return
  material.color.copy(stale ? STALE_COLOR : DRONE_ICON_COLOR)
  material.opacity = stale ? 0.45 : 1
}

/** HUD 用：列出当前数据不新鲜的设备；全部正常时返回空串（整行不占位）。 */
function staleSummary(frame: RelativeFrame): string {
  const parts: string[] = []
  const push = (label: string, node: RelativeNode): void => {
    if (!isNodeStale(node)) return
    parts.push(`${label}${staleMark(node)}`)
  }
  push('基座', frame.baseStation)
  push('雷达', frame.radar)
  push('无人机', frame.drone)
  return parts.length > 0 ? `⚠ 数据不新鲜：${parts.join(' · ')}` : ''
}

/** 生成圆形辉光贴图（用 Canvas 现场画，避免引入图片资源）。 */
function makeGlowTexture(): THREE.Texture {
  const size = 64
  const canvas = document.createElement('canvas')
  canvas.width = size
  canvas.height = size
  const ctx = canvas.getContext('2d')!
  const gradient = ctx.createRadialGradient(size / 2, size / 2, 0, size / 2, size / 2, size / 2)
  gradient.addColorStop(0, 'rgba(255,255,255,1)')
  gradient.addColorStop(0.35, 'rgba(255,255,255,0.7)')
  gradient.addColorStop(1, 'rgba(255,255,255,0)')
  ctx.fillStyle = gradient
  ctx.fillRect(0, 0, size, size)
  const texture = new THREE.CanvasTexture(canvas)
  texture.needsUpdate = true
  return texture
}

/** 地面网格 + 方位刻度环 + 罗盘文字。 */
function Ground({ gridSize }: { gridSize: number }): React.ReactElement {
  const rings = useMemo(() => {
    const step = gridSize <= 100 ? 25 : gridSize <= 400 ? 50 : 100
    const out: number[] = []
    for (let r = step; r <= gridSize; r += step) out.push(r)
    return out
  }, [gridSize])

  return (
    <group>
      <gridHelper
        args={[gridSize * 2, Math.max(4, Math.round((gridSize * 2) / (gridSize <= 100 ? 10 : 20))), 0x1e293b, 0x0f172a]}
        position={[0, 0, 0]}
      />
      {rings.map((r) => (
        <mesh key={r} rotation={[-Math.PI / 2, 0, 0]} position={[0, 0.01, 0]}>
          <ringGeometry args={[r - 0.25, r + 0.25, 128]} />
          <meshBasicMaterial color={0x1d4ed8} transparent opacity={0.28} side={THREE.DoubleSide} />
        </mesh>
      ))}
      <CompassLabel position={[0, 0.05, -gridSize * 0.55]} text="北 N" color="#38bdf8" />
      <CompassLabel position={[gridSize * 0.55, 0.05, 0]} text="东 E" color="#64748b" />
      <CompassLabel position={[0, 0.05, gridSize * 0.55]} text="南 S" color="#64748b" />
      <CompassLabel position={[-gridSize * 0.55, 0.05, 0]} text="西 W" color="#64748b" />
      {rings.map((r) => (
        <CompassLabel key={`t${r}`} position={[1.5, 0.02, -r]} text={`${r} m`} color="#475569" small />
      ))}
    </group>
  )
}

function CompassLabel({
  position,
  text,
  color,
  small,
}: {
  position: [number, number, number]
  text: string
  color: string
  small?: boolean
}): React.ReactElement {
  // 方位刻度与场景里其它标签共用 LABEL_DISTANCE_FACTOR：拉远变小、拉近变大。
  // （曾经这里不传 distanceFactor，任何机位都同样大，反而与其它标签的规则不一致。）
  return (
    <Html position={position} center distanceFactor={LABEL_DISTANCE_FACTOR} zIndexRange={[10, 0]} style={{ pointerEvents: 'none' }}>
      <span style={{ color, fontSize: small ? 9 : 12, whiteSpace: 'nowrap', textShadow: '0 0 4px #000' }}>{text}</span>
    </Html>
  )
}

/** 兜底画圆角矩形（不用 ctx.roundRect：老内核与 lib.dom 类型定义都不保证有）。 */
function roundedRect(
  ctx: CanvasRenderingContext2D,
  x: number,
  y: number,
  w: number,
  h: number,
  r: number,
): void {
  ctx.beginPath()
  ctx.moveTo(x + r, y)
  ctx.arcTo(x + w, y, x + w, y + h, r)
  ctx.arcTo(x + w, y + h, x, y + h, r)
  ctx.arcTo(x, y + h, x, y, r)
  ctx.arcTo(x, y, x + w, y, r)
  ctx.closePath()
}

/**
 * 无人机图标：canvas 画一张俯视四旋翼 → CanvasTexture。
 *
 * 为什么不建 3D 模型：四旋翼的几何要十几行、远处还会缩成一根线，而图标在任何视角都认得出来。
 * 画布**上方是机头**，配合 useFrame 里的屏幕空间自转（航向在屏幕上的投影），机头始终指着真实航向。
 * 拿不到 2D 上下文（jsdom 之类）时返回 null，精灵会退化成纯色方块——真机浏览器里不会发生。
 */
function createDroneIconTexture(): THREE.CanvasTexture | null {
  const size = 96
  const canvas = document.createElement('canvas')
  canvas.width = size
  canvas.height = size
  const ctx = canvas.getContext('2d')
  if (!ctx) return null

  const c = size / 2
  const arms: ReadonlyArray<readonly [number, number]> = [
    [-1, -1],
    [1, -1],
    [-1, 1],
    [1, 1],
  ]

  // 机臂：X 形
  ctx.strokeStyle = '#94a3b8'
  ctx.lineWidth = 7
  ctx.lineCap = 'round'
  for (const [dx, dy] of arms) {
    ctx.beginPath()
    ctx.moveTo(c + dx * 6, c + dy * 6)
    ctx.lineTo(c + dx * 29, c + dy * 29)
    ctx.stroke()
  }

  // 四个旋翼：俯视看就是四个圆
  ctx.strokeStyle = '#e2e8f0'
  ctx.lineWidth = 3
  for (const [dx, dy] of arms) {
    ctx.beginPath()
    ctx.arc(c + dx * 31, c + dy * 31, 13, 0, Math.PI * 2)
    ctx.stroke()
  }

  // 机身
  ctx.fillStyle = '#f8fafc'
  ctx.strokeStyle = '#1e293b'
  ctx.lineWidth = 3
  roundedRect(ctx, c - 13, c - 13, 26, 26, 8)
  ctx.fill()
  ctx.stroke()

  // 机头（画在画布上方）
  ctx.fillStyle = '#38bdf8'
  ctx.beginPath()
  ctx.moveTo(c, c - 10)
  ctx.lineTo(c + 9, c - 27)
  ctx.lineTo(c - 9, c - 27)
  ctx.closePath()
  ctx.fill()

  const texture = new THREE.CanvasTexture(canvas)
  texture.colorSpace = THREE.SRGBColorSpace
  texture.anisotropy = 4
  return texture
}

/** 图标边长（米）。太小认不出是无人机，太大就把目标点盖住了。 */
const DRONE_ICON_SIZE_M = 9

/** 航向投影用的临时向量（模块级复用，避免每帧 new 一个 Vector3）。 */
const droneHeadingScratch = new THREE.Vector3()

/**
 * 无人机：图标 + RTK 位置标记 + 与基座/雷达的测距辅助线。
 *
 * 机体是一张 canvas 画的四旋翼图标（billboard 精灵，永远正对相机）：胶囊体从任何角度看都只是
 * 一根白色柱子，图标一眼就能认出是无人机。图标在屏幕里按航向自转（见 useFrame 里的投影），
 * 所以机头始终指着真实航向（m06464 第 3 点）。
 *
 * 陈旧态（意见 7）：设备断流后**不隐藏**——冻结在最后位置 + 明确标注，比直接消失更容易
 * 排查现场问题；但会把图标/标记压成灰色半透明并给标签补上「（数据陈旧 X.X s）」，
 * 免得「冻结的旧位置」看起来像实时。
 *
 * showDrone（m06464 第 2 点）只关掉无人机**本身**：图标、光晕、RTK 标记与两个标签。
 * 测距辅助线与轨迹各有各的开关（showLinks / showTrack），关掉无人机本身不影响它们。
 */
function DroneLayer({
  showTrack,
  showLinks,
  showDrone,
}: {
  showTrack: boolean
  showLinks: boolean
  showDrone: boolean
}): React.ReactElement {
  const group = useRef<THREE.Group>(null)
  const linkRef = useRef<THREE.LineSegments>(null)
  const iconMaterial = useRef<THREE.SpriteMaterial>(null)
  const haloMaterial = useRef<THREE.MeshBasicMaterial>(null)
  const rtkShellMaterial = useRef<THREE.MeshBasicMaterial>(null)
  const rtkEdgeMaterial = useRef<THREE.MeshBasicMaterial>(null)
  const rtkRingMaterial = useRef<THREE.MeshBasicMaterial>(null)
  const labelRef = useRef<HTMLSpanElement>(null)

  const linkGeometry = useMemo(() => {
    const geometry = new THREE.BufferGeometry()
    geometry.setAttribute('position', new THREE.BufferAttribute(new Float32Array(6 * 2 * 3), 3))
    // LineDashedMaterial 是靠顶点属性 lineDistance 算虚线的；缺了这个属性，
    // three.js 会把虚线材质渲染成**实线**（着色器里 vLineDistance 恒 0）。
    // 这里一次性建好（12 个顶点 = 6 段），下面每帧只改数值，不调 computeLineDistances()
    // 以免每帧重新分配一个 Float32BufferAttribute。
    geometry.setAttribute('lineDistance', new THREE.BufferAttribute(new Float32Array(6 * 2), 1))
    return geometry
  }, [])

  // 图标纹理只建一次；组件卸载时显式释放（贴图不归 React 管，不 dispose 就是显存泄漏）
  const iconTexture = useMemo(() => createDroneIconTexture(), [])
  useEffect(() => () => {
    iconTexture?.dispose()
  }, [iconTexture])

  useFrame((state) => {
    const frame = live.frame
    if (!group.current) return
    if (!frame) {
      group.current.visible = false
      if (linkRef.current) linkRef.current.visible = false
      return
    }
    group.current.visible = true
    const node = frame.drone
    const d = node.position
    group.current.position.set(sx(d.east), sy(d.up), sz(d.north))

    // ── 陈旧态：改观感，不隐藏 ────────────────────────────────────────────
    const stale = isNodeStale(node)
    tintDroneIcon(iconMaterial.current, stale)
    if (haloMaterial.current) haloMaterial.current.opacity = stale ? 0.03 : 0.08
    setText(labelRef.current, stale ? `无人机${staleMark(node)}` : '无人机')

    // ── 航向：图标是 billboard，它在屏幕上永远「正着」，不会自己跟着航向转 ──
    // 所以把罗盘航向（顺时针自北，场景里北 = −Z）投影到相机的成像平面上，
    // 直接得到机头在屏幕里该指的角度。航向几乎与视线平行时投影退化（机头指进/指
    // 出屏幕），这时保持上一帧的角度，免得图标乱跳。
    if (iconMaterial.current) {
      const headingRad = THREE.MathUtils.degToRad(node.headingDeg ?? 0)
      droneHeadingScratch
        .set(Math.sin(headingRad), 0, -Math.cos(headingRad))
        .applyQuaternion(state.camera.quaternion)
      if (Math.hypot(droneHeadingScratch.x, droneHeadingScratch.y) > 1e-3) {
        iconMaterial.current.rotation = Math.atan2(-droneHeadingScratch.x, droneHeadingScratch.y)
      }
    }

    // ── RTK 位置标记的陈旧态：同样只压暗、不隐藏 ─────────────────────────
    tintRtkMarker(rtkShellMaterial.current, stale, 0.18)
    tintRtkMarker(rtkEdgeMaterial.current, stale, 0.9)
    tintRtkMarker(rtkRingMaterial.current, stale, 0.9)

    // ── 无人机 ↔ 基座 / 雷达 的连线 ───────────────────────────────────────
    // ⚠ 这两条线**不是物理连线，也不是数据线**：无人机与基座/雷达之间是无线链路，
    //   现场没有任何线缆。它们只是把「无人机到基座」与「无人机到雷达」的**斜距**画出来，
    //   方便一眼比较两个距离（测距辅助线）。正因为容易被误认成硬件连线，
    //   这里用配置项 ui.showLinks 做开关：关掉后两条线完全不渲染。
    if (!showLinks) {
      if (linkRef.current) linkRef.current.visible = false
      return
    }
    const attr = linkGeometry.getAttribute('position') as THREE.BufferAttribute
    const array = attr.array as Float32Array
    const b = frame.baseStation.position
    const r = frame.radar.position
    const write = (i: number, ax: number, ay: number, az: number, bx: number, by: number, bz: number) => {
      array[i * 6] = ax
      array[i * 6 + 1] = ay
      array[i * 6 + 2] = az
      array[i * 6 + 3] = bx
      array[i * 6 + 4] = by
      array[i * 6 + 5] = bz
    }
    write(0, sx(d.east), sy(d.up), sz(d.north), sx(b.east), sy(b.up), sz(b.north))
    write(1, sx(d.east), sy(d.up), sz(d.north), sx(r.east), sy(r.up), sz(r.north))
    attr.needsUpdate = true

    // lineSegments 的 lineDistance 约定：每段两个顶点，起点记 0、终点记段长。
    const distanceAttr = linkGeometry.getAttribute('lineDistance') as THREE.BufferAttribute
    const distances = distanceAttr.array as Float32Array
    for (let i = 0; i < 2; i += 1) {
      const ox = array[i * 6]
      const oy = array[i * 6 + 1]
      const oz = array[i * 6 + 2]
      const tx = array[i * 6 + 3]
      const ty = array[i * 6 + 4]
      const tz = array[i * 6 + 5]
      distances[i * 2] = 0
      distances[i * 2 + 1] = Math.hypot(tx - ox, ty - oy, tz - oz)
    }
    distanceAttr.needsUpdate = true
    if (linkRef.current) linkRef.current.visible = true
  })

  return (
    <group>
      <group ref={group}>
        {/* showDrone 关掉时这一整块不渲染（图标、光晕、RTK 标记、两个标签一起消失）；
            测距辅助线与轨迹在下面，各受自己的开关控制（showLinks / showTrack） */}
        {showDrone ? (
          <>
            {/*
              机体：canvas 画出来的四旋翼图标。用 <sprite>（billboard）而不是贴图平面——
              精灵永远正对相机，俯视/侧视/自由视角看到的都是同一个清晰图标，不会缩成一条线。
              机头朝向在屏幕里补：材质 rotation 按航向的投影自转（见 useFrame）。
            */}
            <sprite scale={[DRONE_ICON_SIZE_M, DRONE_ICON_SIZE_M, 1]}>
              <spriteMaterial
                ref={iconMaterial}
                map={iconTexture ?? undefined}
                color="#ffffff"
                transparent
                depthWrite={false}
              />
            </sprite>
            <mesh position={[0, 0, 0]}>
              <sphereGeometry args={[1.6, 16, 16]} />
              <meshBasicMaterial ref={haloMaterial} color="#38bdf8" transparent opacity={0.08} />
            </mesh>
            {/*
              ── 无人机 RTK 位置标记（任务书第 1 条）────────────────────────────
              这一组只表示「RTK 报出来的无人机位置」，位置就是 group 的位置（= drone.position）。
              为什么是「菱形（八面体）+ 水平环」：雷达目标是**实心球 / 点云**，颜色取类型色
              （红/橙/绿/青…），这里换成几何上完全不同的线框菱形 + 环，再配一个任何目标类型色
              都没用过的洋红，并做得比目标球更大 —— 一堆目标点里一眼就能认出哪个是无人机真值。
              外接半径 2.1 m > 目标球半径 0.8 m（sphereGeometry 0.5 × 实例缩放 1.6）。
              外壳用半透明而不是实心：实心会把机体图标整个罩住，看不出机头朝哪。
            */}
            <mesh>
              <octahedronGeometry args={[2.1, 0]} />
              <meshBasicMaterial
                ref={rtkShellMaterial}
                color={RTK_HEX}
                transparent
                opacity={0.18}
                depthWrite={false}
              />
            </mesh>
            <mesh>
              <octahedronGeometry args={[2.1, 0]} />
              <meshBasicMaterial ref={rtkEdgeMaterial} color={RTK_HEX} wireframe transparent opacity={0.9} />
            </mesh>
            {/* 水平环：给菱形补一个「水平面」参照，悬停/俯视时也能看出高度关系 */}
            <mesh rotation={[-Math.PI / 2, 0, 0]}>
              <ringGeometry args={[2.5, 2.9, 40]} />
              <meshBasicMaterial
                ref={rtkRingMaterial}
                color={RTK_HEX}
                transparent
                opacity={0.9}
                side={THREE.DoubleSide}
                depthWrite={false}
              />
            </mesh>
            <Html position={[0, 2.6, 0]} center distanceFactor={LABEL_DISTANCE_FACTOR} style={{ pointerEvents: 'none' }}>
              {/* 文本由 useFrame 直接改 DOM（陈旧时补「（数据陈旧 X.X s）」后缀），不进 React state */}
              <span ref={labelRef} className="scene-tag scene-tag--drone">
                无人机
              </span>
            </Html>
            {/* RTK 位置标记的文字：颜色用内联 style（index.css 是共享文件，本任务不许改） */}
            <Html position={[0, -2.4, 0]} center distanceFactor={LABEL_DISTANCE_FACTOR} style={{ pointerEvents: 'none' }}>
              <span className="scene-tag" style={{ color: RTK_HEX, borderColor: '#9d174d' }}>
                RTK
              </span>
            </Html>
          </>
        ) : null}
      </group>
      <lineSegments ref={linkRef} geometry={linkGeometry}>
        <lineDashedMaterial color="#38bdf8" transparent opacity={0.55} dashSize={4} gapSize={3} />
      </lineSegments>
      {showTrack && <TrailLine track="drone" color="#38bdf8" width={1} />}
    </group>
  )
}

/**
 * 「雷达探测点 ↔ 无人机 RTK 点」对比层（任务书第 2 条）。
 *
 * 与 DroneLayer 里那两条测距辅助线**不是一回事**：那两条表达「无人机到基座 / 到雷达的斜距」，
 * 由 ui.showLinks 控制；这一条表达**同一帧里「雷达把无人机探在哪儿」与「RTK 报的无人机在哪儿」
 * 差多少**，是校核雷达探测精度的核心图形。因此它**不受 showLinks 影响**：
 * 只要本帧带 droneComparison 就画，为 null（对比关闭 / 参考未解算 / 本帧无目标）时整层不画。
 *
 * 匹配（matched）：亮橙虚线 + 橙色立方体线框标出雷达探测点 ——「探到了，偏差就这么大」。
 * 未匹配       ：暗橙点线 + 压暗标记。注意后端在未匹配时**仍会填最近目标的坐标与距离**，
 *                所以这条线连的是「最近的那个目标」，一眼就能看出雷达这帧到底探到了哪儿、差多远。
 *
 * 虚线靠 LineDashedMaterial + 顶点上的 lineDistance 属性实现：这个属性必须自己写。
 * 缺它时顶点属性取默认值 0，虚线会静默退化成实线（DroneLayer 里那两条测距线就是这样）。
 */
function DroneComparisonLayer(): React.ReactElement {
  const matchedRef = useRef<THREE.LineSegments>(null)
  const unmatchedRef = useRef<THREE.LineSegments>(null)
  const echoRef = useRef<THREE.Mesh>(null)
  const echoMaterial = useRef<THREE.MeshBasicMaterial>(null)
  const labelGroup = useRef<THREE.Group>(null)
  const labelRef = useRef<HTMLSpanElement>(null)

  // 两套几何各 1 段线（2 个顶点）：匹配/未匹配分开持有，切换状态时只改 visible，
  // 不在 useFrame 里改材质参数，也就不会每帧重编译材质。
  const geometries = useMemo(
    () =>
      [0, 1].map(() => {
        const geometry = new THREE.BufferGeometry()
        geometry.setAttribute('position', new THREE.BufferAttribute(new Float32Array(6), 3))
        geometry.setAttribute('lineDistance', new THREE.BufferAttribute(new Float32Array(2), 1))
        return geometry
      }),
    [],
  )

  useFrame(() => {
    const matchedLine = matchedRef.current
    const unmatchedLine = unmatchedRef.current
    const echo = echoRef.current
    const comparison = live.frame?.droneComparison ?? null

    if (!comparison) {
      if (matchedLine) matchedLine.visible = false
      if (unmatchedLine) unmatchedLine.visible = false
      if (echo) echo.visible = false
      if (labelRef.current) labelRef.current.style.display = 'none'
      return
    }

    // 两端点：雷达探测点（= 后端匹配到的那个目标位置）与 RTK 点
    const ax = sx(comparison.radarEast)
    const ay = sy(comparison.radarUp)
    const az = sz(comparison.radarNorth)
    const bx = sx(comparison.rtkEast)
    const by = sy(comparison.rtkUp)
    const bz = sz(comparison.rtkNorth)
    const dx = bx - ax
    const dy = by - ay
    const dz = bz - az
    const length = Math.sqrt(dx * dx + dy * dy + dz * dz)

    for (const geometry of geometries) {
      const positionAttr = geometry.getAttribute('position') as THREE.BufferAttribute
      const positions = positionAttr.array as Float32Array
      positions[0] = ax
      positions[1] = ay
      positions[2] = az
      positions[3] = bx
      positions[4] = by
      positions[5] = bz
      positionAttr.needsUpdate = true
      const distanceAttr = geometry.getAttribute('lineDistance') as THREE.BufferAttribute
      const distances = distanceAttr.array as Float32Array
      distances[0] = 0
      distances[1] = length
      distanceAttr.needsUpdate = true
    }

    if (matchedLine) matchedLine.visible = comparison.matched
    if (unmatchedLine) unmatchedLine.visible = !comparison.matched
    if (echo) {
      echo.visible = true
      echo.position.set(ax, ay, az)
    }
    if (echoMaterial.current) {
      echoMaterial.current.color.set(comparison.matched ? COMPARE_MATCHED_HEX : COMPARE_UNMATCHED_HEX)
      echoMaterial.current.opacity = comparison.matched ? 0.95 : 0.45
    }
    // 偏差读数跟在两点中间：只平移外层 group，Html 自身不进 React 更新
    if (labelGroup.current) {
      labelGroup.current.position.set((ax + bx) / 2, (ay + by) / 2, (az + bz) / 2)
    }
    const label = labelRef.current
    if (label) {
      if (label.style.display !== 'block') label.style.display = 'block'
      setColor(label, comparison.matched ? COMPARE_MATCHED_HEX : COMPARE_UNMATCHED_HEX)
      setText(
        label,
        comparison.matched
          ? `Δ ${fmt(comparison.deltaDistanceM)} m`
          : `未探到 · 最近 ${fmt(comparison.deltaDistanceM)} m`,
      )
    }
  })

  return (
    <group>
      {/* 初始 visible=false：首帧 useFrame 之前不会有半截线/标记出现在原点 */}
      <lineSegments ref={matchedRef} geometry={geometries[0]} frustumCulled={false} visible={false}>
        <lineDashedMaterial color={COMPARE_MATCHED_HEX} dashSize={3.2} gapSize={2.2} />
      </lineSegments>
      <lineSegments ref={unmatchedRef} geometry={geometries[1]} frustumCulled={false} visible={false}>
        <lineDashedMaterial
          color={COMPARE_UNMATCHED_HEX}
          dashSize={0.9}
          gapSize={1.4}
          transparent
          opacity={0.75}
        />
      </lineSegments>
      {/* 雷达探测点标记：立方体线框 —— 形状与目标球、与无人机的菱形都不同 */}
      <mesh ref={echoRef} frustumCulled={false} visible={false}>
        <boxGeometry args={[1.7, 1.7, 1.7]} />
        <meshBasicMaterial ref={echoMaterial} color={COMPARE_MATCHED_HEX} wireframe transparent opacity={0.95} />
      </mesh>
      <group ref={labelGroup}>
        <Html position={[0, 0, 0]} center distanceFactor={LABEL_DISTANCE_FACTOR} style={{ pointerEvents: 'none' }}>
          <span ref={labelRef} className="scene-tag" style={{ display: 'none' }}>
            对比
          </span>
        </Html>
      </group>
    </group>
  )
}

/**
 * 单条轨迹线：直接读 live.droneTrack / 某目标轨迹，原地更新 position 属性。
 *
 * 用 LineSegments 而不是 Line：轨迹里存在**时序断口**（目标离开雷达视场、或数据流中断），
 * 断口两侧的点之间没有真实轨迹，必须整段跳过；Line 只有一个 drawRange，画不出这种断裂。
 * 顶点按 ENU→场景坐标换算（和所有标记点走同一套 sx/sy/sz，否则轨迹会与标记点镜像错位）。
 */
function TrailLine({
  track,
  targetId,
  color,
  width,
}: {
  track: 'drone' | 'target'
  targetId?: number
  color: string
  width: number
}): React.ReactElement | null {
  const lineRef = useRef<THREE.LineSegments>(null)
  const geometry = useMemo(() => {
    const g = new THREE.BufferGeometry()
    g.setAttribute('position', new THREE.BufferAttribute(new Float32Array(3 * 2 * MAX_TRAIL_SEGMENTS), 3))
    g.setDrawRange(0, 0)
    return g
  }, [])

  useFrame(() => {
    const source = track === 'drone' ? live.droneTrack : targetId != null ? live.targetTracks.get(targetId) : undefined
    if (!source) {
      geometry.setDrawRange(0, 0)
      return
    }
    const attr = geometry.getAttribute('position') as THREE.BufferAttribute
    const dst = attr.array as Float32Array
    const src = source.positions
    const breaks = source.breaks
    // 只保留最近 MAX_TRAIL_SEGMENTS 段
    const first = Math.max(0, source.count - MAX_TRAIL_SEGMENTS - 1)
    let bi = 0
    while (bi < breaks.length && breaks[bi] <= first) bi++
    let segments = 0
    for (let i = first; i < source.count - 1 && segments < MAX_TRAIL_SEGMENTS; i++) {
      // breaks[bi] === i+1 表示第 i+1 个点是新一段的起点，(i, i+1) 之间没有真实轨迹。
      while (bi < breaks.length && breaks[bi] <= i) bi++
      if (bi < breaks.length && breaks[bi] === i + 1) {
        bi++
        continue
      }
      const a = i * 3
      const b = a + 3
      const o = segments * 6
      dst[o] = sx(src[a])
      dst[o + 1] = sy(src[a + 1])
      dst[o + 2] = sz(src[a + 2])
      dst[o + 3] = sx(src[b])
      dst[o + 4] = sy(src[b + 1])
      dst[o + 5] = sz(src[b + 2])
      segments++
    }
    attr.needsUpdate = true
    geometry.setDrawRange(0, segments * 2)
    if (lineRef.current) lineRef.current.visible = segments > 0
  })

  const object = useMemo(() => {
    const material = new THREE.LineBasicMaterial({ color: new THREE.Color(color), transparent: true, opacity: 0.85 })
    const line = new THREE.LineSegments(geometry, material)
    line.frustumCulled = false
    return line
    // width 目前不影响 LineBasicMaterial（WebGL 线宽限制），保留参数以便将来换 Line2
  }, [geometry, color, width])

  return <primitive ref={lineRef} object={object} />
}

/** 所有目标的历史轨迹，合成一个 LineSegments 一次绘制。 */
function TargetTrails({ colors }: { colors: boolean }): React.ReactElement {
  const geometry = useMemo(() => {
    const g = new THREE.BufferGeometry()
    g.setAttribute('position', new THREE.BufferAttribute(new Float32Array(MAX_TRAIL_SEGMENTS * 2 * 3), 3))
    g.setAttribute('color', new THREE.BufferAttribute(new Float32Array(MAX_TRAIL_SEGMENTS * 2 * 3), 3))
    g.setDrawRange(0, 0)
    return g
  }, [])

  useFrame(() => {
    const posAttr = geometry.getAttribute('position') as THREE.BufferAttribute
    const colAttr = geometry.getAttribute('color') as THREE.BufferAttribute
    const pos = posAttr.array as Float32Array
    const col = colAttr.array as Float32Array
    let segments = 0
    const maxSegments = MAX_TRAIL_SEGMENTS
    const frame = live.frame
    for (const [id, track] of live.targetTracks) {
      if (segments >= maxSegments) break
      if (track.count < 2) continue
      const type = frame?.targets.find((t) => t.id === id)?.type ?? 0
      const c = colors ? targetColor(type) : new THREE.Color('#64748b')
      const src = track.positions
      const breaks = track.breaks
      const start = Math.max(0, track.count - 400)
      let bi = 0
      while (bi < breaks.length && breaks[bi] <= start) bi++
      for (let i = start; i < track.count - 1 && segments < maxSegments; i++) {
        // 断口：目标出视场后再回来，前后两点之间没有真实轨迹，跳过这一段。
        while (bi < breaks.length && breaks[bi] <= i) bi++
        if (bi < breaks.length && breaks[bi] === i + 1) {
          bi++
          continue
        }
        const a = i * 3
        const b = a + 3
        const o = segments * 6
        pos[o] = sx(src[a])
        pos[o + 1] = sy(src[a + 1])
        pos[o + 2] = sz(src[a + 2])
        pos[o + 3] = sx(src[b])
        pos[o + 4] = sy(src[b + 1])
        pos[o + 5] = sz(src[b + 2])
        col[o] = c.r
        col[o + 1] = c.g
        col[o + 2] = c.b
        col[o + 3] = c.r
        col[o + 4] = c.g
        col[o + 5] = c.b
        segments++
      }
    }
    posAttr.needsUpdate = true
    colAttr.needsUpdate = true
    geometry.setDrawRange(0, segments * 2)
  })

  return (
    <lineSegments geometry={geometry} frustumCulled={false}>
      <lineBasicMaterial vertexColors transparent opacity={0.45} />
    </lineSegments>
  )
}

/** 目标（非点云帧）：InstancedMesh，容量预分配，原地更新矩阵与颜色；支持点击选中（意见 6）。 */
function TargetsLayer(): React.ReactElement {
  const mesh = useRef<THREE.InstancedMesh>(null)
  const dummy = useMemo(() => new THREE.Object3D(), [])
  const color = useMemo(() => new THREE.Color(), [])
  const size = useThree((s) => s.size)
  const selectedId = useStore((s) => s.selectedTargetId)
  /**
   * instanceId → 目标 id 的映射表（**保证点击不错位的关键**）。
   *
   * 不变式：写入这张表的循环，就是写 setMatrixAt 的那个循环，两者用同一个下标 i、
   * 同一帧的同一个目标对象 t，且每帧把长度截到与 instanced.count 完全一致：
   *      第 i 个实例  ⇔  第 i 个矩阵  ⇔  idByInstance.current[i] === targets[i].id
   * 因此 event.instanceId 直接就是这张表的下标，不需要任何反查/猜测。
   * 用 ref 而不是 state：它每帧都变，属于高频数据，不许进 React（性能红线）。
   */
  const idByInstance = useRef<number[]>([])
  /**
   * 每个实例中心的场景坐标（与 idByInstance 同下标、同源写入）。
   * 拾取时只读这份 Float32Array，不用 getMatrixAt 反解矩阵 —— 指针每移动一次都会触发
   * raycast，这样可以省掉「矩阵分解 × 上千实例」的开销，也不在每帧产生垃圾对象。
   */
  const instancePositions = useRef<Float32Array>(new Float32Array(MAX_TARGET_INSTANCES * 3))

  useFrame(() => {
    const instanced = mesh.current
    if (!instanced) return
    const frame: RelativeFrame | null = live.frame
    if (!frame || frame.isPointCloud) {
      instanced.count = 0
      idByInstance.current.length = 0
      return
    }
    const targets = frame.targets
    const n = Math.min(targets.length, MAX_TARGET_INSTANCES)
    const map = idByInstance.current
    const positions = instancePositions.current
    for (let i = 0; i < n; i++) {
      const t = targets[i]
      map[i] = t.id // ← 与下面 setMatrixAt(i) 严格同源（同一个 i、同一帧的同一个 t）
      const x = sx(t.east)
      const y = sy(t.up)
      const z = sz(t.north)
      positions[i * 3] = x
      positions[i * 3 + 1] = y
      positions[i * 3 + 2] = z
      dummy.position.set(x, y, z)
      dummy.scale.setScalar(selectedId === t.id ? 2.6 : 1.6)
      dummy.updateMatrix()
      instanced.setMatrixAt(i, dummy.matrix)
      if (selectedId != null && selectedId === t.id) {
        color.set('#fde047')
      } else {
        color.copy(targetColor(t.type))
      }
      instanced.setColorAt(i, color)
    }
    map.length = n
    instanced.count = n
    instanced.instanceMatrix.needsUpdate = true
    if (instanced.instanceColor) instanced.instanceColor.needsUpdate = true
  })

  /**
   * 自定义拾取（意见 6）。目标球在世界里往往只有 1~2 px 大，用默认的几何体射线检测
   * 基本点不中；这里改成「屏幕空间容差」：拿每个实例中心算它到指针射线的垂直距离，
   * 落在 PICK_RADIUS_PX 像素对应的世界尺寸以内就算命中，取最近的那个实例，并把**它真实的
   * instanceId** 写进交点里 —— id 仍由 targets 数组下标决定，与绘制、idByInstance 同源，不会错位。
   */
  const pickRaycast = useCallback<THREE.InstancedMesh['raycast']>(
    (raycaster, intersects) => {
      const instanced = mesh.current
      if (!instanced || instanced.count === 0) return
      const camera = raycaster.camera as THREE.PerspectiveCamera
      if (!camera || !Number.isFinite(camera.fov)) return
      const positions = instancePositions.current
      const ox = raycaster.ray.origin.x
      const oy = raycaster.ray.origin.y
      const oz = raycaster.ray.origin.z
      const dx = raycaster.ray.direction.x
      const dy = raycaster.ray.direction.y
      const dz = raycaster.ray.direction.z
      // pixel → 世界的换算：该深度上视野高度 = 2·d·tan(fov/2)，对应 size.height 像素
      const pixelToWorld = (depth: number): number =>
        (2 * depth * Math.tan(THREE.MathUtils.degToRad(camera.fov) / 2)) / Math.max(1, size.height)
      let bestId = -1
      let bestDistance = Infinity
      let bestX = 0
      let bestY = 0
      let bestZ = 0
      for (let i = 0; i < instanced.count; i++) {
        const px = positions[i * 3]
        const py = positions[i * 3 + 1]
        const pz = positions[i * 3 + 2]
        const vx = px - ox
        const vy = py - oy
        const vz = pz - oz
        const along = vx * dx + vy * dy + vz * dz
        if (along <= 0) continue // 在相机背后
        const squared = vx * vx + vy * vy + vz * vz
        // 点到射线的垂直距离 = √(|v|² − (v·d)²)
        const perpendicular = Math.sqrt(Math.max(0, squared - along * along))
        if (perpendicular > PICK_RADIUS_PX * pixelToWorld(along)) continue
        const distance = Math.sqrt(squared)
        if (distance < bestDistance) {
          bestDistance = distance
          bestId = i
          bestX = px
          bestY = py
          bestZ = pz
        }
      }
      if (bestId < 0) return
      intersects.push({
        distance: bestDistance,
        point: new THREE.Vector3(bestX, bestY, bestZ),
        object: instanced,
        instanceId: bestId,
      })
    },
    [size.height],
  )

  const handleClick = useCallback((event: ThreeEvent<MouseEvent>) => {
    // 转动视角也算「点击」，位移大了就当它是在操作相机，不改选中
    if (event.delta > 5) return
    const instanceId = event.instanceId
    if (instanceId == null) return
    const id = idByInstance.current[instanceId]
    if (id == null) return
    event.stopPropagation()
    // 用 getState() 而不是订阅 hook：避免为了这次点击让整个场景重渲染
    const store = useStore.getState()
    // 再次点击同一个目标 = 取消选中
    store.selectTarget(store.selectedTargetId === id ? null : id)
  }, [])

  return (
    <instancedMesh
      ref={mesh}
      args={[undefined, undefined, MAX_TARGET_INSTANCES]}
      frustumCulled={false}
      raycast={pickRaycast}
      onClick={handleClick}
    >
      <sphereGeometry args={[0.5, 10, 8]} />
      <meshStandardMaterial roughness={0.35} metalness={0.05} vertexColors />
    </instancedMesh>
  )
}

/** 点云单独用真正的 Points，性能更好；此层在 isPointCloud 帧时替换 TargetsLayer。 */
function PointCloudLayer(): React.ReactElement {
  const points = useRef<THREE.Points>(null)
  const geometry = useMemo(() => {
    const g = new THREE.BufferGeometry()
    g.setAttribute('position', new THREE.BufferAttribute(new Float32Array(MAX_POINT_INSTANCES * 3), 3))
    g.setAttribute('color', new THREE.BufferAttribute(new Float32Array(MAX_POINT_INSTANCES * 3), 3))
    g.setDrawRange(0, 0)
    return g
  }, [])
  const glow = useMemo(() => makeGlowTexture(), [])
  const pointSize = useStore((s) => s.config?.ui.pointSize ?? 1)

  useEffect(() => () => glow.dispose(), [glow])

  useFrame(() => {
    const frame = live.frame
    if (!frame || !frame.isPointCloud) {
      geometry.setDrawRange(0, 0)
      return
    }
    const posAttr = geometry.getAttribute('position') as THREE.BufferAttribute
    const colAttr = geometry.getAttribute('color') as THREE.BufferAttribute
    const pos = posAttr.array as Float32Array
    const col = colAttr.array as Float32Array
    const n = Math.min(frame.targets.length, MAX_POINT_INSTANCES)
    for (let i = 0; i < n; i++) {
      const t = frame.targets[i]
      pos[i * 3] = sx(t.east)
      pos[i * 3 + 1] = sy(t.up)
      pos[i * 3 + 2] = sz(t.north)
      const k = Math.max(0, Math.min(1, t.snr / 40))
      col[i * 3] = 0.25 + 0.75 * k
      col[i * 3 + 1] = 0.8
      col[i * 3 + 2] = 1
    }
    posAttr.needsUpdate = true
    colAttr.needsUpdate = true
    geometry.setDrawRange(0, n)
    if (points.current) points.current.visible = n > 0
  })

  return (
    <points ref={points} geometry={geometry} frustumCulled={false}>
      <pointsMaterial
        size={1.4 * pointSize}
        sizeAttenuation
        map={glow}
        vertexColors
        transparent
        depthWrite={false}
        blending={THREE.AdditiveBlending}
      />
    </points>
  )
}

/**
 * 雷达方位角输出范围的一半（度）。
 * NSR 协议给出的方位角范围是 ±90°，即雷达只能看**正前方半球**，后半球是盲区；
 * 扇面若画成 ±60° 会让人误以为两侧还有 30° 没探到，与实采数据对不上。
 */
const RADAR_HALF_FOV_DEG = 90

/**
 * 雷达探测扇面：以雷达为顶点，朝向为「探测方向在显示坐标系中的方位角」（罗盘角，顺时针自北）。
 *
 * 几何直接建在场景水平面上（世界系 x = East、z = -North，见文件头），所以朝向只要绕 Y 轴转
 * `-yawDeg` —— 与无人机航向锥（DroneLayer 里 `rotation.y = degToRad(-headingDeg)`）同一套惯例。
 * ⚠ 别改回「先画在 XY 平面、再把整组绕 X 转 90°」：z = -North 让世界水平面是 East-South 的
 * 左手平面，那样转出来的扇面会正好指到正前方 180° 的反方向（扇面朝南、而雷达在探北）。
 */
function RadarFov({
  rangeM,
  yawDeg,
  halfAngleDeg,
  materialRef,
  spinRef,
}: {
  rangeM: number
  yawDeg: number
  halfAngleDeg: number
  /** 由 RadarLayer 持有，用于陈旧时压淡扇面。 */
  materialRef?: React.Ref<THREE.MeshBasicMaterial>
  /** 由 RadarLayer 持有，供每帧按解算出的正前方实时改朝向。 */
  spinRef?: React.Ref<THREE.Group>
}): React.ReactElement {
  const geometry = useMemo(() => {
    const segments = 48
    const half = THREE.MathUtils.degToRad(halfAngleDeg)
    const positions: number[] = [0, 0, 0]
    const indices: number[] = []
    for (let i = 0; i <= segments; i++) {
      const a = -half + (2 * half * i) / segments
      // a = 相对正前方的偏角；正前方在场景里是 -Z（正北），东侧为 +X
      positions.push(Math.sin(a) * rangeM, 0, -Math.cos(a) * rangeM)
    }
    for (let i = 1; i <= segments; i++) indices.push(0, i, i + 1)
    const geometry = new THREE.BufferGeometry()
    geometry.setAttribute('position', new THREE.Float32BufferAttribute(positions, 3))
    geometry.setIndex(indices)
    return geometry
  }, [rangeM, halfAngleDeg])

  return (
    <group ref={spinRef} rotation={[0, THREE.MathUtils.degToRad(-yawDeg), 0]}>
      <mesh geometry={geometry} position={[0, 0, 0]}>
        <meshBasicMaterial ref={materialRef} color="#22d3ee" transparent opacity={0.07} side={THREE.DoubleSide} />
      </mesh>
    </group>
  )
}

/** 雷达本体标记 + 探测扇面。陈旧时（见 isNodeStale）压成灰色半透明并标注数据年龄。 */
function RadarLayer({ rangeM, yawDeg }: { rangeM: number; yawDeg: number }): React.ReactElement {
  const group = useRef<THREE.Group>(null)
  const fovSpin = useRef<THREE.Group>(null)
  const bodyMaterial = useRef<THREE.MeshStandardMaterial>(null)
  const fovMaterial = useRef<THREE.MeshBasicMaterial>(null)
  const labelRef = useRef<HTMLSpanElement>(null)
  useFrame(() => {
    const frame = live.frame
    const node = frame?.radar
    if (!group.current) return
    if (!node) {
      group.current.visible = false
      return
    }
    group.current.visible = true
    group.current.position.set(sx(node.position.east), sy(node.position.up), sz(node.position.north))
    // 扇面朝向必须跟**解算出的**正前方走：yawSource = baseHeadingPlusOffset 时正前方由
    // 基座双天线基线航向实时推算，config.radar.yawDeg（手动绝对角）只是它的兜底值。
    const boresight = frame?.radarBoresightDeg
    if (fovSpin.current && typeof boresight === 'number' && Number.isFinite(boresight)) {
      fovSpin.current.rotation.y = THREE.MathUtils.degToRad(-boresight)
    }
    const stale = isNodeStale(node)
    tintNodeMaterial(bodyMaterial.current, RADAR_COLOR, RADAR_EMISSIVE, stale)
    // 扇面只表示探测范围，陈旧时进一步压淡，避免被误读成「雷达正在扫」
    if (fovMaterial.current) fovMaterial.current.opacity = stale ? 0.03 : 0.07
    setText(labelRef.current, stale ? `雷达${staleMark(node)}` : '雷达')
  })
  return (
    <group ref={group}>
      <mesh position={[0, 1.4, 0]} rotation={[0, 0, 0]}>
        <cylinderGeometry args={[0.35, 0.9, 2.8, 12]} />
        <meshStandardMaterial
          ref={bodyMaterial}
          color="#22d3ee"
          emissive="#0891b2"
          emissiveIntensity={0.5}
          transparent
        />
      </mesh>
      <RadarFov rangeM={rangeM} yawDeg={yawDeg} halfAngleDeg={RADAR_HALF_FOV_DEG} materialRef={fovMaterial} spinRef={fovSpin} />
      <Html position={[0, 4, 0]} center distanceFactor={LABEL_DISTANCE_FACTOR} style={{ pointerEvents: 'none' }}>
        <span ref={labelRef} className="scene-tag scene-tag--radar">
          雷达
        </span>
      </Html>
    </group>
  )
}

/** 基座标记。陈旧时（见 isNodeStale）压成灰色半透明并标注数据年龄。 */
function BaseStationLayer(): React.ReactElement {
  const group = useRef<THREE.Group>(null)
  const poleMaterial = useRef<THREE.MeshStandardMaterial>(null)
  const headMaterial = useRef<THREE.MeshStandardMaterial>(null)
  const labelRef = useRef<HTMLSpanElement>(null)
  useFrame(() => {
    const node = live.frame?.baseStation
    if (!group.current) return
    if (!node) {
      group.current.visible = false
      return
    }
    group.current.visible = true
    group.current.position.set(sx(node.position.east), sy(node.position.up), sz(node.position.north))
    const stale = isNodeStale(node)
    if (poleMaterial.current) poleMaterial.current.opacity = stale ? 0.45 : 1
    if (poleMaterial.current) poleMaterial.current.color.copy(stale ? STALE_COLOR : BASE_COLOR)
    tintNodeMaterial(headMaterial.current, BASE_COLOR, BASE_EMISSIVE, stale)
    setText(labelRef.current, stale ? `基座${staleMark(node)}` : '基座')
  })
  return (
    <group ref={group}>
      <mesh position={[0, 1.6, 0]}>
        <cylinderGeometry args={[0.12, 0.12, 3.2, 8]} />
        <meshStandardMaterial ref={poleMaterial} color="#a78bfa" transparent />
      </mesh>
      <mesh position={[0, 3.4, 0]}>
        <sphereGeometry args={[0.45, 16, 16]} />
        <meshStandardMaterial
          ref={headMaterial}
          color="#a78bfa"
          emissive="#7c3aed"
          emissiveIntensity={0.6}
          transparent
        />
      </mesh>
      <Html position={[0, 4.6, 0]} center distanceFactor={LABEL_DISTANCE_FACTOR} style={{ pointerEvents: 'none' }}>
        <span ref={labelRef} className="scene-tag scene-tag--base">
          基座
        </span>
      </Html>
    </group>
  )
}

/** 视角模式。俯视/侧视是「看图纸」用的固定机位：锁定旋转，只允许平移与缩放。 */
export type ViewMode = 'top' | 'side' | 'free'

const VIEW_MODES: ReadonlyArray<{ id: ViewMode; label: string; hint: string }> = [
  { id: 'top', label: '俯视', hint: '从正上方俯视（正北朝上）：锁定旋转，只可平移与缩放' },
  { id: 'side', label: '侧视', hint: '从正南向北看的高度剖面：锁定旋转，只可平移与缩放' },
  { id: 'free', label: '自由', hint: '任意旋转，含地平线以下的仰视；默认斜视角' },
]

/** 各模式的默认机位。世界系：x = 东，y = 天，z = −北（+z 朝南）。 */
const HOME_VIEWS: Record<ViewMode, (d: number) => THREE.Vector3> = {
  // 正上方留 0.001 的横向偏移：lookAt 与世界 Y 轴完全共线时相机姿态无解（画面会抖）。
  top: (d) => new THREE.Vector3(0, d * 1.15, 0.001),
  side: (d) => new THREE.Vector3(0, d * 0.06, d * 1.15),
  free: (d) => new THREE.Vector3(d * 0.75, d * 0.65, d * 0.95),
}

/** 相机：可选跟随无人机；每次 cameraNonce / view / gridSize 变化就重置到当前视角的默认机位。 */
function CameraRig({
  follow,
  view,
  gridSize,
}: {
  follow: boolean
  view: ViewMode
  gridSize: number
}): null {
  const controls = useThree((s) => s.controls) as { target: THREE.Vector3; update: () => void } | null
  const camera = useThree((s) => s.camera)
  const nonce = useStore((s) => s.cameraNonce)
  /** 上一帧的无人机位置。跟随只按增量走（见 followStep），所以必须记住上一帧在哪。 */
  const lastDrone = useRef<{ x: number; y: number; z: number } | null>(null)

  useEffect(() => {
    const d = Math.max(200, gridSize)
    const home = HOME_VIEWS[view](d)
    camera.position.set(home.x, home.y, home.z)
    camera.lookAt(0, 0, 0)
    if (controls) {
      controls.target.set(0, 0, 0)
      controls.update()
    }
    // nonce / view / gridSize 变化即重置
  }, [nonce, view, gridSize, camera, controls])

  useFrame(() => {
    if (!follow || !controls) {
      lastDrone.current = null
      return
    }
    const node = live.frame?.drone
    if (!node) {
      lastDrone.current = null
      return
    }
    const at = { x: sx(node.position.east), y: sy(node.position.up), z: sz(node.position.north) }
    lastDrone.current = followStep(camera.position, controls.target, at, lastDrone.current)
    controls.update()
  })

  return null
}

/** 轨迹过期：每帧按「尾迹时长」逐点丢过期点，数据断流超过陈旧阈值就整条清掉。
 *
 * 放在 Canvas 内用 useFrame（跟着渲染循环走，不额外起定时器）；配置用 getState 现取，
 * 现场改工具条上的「尾迹」输入框立刻生效，不需要重挂组件。
 *
 * 尾迹允许填 0：0 表示不画尾迹（当场把已有点全部过期掉）。这里**不能**再兜一个最小值，
 * 否则「0 = 不显示尾迹」永远失效——过期判定用的是严格小于 cutoff，所以 0 就是全清。
 */
function TrackTicker(): null {
  useFrame(() => {
    const state = useStore.getState()
    const trailSeconds = state.config?.ui.trailSeconds ?? 60
    const staleMs = state.config?.relative.staleTimeoutMs ?? 3000
    live.tick(Math.max(0, trailSeconds) * 1000, Math.max(2000, staleMs))
  })
  return null
}

function Scene({ view }: { view: ViewMode }): React.ReactElement {
  const ui = useStore((s) => s.config?.ui)
  const relative = useStore((s) => s.config?.relative)
  const follow = useStore((s) => s.followDrone)
  const showPointCloud = ui?.showPointCloud ?? true
  const showTargets = ui?.showTargets ?? true
  const showTrails = ui?.showTargetTrails ?? true
  const showDroneTrack = ui?.showDroneTrack ?? true
  // m06464 第 2 点：无人机本身（图标 + 光晕 + RTK 位置标记 + 标签）单独一个开关
  const showDrone = ui?.showDrone ?? true
  // 意见 5：无人机→基座 / 无人机→雷达的**测距辅助线**开关（不是物理连线，现场没有任何线缆）
  const showLinks = ui?.showLinks ?? true
  const gridSize = ui?.gridSizeM ?? 200
  // 扇面朝向的**兜底值**：首帧到达前先用配置里的手动绝对角，之后由 RadarLayer 每帧换成解算出的
  // radarBoresightDeg。
  const yaw = relative?.radar.yawDeg ?? 0

  return (
    <>
      <color attach="background" args={['#050914']} />
      <fog attach="fog" args={['#050914', gridSize * 2.2, gridSize * 6]} />
      <hemisphereLight args={['#94a3b8', '#020617', 1.1]} />
      <directionalLight position={[80, 140, 60]} intensity={1.4} />
      <directionalLight position={[-90, 60, -80]} intensity={0.5} color="#38bdf8" />

      <Ground gridSize={gridSize} />
      <BaseStationLayer />
      <RadarLayer rangeM={gridSize} yawDeg={yaw} />
      <DroneLayer showTrack={showDroneTrack} showLinks={showLinks} showDrone={showDrone} />
      {/* 雷达探测 ↔ RTK 对比：**不受 showLinks 影响**，只要本帧有 droneComparison 就画 */}
      <DroneComparisonLayer />
      {showTargets && <TargetsLayer />}
      {showPointCloud && <PointCloudLayer />}
      {showTrails && <TargetTrails colors />}

      <OrbitControls
        makeDefault
        enableDamping
        dampingFactor={0.08}
        // 自由视角要能转到地平线以下（原来卡在 Math.PI / 1.95，只能从上方看半个球）；
        // 俯视/侧视是校准用的固定机位，锁掉旋转，只留平移与缩放。
        enableRotate={view === 'free'}
        // 鼠标/触摸映射写死，不吃 OrbitControls 的默认值：默认左键＝旋转、中键＝缩放、右键＝平移，
        // 而俯视/侧视下 enableRotate=false，左键就整个变成「按下去没反应」——现场因此反馈
        // 「三维视图平移不了」。现在俯视/侧视左键即平移，自由视角左键旋转，任何模式右键都能平移。
        mouseButtons={{
          LEFT: view === 'free' ? THREE.MOUSE.ROTATE : THREE.MOUSE.PAN,
          MIDDLE: THREE.MOUSE.DOLLY,
          RIGHT: THREE.MOUSE.PAN,
        }}
        touches={{
          ONE: view === 'free' ? THREE.TOUCH.ROTATE : THREE.TOUCH.PAN,
          TWO: THREE.TOUCH.DOLLY_PAN,
        }}
        minDistance={4}
        maxDistance={Math.max(600, gridSize * 8)}
      />
      <CameraRig follow={follow} view={view} gridSize={gridSize} />
      <TrackTicker />
    </>
  )
}

/** 画布外叠加的 HUD：原点、距离、目标数、更新率、设备数据陈旧提示。 */
function Hud(): React.ReactElement {
  const status = useStore((s) => s.status)
  const ref = useRef<HTMLDivElement>(null)
  const staleRef = useRef<HTMLDivElement>(null)
  const compareRef = useRef<HTMLDivElement>(null)
  const boresightRef = useRef<HTMLDivElement>(null)
  const lastFrameSeq = useRef(-1)
  const rate = useRef(0)
  const rateWindow = useRef<number[]>([])

  // Hud 渲染在 <Canvas> **之外**（它是 DOM 叠加层），所以绝对不能用 useFrame：
  // R3F 在 Canvas 上下文之外调用其 hook 会抛
  // 「R3F: Hooks can only be used within the Canvas component!」，
  // 该异常会让整棵 React 树卸载，页面只剩 body 的深色背景（表现为「整个网页都是黑的」）。
  // 这里与 Viewer2D / MapPanel 用同一套策略：rAF 直接改 textContent，高频数据不进 React state。
  useEffect(() => {
    let raf = 0
    let disposed = false

    const tick = (): void => {
      if (disposed) return
      raf = requestAnimationFrame(tick)
      const el = ref.current
      if (!el) return
      const frame = live.frame
      if (!frame) {
        el.textContent = '等待数据…'
        setText(staleRef.current, '')
        setText(compareRef.current, '')
        setText(boresightRef.current, '')
        return
      }
      if (frame.sequence !== lastFrameSeq.current) {
        lastFrameSeq.current = frame.sequence
        const now = performance.now()
        rateWindow.current.push(now)
        while (rateWindow.current.length > 0 && now - rateWindow.current[0] > 2000) rateWindow.current.shift()
        rate.current = (rateWindow.current.length / 2) * 1
      }
      const d = frame.drone.position
      const parts = [
        `原点 ${frame.originName}${frame.referenceResolved ? '' : '（未解算）'}`,
        `无人机 E ${d.east.toFixed(1)} N ${d.north.toFixed(1)} U ${d.up.toFixed(1)} m`,
        `离基座 ${fmt(frame.droneDistanceToBaseM)} m · 相对高 ${fmt(frame.droneHeightAboveBaseM)} m`,
        `离雷达 ${fmt(frame.droneDistanceToRadarM)} m · 基线 ${frame.radarBaseLineM.toFixed(2)} m`,
        `目标 ${frame.targets.length} 个${frame.isPointCloud ? '（点云帧）' : ''}`,
        `相对位置 ${rate.current.toFixed(1)} Hz`,
      ]
      el.textContent = parts.join('　|　')
      // 意见 7：哪个设备没有数据（陈旧/离线）必须写在 HUD 上，全是实时数据时这一行为空
      setText(staleRef.current, staleSummary(frame))

      // ── 雷达探测 vs RTK 对比读数（任务书第 3 条）────────────────────────
      const comparison = frame.droneComparison
      if (comparison) {
        setText(compareRef.current, comparisonText(comparison))
        setColor(compareRef.current, comparison.matched ? '#fdba74' : '#fbbf24')
      } else {
        // null = 对比功能关闭 / 参考系未解算 / 本帧没有任何雷达目标
        setText(compareRef.current, '雷达探测对比：本帧无对比数据（未启用 / 参考未解算 / 无目标）')
        setColor(compareRef.current, '#94a3b8')
      }
      // ── 雷达正前方指向 + 朝向来源（任务书第 4 条）──────────────────────
      setText(boresightRef.current, boresightText(frame))
      setColor(boresightRef.current, frame.radarBoresightFromBaseline ? '#7dd3fc' : '#fbbf24')
    }

    raf = requestAnimationFrame(tick)
    return () => {
      disposed = true
      cancelAnimationFrame(raf)
    }
  }, [])

  return (
    <div className="viewer-hud">
      <div ref={ref} className="viewer-hud__line">
        等待数据…
      </div>
      {/* 设备数据陈旧/离线提示（意见 7）。没有陈旧设备时文本为空，这一行不占高度 */}
      <div ref={staleRef} className="viewer-hud__sub" style={{ color: '#fbbf24' }} />
      {/*
        「雷达探测 vs RTK」对比读数（任务书第 3 条）：匹配时给三维/水平/高度偏差 +
        距离差/方位差/俯仰差 + 目标号；未匹配时只说「没探到 + 最近目标多远 + 匹配半径」。
        文本和颜色都由上面的 rAF 逐帧写 DOM（不进 React state），初始为空、不占高度。
        允许换行（.viewer-hud__sub 默认 white-space: pre）：这行比主行还长，不换行会被视口截掉。
      */}
      <div
        ref={compareRef}
        className="viewer-hud__sub"
        style={{ whiteSpace: 'normal', maxWidth: 380, color: '#fdba74' }}
      />
      {/* 雷达正前方指向 + 朝向来源（任务书第 4 条）：基线推算=冷色，手动绝对角=琥珀色 */}
      <div
        ref={boresightRef}
        className="viewer-hud__sub"
        style={{ whiteSpace: 'normal', maxWidth: 380, color: '#7dd3fc' }}
      />
      {status && (
        <>
          <div className="viewer-hud__sub">
            原始 {status.storage.rawRecords} 条 · 解析 {status.storage.parsedRecords} 条 · 雷达转基座系{' '}
            {status.storage.radarBaseRecords} 条 · 三设备同帧 {status.storage.frameRecords} 帧 ·{' '}
            {(status.storage.totalBytes / 1024 / 1024).toFixed(1)} MB
          </div>
          {/*
            会话目录：必须显示**完整绝对路径**（用户原话「不知道文件存到哪去了」）。
            以前这里写的是 sessionDirectory.split(/[\\/]/).pop()，只留最后一段文件夹名
            （例如 20260929_120956），等于没说清楚文件到底存在哪儿。
            路径不截断：过长时用 max-width + word-break 换行，title 里再放一份全文。
            ⚠ 只有这一行开 pointer-events（可以选中、复制路径），容器仍是 pointer-events:none，
              不会挡住画布上的点击选目标。
          */}
          <div
            className="viewer-hud__sub"
            title={status.storage.sessionDirectory}
            style={{
              pointerEvents: 'auto',
              userSelect: 'text',
              whiteSpace: 'normal',
              wordBreak: 'break-all',
              maxWidth: 320,
            }}
          >
            会话目录{' '}
            {status.storage.sessionDirectory ||
              (status.storage.enabled ? '（尚未开始采集）' : '（未启用存储）')}
          </div>
        </>
      )}
    </div>
  )
}

/** 目标详情浮层要显示的字段（数组顺序即显示顺序）。取值函数每帧从实时帧现算。 */
const TARGET_DETAIL_FIELDS: ReadonlyArray<{
  key: string
  label: string
  value: (target: RelativeTarget, frame: RelativeFrame) => string
}> = [
  { key: 'id', label: '目标 ID', value: (t) => String(t.id) },
  { key: 'type', label: '类型', value: (t) => `${t.typeName || '未知'}（type = ${t.type}）` },
  { key: 'pos', label: '位置 E/N/U', value: (t) => `${fmt(t.east)} / ${fmt(t.north)} / ${fmt(t.up)} m` },
  { key: 'range', label: '离雷达距离', value: (t) => `${fmt(t.rangeM)} m` },
  { key: 'azimuth', label: '方位角', value: (t) => `${fmt(t.azimuthDeg)}°` },
  { key: 'elevation', label: '俯仰角', value: (t) => `${fmt(t.elevationDeg)}°` },
  { key: 'drone', label: '离无人机', value: (t, f) => targetDistanceToDrone(t, f) },
]

/** 目标到无人机的直线距离：优先用后端算好的 distanceToDroneM，缺了就用 E/N/U 自己算。 */
function targetDistanceToDrone(target: RelativeTarget, frame: RelativeFrame): string {
  if (target.distanceToDroneM != null && Number.isFinite(target.distanceToDroneM)) {
    return `${target.distanceToDroneM.toFixed(1)} m`
  }
  const d = frame.drone.position
  const de = target.east - d.east
  const dn = target.north - d.north
  const du = target.up - d.up
  const distance = Math.sqrt(de * de + dn * dn + du * du)
  return Number.isFinite(distance) ? `${distance.toFixed(1)} m（按 E/N/U 自算）` : '—'
}

/**
 * 目标详情浮层（意见 6）：点击三维视图里的目标后，在这里显示该目标的详情。
 *
 * 与 Hud 一样渲染在 <Canvas> **之外**，所以绝对不能用 useFrame —— 在 Canvas 上下文之外
 * 调用 R3F hook 会抛「R3F: Hooks can only be used within the Canvas component!」，
 * 整棵 React 树随之卸载，页面全黑。这里同样用 requestAnimationFrame 直接改 DOM。
 *
 * 字段值每帧都在变（目标约 10 Hz 更新），所以：结构由 React 只渲染一次，
 * 之后 rAF 里只写 textContent / style.display，**高频数据不进 React state**（性能红线）。
 *
 * 没有选中目标、或选中的目标已经不在当前帧里（目标消失、切到点云帧）时，
 * 整个浮层 display:none —— 不留一个没有内容的空壳。
 */
function TargetDetail(): React.ReactElement {
  const rootRef = useRef<HTMLDivElement>(null)
  const titleRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    let raf = 0
    let disposed = false
    // 字段行只在挂载后查一次 DOM，之后每帧直接写文本
    const cells = new Map<string, HTMLElement>()
    rootRef.current?.querySelectorAll<HTMLElement>('[data-field]').forEach((el) => {
      const key = el.dataset.field
      if (key) cells.set(key, el)
    })

    const tick = (): void => {
      if (disposed) return
      raf = requestAnimationFrame(tick)
      const root = rootRef.current
      if (!root) return
      // 选中 id 只在点击时变，这里用 getState() 读，省掉一次订阅引发的重渲染
      const id = useStore.getState().selectedTargetId
      const frame = live.frame
      if (id == null || frame == null) {
        if (root.style.display !== 'none') root.style.display = 'none'
        return
      }
      const target = frame.targets.find((t) => t.id === id) ?? null
      if (!target) {
        if (root.style.display !== 'none') root.style.display = 'none'
        return
      }
      if (root.style.display !== 'block') root.style.display = 'block'
      setText(titleRef.current, `目标详情 #${target.id}`)
      for (const field of TARGET_DETAIL_FIELDS) {
        setText(cells.get(field.key) ?? null, field.value(target, frame))
      }
    }

    raf = requestAnimationFrame(tick)
    return () => {
      disposed = true
      cancelAnimationFrame(raf)
    }
  }, [])

  return (
    <div
      ref={rootRef}
      // 复用 .viewer-hud 的外观（背景 / 边框 / 毛玻璃），只是挪到右上角；
      // 容器本身要能点（关闭按钮），所以这里把 pointer-events 打开
      className="viewer-hud"
      style={{
        left: 'auto',
        right: 12,
        top: 12,
        width: 286,
        maxWidth: '46%',
        pointerEvents: 'auto',
        display: 'none',
      }}
    >
      <div ref={titleRef} className="viewer-hud__line">
        目标详情
      </div>
      <div className="kv" style={{ marginTop: 4 }}>
        {TARGET_DETAIL_FIELDS.map((field) => (
          <Fragment key={field.key}>
            <div className="kv__k">{field.label}</div>
            <div className="kv__v mono" data-field={field.key}>
              —
            </div>
          </Fragment>
        ))}
      </div>
      <div style={{ marginTop: 6, textAlign: 'right' }}>
        <button type="button" className="btn btn--ghost" onClick={() => useStore.getState().selectTarget(null)}>
          关闭
        </button>
      </div>
    </div>
  )
}

function fmt(value: number | null | undefined): string {
  return value == null || !Number.isFinite(value) ? '—' : value.toFixed(1)
}

/** 偏差是有方向的（雷达比 RTK 远了还是近了、偏左还是偏右），正号必须显式写出来。 */
function fmtSigned(value: number | null | undefined): string {
  if (value == null || !Number.isFinite(value)) return '—'
  return `${value >= 0 ? '+' : ''}${value.toFixed(1)}`
}

/** 匹配半径这类「阈值」量：不留小数更利落（20 m 比 20.0 m 好读）。 */
function fmtCompact(value: number | null | undefined): string {
  return value == null || !Number.isFinite(value) ? '—' : String(Math.round(value))
}

/**
 * HUD 上的「雷达探测 vs RTK」对比读数（任务书第 3 条）。
 *
 * 匹配时把本帧误差一次报全：三维距离 Δ、水平 Δ、高度 Δ，再加极坐标三项偏差
 * （距离差 / 方位差 / 俯仰差，后端算好，约定偏差 = 雷达 − RTK）+ 匹配到的目标号。
 * ⚠ 未匹配时**一个偏差都不报**：那时雷达探到的是别的东西，Delta 系列没有意义，
 *   只说「没探到 + 最近目标有多远 + 匹配半径是多少」，免得把噪声当成探测精度。
 */
function comparisonText(comparison: DroneRadarComparison): string {
  if (!comparison.matched) {
    return `本帧雷达未探到无人机（最近目标 ${fmt(comparison.deltaDistanceM)} m > 匹配半径 ${fmtCompact(comparison.matchRadiusM)} m）`
  }
  return [
    `雷达探测 Δ ${fmt(comparison.deltaDistanceM)} m（水平 ${fmt(comparison.deltaHorizontalM)} m · 高 ${fmt(comparison.deltaUpM)} m）`,
    `距离差 ${fmtSigned(comparison.deltaRangeM)} m · 方位差 ${fmtSigned(comparison.deltaAzimuthDeg)}° · 俯仰差 ${fmtSigned(comparison.deltaElevationDeg)}°`,
    `目标 #${comparison.targetId}`,
  ].join('｜')
}

/**
 * HUD 上的「雷达正前方指向哪儿、这个朝向是怎么来的」（任务书第 4 条）。
 *
 * 双天线基线推算 = THS 双天线定向给出的基线航向 + 配置里的安装夹角，是可信来源；
 * 手动绝对角意味着这个角是人填的、没人核对过，所以在 Hud 里单独用琥珀色提醒。
 * 基线航向为 null（没收到 THS 定向）时也要能正常显示，不能留一行空白。
 */
function boresightText(frame: RelativeFrame): string {
  const boresight = `${fmt(frame.radarBoresightDeg)}°`
  if (!frame.radarBoresightFromBaseline) return `雷达正前方 ${boresight}（手动）`
  const baseline = frame.radarBaselineHeadingDeg
  if (baseline == null || !Number.isFinite(baseline)) {
    return `雷达正前方 ${boresight}（双天线基线未收到定向）`
  }
  // 安装夹角是配置量（雷达正前方相对基线的固定偏角），现取现用；取不到就省略这一段
  const rawOffset = useStore.getState().config?.relative.radar.yawOffsetFromBaselineDeg
  const offset = rawOffset != null && Number.isFinite(rawOffset) ? ` + 安装夹角 ${rawOffset.toFixed(1)}°` : ''
  return `雷达正前方 ${boresight}（双天线基线 ${baseline.toFixed(1)}°${offset}）`
}

export function Viewer3D(): React.ReactElement {
  // 视角是纯界面偏好：记在 localStorage，下次打开还是上次那个视角（不写后端配置）。
  const [view, setView] = usePersistentState<ViewMode>('uav.viewer.mode', 'free')
  return (
    // 右键拖拽也是平移手势之一，禁掉浏览器右键菜单，否则一松手就弹出菜单、拖拽被打断。
    <div className="viewer3d" onContextMenu={(event) => event.preventDefault()}>
      <Canvas
        camera={{ position: [150, 130, 190], fov: 50, near: 0.5, far: 20000 }}
        dpr={[1, 2]}
        gl={{ antialias: true, powerPreference: 'high-performance' }}
        // 点空白处 → 取消选中（意见 6）。R3F 只在「这一次点击没有命中任何带事件的对象」时
        // 触发它，命中了目标时不会走这里，所以不会把刚选中的目标立刻清掉。
        onPointerMissed={() => useStore.getState().selectTarget(null)}
      >
        <Scene view={view} />
      </Canvas>
      <div className="viewer3d__views">
        {VIEW_MODES.map((mode) => (
          <button
            key={mode.id}
            className={view === mode.id ? 'viewbtn viewbtn--active' : 'viewbtn'}
            onClick={() => setView(mode.id)}
            title={mode.hint}
          >
            {mode.label}
          </button>
        ))}
        {/* 操作提示直接写在按钮旁边：视图切到俯视/侧视后左键改了含义，不写出来没人知道还能拖。 */}
        <span className="viewer3d__hint">
          {view === 'free' ? '左键拖拽旋转 · 右键/中键拖拽平移 · 滚轮缩放' : '左键拖拽平移 · 滚轮缩放'}
        </span>
      </div>
      <Hud />
      <TargetDetail />
    </div>
  )
}
