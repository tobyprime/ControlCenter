// TOB-401：实现由 openapi-ts 生成客户端承载（契约见 src/api/gen/），本模块只保留视图层消费的类型与函数签名。
import { getApiAuthMe, postApiAuthLogin, postApiAuthLogout } from './gen'

export interface SessionInfo {
  username: string
  expiresAtUtc?: string
}

export async function login(username: string, password: string): Promise<SessionInfo> {
  const { data } = await postApiAuthLogin<true>({ body: { username, password } })
  return data as SessionInfo
}

export async function logout(): Promise<void> {
  await postApiAuthLogout<true>({})
}

export async function me(): Promise<SessionInfo> {
  const { data } = await getApiAuthMe<true>({})
  return data as SessionInfo
}
