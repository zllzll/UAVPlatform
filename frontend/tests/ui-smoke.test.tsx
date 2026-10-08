/**
 * 前端渲染冒烟测试。
 *
 * 目的：两个面板（ConfigPanel / StatusPanel）与 App 外壳此前只过了
 * `tsc` 与打包，从未真正渲染过一次。这里用真实后端抓下来的 fixture 把它们挂到
 * jsdom 里跑一遍，抓「属性不存在 → 整棵树白屏」这类只在运行时才炸的问题。
 *
 * 明确不覆盖：Viewer3D 需要 WebGL，jsdom 没有实现，因此这个模块在 App 用例里被替换成占位元素。
 */
import { describe, expect, it, vi } from 'vitest'
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { fixture } from './setup'
import type { PlatformConfig, PlatformStatus, SampleSummary } from '../src/types'

// App 会连 SignalR 并挂载三维画布，两者在 jsdom 下都不可用，替换为占位实现。
vi.mock('../src/services/hub', () => ({
  startRealtime: vi.fn(),
  stopRealtime: vi.fn(),
  hasRealtime: () => false,
}))
vi.mock('../src/features/viewer3d/Viewer3D', () => ({
  Viewer3D: () => <div data-testid="viewer3d" />,
}))

const { ConfigPanel } = await import('../src/features/config/ConfigPanel')
const { StatusPanel } = await import('../src/features/status/StatusPanel')
const { App } = await import('../src/app/App')
const { useStore } = await import('../src/store/useStore')

const config = fixture<PlatformConfig>('config.json')
const status = fixture<PlatformStatus>('status.json')
const samples = fixture<{ samples: SampleSummary[] }>('snapshot.json').samples

/** 每个用例都从干净的 store 起步，再走一次真实的 bootstrap()。 */
async function seedStore(): Promise<void> {
  useStore.setState({
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
  })
  await useStore.getState().bootstrap()
}

describe('后端契约（fixture 与 types.ts 的一致性）', () => {
  it('PlatformConfig 里的真枚举是 camelCase', () => {
    expect(config.devices[0].kind).toBe('baseStation')
    expect(config.devices[0].transport).toBe('serial')
    expect(config.devices[1].transport).toBe('tcpClient')
    // 无人机按现场实际走 UDP：它主动把 GPS/姿态回传到本机 8100 端口。
    expect(config.devices[2].transport).toBe('udp')
    expect(config.devices[2].transportSettings.localPort).toBe(8100)
    expect(config.storage.folderMode).toBe('perRun')
    // m04238 第 5 点：显示坐标系原点不再是配置项（全项目统一基座世界坐标系），
    // 这两个键必须从后端契约里彻底消失，免得旧配置又能把画面切到雷达系。
    expect((config.relative as unknown as Record<string, unknown>).displayOrigin).toBeUndefined()
    expect((config.relative as unknown as Record<string, unknown>).rotateToRadarHeading).toBeUndefined()
  })

  it('transportSettings 的 parity/stopBits/handshake 是 BCL 名字符串', () => {
    const t = config.devices[0].transportSettings
    expect(typeof t.parity).toBe('string')
    expect(typeof t.stopBits).toBe('string')
    expect(typeof t.handshake).toBe('string')
    expect(t.parity).toBe('None')
    expect(t.stopBits).toBe('One')
  })

  it('status 的 kind/state/transport 是 PascalCase 字符串（与 config 相反）', () => {
    expect(status.devices[0].kind).toBe('BaseStation')
    expect(status.devices[0].transport).toBe('Serial')
    expect(['Disconnected', 'Connecting', 'Connected', 'Faulted', 'Disabled']).toContain(
      status.devices[0].state,
    )
    // m04238 第 5 点：显示坐标系原点恒为基座，状态里不再有 origin 枚举字段
    expect(status.originName).toBe('基座')
    expect((status as unknown as Record<string, unknown>).origin).toBeUndefined()
  })
})

describe('useStore.bootstrap', () => {
  it('把 snapshot 灌进 store，并载入轨迹', async () => {
    await seedStore()
    const state = useStore.getState()
    expect(state.config?.name).toBe('无人机 + RTK 平台')
    expect(state.configDraft).not.toBeNull()
    expect(state.status?.running).toBe(true)
    expect(state.samples.length).toBe(samples.length)
    expect(state.sessions.length).toBeGreaterThan(0)
  })

  it('草稿与已保存配置是两个独立对象（改草稿不影响 config）', async () => {
    await seedStore()
    useStore.getState().updateDraft((d) => {
      d.name = '被改过的名字'
    })
    expect(useStore.getState().configDraft?.name).toBe('被改过的名字')
    expect(useStore.getState().config?.name).toBe('无人机 + RTK 平台')
    useStore.getState().resetDraft()
    expect(useStore.getState().configDraft?.name).toBe('无人机 + RTK 平台')
  })
})

describe('ConfigPanel', () => {
  /** 面板默认「全部折叠」（一屏先看目录，用户反馈），折叠体里的控件必须先展开再断言。 */
  function expandAll(): void {
    fireEvent.click(screen.getByText('全部展开'))
  }

  /** 按 Field 的标签文字取它内部的控件：标签与控件在同一个 <label className="field"> 里。 */
  function fieldControl(label: string, tag: string): HTMLElement {
    const field = screen.getByText(label).closest('label')
    if (!field) throw new Error(`找不到标签 ${label} 所属的 Field`)
    const el = field.querySelector(tag)
    if (!el) throw new Error(`标签 ${label} 内没有 ${tag}`)
    return el as HTMLElement
  }

  /**
   * 参数可编辑的前提是「没在采集」：采集中整个参数区会被锁死（m04845，见本 describe 最后一个用例）。
   * fixture 里的 status.running 是 true（抓包时后端正在采集），所以下面这些用例先按「已停止采集」渲染。
   */
  async function seedStopped(): Promise<void> {
    await seedStore()
    const current = useStore.getState().status
    if (current) useStore.setState({ status: { ...current, running: false } })
  }

  it('默认的「通讯参数」页签渲染出三个设备区块与真实配置值', async () => {
    await seedStopped()
    const { container } = render(<ConfigPanel />)
    expandAll()

    expect(screen.getByText(`平台配置 · ${config.name}`)).toBeInTheDocument()
    expect(screen.getByText('UM982（NMEA 0183）')).toBeInTheDocument()
    expect(screen.getByText('NSR 雷达（TCP）')).toBeInTheDocument()
    expect(screen.getByText('UCM221（上传流 0xA5 0x5A，小端）')).toBeInTheDocument()

    // 输入框真的拿到了后端配置值，而不只是渲染了个空壳
    const values = Array.from(container.querySelectorAll('input')).map((el) => el.value)
    // 串口名现在是「点一下就展开」的原生下拉（旧的 `<input list>` 点正文不弹候选），值从 select 上查
    expect((fieldControl('串口名', 'select') as HTMLSelectElement).value).toBe('COM3')
    expect(values).toContain('192.168.10.128')
    // 无人机走 UDP：本地监听端口是现场要填的那个数（需求指定 8100）
    expect(values).toContain('8100')
    expect(values).toContain('50000')
    // 三台设备的名称都以文字形式出现在卡片上
    for (const device of config.devices) {
      expect(container.textContent).toContain(device.name)
    }
    // 波特率是下拉框不是输入框，单独查一次选中项
    const selected = Array.from(container.querySelectorAll('select')).find(
      (el) => el.value === '460800',
    )
    expect(selected).toBeDefined()
  })

  it('串口名是可点开的下拉：候选只列本机串口，手输时下拉框也留着（m05836 第 1 点 / m06173 第 4 点 / m00868 第 1 条）', async () => {
    await seedStopped()
    render(<ConfigPanel />)
    expandAll()

    const serial = fieldControl('串口名', 'select') as HTMLSelectElement
    // 这一格（.field 本身就是 <label>）用来查「有没有多出那个手输框」
    const serialField = screen.getByText('串口名').closest('label') as HTMLElement
    // 候选来自 /api/serial-ports（测试桩给 COM1/COM2），是异步拉回来的，得等它落地才能查。
    // m00868 第 1 条：配置里的 COM3 本机没有 ⇒ 候选里不列它、下拉显示「（未设置）」，
    // 但**只改显示**：草稿里的 COM3 必须原样保留（不能因为本机没有就被吞成空）。
    await waitFor(() => {
      expect(Array.from(serial.options).map((o) => o.value)).toEqual([
        '',
        'COM1',
        'COM2',
        '__manual__',
      ])
    })
    expect(serial.value).toBe('')
    expect(serial.options[serial.selectedIndex].textContent).toBe('（未设置）')
    expect(useStore.getState().configDraft?.devices[0].transportSettings.serialPort).toBe('COM3')

    // 选一个枚举到的口：直接写进草稿
    fireEvent.change(serial, { target: { value: 'COM2' } })
    expect(useStore.getState().configDraft?.devices[0].transportSettings.serialPort).toBe('COM2')

    // 没进手输模式时这一格只有下拉框一个控件
    expect(serialField.querySelector('input')).toBeNull()

    // 「手动输入…」在下面多出一个文本框，下拉框**不消失**（现场反馈：切过去就回不到列表了）
    fireEvent.change(serial, { target: { value: '__manual__' } })
    expect(fieldControl('串口名', 'select')).toBeInTheDocument()
    const manual = fieldControl('串口名', 'input') as HTMLInputElement
    fireEvent.change(manual, { target: { value: 'COM7' } })
    expect(useStore.getState().configDraft?.devices[0].transportSettings.serialPort).toBe('COM7')

    // 直接在下拉框里点一项就回列表：手输框收起来，值跟着切
    fireEvent.change(serial, { target: { value: 'COM1' } })
    expect(useStore.getState().configDraft?.devices[0].transportSettings.serialPort).toBe('COM1')
    expect(serialField.querySelector('input')).toBeNull()
  })

  it('相对位置页签区块齐全，且原点固定为基座、界面上不再有原点选择项', async () => {
    await seedStopped()
    const { container } = render(<ConfigPanel />)

    fireEvent.click(screen.getByRole('button', { name: '相对位置' }))
    expandAll()

    // 相对位置页签现在有 5 个可折叠区块，其中「雷达探测精度校核」是本次新增的。
    for (const title of ['相对位置参数', '雷达安装', '目标过滤', '雷达探测精度校核', '重建节奏与轨迹']) {
      expect(screen.getByText(title)).toBeInTheDocument()
    }
    // m06173 第 1 点：原点恒为基座，连只读的那一行都不留（看顶栏的「原点：…」即可）。
    expect(screen.queryByText('显示坐标系原点')).toBeNull()
    expect(screen.queryByDisplayValue('基座（世界坐标系）')).toBeNull()
    expect(container.textContent).not.toContain('以雷达为原点')
    // 「为什么没有原点选项」的解释挂在问号提示里（悬停才出文字，静息不进 textContent），
    // 这里只确认提示点还在；提示正文由真浏览器冒烟脚本悬停后断言。
    expect(container.querySelector('.hintmark')).not.toBeNull()
  })

  it('雷达朝向来源可切，基线夹角与对比校核都写进草稿', async () => {
    await seedStopped()
    render(<ConfigPanel />)

    fireEvent.click(screen.getByRole('button', { name: '相对位置' }))
    expandAll()

    // 来源下拉的取值必须与后端枚举同名（后端 JSON 就是这两个字符串）
    const source = fieldControl('雷达朝向来源', 'select') as HTMLSelectElement
    expect(source.value).toBe('baseHeadingPlusOffset')
    expect(Array.from(source.options).map((o) => o.value)).toEqual([
      'manualAbsolute',
      'baseHeadingPlusOffset',
    ])

    // 切到手动绝对角：夹角框降级但仍带值；绝对角框可编辑
    fireEvent.change(source, { target: { value: 'manualAbsolute' } })
    expect(useStore.getState().configDraft?.relative.radar.yawSource).toBe('manualAbsolute')
    expect((fieldControl('相对基线夹角（度）', 'input') as HTMLInputElement).disabled).toBe(true)

    // 切回基线模式：夹角框解禁，允许负值（顺时针为正）
    fireEvent.change(source, { target: { value: 'baseHeadingPlusOffset' } })
    const offset = fieldControl('相对基线夹角（度）', 'input') as HTMLInputElement
    expect(offset.disabled).toBe(false)
    fireEvent.change(offset, { target: { value: '-12.5' } })
    expect(useStore.getState().configDraft?.relative.radar.yawOffsetFromBaselineDeg).toBe(-12.5)

    // 绝对角只是「双天线失效时的兜底」，双天线模式下也必须还能编辑
    const yaw = fieldControl('探测方向（相对正北，度）', 'input') as HTMLInputElement
    expect(yaw.disabled).toBe(false)
    fireEvent.change(yaw, { target: { value: '87.3' } })
    expect(useStore.getState().configDraft?.relative.radar.yawDeg).toBe(87.3)

    // 切换来源不会把两个角度里的任何一个丢掉
    expect(useStore.getState().configDraft?.relative.radar.yawOffsetFromBaselineDeg).toBe(-12.5)

    // 对比校核：开关（fixture 里后端是打开的）与匹配半径
    fireEvent.click(fieldControl('启用无人机 vs 雷达探测对比', 'input'))
    expect(useStore.getState().configDraft?.relative.comparison.enabled).toBe(false)
    fireEvent.change(fieldControl('匹配半径（米）', 'input'), { target: { value: '8' } })
    expect(useStore.getState().configDraft?.relative.comparison.matchRadiusM).toBe(8)
  })

  it('切到「存储」页签后能看到存储配置', async () => {
    await seedStopped()
    render(<ConfigPanel />)
    expandAll()

    fireEvent.click(screen.getByRole('button', { name: '存储' }))

    // 未选中的页签内容必须消失，否则说明条件渲染写错了
    expect(screen.queryByText('UM982（NMEA 0183）')).not.toBeInTheDocument()
    expect(screen.getByText('每次开始采集新建带时间戳目录')).toBeInTheDocument()
    expect(screen.getByText('十六进制文本')).toBeInTheDocument()
  })

  it('改草稿不会立刻改动已保存配置', async () => {
    await seedStopped()
    render(<ConfigPanel />)
    expandAll()

    // 串口是下拉：先切到「手动输入…」，再手输一个本机没枚举到的口
    const serial = fieldControl('串口名', 'select') as HTMLSelectElement
    fireEvent.change(serial, { target: { value: '__manual__' } })
    const input = fieldControl('串口名', 'input') as HTMLInputElement
    fireEvent.change(input, { target: { value: 'COM7' } })

    await waitFor(() =>
      expect(
        useStore.getState().configDraft?.devices.find((d) => d.kind === 'baseStation')
          ?.transportSettings.serialPort,
      ).toBe('COM7'),
    )
    expect(useStore.getState().config?.devices[0].transportSettings.serialPort).toBe('COM3')
  })

  // m04845：采集中锁定采集参数。
  // 现场问「开始采集后通讯/存储/相对位置还能不能改」——不能：这几段任一改动，后端热应用都会
  // 停掉管线重建（换会话目录 / 设备重连 / 参考点重置），本次采集当场被截成两段。
  // 这里锁的是「参数控件」本身（折叠体套一层 fieldset[disabled]），不是把面板藏起来：
  // 页签照样能翻、折叠照样能开、设备照样能重连，只是改不动、也存不下去。
  it('采集中锁死参数区与保存按钮，停止采集后自动解锁', async () => {
    await seedStore() // fixture 里 status.running = true，正是「采集中」
    const { container } = render(<ConfigPanel />)

    // 采集中同样能翻页签、能展开（锁的是参数，不是导航）
    expandAll()
    expect(screen.getByText('UM982（NMEA 0183）')).toBeInTheDocument()

    // 顶部有一条醒目的说明，而不是让人对着灰控件猜为什么改不动
    const banner = screen.getByRole('status')
    expect(banner.textContent).toContain('正在采集')
    expect(banner.textContent).toContain('已锁定')

    // 参数控件真的在 disabled 的 fieldset 里——浏览器据此拒绝编辑
    const lockedFields = container.querySelectorAll('fieldset.lockfield[disabled]')
    expect(lockedFields.length).toBeGreaterThan(0)
    // 注意：getByDisplayValue 对 <select> 比的是「选中项的文本」，而配置里存的 COM3 现在带
    // 「（配置里保存的，本机未检测到）」后缀（m06464 第 1 点），所以这里按前缀匹配。
    const com3 = screen.getByDisplayValue(/^COM3/)
    expect(Array.from(lockedFields).some((fs) => fs.contains(com3))).toBe(true)

    // 保存按钮在锁定区之外，必须单独禁用，否则点下去就是一次 409
    expect(screen.getByRole('button', { name: '保存并生效' })).toBeDisabled()
    expect(screen.getByText('采集中：参数已锁定，停止采集后才能保存。')).toBeInTheDocument()

    // 页签导航仍然可用：切到「存储」能看到内容（只是同样改不动）
    fireEvent.click(screen.getByRole('button', { name: '存储' }))
    expect(screen.getByText('每次开始采集新建带时间戳目录')).toBeInTheDocument()

    // 停止采集（后端推来的 running 变 false）→ 同一棵树当场解锁，不需要重新挂载
    act(() => {
      const current = useStore.getState().status
      if (current) useStore.setState({ status: { ...current, running: false } })
    })
    await waitFor(() => {
      expect(container.querySelectorAll('fieldset.lockfield[disabled]').length).toBe(0)
    })
    expect(screen.queryByRole('status')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: '保存并生效' })).not.toBeDisabled()
    expect(screen.queryByText('采集中：参数已锁定，停止采集后才能保存。')).not.toBeInTheDocument()
  })
})

describe('StatusPanel', () => {
  it('渲染链路、存储与基座坐标系几何', async () => {
    await seedStore()
    render(<StatusPanel />)

    expect(screen.getByText(`链路与存储状态 · ${status.name}`)).toBeInTheDocument()
    expect(screen.getByText('基座坐标系几何')).toBeInTheDocument()
    // 需求③：面板不再列历史会话，改为直接给出本次采集的完整绝对路径
    expect(screen.queryByText('历史会话')).not.toBeInTheDocument()
    expect(screen.getByText('存储位置')).toBeInTheDocument()
    // 会话目录来自 fixture，说明存储状态确实读到了
    expect(document.body.textContent).toContain(status.storage.sessionDirectory)
    // 无硬件时基座是 Disconnected 且带 lastError，面板必须能显示而不是崩
    expect(document.body.textContent).toContain('基座（UM982）')
  })
})

describe('App 外壳', () => {
  it('挂载后不崩，并同时容纳三维占位与两个面板', async () => {
    await seedStore()
    render(<App />)

    await waitFor(() => expect(screen.getByTestId('viewer3d')).toBeInTheDocument())
    expect(screen.getByText(`平台配置 · ${config.name}`)).toBeInTheDocument()
    expect(screen.getByText(/三维坐标：x = 东/)).toBeInTheDocument()
  })

  it('右栏四个解析页签齐全，运行日志已并入右栏第 4 个页签（m05133 第 4、7 点）', async () => {
    await seedStore()
    render(<App />)

    for (const label of ['雷达解析', '基座解析', '无人机解析', '运行日志']) {
      expect(screen.getByRole('button', { name: label })).toBeInTheDocument()
    }
    // 左栏只剩「参数配置 / 平台状态」两页：日志已经搬走
    expect(screen.getByRole('button', { name: '参数配置' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '平台状态' })).toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: '运行日志' }))
    expect(screen.getByText('链路、解析与存储事件（最多保留最近 600 行）')).toBeInTheDocument()
  })

  it('不再有地图与二维视图入口，也没有重复的底部「解析样本」面板（m05133 第 1、3 点 + m05836 第 2 点）', async () => {
    await seedStore()
    const { container } = render(<App />)

    expect(container.textContent).not.toContain('启用地图')
    expect(screen.queryByRole('button', { name: '地图' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: '二维视图' })).not.toBeInTheDocument()
    // 底部样本表与右栏的「解析原文」是同一份数据的两个出口，已删掉重复的那份
    expect(screen.queryByRole('button', { name: '解析样本' })).not.toBeInTheDocument()
    expect(container.querySelector('.app__bottom')).toBeNull()
    expect(screen.queryByText('最近解析样本')).not.toBeInTheDocument()
  })

  it('工具条能单独关掉无人机本身，尾迹是可填 0~600 的输入框（m06464 第 2/4 点）', async () => {
    await seedStore()
    const { container } = render(<App />)

    // 「无人机」开关排在「无人机轨迹」之前：它只管无人机本身（图标、光晕、RTK 标记、
    // 两个标签），轨迹与测距连线各有各的开关。
    const switches = Array.from(container.querySelectorAll('.app__toolbar .switch'))
    const drone = switches.find((el) => el.textContent?.trim() === '无人机') as HTMLElement | undefined
    const track = switches.find((el) => el.textContent?.trim() === '无人机轨迹')
    expect(drone).toBeDefined()
    expect(track).toBeDefined()
    expect(switches.indexOf(drone!)).toBeLessThan(switches.indexOf(track!))

    const box = drone!.querySelector('input[type="checkbox"]') as HTMLInputElement
    expect(box.checked).toBe(true)
    fireEvent.click(box)
    await waitFor(() => expect(useStore.getState().config?.ui.showDrone).toBe(false))

    // 尾迹：数字输入框（0~600 秒，0 = 不画尾迹），不再是 10/30/60/120/300/600 的下拉
    const trail = container.querySelector('.app__toolbar input[type="number"]') as HTMLInputElement
    expect(trail).not.toBeNull()
    expect(trail.value).toBe('60')
    expect(trail.min).toBe('0')
    expect(trail.max).toBe('600')
    // 打字阶段只改本地文本、不提交（每敲一位就 PUT 会连发请求）
    fireEvent.change(trail, { target: { value: '999' } })
    expect(useStore.getState().config?.ui.trailSeconds).toBe(60)
    // 失焦时夹到上限再提交
    fireEvent.blur(trail)
    await waitFor(() => expect(useStore.getState().config?.ui.trailSeconds).toBe(600))
    // 0 是合法值
    fireEvent.change(trail, { target: { value: '0' } })
    fireEvent.blur(trail)
    await waitFor(() => expect(useStore.getState().config?.ui.trailSeconds).toBe(0))
  })

  it('显示坐标系原点固定为基座，界面不再提供原点开关', async () => {
    await seedStore()
    const { container } = render(<App />)

    // m04238 第 5 点：全项目统一「以基座为原点的世界 ENU 系」，表头与底部提示都写死基座
    await waitFor(() => expect(container.textContent).toContain('原点：基座'))
    expect(container.textContent).toContain('原点 = 基座')
    // 不再存在任何带 baseStation / radar 选项的显示原点下拉
    const originSelect = Array.from(container.querySelectorAll('select')).find((el) =>
      Array.from(el.options).some((o) => o.value === 'baseStation' || o.value === 'radar'),
    )
    expect(originSelect).toBeUndefined()
  })
})

describe('提示条自动消失', () => {
  it('notify 出来的提示条 6 秒后自动消失，手点「×」可提前关（m04238 第 2 点）', async () => {
    await seedStore()
    vi.useFakeTimers()
    try {
      const notify = useStore.getState().notify
      const has = (text: string): boolean =>
        useStore.getState().notices.some((n) => n.text === text)

      notify('自动消失的提示')
      expect(has('自动消失的提示')).toBe(true)
      // 未到点不能提前消失（6 s = 6000 ms，边界在 5999/6000 之间）
      vi.advanceTimersByTime(5999)
      expect(has('自动消失的提示')).toBe(true)
      vi.advanceTimersByTime(1)
      expect(has('自动消失的提示')).toBe(false)

      // 手动关：不必等满 6 秒；到点的定时器随后空跑一次也不应抛错
      notify('手动关闭的提示')
      const id = useStore.getState().notices.find((n) => n.text === '手动关闭的提示')!.id
      useStore.getState().dismissNotice(id)
      expect(has('手动关闭的提示')).toBe(false)
      expect(() => vi.advanceTimersByTime(6000)).not.toThrow()
    } finally {
      vi.useRealTimers()
    }
  })
})
