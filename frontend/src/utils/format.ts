// 字节类数值展示的单一事实源：主页指标卡（dashboard/cardConfig）与指标曲线页（views/MetricsView）共用

/** 字节数值人性化：1024 进位，保留一位小数（原始字节取整）。 */
export function humanizeBytes(value: number): string {
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let current = value
  let index = 0
  while (Math.abs(current) >= 1024 && index < units.length - 1) {
    current /= 1024
    index += 1
  }
  return `${index === 0 ? Math.round(current) : current.toFixed(1)} ${units[index]}`
}

/** 字节/速率单位格式化器：B、B/s 返回人性化换算，其余单位返回 undefined（调用方走各自默认展示）。 */
export function byteUnitFormatter(unit: string): ((value: number) => string) | undefined {
  if (unit === 'B/s') {
    return (value) => `${humanizeBytes(value)}/s`
  }
  if (unit === 'B') {
    return (value) => humanizeBytes(value)
  }
  return undefined
}
