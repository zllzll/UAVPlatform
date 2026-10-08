/**
 * REST 客户端。所有请求都走相对路径，由 Vite dev 代理或后端同源托管转给采集后端。
 */
import type {
  RelativeFrame,
  HealthInfo,
  PlatformConfig,
  PlatformSnapshot,
  PlatformStatus,
  SessionInfo,
  TestResult,
  TrackSnapshot,
  UiSettings,
} from '../types'

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(path, {
    ...init,
    headers: {
      Accept: 'application/json',
      ...(init?.body ? { 'Content-Type': 'application/json' } : {}),
      ...(init?.headers ?? {}),
    },
  })
  if (!response.ok) {
    const text = await response.text().catch(() => '')
    throw new Error(`${init?.method ?? 'GET'} ${path} 失败：HTTP ${response.status} ${text.slice(0, 300)}`)
  }
  if (response.status === 204) return undefined as T
  return (await response.json()) as T
}

function post<T>(path: string, body?: unknown): Promise<T> {
  return request<T>(path, { method: 'POST', body: body === undefined ? undefined : JSON.stringify(body) })
}

export const api = {
  health: () => request<HealthInfo>('/api/health'),
  status: () => request<PlatformStatus>('/api/status'),
  snapshot: () => request<PlatformSnapshot>('/api/snapshot'),
  relative: () => request<RelativeFrame | null>('/api/relative'),
  tracks: () => request<TrackSnapshot>('/api/tracks'),
  sessions: () => request<SessionInfo[]>('/api/sessions'),
  logs: () => request<string[]>('/api/logs'),

  /** 本机可用串口名列表，用于基座串口下拉选择。 */
  serialPorts: () => request<{ ports: string[] }>('/api/serial-ports'),

  getConfig: () => request<PlatformConfig>('/api/config'),
  getDefaultConfig: () => request<PlatformConfig>('/api/config/default'),
  saveConfig: (config: PlatformConfig) =>
    request<PlatformConfig>('/api/config', { method: 'PUT', body: JSON.stringify(config) }),

  /**
   * 只保存界面显示选项。与 saveConfig 的区别：后端不重启采集管线，
   * 所以边采集边勾选「显示目标 / 点云 / 轨迹」不会打断实时绘制。
   * 传完整 ui 对象（调用方负责与现有值合并），避免缺省字段被后端默认值覆盖。
   */
  saveUi: (ui: UiSettings) =>
    request<PlatformConfig>('/api/config/ui', { method: 'PUT', body: JSON.stringify(ui) }),

  startPlatform: () => post<TestResult>('/api/platform/start'),
  stopPlatform: () => post<TestResult>('/api/platform/stop'),
  reconnect: (kind: string) => post<TestResult>(`/api/devices/${kind}/reconnect`),
  sendToDevice: (kind: string, payload: { text?: string; hex?: string }) =>
    post<TestResult>(`/api/devices/${kind}/send`, payload),

  resetReference: () => post<TestResult>('/api/relative/reset-reference'),
  forceRelative: () => post<TestResult>('/api/relative/force'),
  clearTracks: () => post<TestResult>('/api/tracks/clear'),
}

export type Api = typeof api
