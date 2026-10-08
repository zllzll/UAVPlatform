/**
 * 抓取后端真实响应，裁剪成前端 UI 测试用的 fixture。
 *
 * 为什么用 node 而不是 PowerShell：PS 5.1 的 ConvertFrom-Json 读 3 MB 的
 * snapshot 会抛 ArgumentException，且默认编码会把中文读成乱码。
 * node 写盘可能触发本机 DLP 透明加密（跟 python.exe 一样），所以本脚本
 * **只写 stdout**，由 PowerShell 用 [System.IO.File]::WriteAllText 落盘。
 *
 * 用法：node scripts/capture-fixtures.mjs [baseUrl]
 * 输出：一行一个 JSON，格式为 {"name":"config.json","json":"..."}
 */
const BASE = process.argv[2] ?? 'http://localhost:5080'

/** 面板测试只需要少量样本与日志，避免 fixture 过大。 */
const KEEP_SAMPLES = 60
const KEEP_LOGS = 60
/** 点云帧一帧能有上千个点，相对位置帧会膨胀到几百 KB；只留前若干个。 */
const KEEP_TARGETS = 30
/** 轨迹快照保留的条数与每条点数。 */
const KEEP_TRACK_TARGETS = 5
const KEEP_TRACK_POINTS = 40

async function getJson(path) {
  const res = await fetch(`${BASE}${path}`)
  if (!res.ok) throw new Error(`GET ${path} -> HTTP ${res.status}`)
  return res.json()
}

/** 保证平台在跑。样本 / 相对位置帧只能来自真实设备（内置仿真源已按要求删除）。 */
async function ensureRunning() {
  await fetch(`${BASE}/api/platform/start`, { method: 'POST' }).catch(() => {})
  const status = await getJson('/api/status')
  if (!status.running) {
    await fetch(`${BASE}/api/platform/start`, { method: 'POST' })
  }
  // 没接设备时 samples / relative 是空的，抓出来的 fixture 也就没有样本；接好设备再抓。
  await new Promise((r) => setTimeout(r, 4000))
}

await ensureRunning()

const out = {
  'config.json': await getJson('/api/config'),
  'status.json': await getJson('/api/status'),
  'sessions.json': await getJson('/api/sessions'),
}

// 相对位置帧里的 targets 可能是上千个点云点，裁剪后再落盘
const relative = await getJson('/api/relative')
out['relative.json'] = {
  ...relative,
  targets: (relative.targets ?? []).slice(0, KEEP_TARGETS),
}

// snapshot 里的 tracks 有几 MB，裁剪后再落盘。
// 注意：useStore.bootstrap() 会读 snapshot 的 config / status / relative / tracks，
// 所以这几个键必须保留，只对 tracks 与 samples / logs 做截断。
const snap = await getJson('/api/snapshot')
const trackTargets = snap.tracks?.targets ?? {}
const trimmedTargets = Object.fromEntries(
  Object.entries(trackTargets)
    .slice(0, KEEP_TRACK_TARGETS)
    .map(([id, points]) => [id, (points ?? []).slice(-KEEP_TRACK_POINTS)]),
)
out['snapshot.json'] = {
  ...snap,
  ...(snap.relative ? { relative: { ...snap.relative, targets: (snap.relative.targets ?? []).slice(0, KEEP_TARGETS) } } : {}),
  samples: (snap.samples ?? []).slice(-KEEP_SAMPLES),
  logs: (snap.logs ?? []).slice(-KEEP_LOGS),
  tracks: {
    drone: (snap.tracks?.drone ?? []).slice(-KEEP_TRACK_POINTS),
    targets: trimmedTargets,
  },
}

for (const [name, value] of Object.entries(out)) {
  process.stdout.write(`${JSON.stringify({ name, json: JSON.stringify(value) })}\n`)
}
