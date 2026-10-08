/**
 * 配置面板（需求①）：通讯参数 / 存储 / 相对位置设置。
 *
 * 设计要点：
 * - 所有编辑都落在 store 的 configDraft 上（updateDraft 是「克隆 → 变更 → 写回」），
 *   已生效的 config 只用于标题展示。这样界面上永远只有一份「待保存」真相，
 *   不会出现「改了但不知道改的是草稿还是已生效值」的歧义。
 * - 通讯方式切换后只渲染该方式真正会读取的字段：后端各 transport 分支读的字段并不重叠，
 *   把无关字段一起摆出来只会诱导用户去改一堆不生效的输入框。
 * - 数字输入一律经 numOr()/numOrNull() 归一化：前者把空串退化为 0（后端这些字段不可空），
 *   后者把空串退化为 null（经纬度 0 是合法值，绝不能用 0 冒充「未指定」）。
 */
import { createContext, useContext, useEffect, useState } from 'react'
import { api } from '../../services/api'
import { useHoverTip } from '../common/HoverTip'
import { useStore } from '../../store/useStore'
import { DEVICE_KIND_LABEL, DEVICE_KIND_ORDER, TRANSPORT_LABEL } from '../../types'
import type {
  DeviceConfig,
  DeviceKind,
  DroneComparisonSettings,
  RelativeSettings,
  ParsedFormat,
  PlatformConfig,
  RadarPlacementMode,
  RadarProtocolSettings,
  RadarYawSource,
  RawFormat,
  ReferencePointMode,
  SessionFolderMode,
  StorageConfig,
  TransportKind,
  TransportSettings,
  Ucm221ProtocolSettings,
  Um982ProtocolSettings,
} from '../../types'

/** store.updateDraft 的签名，往下传时用别名免得每处都重写。 */
type UpdateDraft = (mutate: (draft: PlatformConfig) => void) => void

/** 采集进行中的「参数锁定」标记：通讯 / 存储 / 相对位置这三段一律不可改。
 *
 * 为什么用 context 而不是逐层传 prop：这三个页签里几十个输入框都由 Field / Check / Collapse
 * 渲染，逐层加参数要改几十个调用点，而且以后新加一个字段忘了接上就悄悄破了锁。
 * 锁定范围就是三个页签的全部正文，标题行里的「重连」这类运行时操作不受影响。
 */
const LockedContext = createContext(false)

/** 串口波特率候选（含 500000 这类非标准值，NSR/UM982 现场都用得到）。 */
const BAUD_RATES = [9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600, 500000]
const DATA_BITS = [5, 6, 7, 8]
/** 这三个是 .NET SerialPort 的枚举名，后端按名字反序列化，不能翻译成中文再发回去。 */
const PARITIES = ['None', 'Odd', 'Even', 'Mark', 'Space']
const STOP_BITS = ['One', 'Two', 'OnePointFive']
const HANDSHAKES = ['None', 'XOnXOff', 'RequestToSend', 'RequestToSendXOnXOff']

/** 设备简称：DEVICE_KIND_LABEL 里带着型号括号（「基座（UM982）」），
 *  存储页签的开关标题只用括号前那截，免得「无人机 GPS（UCM221） 原始数据」长到换行。 */
const DEVICE_KIND_SHORT: Record<DeviceKind, string> = {
  baseStation: DEVICE_KIND_LABEL.baseStation.replace(/（.*$/, ''),
  radar: DEVICE_KIND_LABEL.radar.replace(/（.*$/, ''),
  droneGps: DEVICE_KIND_LABEL.droneGps.replace(/（.*$/, ''),
}

/** 数字输入 → 数字：空串与非法输入都退化为 0，避免把 NaN 写进草稿再发给后端。 */
function numOr(text: string, fallback: number): number {
  const value = Number(text)
  return Number.isFinite(value) ? value : fallback
}

/** 可空数字输入：空串表示「不指定」，必须写 null 而不是 0。 */
function numOrNull(text: string): number | null {
  if (text.trim() === '') return null
  const value = Number(text)
  return Number.isFinite(value) ? value : null
}

/** 雷达站地址在界面上按十六进制编辑（0x10 → "10"）。 */
function hexText(value: number): string {
  return value.toString(16).toUpperCase().padStart(2, '0')
}

/** 十六进制文本 → 0~255；非法输入退化 0、越界截断，避免把越界地址发给雷达。 */
function hexValue(text: string): number {
  const parsed = Number.parseInt(text.trim(), 16)
  if (!Number.isFinite(parsed)) return 0
  return Math.min(255, Math.max(0, parsed))
}

/** 表单一行。用 <label> 包住控件，点标签即聚焦控件；说明文字只在鼠标进来或聚焦时弹出。 */
function Field({
  label,
  hint,
  children,
}: {
  label: string
  hint?: string
  children: React.ReactNode
}): React.ReactElement {
  const tip = useHoverTip(hint)
  return (
    <label className="field" {...tip.handlers}>
      <span className="field__label">{label}</span>
      <span className="field__control">{children}</span>
      {tip.tip}
    </label>
  )
}

/** 段落级说明的「悬停才展开」入口。
 *
 * 一段整段的说明文字没有天然可悬停的锚点（把它藏起来就没人能悬停到它身上），
 * 所以留一个小的圆形标记当锚点：鼠标移上去或键盘 Tab 聚焦才弹出全文。
 */
function HintMark({ text }: { text: string }): React.ReactElement {
  const tip = useHoverTip(text)
  return (
    <span className="hintmark" tabIndex={0} {...tip.handlers}>
      说明
      {tip.tip}
    </span>
  )
}

/** 「手动输入…」这一项的取值：它不是一个真的串口名，只用来把手输入口切出来。 */
const MANUAL_PORT = '__manual__'

/** 串口名选择：原生 `<select>` 常驻，选中「手动输入…」时在它**下面**多出一个手输框。
 *
 * 为什么不用 `<input list>` + `<datalist>`（v1 的做法）：Chrome 里点输入框正文**不会**弹出候选，
 * 只有点最右边那个小箭头、或按方向键才弹，现场反馈就是「点下拉框没反应」；
 * 原生 `<select>` 点哪里都会展开，是唯一稳定的「可点开」控件。
 * 手输能力必须保留：USB 转串口还没插上时本机列表里根本没有目标口，得能直接打字。
 * 但手输不能把下拉框顶掉（v1 是「二选一」，切过去只能点旁边的小按钮回来，现场反馈
 * 「选了手动输入就回不到列表了」——m06173 第 4 点）：下拉框一直留着，想回列表直接点一项。
 * 串口列表来自后端 `GET /api/serial-ports`；取不到就只有手输，绝不阻塞配置。
 *
 * 候选只列**本机枚举到的**串口（m00790）：配置里保存的口若本机没有，下拉就显示「（未设置）」。
 * v1 是把这种口作为一项混在候选里、加个「配置里保存的，本机未检测到」的注解（当时是为了回答
 * 「本机没有 COM3，下拉里怎么有 COM3」），但留着它等于在下拉框里摆一个点不动的口，不如不显示。
 * **这条规则只作用于显示**（m00868 第 1 条）：草稿与配置文件里的原值一律不动 —— 用户不主动改口
 * 就什么都不会变，也不会因此显示「已修改」；鼠标移上去的提示里会说明本机没检测到这个口。
 * 手输态自然豁免：用户自己打进去的值本来就可能还没插上（USB 转串口没插），照样原样保留。
 */
function SerialPortField({
  value,
  onChange,
}: {
  value: string
  onChange: (next: string) => void
}): React.ReactElement {
  // null = 清单还没拿到（此时既不判定、也不改配置）
  const [ports, setPorts] = useState<string[] | null>(null)
  const [failed, setFailed] = useState(false)
  const [manual, setManual] = useState(false)

  useEffect(() => {
    let disposed = false
    void api
      .serialPorts()
      .then((result) => {
        if (!disposed) setPorts(result.ports ?? [])
      })
      .catch(() => {
        if (!disposed) setFailed(true)
      })
    return () => {
      disposed = true
    }
  }, [])

  // 配置里保存的口本机没有 ⇒ 下拉**只按「（未设置）」显示**，草稿与配置文件里的原值一律不动
  // （m00868 第 1 条：要的是「下拉里别摆一个点不动的口」，不是把配置改掉）。
  // 手输态豁免：用户自己打进去的口本来就可能还没插上（USB 转串口没插），不能被抹。
  const missing = !manual && !failed && ports !== null && value !== '' && !ports.includes(value)

  // 串口候选提示（本机串口清单 / 读取失败 / 保存的口本机没有）只在鼠标移到控件上时弹出。
  const portList = ports !== null && ports.length > 0 ? ports.join('、') : '无'
  const tip = useHoverTip(
    failed
      ? '读取本机串口列表失败，请手动输入'
      : missing
        ? `本机未检测到配置里保存的 ${value}，下拉按「未设置」显示（配置不改动）；本机串口：${portList}`
        : ports !== null && ports.length > 0
          ? `本机串口：${portList}`
          : '本机未检测到串口，可手动输入',
  )
  // 清单拿不到（接口失败/非 Windows）时不猜、也不动配置：把手输框放出来，当前值仍然可见可改。
  const showManual = manual || failed
  // 值不在候选里就没有对应 option，原生 select 会静默显示空白 —— 显式落到「（未设置）」。
  // 注意这只是**显示值**：`value`（草稿/配置）原样保留。
  const selectValue = showManual ? MANUAL_PORT : missing ? '' : value
  // 清单还没回来时把当前值先挂成一项，免得下拉在这几百毫秒里显示成空白的「（未设置）」。
  const pendingOption = ports === null && value !== '' ? value : null

  return (
    <>
      <div className="stack">
        {/* 下拉框常驻：手输的时候也不把它换掉，想回列表在下面那个框里点一项就行。
            手输态下下拉框显示「手动输入…」这一项，免得它显示成上一个选过的串口、和下面输入框打架。 */}
        <select
          value={selectValue}
          onChange={(event) => {
            const next = event.target.value
            if (next === MANUAL_PORT) {
              setManual(true)
              return
            }
            setManual(false)
            onChange(next)
          }}
          {...tip.handlers}
        >
          <option value="">（未设置）</option>
          {/* 清单未回来时的当前值：只为显示，不改任何状态 */}
          {pendingOption !== null && <option value={pendingOption}>{pendingOption}</option>}
          {(ports ?? []).map((port) => (
            <option key={port} value={port}>
              {port}
            </option>
          ))}
          <option value={MANUAL_PORT}>手动输入…</option>
        </select>
        {showManual ? (
          <input
            type="text"
            value={value}
            placeholder="直接输入串口名，例如 COM3"
            onChange={(event) => {
              setManual(true)
              onChange(event.target.value)
            }}
            {...tip.handlers}
          />
        ) : null}
      </div>
      {tip.tip}
    </>
  )
}

/** 开关行。刻意不包 Field：复选框要的是 switch 样式而不是「标签 + 控件」两栏。
 *
 * disabled 只在「锁定区之外」需要显式传（例如设备标题行里的「启用」）；
 * 锁定区里的开关由包裹正文的 fieldset[disabled] 连带禁用，不用逐个传。
 */
function Check({
  label,
  checked,
  onChange,
  disabled,
  hint,
}: {
  label: string
  checked: boolean
  onChange: (checked: boolean) => void
  disabled?: boolean
  /** 这个开关「关掉之后会怎样」的说明：只在鼠标移上来 / 键盘聚焦时弹出（见 useHoverTip）。 */
  hint?: string
}): React.ReactElement {
  const tip = useHoverTip(hint)
  return (
    <label className="switch" {...tip.handlers}>
      <input type="checkbox" checked={checked} disabled={disabled} onChange={(e) => onChange(e.target.checked)} />
      {label}
      {tip.tip}
    </label>
  )
}

/** 通讯参数：只渲染当前通讯方式用得到的字段。 */
function TransportFields({
  device,
  edit,
}: {
  device: DeviceConfig
  edit: (mutate: (settings: TransportSettings) => void) => void
}): React.ReactElement {
  const ts = device.transportSettings
  return (
    <>
      <div className="grid2">
        {device.transport === 'tcpClient' ? (
          <>
            <Field label="目标主机">
              <input
                type="text"
                value={ts.host}
                onChange={(e) => {
                  const value = e.target.value
                  edit((s) => {
                    s.host = value
                  })
                }}
              />
            </Field>
            <Field label="目标端口">
              <input
                type="number"
                value={String(ts.port)}
                onChange={(e) => {
                  const value = numOr(e.target.value, 0)
                  edit((s) => {
                    s.port = value
                  })
                }}
              />
            </Field>
          </>
        ) : null}

        {device.transport === 'tcpServer' ? (
          <>
            <Field label="监听地址" hint="0.0.0.0 表示监听所有网卡">
              <input
                type="text"
                value={ts.listenAddress}
                onChange={(e) => {
                  const value = e.target.value
                  edit((s) => {
                    s.listenAddress = value
                  })
                }}
              />
            </Field>
            <Field label="监听端口">
              <input
                type="number"
                value={String(ts.listenPort)}
                onChange={(e) => {
                  const value = numOr(e.target.value, 0)
                  edit((s) => {
                    s.listenPort = value
                  })
                }}
              />
            </Field>
          </>
        ) : null}

        {device.transport === 'udp' ? (
          <>
            {/* UDP 实际只绑定 LocalPort（后端 Transports.cs 的 UdpTransport 用
                IPAddress.Any + LocalPort 建 UdpClient），listenAddress / listenPort 在 UDP 下
                后端根本不读。所以这里不渲染「监听端口」，免得用户以为改了有用；
                真正要配的是本地监听端口、以及本机主动发包时的远端地址。 */}
            <Field label="本地端口" hint="本机绑定的 UDP 监听端口（所有网卡），无人机回传的报文发到这里">
              <input
                type="number"
                value={String(ts.localPort)}
                onChange={(e) => {
                  const value = numOr(e.target.value, 0)
                  edit((s) => {
                    s.localPort = value
                  })
                }}
              />
            </Field>
            <Field label="远端主机" hint="仅在还没收到过设备报文、需要本机主动发包时作为兜底目标">
              <input
                type="text"
                value={ts.host}
                onChange={(e) => {
                  const value = e.target.value
                  edit((s) => {
                    s.host = value
                  })
                }}
              />
            </Field>
            <Field label="远端端口">
              <input
                type="number"
                value={String(ts.port)}
                onChange={(e) => {
                  const value = numOr(e.target.value, 0)
                  edit((s) => {
                    s.port = value
                  })
                }}
              />
            </Field>
            <Check
              label="允许广播"
              checked={ts.udpBroadcast}
              onChange={(checked) =>
                edit((s) => {
                  s.udpBroadcast = checked
                })
              }
            />
          </>
        ) : null}
      </div>

      {device.transport === 'serial' ? (
        <div className="grid3">
          <Field label="串口名">
            <SerialPortField
              value={ts.serialPort}
              onChange={(value) => {
                edit((s) => {
                  s.serialPort = value
                })
              }}
            />
          </Field>
          <Field label="波特率">
            <select
              value={String(ts.baudRate)}
              onChange={(e) => {
                const value = numOr(e.target.value, 115200)
                edit((s) => {
                  s.baudRate = value
                })
              }}
            >
              {/* 草稿里的值不在候选表内时补一项，否则下拉会静默变成空白 */}
              {BAUD_RATES.includes(ts.baudRate) ? null : <option value={String(ts.baudRate)}>{ts.baudRate}</option>}
              {BAUD_RATES.map((rate) => (
                <option key={rate} value={String(rate)}>
                  {rate}
                </option>
              ))}
            </select>
          </Field>
          <Field label="数据位">
            <select
              value={String(ts.dataBits)}
              onChange={(e) => {
                const value = numOr(e.target.value, 8)
                edit((s) => {
                  s.dataBits = value
                })
              }}
            >
              {DATA_BITS.map((bits) => (
                <option key={bits} value={String(bits)}>
                  {bits}
                </option>
              ))}
            </select>
          </Field>
          <Field label="校验位">
            <select
              value={ts.parity}
              onChange={(e) => {
                const value = e.target.value
                edit((s) => {
                  s.parity = value
                })
              }}
            >
              {PARITIES.map((item) => (
                <option key={item} value={item}>
                  {item}
                </option>
              ))}
            </select>
          </Field>
          <Field label="停止位">
            <select
              value={ts.stopBits}
              onChange={(e) => {
                const value = e.target.value
                edit((s) => {
                  s.stopBits = value
                })
              }}
            >
              {STOP_BITS.map((item) => (
                <option key={item} value={item}>
                  {item}
                </option>
              ))}
            </select>
          </Field>
          <Field label="流控">
            <select
              value={ts.handshake}
              onChange={(e) => {
                const value = e.target.value
                edit((s) => {
                  s.handshake = value
                })
              }}
            >
              {HANDSHAKES.map((item) => (
                <option key={item} value={item}>
                  {item}
                </option>
              ))}
            </select>
          </Field>
        </div>
      ) : null}

      <div className="grid3">
        <Field label="读超时（毫秒）">
          <input
            type="number"
            value={String(ts.readTimeoutMs)}
            onChange={(e) => {
              const value = numOr(e.target.value, 0)
              edit((s) => {
                s.readTimeoutMs = value
              })
            }}
          />
        </Field>
      </div>
    </>
  )
}

/** UM982 基座协议参数。 */
function Um982Section({
  settings,
  edit,
}: {
  settings: Um982ProtocolSettings
  edit: (mutate: (settings: Um982ProtocolSettings) => void) => void
}): React.ReactElement {
  return (
    <div className="panel__section">
      <div className="panel__title">UM982（NMEA 0183）</div>
      <div className="grid3">
        <Check
          label="解析 GGA"
          checked={settings.parseGga}
          onChange={(v) =>
            edit((s) => {
              s.parseGga = v
            })
          }
        />
        <Check
          label="解析 RMC"
          checked={settings.parseRmc}
          onChange={(v) =>
            edit((s) => {
              s.parseRmc = v
            })
          }
        />
        <Check
          label="解析 VTG"
          checked={settings.parseVtg}
          onChange={(v) =>
            edit((s) => {
              s.parseVtg = v
            })
          }
        />
        <Check
          label="解析 THS"
          checked={settings.parseThs}
          onChange={(v) =>
            edit((s) => {
              s.parseThs = v
            })
          }
        />
        <Check
          label="RMC 航迹向当航向"
          checked={settings.rmcCourseAsHeading}
          onChange={(v) =>
            edit((s) => {
              s.rmcCourseAsHeading = v
            })
          }
        />
        <Check
          label="校验和必须正确"
          checked={settings.requireChecksum}
          onChange={(v) =>
            edit((s) => {
              s.requireChecksum = v
            })
          }
        />
        <Check
          label="启动时下发初始化命令"
          checked={settings.sendInitCommands}
          onChange={(v) =>
            edit((s) => {
              s.sendInitCommands = v
            })
          }
        />
        <Check
          label="使用真航向（THS）"
          checked={settings.useTrueHeading}
          onChange={(v) =>
            edit((s) => {
              s.useTrueHeading = v
            })
          }
        />
      </div>

      <div className="grid3">
        <Field label="输出间隔（秒）" hint="0.05 = 20 Hz">
          <input
            type="number"
            step="any"
            value={String(settings.outputIntervalSec)}
            onChange={(e) => {
              const value = numOr(e.target.value, 1)
              edit((s) => {
                s.outputIntervalSec = value
              })
            }}
          />
        </Field>
      </div>

      <div className="grid3">
        <Check
          label="启用看门狗"
          checked={settings.enableWatchdog}
          onChange={(v) =>
            edit((s) => {
              s.enableWatchdog = v
            })
          }
        />
        <Field label="看门狗超时（秒）">
          <input
            type="number"
            step="any"
            value={String(settings.watchdogTimeoutSec)}
            onChange={(e) => {
              const value = numOr(e.target.value, 5)
              edit((s) => {
                s.watchdogTimeoutSec = value
              })
            }}
          />
        </Field>
        <Field label="看门狗冷却（秒）">
          <input
            type="number"
            step="any"
            value={String(settings.watchdogCooldownSec)}
            onChange={(e) => {
              const value = numOr(e.target.value, 5)
              edit((s) => {
                s.watchdogCooldownSec = value
              })
            }}
          />
        </Field>
      </div>

      <HintMark text="启动后按 um982_driver 行为下发 gpgga/gprmc/gpvtg/gpths com1 <间隔> 与 saveconfig；GGA 或 RMC 超时 5 s 未更新则重发输出配置（不含 saveconfig）。" />
    </div>
  )
}

/** NSR 雷达协议参数。 */
function RadarProtocolSection({
  settings,
  edit,
}: {
  settings: RadarProtocolSettings
  edit: (mutate: (settings: RadarProtocolSettings) => void) => void
}): React.ReactElement {
  return (
    <div className="panel__section">
      <div className="panel__title">NSR 雷达（TCP）</div>
      <div className="grid2">
        <Field label="本机地址（十六进制）" hint="如 10 / FF">
          <input
            type="text"
            value={hexText(settings.localAddress)}
            onChange={(e) => {
              const value = hexValue(e.target.value)
              edit((s) => {
                s.localAddress = value
              })
            }}
          />
        </Field>
        <Field label="雷达地址（十六进制）" hint="如 10 / FF">
          <input
            type="text"
            value={hexText(settings.radarAddress)}
            onChange={(e) => {
              const value = hexValue(e.target.value)
              edit((s) => {
                s.radarAddress = value
              })
            }}
          />
        </Field>
      </div>
      <div className="grid3">
        <Check
          label="发送心跳"
          checked={settings.sendHeartbeat}
          onChange={(v) =>
            edit((s) => {
              s.sendHeartbeat = v
            })
          }
        />
        <Field label="心跳间隔（秒）">
          <input
            type="number"
            step="any"
            value={String(settings.heartbeatIntervalSec)}
            onChange={(e) => {
              const value = numOr(e.target.value, 1)
              edit((s) => {
                s.heartbeatIntervalSec = value
              })
            }}
          />
        </Field>
        <Check
          label="连接后查询状态"
          checked={settings.queryStatusOnConnect}
          onChange={(v) =>
            edit((s) => {
              s.queryStatusOnConnect = v
            })
          }
        />
        <Check
          label="解析 ACK"
          checked={settings.parseAcks}
          onChange={(v) =>
            edit((s) => {
              s.parseAcks = v
            })
          }
        />
      </div>
    </div>
  )
}

/** UCM221 无人机 GPS 协议参数。 */
function Ucm221Section({
  settings,
  edit,
}: {
  settings: Ucm221ProtocolSettings
  edit: (mutate: (settings: Ucm221ProtocolSettings) => void) => void
}): React.ReactElement {
  return (
    <div className="panel__section">
      <div className="panel__title">UCM221（上传流 0xA5 0x5A，小端）</div>
      <div className="grid2">
        <Check
          label="解析扩展信息"
          checked={settings.parseExtendedInfo}
          onChange={(v) =>
            edit((s) => {
              s.parseExtendedInfo = v
            })
          }
        />
        <Check
          label="解析 UAV 信息"
          checked={settings.parseUavInfo}
          onChange={(v) =>
            edit((s) => {
              s.parseUavInfo = v
            })
          }
        />
      </div>
    </div>
  )
}

/** 通讯参数页签：每台设备一张卡片，卡片内再按「链路 → 存储开关 → 专属协议」分层。 */
/** 可折叠区块。
 *
 * 为什么要有它：左侧参数面板只有 372px 宽，三台设备加相对位置参数竖排下来要滚很久才看得全。
 * 收成一个标题行之后，用户能一屏看到整份配置的目录，只展开当前要改的那块。
 *
 * 这是**受控**组件：展开状态由 ConfigPanel 统一持有，否则做不出「全部展开 / 全部折叠」。
 */
function Collapse({
  id,
  title,
  tail,
  collapsed,
  onToggle,
  children,
}: {
  id: string
  title: React.ReactNode
  /** 标题行右侧的附加内容（状态标签、按钮等）。它们要自己 stopPropagation，否则点一下就折叠了。 */
  tail?: React.ReactNode
  collapsed: boolean
  onToggle: (id: string) => void
  children: React.ReactNode
}): React.ReactElement {
  const locked = useContext(LockedContext)
  // 标题行会被 CSS 截断（左栏只有 372px，「无人机 GPS(UCM221)」这类名字显示不全）：
  // 悬停时把完整标题弹出来。标题是纯字符串时才挂——JSX 标题（带状态标签那种）没有「完整文本」可言。
  const titleTip = useHoverTip(typeof title === 'string' ? title : undefined, {
    onlyWhenTruncated: true,
  })
  return (
    <div className={collapsed ? 'collapse' : 'collapse collapse--open'}>
      <div
        className="collapse__head"
        role="button"
        tabIndex={0}
        aria-expanded={!collapsed}
        onClick={() => onToggle(id)}
        onKeyDown={(event) => {
          if (event.key === 'Enter' || event.key === ' ') {
            event.preventDefault()
            onToggle(id)
          }
        }}
      >
        <span className="collapse__caret" aria-hidden="true">
          ▶
        </span>
        <span className="collapse__name" {...titleTip.handlers}>
          {title}
          {titleTip.tip}
        </span>
        <span className="collapse__tail">{tail}</span>
      </div>
      {/* 采集进行中时用 fieldset[disabled] 连带禁用正文里的全部表单控件：
          标题行（折叠开关、设备「启用」、设备「重连」）在 fieldset 之外，照旧能用——
          采集中仍然要能翻看参数、折叠区块、给掉线的设备重连。 */}
      {collapsed ? null : (
        <div className="collapse__body">
          <fieldset className="lockfield" disabled={locked}>
            {children}
          </fieldset>
        </div>
      )}
    </div>
  )
}

function DevicesTab({
  devices,
  editDevice,
  reconnect,
  isCollapsed,
  onToggle,
}: {
  devices: DeviceConfig[]
  editDevice: (kind: DeviceKind, mutate: (device: DeviceConfig) => void) => void
  reconnect: (kind: string) => Promise<void>
  isCollapsed: (id: string) => boolean
  onToggle: (id: string) => void
}): React.ReactElement {
  const locked = useContext(LockedContext)

  if (devices.length === 0) return <p className="hint">后端没有返回任何设备配置。</p>

  return (
    <>
      {devices.map((device) => (
        <Collapse
          key={device.kind}
          id={`dev:${device.kind}`}
          title={device.name}
          collapsed={isCollapsed(`dev:${device.kind}`)}
          onToggle={onToggle}
          tail={
            <>
              <span className="hint">{DEVICE_KIND_LABEL[device.kind]}</span>
              {/* 勾选框与按钮在标题行里，必须拦住冒泡，否则点「启用」会顺带把卡片折叠掉。 */}
              <span onClick={(event) => event.stopPropagation()}>
                <Check
                  label="启用"
                  checked={device.enabled}
                  disabled={locked}
                  onChange={(checked) =>
                    editDevice(device.kind, (dev) => {
                      dev.enabled = checked
                    })
                  }
                />
              </span>
              {device.enabled ? (
                <button
                  type="button"
                  className="btn"
                  onClick={(event) => {
                    event.stopPropagation()
                    void reconnect(device.kind)
                  }}
                >
                  重连
                </button>
              ) : null}
            </>
          }
        >

          <div className="grid3">
            <Field label="通讯方式">
              <select
                value={device.transport}
                onChange={(e) => {
                  const value = e.target.value as TransportKind
                  editDevice(device.kind, (dev) => {
                    dev.transport = value
                  })
                }}
              >
                {(Object.keys(TRANSPORT_LABEL) as TransportKind[]).map((kind) => (
                  <option key={kind} value={kind}>
                    {TRANSPORT_LABEL[kind]}
                  </option>
                ))}
              </select>
            </Field>
          </div>

          <TransportFields
            device={device}
            edit={(mutate) =>
              editDevice(device.kind, (dev) => {
                mutate(dev.transportSettings)
              })
            }
          />

          <div className="grid2">
            <Check
              label="自动重连"
              checked={device.autoReconnect}
              onChange={(checked) =>
                editDevice(device.kind, (dev) => {
                  dev.autoReconnect = checked
                })
              }
            />
            <Field label="重连间隔（毫秒）">
              <input
                type="number"
                value={String(device.reconnectDelayMs)}
                onChange={(e) => {
                  const value = numOr(e.target.value, 0)
                  editDevice(device.kind, (dev) => {
                    dev.reconnectDelayMs = value
                  })
                }}
              />
            </Field>
          </div>

          {/* 协议参数按设备类型分开渲染：三张协议表在 DeviceConfig 上一直存在，
              但只有对应类型的那张会真正被后端读取。 */}
          {device.kind === 'baseStation' ? (
            <Um982Section
              settings={device.um982}
              edit={(mutate) =>
                editDevice(device.kind, (dev) => {
                  mutate(dev.um982)
                })
              }
            />
          ) : null}
          {device.kind === 'radar' ? (
            <RadarProtocolSection
              settings={device.radar}
              edit={(mutate) =>
                editDevice(device.kind, (dev) => {
                  mutate(dev.radar)
                })
              }
            />
          ) : null}
          {device.kind === 'droneGps' ? (
            <Ucm221Section
              settings={device.ucm221}
              edit={(mutate) =>
                editDevice(device.kind, (dev) => {
                  mutate(dev.ucm221)
                })
              }
            />
          ) : null}
        </Collapse>
      ))}
    </>
  )
}

/** 存储页签。 */
function StorageTab({
  storage,
  devices,
  editDevice,
  updateDraft,
  isCollapsed,
  onToggle,
}: {
  storage: StorageConfig
  /** 三台设备（顺序同通讯页签），用来渲染「按设备保存」里的落盘开关。 */
  devices: DeviceConfig[]
  editDevice: (kind: DeviceKind, mutate: (device: DeviceConfig) => void) => void
  updateDraft: UpdateDraft
  isCollapsed: (id: string) => boolean
  onToggle: (id: string) => void
}): React.ReactElement {
  return (
    <Collapse id="sto:main" title="存储" collapsed={isCollapsed('sto:main')} onToggle={onToggle}>
      <div className="grid2">
        <Check
          label="启用存储"
          checked={storage.enabled}
          onChange={(checked) =>
            updateDraft((d) => {
              d.storage.enabled = checked
            })
          }
        />
        <Field label="存储根目录" hint="留空则用后端程序目录下的 data">
          <input
            type="text"
            value={storage.rootPath}
            onChange={(e) => {
              const value = e.target.value
              updateDraft((d) => {
                d.storage.rootPath = value
              })
            }}
          />
        </Field>
        <Field
          label="目录模式"
          hint={
            storage.folderMode === 'fixed'
              ? '始终写同一个目录，文件按 _0002、_0003 续号，适合长时间连续值守'
              : '每次「开始采集」都新建一个带时间戳的目录，停止后再开始也不会写回上一轮的目录'
          }
        >
          <select
            value={storage.folderMode}
            onChange={(e) => {
              const value = e.target.value as SessionFolderMode
              updateDraft((d) => {
                d.storage.folderMode = value
              })
            }}
          >
            <option value="perRun">每次开始采集新建带时间戳目录</option>
            <option value="fixed">固定目录</option>
          </select>
        </Field>
        <Field
          label="固定目录名"
          hint={storage.folderMode === 'fixed' ? undefined : '仅在「固定目录」模式下生效'}
        >
          <input
            type="text"
            value={storage.fixedSessionName}
            disabled={storage.folderMode !== 'fixed'}
            onChange={(e) => {
              const value = e.target.value
              updateDraft((d) => {
                d.storage.fixedSessionName = value
              })
            }}
          />
        </Field>
        <Field label="单文件上限（MB）" hint="超出后自动新建 xxx_0002 续写">
          <input
            type="number"
            step="any"
            value={String(storage.maxFileSizeMb)}
            onChange={(e) => {
              const value = numOr(e.target.value, 0)
              updateDraft((d) => {
                d.storage.maxFileSizeMb = value
              })
            }}
          />
        </Field>
        <Field label="原始数据格式">
          <select
            value={storage.rawFormat}
            onChange={(e) => {
              const value = e.target.value as RawFormat
              updateDraft((d) => {
                d.storage.rawFormat = value
              })
            }}
          >
            <option value="binary">二进制</option>
            <option value="hexText">十六进制文本</option>
            <option value="text">文本</option>
          </select>
        </Field>
        <Field label="解析数据格式">
          <select
            value={storage.parsedFormat}
            onChange={(e) => {
              const value = e.target.value as ParsedFormat
              updateDraft((d) => {
                d.storage.parsedFormat = value
              })
            }}
          >
            <option value="jsonLines">JSON Lines</option>
            <option value="csv">CSV</option>
          </select>
        </Field>
        <Field label="写缓冲（KB）">
          <input
            type="number"
            value={String(storage.writeBufferKb)}
            onChange={(e) => {
              const value = numOr(e.target.value, 0)
              updateDraft((d) => {
                d.storage.writeBufferKb = value
              })
            }}
          />
        </Field>
        <Field label="刷盘间隔（毫秒）">
          <input
            type="number"
            value={String(storage.flushIntervalMs)}
            onChange={(e) => {
              const value = numOr(e.target.value, 0)
              updateDraft((d) => {
                d.storage.flushIntervalMs = value
              })
            }}
          />
        </Field>
      </div>

      <div className="grid3">
        <Check
          label="保存雷达转基座系"
          checked={storage.saveRadarBase}
          onChange={(checked) =>
            updateDraft((d) => {
              d.storage.saveRadarBase = checked
            })
          }
        />
        <Check
          label="保存三设备同帧"
          checked={storage.saveFrame}
          onChange={(checked) =>
            updateDraft((d) => {
              d.storage.saveFrame = checked
            })
          }
        />
        <Check
          label="解析文件内嵌原始数据"
          checked={storage.embedRawInParsed}
          onChange={(checked) =>
            updateDraft((d) => {
              d.storage.embedRawInParsed = checked
            })
          }
        />
        <Check
          label="写 session.json 清单"
          checked={storage.writeManifest}
          onChange={(checked) =>
            updateDraft((d) => {
              d.storage.writeManifest = checked
            })
          }
        />
      </div>

      {/* 每台设备的「这块数据要不要落盘」原先长在各自的通讯参数卡片里，要逐台展开才看得全，
          看着还像是通讯参数的兄弟项——其实它决定的是 01/02/03 三个目录写不写，属于存储策略，
          所以集中到存储页签，跟格式、滚动、目录设置放在一起。
          仍然保留逐设备粒度（基座/雷达/无人机可以各自只留一种），不开全局一刀切。 */}
      <Collapse
        id="sto:perDevice"
        title="按设备保存"
        collapsed={isCollapsed('sto:perDevice')}
        onToggle={onToggle}
      >
        {devices.map((device) => (
          <div key={device.kind} className="grid2">
            <Check
              label={`${DEVICE_KIND_SHORT[device.kind]} 原始数据`}
              checked={device.saveRaw}
              onChange={(checked) =>
                editDevice(device.kind, (dev) => {
                  dev.saveRaw = checked
                })
              }
            />
            <Check
              label={`${DEVICE_KIND_SHORT[device.kind]} 解析数据`}
              checked={device.saveParsed}
              onChange={(checked) =>
                editDevice(device.kind, (dev) => {
                  dev.saveParsed = checked
                })
              }
            />
          </div>
        ))}
        <HintMark text="关掉某台设备的「原始数据」就不再写它的 raw_*（原始字节），关掉「解析数据」就不再写它的 parsed_*（解析结果）。04_radar_base 与 05_frame 不受这里影响，由上面的「保存雷达转基座系 / 保存三设备同帧」单独控制。" />
      </Collapse>

      <HintMark text="目录结构：01_base_station / 02_radar / 03_drone_gps / 04_radar_base / 05_frame。前三个目录内是 raw_*.bin（原始字节）与 parsed_*.jsonl（解析结果）；04_radar_base 是每帧雷达数据换算到以基座为原点的东北天坐标；05_frame 是同一帧下三个设备一起的信息。每条记录都带设备时间与本机时间。" />
    </Collapse>
  )
}

/** 相对位置页签。 */
function RelativeTab({
  relative,
  updateDraft,
  isCollapsed,
  onToggle,
}: {
  relative: RelativeSettings
  updateDraft: UpdateDraft
  isCollapsed: (id: string) => boolean
  onToggle: (id: string) => void
}): React.ReactElement {
  const placement = relative.radar
  /** 对比设置是后加进契约的，旧配置 / 旧抓包夹具里没有这个键（取出来是 undefined 而不是 null）。
   *  直接读 `relative.comparison.enabled` 会抛 TypeError 把整个参数面板打白，所以这里先兜一个默认值；
   *  写回时用整对象替换，顺带把缺失的键补上，避免留下半截对象。 */
  const comparison: DroneComparisonSettings = relative.comparison ?? { enabled: true, matchRadiusM: 20 }
  return (
    <Collapse id="fus:main" title="相对位置参数" collapsed={isCollapsed('fus:main')} onToggle={onToggle}>
      <div className="grid3">
        {/* RelativeSettings.enabled 在契约里存在，需求清单里没点名，但少了它界面上就没法开关相对位置，
            故一并放出；若不需要直接删掉这一行即可。 */}
        <Check
          label="启用相对位置"
          checked={relative.enabled}
          hint="相对位置总开关。关掉后不再产出相对位置帧：雷达报的本体极坐标不再换算进世界 ENU 系，三维视图里没有雷达目标与「离基座/离雷达」读数，落盘的 04_radar_base 与 05_frame 两层一起停。原始字节与解析结果（01~03 目录）由「存储」页的「启用存储」单独控制，不受本开关影响。"
          onChange={(checked) =>
            updateDraft((d) => {
              d.relative.enabled = checked
            })
          }
        />
      </div>

      <div className="grid3">
        <Field label="参考点来源">
          <select
            value={relative.referenceMode}
            onChange={(e) => {
              const value = e.target.value as ReferencePointMode
              updateDraft((d) => {
                d.relative.referenceMode = value
              })
            }}
          >
            <option value="auto">自动（取基准设备定位）</option>
            <option value="manual">手动指定</option>
          </select>
        </Field>
      </div>
      <HintMark text="显示坐标系恒以基座为原点（东 X+、北 Y+、天 Z+），界面上不再放原点选择项：基座与无人机的定位本身就是世界坐标，雷达只报本体极坐标，由安装姿态换算进同一坐标系后才能直接比对。当前实际原点看顶栏的「原点：…」——基座没定位时它会退化成雷达位置（帧里的 reference.source 会写明）。" />

      {relative.referenceMode === 'manual' ? (
        <div className="grid3">
          <Field label="手动经度">
            <input
              type="number"
              step="any"
              value={relative.manualLongitude === null ? '' : String(relative.manualLongitude)}
              onChange={(e) => {
                const value = numOrNull(e.target.value)
                updateDraft((d) => {
                  d.relative.manualLongitude = value
                })
              }}
            />
          </Field>
          <Field label="手动纬度">
            <input
              type="number"
              step="any"
              value={relative.manualLatitude === null ? '' : String(relative.manualLatitude)}
              onChange={(e) => {
                const value = numOrNull(e.target.value)
                updateDraft((d) => {
                  d.relative.manualLatitude = value
                })
              }}
            />
          </Field>
          <Field label="手动海拔（米）">
            <input
              type="number"
              step="any"
              value={relative.manualAltitudeM === null ? '' : String(relative.manualAltitudeM)}
              onChange={(e) => {
                const value = numOrNull(e.target.value)
                updateDraft((d) => {
                  d.relative.manualAltitudeM = value
                })
              }}
            />
          </Field>
          <HintMark text="留空表示不指定（写 null），不会被当成 0。" />
        </div>
      ) : null}

      <Collapse id="fus:radar" title="雷达安装" collapsed={isCollapsed('fus:radar')} onToggle={onToggle}>
      <div className="grid3">
        <Field label="安装方式">
          <select
            value={placement.mode}
            onChange={(e) => {
              const value = e.target.value as RadarPlacementMode
              updateDraft((d) => {
                d.relative.radar.mode = value
              })
            }}
          >
            <option value="coLocatedWithBase">与基座共址</option>
            <option value="manualWgs84">手动经纬高</option>
            <option value="offsetFromBase">相对基座偏移</option>
          </select>
        </Field>
      </div>

      {placement.mode === 'manualWgs84' ? (
        <div className="grid3">
          <Field label="经度">
            <input
              type="number"
              step="any"
              value={String(placement.longitude)}
              onChange={(e) => {
                const value = numOr(e.target.value, 0)
                updateDraft((d) => {
                  d.relative.radar.longitude = value
                })
              }}
            />
          </Field>
          <Field label="纬度">
            <input
              type="number"
              step="any"
              value={String(placement.latitude)}
              onChange={(e) => {
                const value = numOr(e.target.value, 0)
                updateDraft((d) => {
                  d.relative.radar.latitude = value
                })
              }}
            />
          </Field>
          <Field label="海拔（米）">
            <input
              type="number"
              step="any"
              value={String(placement.altitudeM)}
              onChange={(e) => {
                const value = numOr(e.target.value, 0)
                updateDraft((d) => {
                  d.relative.radar.altitudeM = value
                })
              }}
            />
          </Field>
        </div>
      ) : null}

      {placement.mode === 'offsetFromBase' ? (
        <div className="grid3">
          <Field label="东向偏移（米）">
            <input
              type="number"
              step="any"
              value={String(placement.offsetEastM)}
              onChange={(e) => {
                const value = numOr(e.target.value, 0)
                updateDraft((d) => {
                  d.relative.radar.offsetEastM = value
                })
              }}
            />
          </Field>
          <Field label="北向偏移（米）">
            <input
              type="number"
              step="any"
              value={String(placement.offsetNorthM)}
              onChange={(e) => {
                const value = numOr(e.target.value, 0)
                updateDraft((d) => {
                  d.relative.radar.offsetNorthM = value
                })
              }}
            />
          </Field>
          <Field label="天向偏移（米）">
            <input
              type="number"
              step="any"
              value={String(placement.offsetUpM)}
              onChange={(e) => {
                const value = numOr(e.target.value, 0)
                updateDraft((d) => {
                  d.relative.radar.offsetUpM = value
                })
              }}
            />
          </Field>
        </div>
      ) : null}

      {/* 「雷达正前方」有两种给法：现场直接量一个相对正北的绝对角，或者只量一个相对**基座双天线基线**
          的安装夹角，由基座实时报回的 THS 基线航向推算。基座与雷达同址安装，动一下就得重新量绝对角；
          用基线夹角则不用，所以后者是推荐值。两个角度字段都始终留在配置里，切换来源不会丢值。 */}
      {/* 这一行刻意不套 grid2/grid3：选项文案较长，分栏会把选中项截断。 */}
      <Field
        label="雷达朝向来源"
        hint={
          placement.yawSource === 'baseHeadingPlusOffset'
            ? '基线航向来自 UM982 的 THS 双天线定向，随基座实时更新，现场只需量一个安装夹角'
            : '现场用罗盘或地图量取绝对方位角，不依赖双天线定向'
        }
      >
        <select
          value={placement.yawSource}
          onChange={(e) => {
            const value = e.target.value as RadarYawSource
            updateDraft((d) => {
              d.relative.radar.yawSource = value
            })
          }}
        >
          <option value="manualAbsolute">手动绝对角（相对正北）</option>
          <option value="baseHeadingPlusOffset">双天线基线 + 安装夹角（推荐）</option>
        </select>
      </Field>

      <div className="grid3">
        <Field
          label="相对基线夹角（度）"
          hint={
            placement.yawSource === 'baseHeadingPlusOffset'
              ? '雷达正前方 = 基座双天线基线航向 + 该夹角（顺时针为正，0 = 与基线同向，180 = 反向）；基线航向来自 UM982 的 THS 双天线定向，取不到定向时自动回落到下面的手动绝对角。'
              : '当前用手动绝对角，此项不生效；切到「双天线基线 + 安装夹角」后生效。'
          }
        >
          <input
            type="number"
            step="any"
            disabled={placement.yawSource !== 'baseHeadingPlusOffset'}
            value={String(placement.yawOffsetFromBaselineDeg)}
            onChange={(e) => {
              const value = numOr(e.target.value, 0)
              updateDraft((d) => {
                d.relative.radar.yawOffsetFromBaselineDeg = value
              })
            }}
          />
        </Field>
      </div>

      <div className="grid3">
        <Field
          label="探测方向（相对正北，度）"
          hint={
            placement.yawSource === 'baseHeadingPlusOffset'
              ? '仅作双天线失效时的兜底：THS 定向取不到时回落到这个绝对角（仍可编辑，建议现场先填好）。'
              : '探测方向相对正北的顺时针夹角'
          }
        >
          <input
            type="number"
            step="any"
            /* 双天线模式下这一项只是兜底，灰掉**但不能 disabled**：现场要能先把它填上，
               THS 一断就立刻有值可用。禁用会同时挡住编辑，所以用内联 opacity 表达「非当前生效项」。 */
            style={placement.yawSource === 'baseHeadingPlusOffset' ? { opacity: 0.55 } : undefined}
            value={String(placement.yawDeg)}
            onChange={(e) => {
              const value = numOr(e.target.value, 0)
              updateDraft((d) => {
                d.relative.radar.yawDeg = value
              })
            }}
          />
        </Field>
        <Field label="俯仰角（度）">
          <input
            type="number"
            step="any"
            value={String(placement.pitchDeg)}
            onChange={(e) => {
              const value = numOr(e.target.value, 0)
              updateDraft((d) => {
                d.relative.radar.pitchDeg = value
              })
            }}
          />
        </Field>
        <Field label="横滚角（度）">
          <input
            type="number"
            step="any"
            value={String(placement.rollDeg)}
            onChange={(e) => {
              const value = numOr(e.target.value, 0)
              updateDraft((d) => {
                d.relative.radar.rollDeg = value
              })
            }}
          />
        </Field>
      </div>
      </Collapse>

      <Collapse id="fus:filter" title="目标过滤" collapsed={isCollapsed('fus:filter')} onToggle={onToggle}>
      <div className="grid3">
        <Field label="最大距离（米）">
          <input
            type="number"
            step="any"
            value={String(relative.filter.maxRangeM)}
            onChange={(e) => {
              const value = numOr(e.target.value, 0)
              updateDraft((d) => {
                d.relative.filter.maxRangeM = value
              })
            }}
          />
        </Field>
        <Field label="最低 SNR">
          <input
            type="number"
            step="any"
            value={String(relative.filter.minSnr)}
            onChange={(e) => {
              const value = numOr(e.target.value, 0)
              updateDraft((d) => {
                d.relative.filter.minSnr = value
              })
            }}
          />
        </Field>
        <Field label="最多目标数">
          <input
            type="number"
            value={String(relative.filter.maxTargets)}
            onChange={(e) => {
              const value = numOr(e.target.value, 0)
              updateDraft((d) => {
                d.relative.filter.maxTargets = value
              })
            }}
          />
        </Field>
        <Check
          label="丢弃已删除目标"
          checked={relative.filter.dropDeleted}
          onChange={(checked) =>
            updateDraft((d) => {
              d.relative.filter.dropDeleted = checked
            })
          }
        />
      </div>
      </Collapse>

      {/* 无人机本身就是天上飞的一个运动目标：雷达按自己的坐标系探到它，RTK 又给出它相对基座的真实位置。
          两者换算到同一坐标系里同框比对，就能逐帧算出「雷达探测 vs 真值」的偏差，
          用来自查（以及据此标定）雷达的测角、测距精度。 */}
      <Collapse id="fus:compare" title="雷达探测精度校核" collapsed={isCollapsed('fus:compare')} onToggle={onToggle}>
      <div className="row">
        {/* 标题较长，占满一行而不是塞进 grid3：.switch 是 nowrap，窄列会把文字截断。 */}
        <Check
          label="启用无人机 vs 雷达探测对比"
          checked={comparison.enabled}
          onChange={(checked) =>
            updateDraft((d) => {
              d.relative.comparison = { ...comparison, enabled: checked }
            })
          }
        />
      </div>

      <div className="grid3">
        <Field
          label="匹配半径（米）"
          hint="比对时在雷达目标里取离无人机 RTK 位置最近的一个当作「无人机回波」；两者三维距离超过该半径，即认为本帧雷达没探到无人机。"
        >
          <input
            type="number"
            step="any"
            value={String(comparison.matchRadiusM)}
            onChange={(e) => {
              const value = numOr(e.target.value, 0)
              updateDraft((d) => {
                d.relative.comparison = { ...comparison, matchRadiusM: value }
              })
            }}
          />
        </Field>
      </div>

      <HintMark text="无人机本身就是天上飞的一个运动目标：雷达按自己的坐标系探到它，RTK 给出它相对基座的真实位置，两者换算到同一坐标系里同框比对，就能校核雷达探得准不准——逐帧的偏差反映测角/测距误差，匹配半径则是判定「这一帧雷达到底有没有探到无人机」的容差。" />
      </Collapse>

      <Collapse id="fus:rhythm" title="重建节奏与轨迹" collapsed={isCollapsed('fus:rhythm')} onToggle={onToggle}>
      <div className="grid3">
        <Field label="相对位置间隔（毫秒）">
          <input
            type="number"
            value={String(relative.relativeIntervalMs)}
            onChange={(e) => {
              const value = numOr(e.target.value, 0)
              updateDraft((d) => {
                d.relative.relativeIntervalMs = value
              })
            }}
          />
        </Field>
        <Field label="数据陈旧阈值（毫秒）">
          <input
            type="number"
            value={String(relative.staleTimeoutMs)}
            onChange={(e) => {
              const value = numOr(e.target.value, 0)
              updateDraft((d) => {
                d.relative.staleTimeoutMs = value
              })
            }}
          />
        </Field>
        <Check
          label="跟随参考点漂移"
          checked={relative.followReferenceDrift}
          hint="默认不勾：原点在第一次解算成功后就锁死，基座后来移动（或定位漂移）只表现为「基座节点偏离原点」，轨迹参考系稳定、跨帧可比。勾上后每一帧都把原点重新贴在基座当前位置上——基座恒在 (0,0,0)，漂移与移动被吸收进雷达和无人机的坐标里，画面看起来是整个世界在动；此前记录的轨迹点用的是旧参考系，前端不会重算，新旧点会接不上。只有「基座本身装在车上/船上、希望基座永远画在原点」时才勾。"
          onChange={(checked) =>
            updateDraft((d) => {
              d.relative.followReferenceDrift = checked
            })
          }
        />
      </div>

      <HintMark text="尾迹时长（轨迹在屏幕上画多久）只在工具条上那个输入框里设置：它同时决定后端保留多少轨迹数据——刷新页面后能回看的长度与它一致（下限 30 秒，尾迹填 0 也能回看半分钟）。这里不再单独填「轨迹保留」，两个旋钮必然对不上。" />
      </Collapse>
    </Collapse>
  )
}

export function ConfigPanel(): React.ReactElement {
  const config = useStore((s) => s.config)
  const draft = useStore((s) => s.configDraft)
  const updateDraft = useStore((s) => s.updateDraft)
  const saveConfig = useStore((s) => s.saveConfig)
  const resetDraft = useStore((s) => s.resetDraft)
  const loadConfig = useStore((s) => s.loadConfig)
  const reconnect = useStore((s) => s.reconnect)
  const busy = useStore((s) => s.busy)
  // 采集进行中：通讯 / 存储 / 相对位置三页全部锁定。这三段任一改动，后端热应用都要把采集
  // 管线停掉重建（换会话目录 / 设备重连 / 参考点重置），本次会话的落盘数据当场分成两段。
  const running = useStore((s) => s.status?.running ?? false)
  const [tab, setTab] = useState<'devices' | 'storage' | 'relative'>('devices')

  /** 折叠状态：先查逐块的显式开关，没被单独点过的跟随「全部折叠」。
   *  这样「全部折叠」只改一个标志位，不用枚举所有分区 id（分区 id 散在各个页签里）。
   *
   *  默认**全部折叠**：用户反馈「参数面板要滚动才看得全」。收起后一屏就能看完整份配置的目录
   *  （每台设备的名称、启用状态、重连按钮仍在标题行上），要改哪块再展开哪块。 */
  const [collapsedMap, setCollapsedMap] = useState<Record<string, boolean>>({})
  const [allCollapsed, setAllCollapsed] = useState(true)

  const isCollapsed = (id: string): boolean => collapsedMap[id] ?? allCollapsed

  const toggleCollapse = (id: string): void => {
    setCollapsedMap((prev) => ({ ...prev, [id]: !(prev[id] ?? allCollapsed) }))
  }

  const collapseAll = (value: boolean): void => {
    setAllCollapsed(value)
    setCollapsedMap({})
  }

  /** 改某台设备的某个字段。找不到就什么都不做，避免脏草稿把设备数组撑坏。 */
  const editDevice = (kind: DeviceKind, mutate: (device: DeviceConfig) => void): void => {
    updateDraft((d) => {
      const device = d.devices.find((item) => item.kind === kind)
      if (device) mutate(device)
    })
  }

  /** 「重新载入已保存配置」：从后端重新拉一份并重建草稿。
   *  store.loadConfig 内部没有 try/catch，这里兜住失败，免得抛未处理的 Promise 拒绝。 */
  const reloadSaved = (): void => {
    void loadConfig().catch(() => undefined)
  }

  // 按 DEVICE_KIND_ORDER 排序，保证卡片顺序稳定（后端数组顺序不作保证）
  const devices = DEVICE_KIND_ORDER.map((kind) => draft?.devices.find((device) => device.kind === kind)).filter(
    (device): device is DeviceConfig => device !== undefined,
  )

  return (
    <div className="panel">
      <div className="panel__title">平台配置{config ? ` · ${config.name}` : ''}</div>

      <div className="panel__actions">
        <button
          type="button"
          className={tab === 'devices' ? 'btn btn--primary' : 'btn btn--ghost'}
          onClick={() => setTab('devices')}
        >
          通讯参数
        </button>
        <button
          type="button"
          className={tab === 'storage' ? 'btn btn--primary' : 'btn btn--ghost'}
          onClick={() => setTab('storage')}
        >
          存储
        </button>
        <button
          type="button"
          className={tab === 'relative' ? 'btn btn--primary' : 'btn btn--ghost'}
          onClick={() => setTab('relative')}
        >
          相对位置
        </button>
        <span className="app__spacer" />
        <button type="button" className="btn btn--ghost" onClick={() => collapseAll(false)}>
          全部展开
        </button>
        <button type="button" className="btn btn--ghost" onClick={() => collapseAll(true)}>
          全部折叠
        </button>
      </div>

      {running ? (
        <p className="locknote" role="status">
          正在采集：通讯参数、存储与相对位置已锁定，先「停止采集」才能修改——采集中改这些会把本次会话拦腰截断（换会话目录、
          设备重连、参考点重置），落盘数据就分成两段了。显示开关不受影响，随时可调。
        </p>
      ) : null}

      <div className="panel__body">
        <LockedContext.Provider value={running}>
          {draft === null ? (
            <p className="hint">正在加载配置…（若长时间无响应，请检查后端 /api/snapshot 是否可达）</p>
          ) : null}
          {draft !== null && tab === 'devices' ? (
            <DevicesTab
              devices={devices}
              editDevice={editDevice}
              reconnect={reconnect}
              isCollapsed={isCollapsed}
              onToggle={toggleCollapse}
            />
          ) : null}
          {draft !== null && tab === 'storage' ? (
            <StorageTab
              storage={draft.storage}
              devices={devices}
              editDevice={editDevice}
              updateDraft={updateDraft}
              isCollapsed={isCollapsed}
              onToggle={toggleCollapse}
            />
          ) : null}
          {draft !== null && tab === 'relative' ? (
            <RelativeTab
              relative={draft.relative}
              updateDraft={updateDraft}
              isCollapsed={isCollapsed}
              onToggle={toggleCollapse}
            />
          ) : null}
        </LockedContext.Provider>
      </div>

      <div className="panel__actions">
        <button
          type="button"
          className="btn btn--primary"
          disabled={busy !== null || running}
          onClick={() => void saveConfig()}
        >
          保存并生效
        </button>
        <button type="button" className="btn btn--ghost" onClick={resetDraft}>
          放弃修改
        </button>
        <button type="button" className="btn btn--ghost" onClick={reloadSaved}>
          重新载入已保存配置
        </button>
        {running ? (
          <span className="hint">采集中：参数已锁定，停止采集后才能保存。</span>
        ) : busy ? (
          <span className="hint">{busy}</span>
        ) : null}
      </div>
    </div>
  )
}
