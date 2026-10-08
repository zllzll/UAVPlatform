// 类名审计：找出「组件用了但 CSS 里没定义」以及「CSS 定义了但没人用」的类名。
//
// 为什么需要它：`Viewer3D.tsx` 渲染的是 <div className="viewer3d">，而 index.css 里写的是
// `.viewer-wrap` —— 名字对不上，容器就没有任何样式，高度塌成 auto，R3F 的 height:100%
// 无处可解析，画布退化成 HTML 默认的 300x150。表现是「三维区域很窄、下面一大片黑」。
// tsc、打包、jsdom 测试全都不会报，只有真浏览器量几何才看得见。
// 这个脚本把这类静默错配在构建前就挡下来。
//
// 用法：node scripts/audit-css-classes.mjs
// 退出码：发现「用了但没定义」时为 1（这是真 bug）；只有「定义了但没用」时为 0（仅是死代码）。

import { readFileSync, readdirSync, statSync } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const srcDir = path.join(root, 'src')

function walk(dir, out = []) {
  for (const e of readdirSync(dir, { withFileTypes: true })) {
    const p = path.join(dir, e.name)
    if (e.isDirectory()) {
      if (e.name === 'node_modules') continue
      walk(p, out)
    } else {
      out.push(p)
    }
  }
  return out
}

const files = walk(srcDir)
const tsFiles = files.filter((f) => /\.(tsx?|jsx?)$/.test(f))
const cssFiles = files.filter((f) => f.endsWith('.css'))

// --- 从组件里抽出所有类名字符串 ---------------------------------------------
function extractClassStrings(code) {
  const out = []
  const re = /className\s*=\s*/g
  let m
  while ((m = re.exec(code)) !== null) {
    const i = re.lastIndex
    const ch = code[i]
    if (ch === '"' || ch === "'") {
      const end = code.indexOf(ch, i + 1)
      if (end > 0) out.push(code.slice(i + 1, end))
    } else if (ch === '{') {
      // 花括号配对，取整个表达式，再从里面抽字符串字面量（含模板串的静态部分）
      let depth = 0
      let j = i
      for (; j < code.length; j++) {
        if (code[j] === '{') depth++
        else if (code[j] === '}') {
          depth--
          if (depth === 0) {
            j++
            break
          }
        }
      }
      // 先抹掉比较运算的操作数：`tab === 'devices' ? ...` 里的 'devices' 是状态值不是类名，
      // 否则会把这类值误报成「用了但 CSS 没定义」。
      const expr = code
        .slice(i + 1, j - 1)
        .replace(/(['"`][^'"`]*['"`])\s*(===|!==|==|!=)/g, ' ')
        .replace(/(===|!==|==|!=)\s*(['"`][^'"`]*['"`])/g, ' ')
      for (const sm of expr.matchAll(/"([^"\\]*)"|'([^'\\]*)'|`([^`\\]*)`/g)) {
        out.push(sm[1] ?? sm[2] ?? sm[3] ?? '')
      }
    }
  }
  return out
}

const used = new Map() // 类名 -> 首次出现的文件
const rawClassStrings = [] // 所有 className 串的原文，用于识别动态拼接出来的类名
for (const f of tsFiles) {
  const code = readFileSync(f, 'utf8')
  for (const raw of extractClassStrings(code)) {
    rawClassStrings.push(raw)
    for (let tok of raw.split(/\s+/)) {
      tok = tok.trim()
      if (!tok) continue
      // 丢掉模板串被 ${} 截断的残片（如 `chip--`）与不像类名的东西
      if (tok.endsWith('-') || tok.endsWith('_')) continue
      if (!/^[A-Za-z][A-Za-z0-9_-]*$/.test(tok)) continue
      if (!used.has(tok)) used.set(tok, path.relative(root, f))
    }
  }
}

// --- 从 CSS 里抽出所有已定义的类名 ------------------------------------------
const cssText = cssFiles.map((f) => readFileSync(f, 'utf8')).join('\n')
const defined = new Set()
for (const m of cssText.matchAll(/\.(-?[A-Za-z_][A-Za-z0-9_-]*)/g)) defined.add(m[1])

// --- 比对 -------------------------------------------------------------------
const missing = [...used.entries()].filter(([c]) => !defined.has(c)).sort()
// 死 CSS：只在 className 串里找，**不能在整个源码里做子串匹配** ——
// 形如 `samples` 这样的类名会撞上 `snapshot.samples` 这类数据标识符，被误判成「有人用」。
//
// 但也不能只认完整类名：`chip chip--${state}` 这种拼接出来的 chip--ok / chip--warn
// 并不会以完整形式出现。所以按 BEM 主干（`--` / `__` 之前的部分）判断：
// 只要主干在某个 className 串里作为独立 token 出现过，就认为该类的变体可能被用到。
const rawTokenStems = new Set()
for (const s of rawClassStrings) {
  for (let tok of s.split(/\s+/)) {
    tok = tok.trim()
    if (!tok) continue
    rawTokenStems.add(tok)
    rawTokenStems.add(tok.replace(/(-{2}|__).*$/, ''))
  }
}
const stemOf = (c) => c.replace(/(-{2}|__).*$/, '')
const unused = [...defined].filter((c) => !used.has(c) && !rawTokenStems.has(stemOf(c))).sort()

console.log(`扫描 ${tsFiles.length} 个源文件 / ${cssFiles.length} 个样式文件`)
console.log(`组件使用类名 ${used.size} 个，CSS 定义类名 ${defined.size} 个\n`)

if (missing.length > 0) {
  console.log(`❌ 组件用了但 CSS 未定义（${missing.length} 个）—— 这些元素没有任何样式：`)
  for (const [c, where] of missing) console.log(`   ${c}   ← ${where}`)
} else {
  console.log('✅ 组件用到的类名全部都有 CSS 定义。')
}

if (unused.length > 0) {
  console.log(`\n⚠️  CSS 定义但组件未使用（${unused.length} 个，只是死代码）：`)
  console.log(`   ${unused.join(', ')}`)
} else {
  console.log('\n✅ 没有未使用的 CSS 类名。')
}

process.exit(missing.length > 0 ? 1 : 0)
