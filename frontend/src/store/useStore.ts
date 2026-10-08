/**
 * 低频应用状态（zustand）。高频相对位置帧请勿放这里，见 services/live.ts。
 */
import { create } from 'zustand'
import { api } from '../services/api'
import { live, loadTracks } from '../services/live'
import type {
  PlatformConfig,
  PlatformStatus,
  SampleSummary,
  SessionInfo,
  UiSettings,
} from '../types'

export type ConnectionState = 'connecting' | 'online' | 'offline'
export type PanelId = 'devices' | 'storage' | 'relative' | 'sessions' | 'samples' | 'logs'

export interface Notice {
  id: number
  text: string
  ok: boolean
  at: number
}

interface AppState {
  connection: ConnectionState
  connectionError: string | null
  config: PlatformConfig | null
  configDraft: PlatformConfig | null
  status: PlatformStatus | null
  sessions: SessionInfo[]
  samples: SampleSummary[]
  logs: string[]
  notices: Notice[]
  busy: string | null

  panel: PanelId
  selectedTargetId: number | null
  followDrone: boolean
  cameraNonce: number

  setConnection: (state: ConnectionState, error?: string | null) => void
  setStatus: (status: PlatformStatus) => void
  appendSamples: (samples: SampleSummary[]) => void
  appendLogs: (lines: string[]) => void
  notify: (text: string, ok?: boolean) => void
  dismissNotice: (id: number) => void

  bootstrap: () => Promise<void>
  loadConfig: () => Promise<void>
  updateDraft: (mutate: (draft: PlatformConfig) => void) => void
  resetDraft: () => void
  saveConfig: () => Promise<void>
  updateUi: (patch: Partial<UiSettings>) => Promise<void>
  setPanel: (panel: PanelId) => void
  selectTarget: (id: number | null) => void
  setFollowDrone: (follow: boolean) => void
  resetCamera: () => void
  refreshSessions: () => Promise<void>

  runAction: (label: string, action: () => Promise<{ ok: boolean; message: string }>) => Promise<void>
  startPlatform: () => Promise<void>
  stopPlatform: () => Promise<void>
  reconnect: (kind: string) => Promise<void>
  clearTracks: () => Promise<void>
  resetReference: () => Promise<void>
}

let noticeSeq = 1

/** 提示条存活时长（毫秒）。到点自动消失，右下角不会越堆越多。 */
const NOTICE_TTL_MS = 6000

/** 深拷贝配置草稿：用 JSON 往返即可，配置对象里没有 Date / Map。 */
function cloneConfig(config: PlatformConfig): PlatformConfig {
  return JSON.parse(JSON.stringify(config)) as PlatformConfig
}

export const useStore = create<AppState>((set, get) => ({
  connection: 'connecting',
  connectionError: null,
  config: null,
  configDraft: null,
  status: null,
  sessions: [],
  samples: [],
  logs: [],
  notices: [],
  busy: null,

  panel: 'devices',
  selectedTargetId: null,
  followDrone: true,
  cameraNonce: 0,

  setConnection: (connection, error = null) => set({ connection, connectionError: error }),

  setStatus: (status) => set({ status }),

  appendSamples: (incoming) =>
    set((state) => {
      const merged = incoming.length >= 40 ? incoming : [...state.samples, ...incoming]
      return { samples: merged.length > 400 ? merged.slice(merged.length - 400) : merged }
    }),

  appendLogs: (lines) =>
    set((state) => {
      const merged = [...state.logs, ...lines]
      return { logs: merged.length > 600 ? merged.slice(merged.length - 600) : merged }
    }),

  notify: (text, ok = true) => {
    const id = noticeSeq++
    set((state) => ({
      notices: [...state.notices.slice(-6), { id, text, ok, at: Date.now() }],
    }))
    // 到点自动消失；手动点「×」仍然可以提前关掉（dismissNotice 对已删除的 id 是空操作）。
    window.setTimeout(() => get().dismissNotice(id), NOTICE_TTL_MS)
  },

  dismissNotice: (id) => set((state) => ({ notices: state.notices.filter((n) => n.id !== id) })),

  bootstrap: async () => {
    const snapshot = await api.snapshot()
    set({
      config: snapshot.config,
      configDraft: cloneConfig(snapshot.config),
      status: snapshot.status,
      samples: snapshot.samples,
      logs: snapshot.logs,
    })
    if (snapshot.relative) live.update(snapshot.relative)
    loadTracks(snapshot.tracks.drone, snapshot.tracks.targets)
    await get().refreshSessions()
  },

  loadConfig: async () => {
    const config = await api.getConfig()
    set({ config, configDraft: cloneConfig(config) })
  },

  updateDraft: (mutate) =>
    set((state) => {
      if (!state.configDraft) return {}
      const draft = cloneConfig(state.configDraft)
      mutate(draft)
      return { configDraft: draft }
    }),

  resetDraft: () => {
    const config = get().config
    if (config) set({ configDraft: cloneConfig(config) })
  },

  saveConfig: async () => {
    const draft = get().configDraft
    if (!draft) return
    set({ busy: '正在保存配置…' })
    try {
      const saved = await api.saveConfig(draft)
      set({ config: saved, configDraft: cloneConfig(saved) })
      get().notify('配置已保存并生效。')
    } catch (error) {
      get().notify(`配置保存失败：${(error as Error).message}`, false)
      throw error
    } finally {
      set({ busy: null })
    }
  },

  updateUi: async (patch) => {
    const config = get().config
    if (!config) return
    const next = cloneConfig(config)
    next.ui = { ...next.ui, ...patch }
    // 乐观更新，避免勾选框回弹
    set({ config: next, configDraft: get().configDraft ? { ...get().configDraft!, ui: next.ui } : null })
    try {
      // 只提交 ui 段：走后端 /api/config/ui，不重启采集管线。
      // 若改用 saveConfig，每勾一个复选框后端都会「停→应用配置→启」，
      // 正在跑的采集会被打断，实时绘制当场停住。
      const saved = await api.saveUi(next.ui)
      set({ config: saved })
    } catch (error) {
      get().notify(`界面设置保存失败：${(error as Error).message}`, false)
    }
  },

  setPanel: (panel) => set({ panel }),
  selectTarget: (selectedTargetId) => set({ selectedTargetId }),
  setFollowDrone: (followDrone) => set({ followDrone }),
  resetCamera: () => set((state) => ({ cameraNonce: state.cameraNonce + 1 })),

  refreshSessions: async () => {
    try {
      set({ sessions: await api.sessions() })
    } catch {
      /* 会话列表拉取失败不影响主流程 */
    }
  },

  runAction: async (label, action) => {
    set({ busy: label })
    try {
      const result = await action()
      get().notify(result.message, result.ok)
    } catch (error) {
      get().notify(`${label}失败：${(error as Error).message}`, false)
    } finally {
      set({ busy: null })
      void get().refreshSessions()
    }
  },

  startPlatform: () => get().runAction('启动采集', () => api.startPlatform()),
  stopPlatform: () => get().runAction('停止采集', () => api.stopPlatform()),
  reconnect: (kind) => get().runAction('重连设备', () => api.reconnect(kind)),
  clearTracks: () =>
    get().runAction('清空轨迹', async () => {
      const result = await api.clearTracks()
      live.clear()
      return result
    }),
  resetReference: () => get().runAction('重算坐标系原点', () => api.resetReference()),
}))
