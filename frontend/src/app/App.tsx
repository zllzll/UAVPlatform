/**
 * 应用主框架。
 *
 * 布局：顶栏（连接状态 / 显示坐标系原点 / 采集控制）
 *      + 左栏（参数配置 / 平台状态）
 *      + 中央三维视图（俯视 / 侧视 / 自由三种视角）
 *      + 右栏（雷达解析 / 基座解析 / 无人机解析 / 运行日志）
 * 左右两栏都能收成一条窄边条；收起状态与上次看的页签记在 localStorage（纯界面偏好，见 usePersistentState）。
 *
 * 中间不再有底部「解析样本」面板：它的内容与右栏三个解析页签的「解析原文」完全重复
 * （同一份 SignalR samples 数据），留着只是白占三维视图的高度。
 *
 * 数据通道分工（重要，改代码前先读）：
 *   - SignalR 的 `relative` 事件 → `services/live.ts` 的模块级对象 → 三维视图与右栏「最新一帧」由 rAF 直读。
 *     相对位置帧 10 Hz，**绝不能**放进 React state，否则整棵树每 100 ms 重渲染、三维掉帧。
 *   - SignalR 的 `status` / `samples` / `logs` 与配置走 zustand（低频）。
 */
import { useEffect, useState } from 'react'
import { ConfigPanel } from '../features/config/ConfigPanel'
import { InfoPanel, INFO_TABS, type InfoTab } from '../features/info/InfoPanel'
import { StatusPanel } from '../features/status/StatusPanel'
import { Viewer3D } from '../features/viewer3d/Viewer3D'
import { usePersistentState } from '../features/common/usePersistentState'
import { startRealtime, stopRealtime } from '../services/hub'
import { useStore } from '../store/useStore'

type SidePanel = 'config' | 'status'

export function App(): React.ReactElement {
  const connection = useStore((s) => s.connection)
  const connectionError = useStore((s) => s.connectionError)
  const status = useStore((s) => s.status)
  const config = useStore((s) => s.config)
  const busy = useStore((s) => s.busy)
  const notices = useStore((s) => s.notices)
  const followDrone = useStore((s) => s.followDrone)

  const setFollowDrone = useStore((s) => s.setFollowDrone)
  const resetCamera = useStore((s) => s.resetCamera)
  const updateUi = useStore((s) => s.updateUi)
  const startPlatform = useStore((s) => s.startPlatform)
  const stopPlatform = useStore((s) => s.stopPlatform)
  const dismissNotice = useStore((s) => s.dismissNotice)

  const [side, setSide] = usePersistentState<SidePanel>('uav.panel.side', 'config')
  const [info, setInfo] = usePersistentState<InfoTab>('uav.panel.info', 'radar')
  const [sideFold, setSideFold] = usePersistentState<boolean>('uav.panel.leftfold', false)
  const [infoFold, setInfoFold] = usePersistentState<boolean>('uav.panel.rightfold', false)

  useEffect(() => {
    let disposed = false
    void (async () => {
      try {
        await useStore.getState().bootstrap()
      } catch (error) {
        useStore.getState().notify(`初始化失败：${(error as Error).message}`, false)
      }
      if (!disposed) startRealtime()
    })()
    return () => {
      disposed = true
      stopRealtime()
    }
  }, [])

  const running = status?.running ?? false
  const ui = config?.ui
  const trailSeconds = ui?.trailSeconds ?? 60
  const bodyClass =
    'app__body' + (sideFold ? ' app__body--leftfold' : '') + (infoFold ? ' app__body--rightfold' : '')

  return (
    <div className="app">
      <header className="app__header">
        <h1 className="app__title">
          {config?.name ?? '无人机 + RTK 平台'}
          <small>基座 UM982 · 雷达 NSR · 无人机 GPS UCM221</small>
        </h1>

        <ConnectionChip state={connection} error={connectionError} />
        {running ? <span className="chip chip--ok">采集中</span> : <span className="chip chip--idle">未采集</span>}
        {status && (
          <span className="chip" title="显示坐标系原点恒为基座（世界 ENU 系）">
            原点：{status.originName}
            {status.referenceResolved ? '' : '（未解算）'}
          </span>
        )}
        {busy && <span className="chip chip--warn">{busy}</span>}

        <span className="app__spacer" />

        <button className="btn btn--ghost" onClick={resetCamera} title="把相机复位到当前视角的默认机位">
          相机复位
        </button>
        {running ? (
          <button className="btn btn--danger" onClick={() => void stopPlatform()} disabled={busy !== null}>
            停止采集
          </button>
        ) : (
          <button className="btn btn--primary" onClick={() => void startPlatform()} disabled={busy !== null}>
            开始采集
          </button>
        )}
      </header>

      <div className="app__toolbar">
        <label className="switch">
          <input type="checkbox" checked={ui?.showTargets ?? true} onChange={(e) => void updateUi({ showTargets: e.target.checked })} />
          目标
        </label>
        <label className="switch">
          <input type="checkbox" checked={ui?.showPointCloud ?? true} onChange={(e) => void updateUi({ showPointCloud: e.target.checked })} />
          点云
        </label>
        <label className="switch" title="只控制无人机本身（图标、航向箭头、RTK 位置标记）；轨迹与测距连线另有开关">
          <input type="checkbox" checked={ui?.showDrone ?? true} onChange={(e) => void updateUi({ showDrone: e.target.checked })} />
          无人机
        </label>
        <label className="switch">
          <input type="checkbox" checked={ui?.showDroneTrack ?? true} onChange={(e) => void updateUi({ showDroneTrack: e.target.checked })} />
          无人机轨迹
        </label>
        <label className="switch">
          <input type="checkbox" checked={ui?.showTargetTrails ?? true} onChange={(e) => void updateUi({ showTargetTrails: e.target.checked })} />
          目标尾迹
        </label>
        <label className="switch">
          <input type="checkbox" checked={ui?.showLinks ?? true} onChange={(e) => void updateUi({ showLinks: e.target.checked })} />
          测距连线
        </label>
        <label className="switch">
          <input type="checkbox" checked={followDrone} onChange={(e) => setFollowDrone(e.target.checked)} />
          跟随无人机
        </label>
        <TrailField value={trailSeconds} onChange={(value) => void updateUi({ trailSeconds: value })} />
        <span className="app__spacer" />
        <span className="hint">
          三维坐标：x = 东，y = 天，z = −北；原点 = {status?.originName ?? '基座'}
        </span>
      </div>

      <div className={bodyClass}>
        {sideFold ? (
          <aside className="app__rail">
            <button className="app__railbtn" onClick={() => setSideFold(false)} title="展开左栏（参数配置 / 平台状态）">
              参数配置 · 平台状态
            </button>
          </aside>
        ) : (
          <aside className="app__sidebar">
            <div className="tabs">
              <TabButton active={side === 'config'} onClick={() => setSide('config')}>
                参数配置
              </TabButton>
              <TabButton active={side === 'status'} onClick={() => setSide('status')}>
                平台状态
              </TabButton>
              <span className="app__spacer" />
              <button className="btn btn--ghost btn--tiny" onClick={() => setSideFold(true)} title="收起左栏">
                ◀ 收起
              </button>
            </div>
            {side === 'config' && <ConfigPanel />}
            {side === 'status' && <StatusPanel />}
          </aside>
        )}

        <main className="app__center">
          <div className="app__viewer">
            <Viewer3D />
          </div>
        </main>

        {infoFold ? (
          <aside className="app__rail app__rail--right">
            <button className="app__railbtn" onClick={() => setInfoFold(false)} title="展开右栏（解析文本 / 运行日志）">
              解析文本 · 运行日志
            </button>
          </aside>
        ) : (
          <aside className="app__info">
            <div className="tabs tabs--wrap">
              {INFO_TABS.map((item) => (
                <TabButton key={item.id} active={info === item.id} onClick={() => setInfo(item.id)}>
                  {item.label}
                </TabButton>
              ))}
              <span className="app__spacer" />
              <button className="btn btn--ghost btn--tiny" onClick={() => setInfoFold(true)} title="收起右栏">
                收起 ▶
              </button>
            </div>
            <InfoPanel tab={info} />
          </aside>
        )}
      </div>

      <div className="notices">
        {notices.map((n) => (
          <div key={n.id} className={n.ok ? 'notice' : 'notice notice--bad'}>
            <span style={{ flex: '1 1 auto' }}>{n.text}</span>
            <button onClick={() => dismissNotice(n.id)} title="关闭">
              ×
            </button>
          </div>
        ))}
      </div>
    </div>
  )
}

function TabButton({
  active,
  onClick,
  children,
}: {
  active: boolean
  onClick: () => void
  children: React.ReactNode
}): React.ReactElement {
  return (
    <button className={active ? 'tabs__item tabs__item--active' : 'tabs__item'} onClick={onClick}>
      {children}
    </button>
  )
}

/** 尾迹时长合法性：空串/非数字当 0，超界夹到 0~600。 */
function clampTrail(text: string): number {
  const parsed = Number(text)
  if (!Number.isFinite(parsed)) return 0
  return Math.min(600, Math.max(0, Math.round(parsed)))
}

/**
 * 尾迹时长：改的是后端 ui.trailSeconds（走 /api/config/ui，不会打断采集）。
 *
 * 输入框（0~600 秒）而不是下拉：现场就是想要一个能自由填的数。打字期间只动本地文本，
 * 失焦或回车才提交——每敲一位就 PUT 一次的话，敲「120」的中间态「1」「12」也会被存进去。
 */
function TrailField({ value, onChange }: { value: number; onChange: (value: number) => void }): React.ReactElement {
  const [text, setText] = useState(String(value))

  // 外部值变了（另一个窗口改了配置、后端回填）时同步回来，但别打断正在输入的人
  useEffect(() => {
    setText((current) => (clampTrail(current) === value ? current : String(value)))
  }, [value])

  const commit = (): void => {
    const next = clampTrail(text)
    setText(String(next))
    if (next !== value) onChange(next)
  }

  return (
    <label
      className="switch"
      style={{ gap: 4 }}
      title="轨迹在三维里保留多久（0~600 秒）：超过这个时长的点逐点消失，断流后整条清空；0 表示不画尾迹"
    >
      尾迹
      <input
        type="number"
        min={0}
        max={600}
        step={5}
        value={text}
        onChange={(e) => setText(e.target.value)}
        onBlur={commit}
        onKeyDown={(e) => {
          if (e.key === 'Enter') {
            commit()
            e.currentTarget.blur()
          } else if (e.key === 'Escape') {
            setText(String(value))
          }
        }}
        style={{
          width: 62,
          background: 'var(--bg-input)',
          color: 'var(--text)',
          border: '1px solid var(--border)',
          borderRadius: 5,
          padding: '2px 4px',
        }}
      />
      s
    </label>
  )
}

function ConnectionChip({
  state,
  error,
}: {
  state: 'connecting' | 'online' | 'offline'
  error: string | null
}): React.ReactElement {
  if (state === 'online') return <span className="chip chip--ok">后端已连接</span>
  if (state === 'connecting') return <span className="chip chip--warn">连接后端中…</span>
  return (
    <span className="chip chip--bad" title={error ?? undefined}>
      后端断开{error ? `：${error}` : ''}
    </span>
  )
}
