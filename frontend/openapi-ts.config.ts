// TOB-401：前端 API 客户端自动生成配置。
// 输入是后端构建期产物 artifacts/openapi/DevicePanel.Web_openapi.json（dotnet build 产出，不入库），
// 产物 frontend/src/api/gen/ 全量入库（drift check 靠 git diff 比对入库产物）。
import { defineConfig } from '@hey-api/openapi-ts'

export default defineConfig({
  input: '../artifacts/openapi/DevicePanel.Web_openapi.json',
  output: 'src/api/gen',
  plugins: [
    // 运行时客户端：自定义 fetch 适配层（credentials/{error} 解析在 src/api/base.ts 统一接管）
    '@hey-api/client-fetch',
    '@hey-api/typescript',
    {
      name: '@hey-api/sdk',
      // 生成后调用即抛错（错误对象由适配层构造，保留 {error} 文案与 HTTP status）
      throwOnError: true,
    },
  ],
})
