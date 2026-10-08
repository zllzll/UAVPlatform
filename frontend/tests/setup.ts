/**
 * UI 冒烟测试的公共环境。
 *
 * fixture 由 `scripts/capture-fixtures.mjs` 从真实后端（`/api/snapshot` 等）
 * 抓取后裁剪而来，保证测试里看到的数据结构与真机一致，而不是手写臆测的形状。
 * 若后端契约变了，重跑抓取脚本即可让测试跟着变。
 */
import '@testing-library/jest-dom/vitest'
import { afterEach, vi } from 'vitest'
import { cleanup } from '@testing-library/react'
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { dirname, join } from 'node:path'

const fixturesDir = join(dirname(fileURLToPath(import.meta.url)), 'fixtures')
const fixtureCache = new Map<string, unknown>()

/** 读取一个 fixture（结果会被缓存，避免每个用例重复解析）。 */
export function fixture<T = unknown>(name: string): T {
  if (!fixtureCache.has(name)) {
    fixtureCache.set(name, JSON.parse(readFileSync(join(fixturesDir, name), 'utf8')))
  }
  return fixtureCache.get(name) as T
}

/** 后端所有 POST 动作统一返回这个形状。 */
const OK = { ok: true, message: '测试桩：已接受。', elapsedMs: 0 }

function route(path: string, method: string, body: unknown): unknown {
  if (method === 'PUT' && path === '/api/config') return body ?? fixture('config.json')
  // 真实后端 PUT /api/config/ui 返回的是整份 PlatformConfig（ui 段就是刚存下的那份，
  // 见 PlatformService.SaveUiAsync）。桩里必须照做：否则 store 的乐观更新会被一份
  // {ok:true,...} 覆盖掉，界面上勾完的开关看起来「没生效」。
  if (method === 'PUT' && path === '/api/config/ui') {
    return { ...fixture<Record<string, unknown>>('config.json'), ui: body }
  }
  switch (path) {
    case '/api/health':
      return {
        ok: true,
        running: true,
        clients: 1,
        configPath: '测试桩',
        sessionDirectory: '测试桩',
        storageRoot: '测试桩',
        time: '2026-01-01T00:00:00+08:00',
      }
    case '/api/snapshot':
      return fixture('snapshot.json')
    case '/api/status':
      return fixture('status.json')
    case '/api/config':
      return fixture('config.json')
    case '/api/relative':
      return fixture('relative.json')
    case '/api/sessions':
      return fixture('sessions.json')
    case '/api/logs':
      return ['测试桩日志']
    case '/api/tracks':
      return { drone: [], targets: {} }
    // 与本机后端真实返回一致（`GET /api/serial-ports` → {"ports":["COM1","COM2"]}）：
    // 串口下拉必须由这份真实数据填出来，测试里不能靠前端硬编码。
    case '/api/serial-ports':
      return { ports: ['COM1', 'COM2'] }
    default:
      return OK
  }
}

globalThis.fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
  const url =
    typeof input === 'string' ? input : input instanceof URL ? input.href : (input as Request).url
  const path = url.replace(/^https?:\/\/[^/]+/, '').split('?')[0]
  const method = (init?.method ?? 'GET').toUpperCase()
  let body: unknown
  if (typeof init?.body === 'string' && init.body.length > 0) {
    try {
      body = JSON.parse(init.body)
    } catch {
      body = undefined
    }
  }
  return new Response(JSON.stringify(route(path, method, body)), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  })
}) as unknown as typeof fetch

afterEach(() => {
  cleanup()
})
