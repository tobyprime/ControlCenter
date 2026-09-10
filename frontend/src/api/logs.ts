// TOB-401：实现由 openapi-ts 生成客户端承载（契约见 src/api/gen/），本模块只保留视图层消费的类型与函数签名。
import { getApiCollectorsLogsServices, getApiCollectorsLogsTail } from './gen'

export type LogKind = 'systemd' | 'docker'

export type LogLevel = 'error' | 'warn' | 'info' | 'debug'

export interface LogServiceInfo {
  name: string
  kind: LogKind
  description: string
}

export interface LogLineInfo {
  ts: string
  level: LogLevel | string
  message: string
}

/** 日志归并为采集器数据类型（三期模块3）：查询按采集器定位，经 agent 只读拉取。 */
export async function listLogServices(collectorId: number): Promise<LogServiceInfo[]> {
  const { data } = await getApiCollectorsLogsServices<true>({ path: { collectorId } })
  return (data as { services: LogServiceInfo[] }).services
}

export async function fetchLogTail(
  collectorId: number,
  service: string,
  kind: LogKind,
  lines: number,
): Promise<LogLineInfo[]> {
  const { data } = await getApiCollectorsLogsTail<true>({
    path: { collectorId },
    query: { service, kind, lines },
  })
  return (data as { lines: LogLineInfo[] }).lines
}
