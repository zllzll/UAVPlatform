// 本轮三处界面改动的真浏览器验证：
//  ① 存储页签新增「按设备保存」（三台设备的原始/解析落盘开关集中于此），设备卡片里那两个开关已移除；
//  ② 3D 罗盘「北 N / 东 E / 100 m」的字号随距离缩放（六处标签共用一个 LABEL_DISTANCE_FACTOR）；
//  ③ 串口名下拉只列本机枚举到的串口，配置里保存的口若本机没有就落到「（未设置）」。
// 用法：node _diag/verify-ux-fixes.mjs   （后端需已在 5080 运行）
import { spawn, spawnSync } from 'node:child_process'
import fs from 'node:fs'
import path from 'node:path'

const CHROME = 'C:/Program Files/Google/Chrome/Application/chrome.exe'
const PORT = 9300 + (process.pid % 400)
const APP = 'http://localhost:5080/'
const OUT = 'D:/DeepseekHarness/UAVPlatform/_diag'
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

const report = {}
const fails = []
const check = (name, ok, detail) => {
  report[name] = { ok: !!ok, detail }
  if (!ok) fails.push(name)
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}  ${typeof detail === 'string' ? detail : JSON.stringify(detail)}`)
}

class Cdp {
  constructor(ws) {
    this.ws = ws
    this.id = 0
    this.pending = new Map()
    ws.addEventListener('message', (ev) => {
      const msg = JSON.parse(ev.data)
      if (msg.id && this.pending.has(msg.id)) {
        const { resolve, reject } = this.pending.get(msg.id)
        this.pending.delete(msg.id)
        if (msg.error) reject(new Error(JSON.stringify(msg.error)))
        else resolve(msg.result)
      }
    })
  }
  static async connect(url) {
    const ws = new WebSocket(url)
    await new Promise((resolve, reject) => {
      ws.addEventListener('open', resolve, { once: true })
      ws.addEventListener('error', () => reject(new Error('ws error')), { once: true })
    })
    return new Cdp(ws)
  }
  send(method, params = {}, sessionId) {
    const id = ++this.id
    const payload = { id, method, params }
    if (sessionId) payload.sessionId = sessionId
    this.ws.send(JSON.stringify(payload))
    return new Promise((resolve, reject) => {
      this.pending.set(id, { resolve, reject })
      setTimeout(() => {
        if (this.pending.has(id)) {
          this.pending.delete(id)
          reject(new Error('timeout ' + method))
        }
      }, 30000)
    })
  }
  async eval(sessionId, expression) {
    const r = await this.send(
      'Runtime.evaluate',
      { expression, awaitPromise: true, returnByValue: true, userGesture: true },
      sessionId,
    )
    if (r.exceptionDetails) throw new Error(r.exceptionDetails.text + ' ' + JSON.stringify(r.exceptionDetails.exception?.description ?? ''))
    return r.result.value
  }
}

// 量罗盘标签：屏幕上真实像素尺寸（getBoundingClientRect 已含祖先 transform 的缩放）
const MEASURE = [
  '(() => {',
  "  const root = document.querySelector('.viewer3d')",
  '  if (!root) return { error: "no .viewer3d" }',
  "  const spans = Array.from(root.querySelectorAll('span'))",
  '  const read = (el) => {',
  '    if (!el) return null',
  '    const rect = el.getBoundingClientRect()',
  '    const cs = getComputedStyle(el)',
  '    let scale = null',
  '    let node = el.parentElement',
  '    while (node && scale === null) {',
  "      const tr = getComputedStyle(node).transform",
  "      if (tr && tr !== 'none') { try { const m = new DOMMatrixReadOnly(tr); scale = +Math.hypot(m.a, m.b).toFixed(4) } catch (e) { scale = null } }",
  '      node = node.parentElement',
  '    }',
  '    return { text: el.textContent.trim(), w: +rect.width.toFixed(2), h: +rect.height.toFixed(2), fontSize: cs.fontSize, scale }',
  '  }',
  "  const byText = (t) => spans.find((s) => s.textContent.trim() === t)",
  "  const radius = spans.find((s) => / m$/.test(s.textContent.trim()))",
  "  const canvas = root.querySelector('canvas')",
  '  const cr = canvas ? canvas.getBoundingClientRect() : null',
  '  return {',
  '    canvas: cr ? { cx: +(cr.left + cr.width / 2).toFixed(0), cy: +(cr.top + cr.height / 2).toFixed(0) } : null,',
  "    north: read(byText('北 N')),",
  '    radius: read(radius),',
  '  }',
  '})()',
].join('\n')

// 三维区域里的全部文字：设备的「无人机 / RTK / 雷达 / 基座」标签已按需求删除，只该剩下方位刻度与半径标注
const SCENE = [
  '(() => {',
  "  const root = document.querySelector('.viewer3d')",
  '  if (!root) return { error: "no .viewer3d" }',
  "  const visible = (el) => { const r = el.getBoundingClientRect(); return r.width > 0 && r.height > 0 }",
  "  const spans = Array.from(root.querySelectorAll('span'))",
  '  const txt = (el) => el.textContent.trim()',
  '  return {',
  '    spans: spans.map(txt),',
  "    visibleTags: spans.filter((s) => s.className.indexOf('scene-tag') >= 0 && visible(s)).map(txt),",
  "    deviceWords: spans.filter((s) => ['无人机', '雷达', '基座', 'RTK'].indexOf(txt(s)) >= 0).map(txt),",
  '  }',
  '})()',
].join('\n')

// 面板 DOM 快照：页签按钮、折叠区块名、开关文案
const PANEL = [
  '(() => {',
  "  const panel = document.querySelector('.panel')",
  '  if (!panel) return { error: "no .panel" }',
  "  const buttons = Array.from(panel.querySelectorAll('.panel__actions button')).map((b) => b.textContent.trim())",
  "  const heads = Array.from(panel.querySelectorAll('.collapse__name')).map((el) => el.textContent.trim())",
  "  const switches = Array.from(panel.querySelectorAll('.collapse__body .switch')).map((el) => el.textContent.trim())",
  "  const open = Array.from(panel.querySelectorAll('.collapse')).map((el) => ({",
  "    name: (el.querySelector('.collapse__name') || {}).textContent || '',",
  "    open: el.className.indexOf('collapse--open') >= 0,",
  '  }))',
  '  return { buttons, heads, switches, open }',
  '})()',
].join('\n')

const clickByText = (selector, text) =>
  [
    '(() => {',
    "  const el = Array.from(document.querySelectorAll('" + selector + "')).find((n) => n.textContent.trim().indexOf('" + text + "') >= 0)",
    "  if (!el) return 'not-found'",
    '  const disabled = !!el.disabled',
    '  el.click()',
    "  return disabled ? 'disabled' : 'clicked'",
    '})()',
  ].join('\n')

/** 点某个折叠区块的标题行（按 .collapse__name 的文本模糊定位到所属 .collapse__head）。 */
const clickCollapse = (text) =>
  [
    '(() => {',
    "  const name = Array.from(document.querySelectorAll('.collapse__name')).find((n) => n.textContent.trim().indexOf('" + text + "') >= 0)",
    "  if (!name) return 'not-found'",
    "  const head = name.closest('.collapse__head')",
    "  if (!head) return 'no-head'",
    '  head.click()',
    "  return 'clicked'",
    '})()',
  ].join('\n')

/** 翻转某个开关（按文案模糊定位 <label class="switch">）并回报翻转前后的勾选状态。 */
const toggleSwitch = (text) =>
  [
    '(() => {',
    "  const label = Array.from(document.querySelectorAll('.collapse__body .switch')).find((n) => n.textContent.trim().indexOf('" + text + "') >= 0)",
    "  if (!label) return { error: 'not-found' }",
    "  const input = label.querySelector('input[type=checkbox]')",
    '  const before = input.checked',
    '  input.click()',
    '  return { before, after: input.checked, disabled: input.disabled }',
    '})()',
  ].join('\n')

const readSwitch = (text) =>
  [
    '(() => {',
    "  const label = Array.from(document.querySelectorAll('.collapse__body .switch')).find((n) => n.textContent.trim().indexOf('" + text + "') >= 0)",
    "  if (!label) return { error: 'not-found' }",
    "  const input = label.querySelector('input[type=checkbox]')",
    '  return { checked: input.checked, disabled: input.disabled }',
    '})()',
  ].join('\n')

/** 一次读多个开关的勾选 / 禁用状态（按文案模糊定位 <label class="switch">）。 */
const readSwitches = (labels) =>
  [
    '(() => {',
    '  const want = ' + JSON.stringify(labels),
    "  const all = Array.from(document.querySelectorAll('.collapse__body .switch'))",
    '  return want.map((text) => {',
    "    const label = all.find((n) => n.textContent.trim().indexOf(text) >= 0)",
    "    if (!label) return { label: text, error: 'not-found' }",
    "    const input = label.querySelector('input[type=checkbox]')",
    '    return { label: text, checked: input.checked, disabled: input.disabled }',
    '  })',
    '})()',
  ].join('\n')

/** 找到某个开关并回报「鼠标该落在哪」——用来做真实悬停（浮层只认真实鼠标 / 键盘聚焦）。
 *
 * 落点取行的右侧：左边是复选框本体，落在它上面时浏览器会把鼠标事件算到控件上，
 * 而禁用的控件不派发鼠标事件，悬停就白做了。
 */
const hoverSwitch = (text) =>
  [
    '(() => {',
    "  const label = Array.from(document.querySelectorAll('.collapse__body .switch')).find((n) => n.textContent.trim().indexOf('" + text + "') >= 0)",
    "  if (!label) return { error: 'not-found' }",
    "  label.scrollIntoView({ block: 'center' })",
    '  const r = label.getBoundingClientRect()',
    "  if (r.width < 8 || r.height < 8) return { error: 'zero-rect' }",
    '  return { x: r.right - 16, y: r.top + r.height / 2, disabled: !!label.querySelector("input[type=checkbox]").disabled }',
    '})()',
  ].join('\n')

/** 读当前弹出的说明浮层文本。浮层是挂到 document.body 上的 portal（见 HoverTip.tsx:27）。 */
const shownTip = () =>
  [
    '(() => {',
    "  const tip = document.querySelector('.hinttip')",
    '  return tip ? tip.textContent : null',
    '})()',
  ].join('\n')

/** 数一数面板文本里某个词出现了几次（用于断言换成输入框的字段还在）。 */
const countText = (text) =>
  [
    '(() => {',
    "  const panel = document.querySelector('.panel')",
    '  if (!panel) return -1',
    '  return (panel.textContent.split("' + text + '").length - 1)',
    '})()',
  ].join('\n')

/** 数一数整页文本里某个词出现了几次（用来断言「仿真」相关的按钮/参数条确实没了）。 */
const countBodyText = (text) =>
  [
    '(() => {',
    '  const body = document.body',
    '  if (!body) return -1',
    '  return (body.textContent.split("' + text + '").length - 1)',
    '})()',
  ].join('\n')

/** 找到「串口名」那个 Field（`.field__label` 文本精确等于「串口名」），返回它内部的 select/input。 */
const PORT_FIELD = [
  "  const panel = document.querySelector('.panel')",
  '  if (!panel) return null',
  "  const field = Array.from(panel.querySelectorAll('.field')).find((f) => {",
  "    const l = f.querySelector('.field__label')",
  "    return l && l.textContent.trim() === '串口名'",
  '  })',
].join('\n')

/** 串口名下拉的快照：候选项、当前值、当前显示项、手输框是否存在及其内容。 */
const PORTS = [
  '(() => {',
  PORT_FIELD,
  '  if (!field) return []',
  '  return [field].map((f) => {',
  "    const sel = f.querySelector('select')",
  "    const input = f.querySelector('input[type=text]')",
  '    return {',
  "      options: sel ? Array.from(sel.options).map((o) => ({ value: o.value, text: o.textContent.trim() })) : null,",
  '      value: sel ? sel.value : null,',
  "      shown: sel && sel.selectedIndex >= 0 ? sel.options[sel.selectedIndex].textContent.trim() : null,",
  '      hasManualInput: !!input,',
  '      manualValue: input ? input.value : null,',
  '    }',
  '  })',
  '})()',
].join('\n')

/** 在下拉里选中「手动输入…」：必须走原型上的 value setter，绕过 React 的值跟踪器，change 才会被认。 */
const selectManual = () =>
  [
    '(() => {',
    PORT_FIELD,
    "  if (!field) return 'no-field'",
    "  const sel = field.querySelector('select')",
    "  if (!sel) return 'no-select'",
    "  const opt = Array.from(sel.options).find((o) => o.textContent.indexOf('手动输入') >= 0)",
    "  if (!opt) return 'no-manual-option'",
    '  const setter = Object.getOwnPropertyDescriptor(window.HTMLSelectElement.prototype, "value").set',
    '  setter.call(sel, opt.value)',
    '  sel.dispatchEvent(new Event("change", { bubbles: true }))',
    "  return 'selected'",
    '})()',
  ].join('\n')

/** 往手输框里打字（同样要绕过 React 的值跟踪器，否则 onChange 不触发）。 */
const typePort = (text) =>
  [
    '(() => {',
    PORT_FIELD,
    "  if (!field) return 'no-field'",
    "  const input = field.querySelector('input[type=text]')",
    "  if (!input) return 'no-input'",
    '  const setter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, "value").set',
    '  setter.call(input, "' + text + '")',
    '  input.dispatchEvent(new Event("input", { bubbles: true }))',
    "  return 'typed'",
    '})()',
  ].join('\n')

/** 本机真实串口清单（后端枚举）与配置里保存的串口名，用来算「应该看到什么」。 */
const localPorts = await (await fetch('http://localhost:5080/api/serial-ports')).json().then((r) => r.ports ?? [])
const savedCfg = await (await fetch('http://localhost:5080/api/config')).json()
const savedPort = ((savedCfg.devices ?? []).find((d) => d.transport === 'serial') ?? { transportSettings: {} })
  .transportSettings.serialPort
/** 配置里「启用存储」的真值。本脚本从不写配置，所以断言一律跟它比，绝不把某个状态当成常量。 */
const savedStorage = !!(savedCfg.storage && savedCfg.storage.enabled)

const userDir = path.join(OUT, 'cdp-profile-' + process.pid)
try {
  fs.rmSync(userDir, { recursive: true, force: true })
} catch {
  /* 旧 profile 没清掉就用新的 */
}

const chrome = spawn(
  CHROME,
  [
    '--headless=new',
    '--no-first-run',
    '--no-default-browser-check',
    '--use-angle=swiftshader',
    '--enable-unsafe-swiftshader',
    '--remote-debugging-port=' + PORT,
    '--user-data-dir=' + userDir,
    '--window-size=1680,1000',
    'about:blank',
  ],
  { stdio: 'ignore', detached: true },
)
// Windows 上 process.kill(-pid) 杀不掉进程组，必须用 taskkill /T 收掉整棵子树，
// 否则无头 Chrome 会残留（曾经攒下 10 个 SwiftShader 实例把机器拖到 grep 都超时）。
process.on('exit', () => {
  try {
    spawnSync('taskkill', ['/F', '/T', '/PID', String(chrome.pid)], { stdio: 'ignore' })
  } catch {
    /* 已经退了 */
  }
})

let version = null
for (let i = 0; i < 80; i++) {
  try {
    version = await (await fetch('http://127.0.0.1:' + PORT + '/json/version')).json()
    break
  } catch {
    await sleep(300)
  }
}
if (!version) throw new Error('Chrome 未在 ' + PORT + ' 上就绪')

const cdp = await Cdp.connect(version.webSocketDebuggerUrl)
const { targetId } = await cdp.send('Target.createTarget', { url: 'about:blank' })
const { sessionId } = await cdp.send('Target.attachToTarget', { targetId, flatten: true })
await cdp.send('Runtime.enable', {}, sessionId)
await cdp.send('Page.enable', {}, sessionId)
await cdp.send('Emulation.setDeviceMetricsOverride', { width: 1680, height: 1000, deviceScaleFactor: 1, mobile: false }, sessionId)
await cdp.send('Page.navigate', { url: APP }, sessionId)

const shoot = async (name) => {
  const r = await cdp.send('Page.captureScreenshot', { format: 'png' }, sessionId)
  const file = path.join(OUT, 'ux-' + name + '.png')
  fs.writeFileSync(file, Buffer.from(r.data, 'base64'))
  return file
}

let mounted = false
for (let i = 0; i < 80; i++) {
  mounted = await cdp.eval(sessionId, "!!document.querySelector('.app__toolbar') && !!document.querySelector('.viewer3d canvas')")
  if (mounted) break
  await sleep(300)
}
check('页面已挂载（工具条 + 三维画布）', mounted, mounted)

// ── ② 罗盘字号 ───────────────────────────────────────────────────────────────
let load = null
for (let i = 0; i < 80; i++) {
  load = await cdp.eval(sessionId, MEASURE)
  if (load && load.north) break
  await sleep(300)
}
check('罗盘标签已渲染（北 N）', !!(load && load.north), load && load.north ? load.north.text : 'null')

const wheel = async (deltaY, times) => {
  for (let i = 0; i < times; i++) {
    await cdp.send(
      'Input.dispatchMouseEvent',
      { type: 'mouseWheel', x: load.canvas.cx, y: load.canvas.cy, deltaX: 0, deltaY, pointerType: 'mouse' },
      sessionId,
    )
    await sleep(120)
  }
  await sleep(900)
}

// 先轻推一下视角：drei 只在盒子的帧循环里施加距离缩放，首帧量到的是「尚未缩放」的自然字号。
// 出去一步再回来一步 ≈ 回到默认机位，此时量到的才是默认机位下的真实尺寸。
await wheel(600, 1)
await wheel(-600, 1)
const home = await cdp.eval(sessionId, MEASURE)
const shotLoad = await shoot('compass-1-home')
await wheel(600, 5)
const far = await cdp.eval(sessionId, MEASURE)
const shotOut = await shoot('compass-2-zoomout')
await wheel(-600, 10)
const near = await cdp.eval(sessionId, MEASURE)
await shoot('compass-3-zoomin')

const h = (m, k) => (m && m[k] ? m[k].h : null)
const north = [h(home, 'north'), h(far, 'north'), h(near, 'north')]
const radius = [h(home, 'radius'), h(far, 'radius'), h(near, 'radius')]
const num = (arr) => arr.every((v) => typeof v === 'number' && v > 0)
// 期望：默认机位 ≈ CSS 字号（12px 的字 → 16px 行盒），拉远变小、拉近变大。
check(
  '默认机位下「北 N」≈ 刚打开时看到的自然字号（15~17px 行盒）',
  num(north) && north[0] >= 14.5 && north[0] <= 17.5,
  '默认=' + north[0] + 'px（缩小/放大 ' + north[1] + '/' + north[2] + '）',
)
check(
  '拉远变小：缩 5 档后「北 N」明显更小，且仍看得清（≥6px）',
  num(north) && north[1] < north[0] * 0.92 && north[1] >= 6,
  JSON.stringify(north),
)
check('拉近变大：再放大后「北 N」比默认机位大', num(north) && north[2] > north[0] * 1.02, JSON.stringify(north))
check(
  '「xx m」半径标注跟随同一规则（默认 ≈12px 行盒，拉远变小）',
  num(radius) && radius[0] >= 10 && radius[0] <= 15 && radius[1] < radius[0],
  JSON.stringify(radius),
)

// ── ⑤ 三维区域只留图标（设备文字标签已删）─────────────────────────────────────
const scene = await cdp.eval(sessionId, SCENE)
check(
  '三维区域里没有任何可见的贴附文字标签（.scene-tag）',
  !!scene && !scene.error && scene.visibleTags.length === 0,
  scene && !scene.error ? JSON.stringify(scene.visibleTags) : String(scene && scene.error),
)
check(
  '三维区域里没有「无人机 / 雷达 / 基座 / RTK」字样',
  !!scene && !scene.error && scene.deviceWords.length === 0,
  scene && !scene.error ? '区域文字=' + JSON.stringify(scene.spans) : String(scene && scene.error),
)
check(
  '方位刻度与半径标注仍在（三维区域里该有的文字只有这些）',
  !!scene && !scene.error && scene.spans.indexOf('北 N') >= 0 && scene.spans.some((t) => / m$/.test(t)),
  scene && !scene.error ? JSON.stringify(scene.spans) : String(scene && scene.error),
)
await shoot('scene-no-device-labels')

// ── ① 存储页签的落盘开关 ─────────────────────────────────────────────────────
// ── ④ 仿真功能已删除（m00868 第 2 条）：界面没有按钮/参数条，后端没有端点 ─────────
const simHits = await cdp.eval(sessionId, countBodyText('仿真'))
check('界面里已无「仿真」按钮与仿真参数条', simHits === 0, '整页「仿真」出现次数=' + simHits)
const simGet = await fetch('http://localhost:5080/api/simulation')
  .then(async (r) => ({ status: r.status, body: (await r.text()).slice(0, 200) }))
  .catch((e) => ({ status: 'ERR', body: String(e && e.message) }))
const simStart = await fetch('http://localhost:5080/api/simulation/start', { method: 'POST' })
  .then((r) => r.status)
  .catch((e) => 'ERR ' + (e && e.message))
const simStop = await fetch('http://localhost:5080/api/simulation/stop', { method: 'POST' })
  .then((r) => r.status)
  .catch((e) => 'ERR ' + (e && e.message))
check(
  '后端三个 /api/simulation 端点都已移除（不再返回仿真状态）',
  !/"running"/.test(simGet.body || '') && simStart !== 200 && simStop !== 200,
  'GET=' + simGet.status + ' ' + JSON.stringify((simGet.body || '').slice(0, 80)) + ' start=' + simStart + ' stop=' + simStop,
)
report.sim = { simHits, simGet, simStart, simStop }

await cdp.eval(sessionId, clickByText('.panel__actions button', '通讯参数'))
await sleep(400)
// 折叠块的正文只有展开后才进 DOM，先展开三张设备卡片再数开关
for (const kind of ['基座', '雷达', '无人机 GPS']) {
  await cdp.eval(sessionId, clickCollapse(kind))
  await sleep(250)
}
const devicesTab = await cdp.eval(sessionId, PANEL)
const reconnectIntervals = await cdp.eval(sessionId, countText('重连间隔'))
const leftovers = devicesTab.switches.filter((t) => t.includes('保存原始数据') || t.includes('保存解析数据'))
check(
  '通讯参数页签里已无「保存原始数据 / 保存解析数据」开关',
  leftovers.length === 0,
  '残余=' + JSON.stringify(leftovers) + ' 全部开关=' + JSON.stringify(devicesTab.switches),
)
check(
  '通讯参数页签三台设备仍各有「自动重连」开关与「重连间隔」输入框',
  devicesTab.switches.filter((t) => t.includes('自动重连')).length === 3 && reconnectIntervals === 3,
  '自动重连开关=' + devicesTab.switches.filter((t) => t.includes('自动重连')).length + ' 重连间隔=' + reconnectIntervals + ' 全部开关=' + JSON.stringify(devicesTab.switches),
)

// ── ③ 串口名下拉：只列本机有的口 ─────────────────────────────────────────────
const portSnapshot = await cdp.eval(sessionId, PORTS)
const field0 = portSnapshot && portSnapshot[0]
check('通讯参数页签有「串口名」下拉（基座走串口链路）', !!field0 && Array.isArray(field0.options), JSON.stringify(field0))

const MANUAL = '__manual__'
const candidates = ((field0 && field0.options) || []).filter((o) => o.value !== '' && o.value !== MANUAL)
const strays = candidates.filter((o) => !localPorts.includes(o.value))
const annotated = ((field0 && field0.options) || []).filter((o) => /配置里保存的|本机未检测到/.test(o.text))
check(
  '串口候选只列本机枚举到的串口（不再混入配置里保存的口）',
  !!field0 &&
    strays.length === 0 &&
    annotated.length === 0 &&
    localPorts.every((p) => candidates.some((o) => o.value === p)),
  '本机=' + JSON.stringify(localPorts) + ' 候选=' + JSON.stringify(candidates.map((o) => o.value)) + ' 越界=' + JSON.stringify(strays) + ' 带注解=' + JSON.stringify(annotated),
)
check(
  '下拉里仍有「（未设置）」与「手动输入…」',
  !!field0 && (field0.options || []).some((o) => o.value === '') && (field0.options || []).some((o) => o.value === MANUAL),
  JSON.stringify((field0 && field0.options) || []),
)

const savedAbsent = savedPort !== '' && !localPorts.includes(savedPort)
check(
  savedAbsent
    ? `配置里保存的 ${savedPort} 本机没有 ⇒ 下拉落到「（未设置）」`
    : `配置里保存的 ${savedPort} 本机有 ⇒ 下拉正常选中它`,
  !!field0 && (savedAbsent ? field0.value === '' && /未设置/.test(field0.shown || '') : field0.value === savedPort),
  '保存值=' + savedPort + ' 当前=' + JSON.stringify({ value: field0 && field0.value, shown: field0 && field0.shown }),
)
const shotSerial = await shoot('serial-port')

// m00868 第 1 条：下拉只改显示、不动配置。切到「手动输入…」把手输框露出来——框里的内容就是
// 草稿里的原值，所以这里能直接看到「下拉显示（未设置）」的时候，草稿是不是还留着那个口。
const reveal = await cdp.eval(sessionId, selectManual())
await sleep(300)
const revealed = (await cdp.eval(sessionId, PORTS))[0]
check(
  savedAbsent
    ? `下拉显示「（未设置）」，但草稿里仍保留保存的 ${savedPort}（只改显示、不动配置）`
    : `下拉与草稿一致，草稿里是 ${savedPort}`,
  !!revealed && revealed.hasManualInput && revealed.manualValue === savedPort,
  JSON.stringify({ reveal, revealed, savedPort, savedAbsent }),
)

// 手输的口本来就可能还没插上，不能因为「本机没有」就被清掉
const toManual = await cdp.eval(sessionId, selectManual())
await sleep(300)
const typed = await cdp.eval(sessionId, typePort('COM9'))
await sleep(400)
const afterType = (await cdp.eval(sessionId, PORTS))[0]
check(
  '切到「手动输入…」后能打字，且打进去的口不会被清掉',
  toManual === 'selected' && typed === 'typed' && !!afterType && afterType.hasManualInput && afterType.manualValue === 'COM9',
  JSON.stringify({ toManual, typed, afterType }),
)

await cdp.eval(sessionId, clickByText('.panel__actions button', '存储'))
await sleep(400)
await cdp.eval(sessionId, clickCollapse('存储')) // 展开「存储」主块，嵌套的「按设备保存」才会进 DOM
await sleep(400)
let storageTab = await cdp.eval(sessionId, PANEL)
check('存储页签出现「按设备保存」折叠区块', storageTab.heads.some((t) => t.includes('按设备保存')), JSON.stringify(storageTab.heads))

const expanded = await cdp.eval(sessionId, clickCollapse('按设备保存'))
await sleep(400)
storageTab = await cdp.eval(sessionId, PANEL)
const want = [
  '基座 原始数据',
  '基座 解析数据',
  '雷达 原始数据',
  '雷达 解析数据',
  '无人机 GPS 原始数据',
  '无人机 GPS 解析数据',
]
const missing = want.filter((t) => !storageTab.switches.some((s) => s.includes(t)))
check('六个逐设备开关都在存储页签里', missing.length === 0, '缺失=' + JSON.stringify(missing) + ' 实际=' + JSON.stringify(storageTab.switches))
check(
  '「按设备保存」可展开',
  expanded === 'clicked' && storageTab.open.some((c) => c.name.includes('按设备保存') && c.open),
  expanded + ' ' + JSON.stringify(storageTab.open),
)

const shotPanel = await shoot('storage-tab')

// 面板载入时的草稿态取决于真配置里存的是什么（「启用存储」完全可能是关的），而「放弃修改」
// 会把整份草稿拉回配置值 ⇒ 每次要跑「总开关 + 子开关」的序列之前，先把它归一到「开」。
// 只点开关、不点「保存并生效」，配置一个字节都不会被改写。
const ensureMasterOn = async () => {
  const before = await cdp.eval(sessionId, readSwitch('启用存储'))
  if (before.checked !== true) {
    await cdp.eval(sessionId, toggleSwitch('启用存储'))
    await sleep(400)
  }
  const after = await cdp.eval(sessionId, readSwitch('启用存储'))
  return { before, after }
}
const normFirst = await ensureMasterOn()
check(
  '草稿已归一：总开关打开（配置里=' + savedStorage + '）',
  normFirst.after.checked === true && !normFirst.after.disabled,
  '载入=' + JSON.stringify(normFirst.before) + ' 归一后=' + JSON.stringify(normFirst.after),
)

// 拨一下开关：草稿要变、放弃修改要能还原（不写后端配置）
const toggled = await cdp.eval(sessionId, toggleSwitch('雷达 解析数据'))
const found = !!(toggled && !toggled.error)
const flipped = found && toggled.before !== toggled.after
check('拨动「雷达 解析数据」开关后勾选状态翻转', flipped, JSON.stringify(toggled))
const afterToggle = await cdp.eval(sessionId, readSwitch('雷达 解析数据'))
const reset = await cdp.eval(sessionId, clickByText('.panel__actions button', '放弃修改'))
await sleep(500)
const afterReset = await cdp.eval(sessionId, readSwitch('雷达 解析数据'))
check(
  '「放弃修改」把开关还原回已保存的值',
  reset === 'clicked' && flipped && !afterReset.error && afterReset.checked === toggled.before,
  JSON.stringify({ toggled, afterToggle, reset, afterReset }),
)

// ── ④ 存储总开关：不勾「启用存储」，下面那些子开关一个都不落盘 ─────────────────────
// 后端 SessionStorage.Open() 第一行就是 `if (!_config.Enabled) return;`（连会话目录都不建），
// WriteRaw / WriteParsed / WriteRadarBase / WriteFrame 每个写入口也都先看它。所以界面必须诚实：
// 总开关一关，子开关就该是灰的，并且要说清为什么。只改显示——灰掉不动值，勾回来立刻恢复。
const KIDS = ['保存雷达转基座系', '保存三设备同帧', '解析文件内嵌原始数据', '写 session.json 清单', '基座 原始数据']
// 上面的「放弃修改」刚把草稿整份拉回配置值（配置里总开关可能是关的）⇒ 这里再归一一次
const normAgain = await ensureMasterOn()
const beforeMaster = await cdp.eval(sessionId, readSwitches(['启用存储', ...KIDS]))
const kidsBefore = beforeMaster.slice(1)
check(
  '总开关打开时，子开关都可点',
  beforeMaster[0].checked === true && kidsBefore.every((s) => !s.error && !s.disabled),
  JSON.stringify(beforeMaster),
)

const offMaster = await cdp.eval(sessionId, toggleSwitch('启用存储'))
await sleep(400)
const afterOff = await cdp.eval(sessionId, readSwitches(['启用存储', ...KIDS]))
const kidsOff = afterOff.slice(1)
check(
  '关掉「启用存储」后子开关一起变灰（值一个都没动）',
  offMaster.after === false && kidsOff.every((s, i) => s.disabled === true && s.checked === kidsBefore[i].checked),
  JSON.stringify({ offMaster, afterOff }),
)

/** 把真实鼠标移到某个开关上：浮层只认真实悬停 / 键盘聚焦，合成事件不算数。 */
const hoverAt = async (pos) => {
  if (!pos || pos.error) return pos
  await cdp.send(
    'Input.dispatchMouseEvent',
    { type: 'mouseMoved', x: Math.round(pos.x), y: Math.round(pos.y), button: 'none', pointerType: 'mouse' },
    sessionId,
  )
  await sleep(600)
  return 'moved'
}
const mouseAway = () =>
  cdp.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: 8, y: 8, button: 'none', pointerType: 'mouse' }, sessionId)

const masterPos = await cdp.eval(sessionId, hoverSwitch('启用存储'))
const hoveredMaster = await hoverAt(masterPos)
const masterTip = await cdp.eval(sessionId, shownTip())
const shotMaster = await shoot('storage-master-off')
check(
  '总开关的说明浮层讲清「不建会话目录、子开关都不落盘」',
  hoveredMaster === 'moved' && !!masterTip && /总开关/.test(masterTip) && /不建会话目录/.test(masterTip),
  JSON.stringify({ masterPos, masterTip }),
)

const kidPos = await cdp.eval(sessionId, hoverSwitch('保存三设备同帧'))
const hoveredKid = await hoverAt(kidPos)
const kidTip = await cdp.eval(sessionId, shownTip())
check(
  '灰掉的子开关上也能看到「总开关没勾，这个开关当前不生效」',
  hoveredKid === 'moved' && !!kidTip && /当前不生效/.test(kidTip),
  JSON.stringify({ kidPos, kidTip }),
)
await mouseAway()

const onMaster = await cdp.eval(sessionId, toggleSwitch('启用存储'))
await sleep(400)
const afterOn = await cdp.eval(sessionId, readSwitches(['启用存储', ...KIDS]))
check(
  '勾回总开关后子开关立刻恢复可点，值照旧',
  onMaster.after === true && afterOn.slice(1).every((s, i) => !s.disabled && s.checked === kidsBefore[i].checked),
  JSON.stringify({ onMaster, afterOn }),
)
// 面板自始至终没把配置写回去（我们没点「保存并生效」）：配置文件里仍是原来那个口
const cfgAfter = await (await fetch('http://localhost:5080/api/config')).json()
const serialAfter = (cfgAfter.devices ?? []).find((d) => d.transport === 'serial')
const savedAfter = serialAfter ? serialAfter.transportSettings.serialPort : null
check(
  '配置文件里的串口号没被面板自动改写',
  savedAfter === savedPort,
  '保存值=' + JSON.stringify(savedPort) + ' 现在=' + JSON.stringify(savedAfter),
)
report.savedPort = { before: savedPort, after: savedAfter }
report.savedStorage = { before: savedStorage, after: cfgAfter.storage && cfgAfter.storage.enabled }
check(
  '配置文件里的「启用存储」也没被面板自动改写',
  !!(cfgAfter.storage && cfgAfter.storage.enabled === savedStorage),
  '载入时=' + savedStorage + ' 现在=' + JSON.stringify(cfgAfter.storage && cfgAfter.storage.enabled),
)
report.storage = { savedStorage, normFirst, normAgain, master: beforeMaster[0], kids: kidsBefore, afterOff, afterOn }

report.shots = { shotLoad, shotOut, shotPanel, shotSerial, shotMaster }
report.compass = { home, far, near }
const file = path.join(OUT, 'verify-ux-fixes.json')
fs.writeFileSync(file, JSON.stringify(report, null, 2))
console.log('\n截图：' + shotLoad + ' , ' + shotOut + ' , ' + shotPanel + ' , ' + shotSerial + ' , ' + shotMaster)
console.log('落盘：' + file)
console.log(fails.length === 0 ? '\n全部通过' : '\n失败项：' + fails.join(' / '))
process.exit(fails.length === 0 ? 0 : 2)
