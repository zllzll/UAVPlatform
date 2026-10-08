/**
 * SignalR 实时通道。
 *
 * 服务端 → 客户端事件：`relative`（RelativeFrame）、`samples`（SampleSummary[]）、
 * `status`（PlatformStatus）、`logs`（string[]）、`notice`（string）。
 * 高频的 `relative` 直接写进 services/live.ts 的非响应式对象，不进 React state。
 */
import { HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr'
import { live } from './live'
import { useStore } from '../store/useStore'
import type { RelativeFrame, PlatformStatus, SampleSummary } from '../types'

let connection: HubConnection | null = null
let started = false

export function buildConnection(): HubConnection {
  const hub = new HubConnectionBuilder()
    .withUrl('/hubs/platform')
    // 退避重连间隔（毫秒）。signalr 10.x 的该重载接受 number[]，
    // 不接受 v6 时代的 { nextRetryDelayInMilliseconds }[] 字面量数组。
    .withAutomaticReconnect([500, 1000, 2000, 5000, 10000])
    .configureLogging(LogLevel.Warning)
    .build()

  hub.on('relative', (frame: RelativeFrame) => {
    live.update(frame)
  })

  hub.on('samples', (samples: SampleSummary[]) => {
    useStore.getState().appendSamples(samples)
  })

  hub.on('status', (status: PlatformStatus) => {
    useStore.getState().setStatus(status)
  })

  hub.on('logs', (lines: string[]) => {
    useStore.getState().appendLogs(lines)
  })

  hub.on('notice', (text: string) => {
    useStore.getState().notify(text)
  })

  hub.onreconnecting((error) => {
    useStore.getState().setConnection('connecting', error?.message ?? null)
  })
  hub.onreconnected(() => {
    useStore.getState().setConnection('online', null)
    void refreshAfterReconnect()
  })
  hub.onclose((error) => {
    useStore.getState().setConnection('offline', error?.message ?? null)
    // 自动重连在 onclose 后不再生效，这里自行重试，避免刷新页面才能恢复。
    if (started) window.setTimeout(() => void startHub(), 3000)
  })

  return hub
}

async function refreshAfterReconnect(): Promise<void> {
  try {
    await useStore.getState().bootstrap()
  } catch {
    /* 忽略：下一轮 status 事件会补上 */
  }
}

async function startHub(): Promise<void> {
  if (!connection) connection = buildConnection()
  if (connection.state !== HubConnectionState.Disconnected) return
  try {
    await connection.start()
    useStore.getState().setConnection('online', null)
    await refreshAfterReconnect()
  } catch (error) {
    useStore.getState().setConnection('offline', (error as Error).message)
    window.setTimeout(() => void startHub(), 3000)
  }
}

export function startRealtime(): void {
  started = true
  void startHub()
}

export function stopRealtime(): void {
  started = false
  void connection?.stop()
  connection = null
}

/** 供 REST 兜底使用：后端没推事件时也能手动刷一次快照。 */
export function hasRealtime(): boolean {
  return connection?.state === HubConnectionState.Connected
}
