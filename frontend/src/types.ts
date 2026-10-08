/**
 * 与后端契约一一对应的类型定义。
 * 事实来源：实测 `GET /api/config`、`GET /api/status`、`GET /api/relative`、`GET /api/snapshot`、
 * `GET /api/sessions` 的真实响应（camelCase；配置里的枚举是 camelCase 字符串）。
 */

// ── 枚举（配置对象里是 camelCase 字符串）──────────────────────────────────────
export type DeviceKind = 'baseStation' | 'radar' | 'droneGps'
export type TransportKind = 'tcpClient' | 'tcpServer' | 'udp' | 'serial'
export type LinkState = 'disabled' | 'disconnected' | 'connecting' | 'connected' | 'faulted'
export type RawFormat = 'binary' | 'hexText' | 'text'
export type SessionFolderMode = 'perRun' | 'fixed'
export type ParsedFormat = 'jsonLines' | 'csv'
export type ReferencePointMode = 'auto' | 'manual'
export type RadarPlacementMode = 'coLocatedWithBase' | 'manualWgs84' | 'offsetFromBase'

// ── 配置 ────────────────────────────────────────────────────────────────────
export interface TransportSettings {
  host: string
  port: number
  listenAddress: string
  listenPort: number
  localPort: number
  udpBroadcast: boolean
  serialPort: string
  baudRate: number
  dataBits: number
  /** BCL 枚举名，如 'None' / 'Odd' / 'Even' / 'Mark' / 'Space' */
  parity: string
  /** 'One' / 'Two' / 'OnePointFive' */
  stopBits: string
  /** 'None' / 'XOnXOff' / 'RequestToSend' / 'RequestToSendXOnXOff' */
  handshake: string
  readTimeoutMs: number
}

export interface RadarProtocolSettings {
  localAddress: number
  radarAddress: number
  outputMode: string
  sendHeartbeat: boolean
  heartbeatIntervalSec: number
  queryStatusOnConnect: boolean
  parseAcks: boolean
}

export interface Ucm221ProtocolSettings {
  bigEndian: boolean
  parseExtendedInfo: boolean
  parseUavInfo: boolean
}

export interface Um982ProtocolSettings {
  parseGga: boolean
  parseRmc: boolean
  parseVtg: boolean
  parseThs: boolean
  rmcCourseAsHeading: boolean
  requireChecksum: boolean
  sendInitCommands: boolean
  outputIntervalSec: number
  enableWatchdog: boolean
  watchdogTimeoutSec: number
  watchdogCooldownSec: number
  useTrueHeading: boolean
}

export interface DeviceConfig {
  kind: DeviceKind
  name: string
  enabled: boolean
  transport: TransportKind
  transportSettings: TransportSettings
  radar: RadarProtocolSettings
  ucm221: Ucm221ProtocolSettings
  um982: Um982ProtocolSettings
  autoReconnect: boolean
  reconnectDelayMs: number
  saveRaw: boolean
  saveParsed: boolean
  /** 只读：后端生成的链路描述 */
  linkDescription?: string
}

export interface StorageConfig {
  enabled: boolean
  /** 空字符串表示用后端程序目录下的 data */
  rootPath: string
  folderMode: SessionFolderMode
  fixedSessionName: string
  maxFileSizeMb: number
  rawFormat: RawFormat
  parsedFormat: ParsedFormat
  /** 是否落盘「雷达转换到基座系」（04_radar_base）。 */
  saveRadarBase: boolean
  /** 是否落盘「同一帧下三个设备一起的信息」（05_frame）。 */
  saveFrame: boolean
  embedRawInParsed: boolean
  writeBufferKb: number
  flushIntervalMs: number
  writeManifest: boolean
}

/** 雷达正前方（本体 +Y 轴）指向的解算方式。 */
export type RadarYawSource =
  /** 直接给相对正北的绝对方位角（现场用罗盘或地图量取）。 */
  | 'manualAbsolute'
  /** 基座双天线基线航向（THS 真航向）+ 安装夹角实时推算；双天线无定向时回落到手动绝对角。 */
  | 'baseHeadingPlusOffset'

export interface RadarPlacementSettings {
  mode: RadarPlacementMode
  longitude: number
  latitude: number
  altitudeM: number
  offsetEastM: number
  offsetNorthM: number
  offsetUpM: number
  /** 探测方向相对正北的顺时针夹角（度）；yawSource 为 manualAbsolute 时使用 */
  yawDeg: number
  /** 雷达正前方相对基座双天线基线的安装夹角（度，顺时针为正）；0 = 与基线同向，180 = 反向 */
  yawOffsetFromBaselineDeg: number
  /** 雷达正前方指向的来源 */
  yawSource: RadarYawSource
  pitchDeg: number
  rollDeg: number
}

export interface TargetFilterSettings {
  maxRangeM: number
  minSnr: number
  dropDeleted: boolean
  maxTargets: number
}

/** 无人机「雷达探测 vs RTK 定位」对比设置：把无人机当运动靶标校核雷达探测精度。 */
export interface DroneComparisonSettings {
  enabled: boolean
  /** 匹配半径（米）：在雷达目标里取离无人机 RTK 位置最近的一个作为「无人机回波」 */
  matchRadiusM: number
}

export interface RelativeSettings {
  enabled: boolean
  referenceMode: ReferencePointMode
  manualLongitude: number | null
  manualLatitude: number | null
  manualAltitudeM: number | null
  radar: RadarPlacementSettings
  filter: TargetFilterSettings
  comparison: DroneComparisonSettings
  relativeIntervalMs: number
  staleTimeoutMs: number
  followReferenceDrift: boolean
}

export interface UiSettings {
  showTargets: boolean
  showPointCloud: boolean
  /** 显示无人机本身（图标 + 航向箭头 + 光晕 + RTK 位置标记）；轨迹与测距连线另有开关。 */
  showDrone: boolean
  showDroneTrack: boolean
  showTargetTrails: boolean
  /** 显示无人机到基座、雷达的测距辅助线（不是物理连线，只是把斜距画出来）。 */
  showLinks: boolean
  /** 尾迹时长（秒，0~600）：超过这个时长的轨迹点逐点消失，数据断流整条清空；0 表示不画尾迹。
   *  后端的轨迹保留窗口也由它推出（不再有单独的「轨迹保留」参数）。 */
  trailSeconds: number
  pointSize: number
  gridSizeM: number
}

export interface PlatformConfig {
  version: number
  name: string
  devices: DeviceConfig[]
  storage: StorageConfig
  relative: RelativeSettings
  ui: UiSettings
}

// ── 实时数据 ────────────────────────────────────────────────────────────────
export interface EnuPoint {
  east: number
  north: number
  up: number
  horizontalDistance: number
  distance: number
}

export interface GeoReference {
  latitude: number
  longitude: number
  altitudeM: number
  /** 中文来源，如「基座」/「雷达」/「手动」 */
  source: string
  resolved: boolean
  isZero: boolean
}

export interface RelativeNode {
  name: string
  online: boolean
  fresh: boolean
  position: EnuPoint
  latitude: number | null
  longitude: number | null
  altitudeM: number | null
  /** 0~360 罗盘方位角 */
  headingDeg: number | null
  /** 0 无效 / 1 单点 / 2 差分 / 4 RTK 固定 / 5 RTK 浮动 */
  fixQuality: number | null
  satellites: number | null
  pitchDeg: number | null
  rollDeg: number | null
  ageMs: number
  /** 本机收到该设备这一帧数据的时刻（ISO 8601）。 */
  pcTime: string | null
  /** 设备自带的时间字符串（若协议携带）。 */
  deviceTime: string | null
  /** 该设备自本次连接以来的帧序号。 */
  sequence: number
  note: string | null
}

export interface RelativeTarget {
  id: number
  type: number
  typeName: string
  east: number
  north: number
  up: number
  rangeM: number
  azimuthDeg: number
  elevationDeg: number
  snr: number
  peakEnergyDb: number
  areaMask: number
  velocityEast: number
  velocityNorth: number
  velocityUp: number
  speedMps: number
  distanceToDroneM: number | null
  heightAboveDroneM: number | null
}

/**
 * 无人机「雷达探测 vs RTK 定位」的对比结果：用无人机当运动靶标校核雷达探测精度。
 * 位置一律是显示坐标系（米，东/北/天），偏差定义为 雷达 − RTK。
 */
export interface DroneRadarComparison {
  /** 是否在匹配半径内找到了对应的雷达回波 */
  matched: boolean
  matchRadiusM: number
  /** 最近的雷达目标 id（未在匹配半径内时 matched = false） */
  targetId: number
  targetType: number
  targetTypeName: string
  /** 雷达探测位置（显示坐标系，米） */
  radarEast: number
  radarNorth: number
  radarUp: number
  /** 无人机 RTK 上报位置（显示坐标系，米） */
  rtkEast: number
  rtkNorth: number
  rtkUp: number
  /** 位置偏差（米，雷达 − RTK） */
  deltaEastM: number
  deltaNorthM: number
  deltaUpM: number
  /** 三维偏差（米），即本帧的探测误差 */
  deltaDistanceM: number
  /** 水平偏差（米） */
  deltaHorizontalM: number
  /** RTK 位置换算到雷达本体系后的极坐标（真值） */
  rtkRangeM: number
  rtkAzimuthDeg: number
  rtkElevationDeg: number
  /** 雷达实测极坐标 */
  radarRangeM: number
  radarAzimuthDeg: number
  radarElevationDeg: number
  /** 极坐标偏差（雷达 − RTK） */
  deltaRangeM: number
  deltaAzimuthDeg: number
  deltaElevationDeg: number
}

export interface RelativeFrame {
  timestamp: string
  sequence: number
  originName: string
  referenceResolved: boolean
  reference: GeoReference
  baseStation: RelativeNode
  radar: RelativeNode
  drone: RelativeNode
  radarBaseLineM: number
  /** 本帧实际用于目标换算的雷达正前方方位角（度，相对正北） */
  radarBoresightDeg: number
  /** 该朝向是否由基座双天线基线航向推算得到（false = 用了手动绝对角） */
  radarBoresightFromBaseline: boolean
  /** 基座双天线基线航向（度），未收到 THS 定向时为 null */
  radarBaselineHeadingDeg: number | null
  targets: RelativeTarget[]
  isPointCloud: boolean
  droneDistanceToBaseM: number | null
  droneHeightAboveBaseM: number | null
  droneDistanceToRadarM: number | null
  trigger: string
  baseToDroneSkewMs: number | null
  /** 无人机与最近雷达目标的对比结果；未开启对比或缺数据时为 null */
  droneComparison: DroneRadarComparison | null
}

export interface TrackPoint {
  timestamp: string
  east: number
  north: number
  up: number
}

export interface TrackSnapshot {
  drone: TrackPoint[]
  targets: Record<string, TrackPoint[]>
}

export interface SampleSummary {
  device: string
  deviceLabel: string
  /** 后端为字符串（PascalCase，如 'BaseStation'） */
  deviceKind: string
  timestamp: string
  sequence: number
  summary: string
  latitude: number | null
  longitude: number | null
  altitudeM: number | null
  speedMps: number | null
  headingDeg: number | null
  satellites: number | null
  fixQuality: number | null
  targetCount: number
  isPointCloud: boolean
  rangeM: number | null
  azimuthDeg: number | null
  elevationDeg: number | null
  snr: number | null
  targetId: number | null
  targetType: number | null
}

// ── 状态 ────────────────────────────────────────────────────────────────────
export interface DeviceStatus {
  /** 后端为字符串（PascalCase，如 'BaseStation'） */
  kind: string
  kindLabel: string
  name: string
  enabled: boolean
  /** PascalCase，如 'Connected' */
  state: string
  /** PascalCase，如 'Serial' */
  transport: string
  transportDescription: string
  protocol: string
  remoteEndPoint: string | null
  lastError: string | null
  lastDataAt: string | null
  idleMs: number | null
  reconnects: number
  bytesReceived: number
  framesReceived: number
  parseErrors: number
  skippedBytes: number
  samples: number
  targetCount: number
  saveRaw: boolean
  saveParsed: boolean
}

export interface StorageFileStatus {
  device: string
  rawPath: string
  parsedPath: string
}

export interface StorageStatus {
  enabled: boolean
  sessionDirectory: string
  rawRecords: number
  parsedRecords: number
  /** 已写出的「雷达转换到基座系」记录条数（04_radar_base）。 */
  radarBaseRecords: number
  /** 已写出的「同一帧下三个设备一起的信息」记录条数（05_frame）。 */
  frameRecords: number
  totalBytes: number
  files: StorageFileStatus[]
}

export interface PlatformStatus {
  name: string
  timestamp: string
  running: boolean
  originName: string
  referenceResolved: boolean
  reference: GeoReference
  devices: DeviceStatus[]
  storage: StorageStatus
  relativeFrames: number
  droppedRelativeFrames: number
  droneTrackPoints: number
  trackedTargets: number
  drone: RelativeNode | null
  baseStation: RelativeNode | null
  radar: RelativeNode | null
  droneDistanceToBaseM: number | null
  droneHeightAboveBaseM: number | null
  droneDistanceToRadarM: number | null
}

export interface PlatformSnapshot {
  status: PlatformStatus
  config: PlatformConfig
  relative: RelativeFrame | null
  tracks: TrackSnapshot
  samples: SampleSummary[]
  logs: string[]
}

export interface SessionInfo {
  name: string
  path: string
  startedAt: string | null
  totalBytes: number
  fileCount: number
  active: boolean
  subFolders: string[]
}

export interface TestResult {
  ok: boolean
  message: string
  elapsedMs: number
}

export interface HealthInfo {
  ok: boolean
  running: boolean
  configPath: string
  sessionDirectory: string
  storageRoot: string
  time: string
}

// ── 工具 ────────────────────────────────────────────────────────────────────
/** 把 PascalCase / camelCase 统一成小写首字母的 camelCase，用于容忍后端字符串枚举两种写法。 */
export function toCamel(value: string | null | undefined): string {
  if (!value) return ''
  return value.charAt(0).toLowerCase() + value.slice(1)
}

/** 定位质量码 → 中文。0 无效 / 1 单点 / 2 差分 / 4 RTK 固定 / 5 RTK 浮动。 */
export function fixQualityLabel(q: number | null | undefined): string {
  switch (q) {
    case 0:
      return '无效'
    case 1:
      return '单点'
    case 2:
      return '差分'
    case 3:
      return 'PPS'
    case 4:
      return 'RTK 固定'
    case 5:
      return 'RTK 浮动'
    case 6:
      return '估算'
    default:
      return q == null ? '—' : `未知(${q})`
  }
}

export const DEVICE_KIND_LABEL: Record<DeviceKind, string> = {
  baseStation: '基座（UM982）',
  radar: '雷达（NSR）',
  droneGps: '无人机 GPS（UCM221）',
}

export const DEVICE_KIND_ORDER: DeviceKind[] = ['baseStation', 'radar', 'droneGps']

export const TRANSPORT_LABEL: Record<TransportKind, string> = {
  tcpClient: 'TCP 客户端（本机主动连接设备）',
  tcpServer: 'TCP 服务端（设备连入本机）',
  udp: 'UDP',
  serial: '串口',
}
