/**
 * 目标类型 → 显示颜色 / 中文名的共享表。
 *
 * 类型码来源：`_ref\NSR_radar_V1.2.9.txt` 表 4-22（点云）与目标协议（4.14 节）：
 * 0 未识别 / 1 人 / 2 车 / 3 树 / 4 船 / 5 空 / 6 小船 / 7 中船 / 8 大船 / 0xFFFF 已删除。
 * 与后端 `RadarTargetTypes.Describe` 保持一致。
 */
export const TYPE_COLORS: Record<number, string> = {
  0: '#94a3b8',
  1: '#ef4444',
  2: '#f59e0b',
  3: '#22c55e',
  4: '#06b6d4',
  // 5 = 空中目标。默认场景里雷达只看得见无人机这一个空中目标，所以这条颜色要和
  // 无人机 GPS 轨迹（#38bdf8 天蓝）拉开距离，取品红。
  5: '#ec4899',
  6: '#14b8a6',
  7: '#0ea5e9',
  8: '#6366f1',
  0xffff: '#475569',
}

const FALLBACK_COLOR = '#94a3b8'

export function typeColor(type: number): string {
  return TYPE_COLORS[type] ?? FALLBACK_COLOR
}
