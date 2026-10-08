// 真实浏览器 UI 冒烟测试（无头 Chrome + CDP）。
//
// 为什么需要它：`tests/ui-smoke.test.tsx` 跑在 jsdom 里，而 jsdom 没有 WebGL / Canvas2D，
// 所以 Viewer3D / Viewer2D / MapPanel 在那套测试里是被 mock 掉的。三个视图恰恰是最容易
// 出「运行时才炸」问题的地方（例如 R3F hook 用在 <Canvas> 之外，会让整棵 React 树卸载、
// 页面全黑）。本脚本用真正的 Chrome 打开真实页面，补上这一段。
//
// 用法：
//   node scripts/ui-smoke.mjs [url] [等待毫秒]
//   npm run smoke:ui
//
// 前置：后端需已启动（默认 http://localhost:5080）。
// 依赖：node 自带的 fetch / WebSocket / child_process / net，无需任何 npm 包。

import { spawn, spawnSync } from 'node:child_process'
import { existsSync } from 'node:fs'
import net from 'node:net'
import os from 'node:os'
import path from 'node:path'

const targetUrl = process.argv[2] || 'http://localhost:5080/'
const waitMs = Number(process.argv[3] || 12000)
const origin = new URL(targetUrl).origin

const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

const CHROME_CANDIDATES = [
  process.env.CHROME_PATH,
  'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',
  'C:\\Program Files (x86)\\Google\\Chrome\\Application\\chrome.exe',
  'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
  'C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe',
  '/usr/bin/google-chrome',
  '/usr/bin/chromium',
].filter(Boolean)

function findBrowser() {
  for (const p of CHROME_CANDIDATES) if (existsSync(p)) return p
  return null
}

function freePort() {
  return new Promise((resolve, reject) => {
    const srv = net.createServer()
    srv.once('error', reject)
    srv.listen(0, '127.0.0.1', () => {
      const { port } = srv.address()
      srv.close(() => resolve(port))
    })
  })
}

function fail(msg) {
  console.error(`\n❌ ${msg}`)
  process.exit(1)
}

// --- 前置：后端必须已经在跑 -------------------------------------------------
try {
  const r = await fetch(`${origin}/api/health`)
  if (!r.ok) fail(`后端 ${origin}/api/health 返回 HTTP ${r.status}，请先启动后端。`)
} catch (e) {
  fail(`连不上后端 ${origin}（${e.message}）。请先运行 run.ps1 或 dotnet run 启动后端。`)
}

const browser = findBrowser()
if (!browser) fail('没有找到 Chrome / Edge，可用 CHROME_PATH 环境变量指定。')

const port = await freePort()
const profile = path.join(os.tmpdir(), `uavplatform-ui-smoke-${Date.now()}`)
const cdp = `http://127.0.0.1:${port}`

console.log(`浏览器：${browser}`)
console.log(`目标页面：${targetUrl}`)
console.log(`CDP 端口：${port}\n`)

// 注意：stdio 用 'ignore'，不要用 'pipe'——某些受限模式下管道捕获会被拒绝。
const child = spawn(
  browser,
  [
    '--headless=new',
    '--no-sandbox',
    '--disable-gpu',
    '--enable-unsafe-swiftshader', // 允许软件 WebGL，否则 three.js 建不出上下文
    '--no-first-run',
    '--no-default-browser-check',
    '--hide-scrollbars',
    '--window-size=1680,1000',
    `--remote-debugging-port=${port}`,
    `--user-data-dir=${profile}`,
    'about:blank',
  ],
  { stdio: 'ignore', windowsHide: true },
)

function killBrowser() {
  try {
    if (process.platform === 'win32') {
      spawnSync('taskkill', ['/PID', String(child.pid), '/T', '/F'], { stdio: 'ignore' })
    } else {
      child.kill('SIGKILL')
    }
  } catch {
    /* 清理失败不影响结论 */
  }
}
process.on('exit', killBrowser)

// --- 等 CDP 起来并挑一个 page target ---------------------------------------
let page = null
for (let i = 0; i < 60 && !page; i++) {
  await sleep(250)
  try {
    const list = await (await fetch(`${cdp}/json/list`)).json()
    page = list.find((t) => t.type === 'page') ?? null
  } catch {
    /* 还没起来 */
  }
}
if (!page) {
  killBrowser()
  fail(`等了 15 秒仍连不上 CDP（${cdp}）。`)
}

const ws = new WebSocket(page.webSocketDebuggerUrl)
const events = []
const failures = []
let nextId = 0
const pending = new Map()

function send(method, params = {}) {
  const id = ++nextId
  ws.send(JSON.stringify({ id, method, params }))
  return new Promise((resolve) => pending.set(id, resolve))
}

ws.addEventListener('message', (ev) => {
  const msg = JSON.parse(ev.data)
  if (msg.id && pending.has(msg.id)) {
    pending.get(msg.id)(msg.result)
    pending.delete(msg.id)
    return
  }
  switch (msg.method) {
    case 'Runtime.exceptionThrown': {
      const d = msg.params.exceptionDetails || {}
      const desc = d.exception?.description || d.text || '(无描述)'
      events.push(`[未捕获异常] ${desc}`)
      failures.push(`未捕获异常：${desc.split('\n')[0]}`)
      break
    }
    case 'Runtime.consoleAPICalled': {
      const type = msg.params.type
      const text = (msg.params.args || [])
        .map((a) => a.value ?? a.description ?? `<${a.type}>`)
        .join(' ')
      events.push(`[console.${type}] ${text}`)
      if (type === 'error') failures.push(`console.error：${text}`)
      break
    }
    case 'Log.entryAdded': {
      const e = msg.params.entry || {}
      if (e.level === 'error') {
        events.push(`[log.error] ${e.text}${e.url ? `  ← ${e.url}` : ''}`)
        failures.push(`页面错误：${e.text}`)
      }
      break
    }
  }
})

await new Promise((resolve) => ws.addEventListener('open', resolve, { once: true }))
await send('Runtime.enable')
await send('Log.enable')
await send('Page.enable')

await send('Page.navigate', { url: 'about:blank' })
await sleep(300)
console.log(`导航并渲染 ${waitMs} ms …\n`)
await send('Page.navigate', { url: targetUrl })
await sleep(waitMs)

// 探针同时抓「渲染是否活着」和「各区域真实几何」。后者专门用来定位
// 「三维区域很窄 / 下面一片黑」这类只在高频布局上出现的问题：光看 CSS 看不出，
// 必须量每个盒子的 getBoundingClientRect 与 canvas 的 CSS 尺寸/绘制缓冲尺寸。
const probe = `(function(){
  var rect = function(sel){
    var e = document.querySelector(sel);
    if (!e) return null;
    var r = e.getBoundingClientRect();
    return { x: Math.round(r.x), y: Math.round(r.y), w: Math.round(r.width), h: Math.round(r.height) };
  };
  // '.app__bottom' 留着当回归探针：中间那块重复的「解析样本」面板已删除（m05836 第 2 点），
  // 这里必须量出 null；'.viewer2d*' 已随二维视图在 m05133 第 3 点里删掉。
  var sels = ['.app','.app__body','.app__sidebar','.app__center','.app__viewer',
              '.viewer3d','.viewer3d__views','.app__bottom'];
  var rects = {};
  sels.forEach(function(s){ rects[s] = rect(s); });
  return JSON.stringify({
    rootExists: !!document.getElementById('root'),
    rootHtmlLen: document.getElementById('root') ? document.getElementById('root').innerHTML.length : -1,
    rootChildren: document.getElementById('root') ? document.getElementById('root').children.length : -1,
    canvases: document.querySelectorAll('canvas').length,
    tabs: Array.prototype.map.call(document.querySelectorAll('.tabs__item'), function(e){return e.textContent;}),
    hud: (function(){var e=document.querySelector('.viewer-hud__line');return e?e.textContent:null;})(),
    notices: Array.prototype.map.call(document.querySelectorAll('.notice'), function(e){return e.textContent;}),
    viewport: { w: window.innerWidth, h: window.innerHeight, dpr: window.devicePixelRatio },
    document: { scrollW: document.documentElement.scrollWidth, scrollH: document.documentElement.scrollHeight },
    header: rect('.app__header'),
    // 三维画布必须撑满 .app__viewer。曾经这里是 150/494 —— 容器类名写成 .viewer-wrap
    // 而组件用的是 .viewer3d，容器没样式 → 高度塌成 auto → canvas 退化成 HTML 默认 300x150。
    threeDFill: (function(){
      var v = document.querySelector('.app__viewer');
      var c = v && v.querySelector('canvas');
      if (!v || !c) return null;
      var vr = v.getBoundingClientRect(), cr = c.getBoundingClientRect();
      return { viewH: Math.round(vr.height), canvasH: Math.round(cr.height),
               ratio: vr.height > 0 ? +(cr.height / vr.height).toFixed(3) : 0 };
    })(),
    toolbars: Array.prototype.map.call(document.querySelectorAll('.app__toolbar'), function(e){
      var r = e.getBoundingClientRect(); return { w: Math.round(r.width), h: Math.round(r.height) };
    }),
    rects: rects,
    canvasDetail: Array.prototype.map.call(document.querySelectorAll('canvas'), function(c){
      var r = c.getBoundingClientRect();
      var cs = getComputedStyle(c);
      return { cls: c.className || '(no class)',
               css: Math.round(r.width) + 'x' + Math.round(r.height),
               cssPos: Math.round(r.left) + ',' + Math.round(r.top),
               buffer: c.width + 'x' + c.height,
               display: cs.display, position: cs.position };
    })
  });
})()`

const { result } = await send('Runtime.evaluate', { expression: probe, returnByValue: true })
const info = result?.value ? JSON.parse(result.value) : null

// --- 交互探针：参数配置面板 --------------------------------------------------
// 需求① 的串口下拉、需求② 的折叠展开都只有「真的点一下」才能证明。
// 静态 DOM 快照看不出 <datalist> 有没有被 /api/serial-ports 填充，也看不出
// 点「全部展开」是否真的把 .collapse 变成 .collapse--open。
const interact = `(async function(){
  var out = {};
  var wait = function(ms){ return new Promise(function(r){ setTimeout(r, ms); }); };
  var qa = function(s){ return Array.prototype.slice.call(document.querySelectorAll(s)); };
  var byText = function(text){
    return qa('button, .tabs__item, .collapse__head').filter(function(e){
      return e.textContent.trim() === text;
    })[0] || null;
  };
  var click = function(text){ var b = byText(text); if (b) { b.click(); return true; } return false; };

  // 先确保切到「参数配置」页签
  out.clickedConfigTab = click('参数配置');
  await wait(200);

  // 默认应当是全部折叠：用户抱怨的正是「要滚很久才看得全」。
  // 折叠态下 .collapse__body 根本不渲染（Collapse 内部是 {collapsed ? null : ...}），
  // 所以串口输入框此刻不在 DOM 里 —— 查下拉之前必须先展开。
  out.collapseAtStart = qa('.collapse, .collapse--open').length;
  out.openAtStart = qa('.collapse--open').length;
  out.bodyAtStart = qa('.collapse__body').length;

  out.clickedExpand = click('全部展开');
  await wait(250);
  out.openAfterExpand = qa('.collapse--open').length;
  out.bodyAfterExpand = qa('.collapse__body').length;
  out.collapseHeads = qa('.collapse__head').map(function(e){ return e.textContent.trim(); });

  // 串口是原生 <select>（v1 的 <input list> + <datalist> 在 Chrome 里点正文不弹候选，
  // 现场反馈就是「串口点击下拉框无反应」→ m05836 第 1 点）。候选由 api.serialPorts()
  // 异步拉回来后才填进 options；后端刚起（正在建会话、写文件）时这个请求可能慢几百毫秒，
  // 那就等它落地再读 —— 否则会把「还没到」误判成「下拉是空的」（曾经偶发失败过一次）。
  // 真正没枚举到串口时下面的断言会走「必须明确提示可手输」那条分支。
  var fieldByLabel = function(text){
    return qa('.field').filter(function(f){
      var l = f.querySelector('.field__label');
      return l && l.textContent.trim() === text;
    })[0] || null;
  };
  var serialField = fieldByLabel('串口名');
  for (var wt = 0; wt < 30; wt++) {
    var probeSelect = serialField && serialField.querySelector('select');
    if (probeSelect && probeSelect.options.length > 1) break;
    await wait(150);
  }
  out.serialControls = serialField
    ? Array.prototype.map.call(serialField.querySelectorAll('select, input'), function(c){
        return c.tagName.toLowerCase();
      })
    : [];
  var serialSelect = serialField ? serialField.querySelector('select') : null;
  out.serialSelect = serialSelect
    ? {
        value: serialSelect.value,
        options: Array.prototype.map.call(serialSelect.options, function(o){ return o.value; }),
        labels: Array.prototype.map.call(serialSelect.options, function(o){ return o.textContent; }),
        width: Math.round(serialSelect.getBoundingClientRect().width),
        selectedLabel: serialSelect.options[serialSelect.selectedIndex]
          ? serialSelect.options[serialSelect.selectedIndex].textContent
          : '',
      }
    : null;

  // 「下拉框不够长，里面内容显示不全」的自动探测（m05836 第 4 点）：
  // 原生 <select> 的选中项文字由控件自己裁，浏览器不给它 title、也悬停不出全文，
  // 所以直接把选中项文字量出来跟控件宽度比 —— 超了就是当场显示不全。
  var textWidth = function(text, cs){
    var cv = document.createElement('canvas');
    var ctx = cv.getContext('2d');
    ctx.font = cs.fontWeight + ' ' + cs.fontSize + ' ' + cs.fontFamily;
    return ctx.measureText(text).width;
  };
  out.selectTextFit = qa('.field select').map(function(s){
    var idx = s.selectedIndex >= 0 ? s.selectedIndex : 0;
    var label = s.options[idx] ? s.options[idx].textContent : '';
    var cs = getComputedStyle(s);
    var need = Math.round(textWidth(label, cs));
    var has = Math.round(s.getBoundingClientRect().width);
    return { label: label, need: need, has: has, fits: need <= has };
  });

  // 被祖先 overflow:hidden 裁掉文字的控件（「解析 RMC」只显示出「解析」就是这一类）：
  // 元素自己的 rect 是完整宽度，得拿它跟最近的裁剪祖先比右边界。
  var clipperOf = function(el){
    var p = el.parentElement;
    while (p) {
      var cs = getComputedStyle(p);
      if (cs.overflowX === 'hidden' || cs.overflowX === 'clip') return p;
      p = p.parentElement;
    }
    return null;
  };
  out.clipped = [];
  var checkClip = function(el, text){
    if (!el || !text) return;
    var r = el.getBoundingClientRect();
    if (r.width < 1 || r.height < 1) return;
    var box = clipperOf(el);
    if (!box) return;
    var br = box.getBoundingClientRect();
    if (r.right > br.right + 1) {
      out.clipped.push({
        cls: String(el.className || el.tagName),
        text: text.slice(0, 20),
        need: Math.round(r.width),
        room: Math.round(br.right - r.left),
      });
    }
  };
  qa('.switch').forEach(function(el){ checkClip(el, el.textContent.trim()) });
  qa('.field__label').forEach(function(el){ checkClip(el, el.textContent.trim()) });
  qa('.viewbtn').forEach(function(el){ checkClip(el, el.textContent.trim()) });
  qa('.tabs__item').forEach(function(el){ checkClip(el, el.textContent.trim()) });
  qa('.btn').forEach(function(el){ checkClip(el, el.textContent.trim()) });

  // 参数提示必须是「悬停才出现、离开就消失」：静息态先确认页面上一个浮层都没有，
  // 再模拟鼠标进入目标元素（React 的 onMouseEnter 由冒泡的 mouseover/mouseout 合成）。
  var hoverIn = function(el){
    el.dispatchEvent(new MouseEvent('mouseover', { bubbles: true, relatedTarget: document.body }));
  };
  var hoverOut = function(el){
    el.dispatchEvent(new MouseEvent('mouseout', { bubbles: true, relatedTarget: document.body }));
  };
  var tipText = function(){
    var t = document.querySelector('.hinttip');
    return t ? t.textContent.trim() : null;
  };

  out.tipsAtRest = qa('.hinttip').length;
  out.hintMarks = qa('.hintmark').length;

  // 串口提示挂在控件本体上（下拉或手输框），鼠标移上去才弹。
  var serialControl = serialField
    ? serialField.querySelector('select') || serialField.querySelector('input')
    : null;
  if (serialControl) {
    hoverIn(serialControl);
    await wait(100);
    out.serialHintOnHover = tipText();
    out.tipAfterEnter = qa('.hinttip').length;
    hoverOut(serialControl);
    await wait(100);
    out.serialHintAfterLeave = tipText();
  }

  // 普通参数框：悬停整行（标签 + 控件）就该弹出该参数的说明。
  // （fieldByLabel 在上面查串口时就定义了，这里直接用。）
  var hintedField = fieldByLabel('远端主机');
  if (hintedField) {
    hoverIn(hintedField);
    await wait(100);
    out.fieldHintOnHover = tipText();
    hoverOut(hintedField);
    await wait(100);
  }
  out.tipsAfterLeave = qa('.hinttip').length;

  // 三个子页签各自都得能折叠（通讯参数 / 存储 / 相对位置）
  out.subTabs = {};
  var names = ['通讯参数', '存储', '相对位置'];
  for (var i = 0; i < names.length; i++) {
    click(names[i]);
    await wait(200);
    click('全部展开');
    await wait(200);
    out.subTabs[names[i]] = qa('.collapse--open').length;
  }

  click('通讯参数');
  await wait(200);
  out.clickedCollapseAll = click('全部折叠');
  await wait(250);
  out.openAfterCollapseAll = qa('.collapse--open').length;
  out.bodyAfterCollapseAll = qa('.collapse__body').length;

  return JSON.stringify(out);
})()`

const { result: ires } = await send('Runtime.evaluate', {
  expression: interact,
  returnByValue: true,
  awaitPromise: true,
})
const ui = ires?.value ? JSON.parse(ires.value) : null
ws.close()

console.log('=== 浏览器控制台事件 ===')
if (events.length === 0) console.log('(无任何异常或错误)')
else events.forEach((e) => console.log(`  ${e}`))

console.log('\n=== 渲染结果 ===')
console.log(
  info
    ? JSON.stringify(
        {
          rootExists: info.rootExists,
          rootHtmlLen: info.rootHtmlLen,
          rootChildren: info.rootChildren,
          canvases: info.canvases,
          tabs: info.tabs,
          notices: info.notices,
        },
        null,
        2,
      )
    : '(取值失败)',
)
console.log(`\nHUD：${info?.hud ?? '(无)'}`)

// 布局几何表：纵向累计高度是排查「区域太窄 / 下方留黑」的关键。
if (info?.rects && info.viewport) {
  const vp = info.viewport
  console.log('\n=== 布局几何（px）===')
  console.log(`  视口 ${vp.w}x${vp.h} @dpr${vp.dpr}　文档滚动尺寸 ${info.document.scrollW}x${info.document.scrollH}`)
  const rows = [
    ['.app__header', info.header],
    ...(info.toolbars || []).map((t, i) => [`.app__toolbar[${i}]`, t]),
    ...Object.entries(info.rects),
  ]
  for (const [name, r] of rows) {
    console.log(
      r
        ? `  ${name.padEnd(20)} y=${String(r.y).padStart(5)}  h=${String(r.h).padStart(5)}  w=${String(r.w).padStart(5)}  x=${String(r.x).padStart(4)}`
        : `  ${name.padEnd(20)} (该元素当前未挂载)`,
    )
  }
  console.log('\n  <canvas> 明细（css 尺寸应与布局一致，buffer 通常 = css x dpr）：')
  for (const c of info.canvasDetail || []) {
    console.log(`    ${c.cls.padEnd(18)} css=${c.css.padEnd(11)} @${c.cssPos.padEnd(11)} buffer=${c.buffer.padEnd(11)} ${c.display}/${c.position}`)
  }
  // 三维画布占比：这里量的是 canvas 本身，不是容器 —— 二者不一致正是历史 bug 的特征。
  const f = info.threeDFill
  if (f) {
    const pct = Math.round(f.ratio * 100)
    console.log(
      `\n  三维 canvas ${f.canvasH}px / 容器 ${f.viewH}px = ${pct}%` +
        (pct < 90 ? '  ← 偏小！容器类名是否与 CSS 选择器一致？（曾因 .viewer3d vs .viewer-wrap 塌成 150px）' : ''),
    )
  }
}

// --- 参数配置面板实测（需求① 串口下拉 / 需求② 折叠展开）----------------------
if (ui) {
  console.log('参数配置面板（真实点击后）：')
  console.log(`  进入时：可折叠区块 ${ui.collapseAtStart} 个，展开 ${ui.openAtStart} 个，渲染出内容 ${ui.bodyAtStart} 个`)
  if (ui.collapseHeads.length) console.log(`  区块标题：${ui.collapseHeads.join(' | ')}`)
  console.log(`  点「全部展开」→ 展开 ${ui.openAfterExpand} 个，内容 ${ui.bodyAfterExpand} 个`)
  console.log(`  串口控件：${(ui.serialControls ?? []).join(' + ') || '（找不到）'}`)
  if (ui.serialSelect) {
    const options = ui.serialSelect.options ?? []
    console.log(`  串口下拉候选：${options.length ? options.join('、') : '（空）'}`)
    console.log(`  串口下拉当前值：${ui.serialSelect.value || '（未设置）'}，控件宽 ${ui.serialSelect.width}px`)
  }
  console.log(`  串口框悬停提示：${ui.serialHintOnHover ?? '（未弹出）'}`)
  const fit = ui.selectTextFit ?? []
  if (fit.length) {
    console.log(
      `  下拉选中项文字：${fit.map((s) => `${s.fits ? '✔' : '✘'}「${s.label}」${s.need}/${s.has}px`).join('  ')}`,
    )
  }
  const clipped = ui.clipped ?? []
  console.log(
    `  被容器裁掉的文字：${clipped.length ? clipped.map((c) => `${c.cls}「${c.text}」${c.need}/${c.room}px`).join('；') : '无'}`,
  )
  for (const [name, n] of Object.entries(ui.subTabs ?? {})) {
    console.log(`  子页签「${name}」展开后 ${n} 个区块`)
  }
  console.log(`  点「全部折叠」→ 展开 ${ui.openAfterCollapseAll} 个，内容 ${ui.bodyAfterCollapseAll} 个`)
  console.log('')
}

// --- 断言 -------------------------------------------------------------------
if (!info) fail('无法读取页面渲染状态。')
if (!info.rootExists) fail('#root 不存在，index.html 可能被改动。')
if (info.rootHtmlLen <= 0) fail('React 没有渲染任何内容（页面全黑）。看上面的控制台事件定位异常。')
if (info.canvases < 1) fail('页面上没有任何 <canvas>，三维视图没起来。')
// 「三维区域很窄 / 下面一片黑」的自动防线：画布必须几乎撑满它的容器。
if (info.threeDFill && info.threeDFill.ratio < 0.9) {
  fail(
    `三维画布只占容器 ${Math.round(info.threeDFill.ratio * 100)}%` +
      `（canvas ${info.threeDFill.canvasH}px / 容器 ${info.threeDFill.viewH}px）。` +
      `多半是容器类名与 CSS 选择器不一致，导致高度塌成 auto、画布退化成 HTML 默认 300x150。` +
      `先跑 node scripts/audit-css-classes.mjs。`,
  )
}
// m05836 第 2 点：中间那块「解析样本」面板和右栏「解析原文」是同一份数据的两个出口，已删掉。
if (info.rects['.app__bottom']) {
  fail('中间仍然有底部「解析样本」面板（.app__bottom），它与右栏的解析文本重复。')
}
// 需求②：参数面板必须可折叠，且能一键全展开 / 全折叠。
if (ui) {
  if (!ui.clickedConfigTab) fail('左栏找不到「参数配置」页签。')

  // 默认应当是全部折叠 —— 用户抱怨的就是「要滚很久才看得全」。
  if (ui.openAtStart !== 0 || ui.bodyAtStart !== 0) {
    fail(`参数配置进入时有 ${ui.openAtStart} 个区块展开、${ui.bodyAtStart} 个内容可见，默认应全部折叠。`)
  }
  if (ui.collapseAtStart < 3) {
    fail(`通讯参数页签只有 ${ui.collapseAtStart} 个可折叠区块；三台设备都应可折叠。`)
  }
  if (!ui.clickedExpand) fail('参数配置里找不到「全部展开」按钮。')
  if (ui.openAfterExpand !== ui.collapseAtStart) {
    fail(`点「全部展开」后展开 ${ui.openAfterExpand} 个，应有 ${ui.collapseAtStart} 个。`)
  }
  if (ui.bodyAfterExpand !== ui.collapseAtStart) {
    fail(`点「全部展开」后有 ${ui.collapseAtStart - ui.bodyAfterExpand} 个区块没渲染出内容。`)
  }

  // 存储页签与相对位置页签同样要能折叠。
  const sub = ui.subTabs ?? {}
  if ((sub['存储'] ?? 0) < 1) fail('存储页签里没有可折叠区块。')
  if ((sub['相对位置'] ?? 0) < 4) {
    fail(
      `相对位置页签展开后只有 ${sub['相对位置'] ?? 0} 个区块；` +
        `相对位置参数 / 雷达安装 / 目标过滤 / 相对位置节奏 都应可折叠。`,
    )
  }

  if (!ui.clickedCollapseAll) fail('参数配置里找不到「全部折叠」按钮。')
  if (ui.openAfterCollapseAll !== 0 || ui.bodyAfterCollapseAll !== 0) {
    fail(
      `点「全部折叠」后仍有 ${ui.openAfterCollapseAll} 个区块展开、` +
        `${ui.bodyAfterCollapseAll} 个内容可见。`,
    )
  }

  // 需求①（m05836 第 1 点）：基座串口必须是「点哪里都能展开」的原生 <select>，候选来自后端，
  // 并且保留一条手输入口（设备还没插上时本机列表里根本没有目标口，必须能直接打字）。
  if (!ui.serialSelect) {
    fail('展开基座卡片后找不到串口下拉（<select>）。Chrome 里 <input list> 点正文不弹候选，基座串口应为原生下拉。')
  }
  const serialOptions = ui.serialSelect?.options ?? []
  // 枚举失败也允许，但必须明确告诉用户「可以手输」，不能静默给个空下拉。
  if (serialOptions.length <= 1) {
    const serialTip = ui.serialHintOnHover ?? ''
    const explained = serialTip.includes('未检测到串口') || serialTip.includes('失败')
    if (!explained) {
      fail('串口下拉里没有任何候选，且鼠标移到串口控件上也没有「读取失败 / 未检测到串口」的提示。')
    }
    console.log('⚠ 本机没有枚举到串口，已确认界面给出「可手动输入」提示。')
  } else {
    console.log(`✅ 串口下拉已由 /api/serial-ports 填充：${serialOptions.join('、')}（真实串口名，非前端硬编码）。`)
  }
  if (!serialOptions.includes('__manual__')) {
    fail('串口下拉里没有「手动输入…」入口；设备没插上时就没法配串口了。')
  }
  if ((ui.serialControls ?? []).indexOf('select') < 0) {
    fail('串口字段里没有 <select> 控件。')
  }
  if (ui.serialSelect && ui.serialSelect.value === '') {
    console.log('⚠ 串口下拉当前显示「（未设置）」：配置里基座串口为空，或已保存的值没被列进候选。')
  }

  // m05836 第 4 点：文字不许被裁。分两类查——控件被祖先 overflow:hidden 裁掉（「解析 RMC」→「解析」）、
  // 以及下拉框自身比选中项文字还窄（原生下拉不给悬停全文，只能靠宽度）。
  const clipped = ui.clipped ?? []
  if (clipped.length) {
    fail(
      `有 ${clipped.length} 处控件的文字被容器裁掉：` +
        clipped.map((c) => `${c.cls}「${c.text}」(需 ${c.need}px / 只有 ${c.room}px)`).join('；'),
    )
  }
  const unfit = (ui.selectTextFit ?? []).filter((s) => !s.fits)
  if (unfit.length) {
    fail(
      '有下拉框的选中项文字比控件还宽，当场显示不全：' +
        unfit.map((s) => `「${s.label}」(需 ${s.need}px / 控件 ${s.has}px)`).join('；'),
    )
  }

  // 参数提示不能常驻：静息态一个浮层都不该有，鼠标进入才弹、离开必须消失。
  if (ui.tipsAtRest !== 0) {
    fail(`页面静息状态下仍有 ${ui.tipsAtRest} 个参数提示浮层常驻显示。`)
  }
  if (ui.tipAfterEnter < 1 || !ui.serialHintOnHover) {
    fail('鼠标移到串口输入框上，参数提示没有弹出。')
  }
  if (ui.serialHintAfterLeave !== null || ui.tipsAfterLeave !== 0) {
    fail('鼠标离开参数后提示没有消失。')
  }
  if (!ui.fieldHintOnHover) {
    fail('鼠标移到带说明的参数行上，参数说明没有弹出。')
  }
  if (!ui.hintMarks || ui.hintMarks < 1) {
    fail('段落级说明没有收成可悬停的「说明」标记。')
  }
  console.log(
    `✅ 参数提示改为悬停弹出：静息 ${ui.tipsAtRest} 个浮层 → 进入 ${ui.tipAfterEnter} 个 → 离开 ${ui.tipsAfterLeave} 个。`,
  )
  console.log(`   串口框悬停提示：${ui.serialHintOnHover}`)
  console.log(`   参数行悬停提示：${ui.fieldHintOnHover}`)
  console.log(`   段落级「说明」标记：${ui.hintMarks} 个`)
}

if (failures.length > 0) fail(`捕获到 ${failures.length} 个运行时错误。`)

console.log('\n✅ PASS：页面正常渲染，无未捕获异常。')
process.exit(0)
