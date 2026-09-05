# 交互模式（InteractionMode）注册指南

本文是目标交互入口的抽象说明与扩展点约定（TOB-365 交付，约束 C）：核心按**目标声明的模式**渲染交互入口，不绑定「控制台」单一形态。现有 Web shell 终端（TOB-339）注册为首个模式，协议与功能保持不变。

## 总体结构

```
src/DevicePanel.Web/Interactions   交互模式抽象（本文范围）
  ├─ InteractionMode        IInteractionMode 接口：模式稳定标识 + 展示元数据（服务端扩展点）
  ├─ ShellInteractionMode   Web shell 终端（首个内置模式，仅登记，不改动终端实现）
  ├─ InteractionModeRegistry 注册表：收集 DI 中全部 IInteractionMode，按键查找
  ├─ InteractionModeCatalog 目标声明目录：目标 → 声明的模式 key 列表（约束 C 的「目标声明」侧）
  └─ InteractionEndpoints   查询 API：全量模式清单 + 目标声明入口
frontend/src/api/interactions.ts  前端交互模式 API 模块（入口渲染数据源）
frontend/src/views/TerminalView.vue  shell 模式的入口与交互界面（按声明渲染入口）
```

设计边界：终端中继（TerminalRelay）、留痕（TerminalStore）与 `term.*` 通道**均不感知本抽象**——交互模式是「入口层」的注册与渲染约束，不改变任何既有链路。

## 核心接口

### IInteractionMode（模式契约）

```csharp
public interface IInteractionMode
{
    /// <summary>模式稳定标识（如 shell），目标声明与前端入口渲染均以它为准。</summary>
    string Key { get; }

    /// <summary>展示名（如「Shell 终端」）。</summary>
    string DisplayName { get; }

    /// <summary>可选说明文案。</summary>
    string? Description { get; }
}
```

模式只承载**元数据**：交互的具体链路（协议、连接、界面）由模式自己的实现域负责（shell 模式即既有 `/api/devices/{id}/terminal` WS + TerminalView）。注册表不对交互链路做任何约束。

### InteractionModeRegistry（注册表）

构造时注入 `IEnumerable<IInteractionMode>`，由 DI 收集全部已注册实现；key 重复在启动时即抛 `ArgumentException`（fail-fast）。

```csharp
builder.Services.AddSingleton<IInteractionMode, ShellInteractionMode>();
builder.Services.AddSingleton<InteractionModeRegistry>();
```

### IInteractionModeCatalog（目标声明）

```csharp
public interface IInteractionModeCatalog
{
    /// <summary>目标声明可用的交互模式 key 列表；目标不存在或未声明时返回空列表。</summary>
    IReadOnlyList<string> GetDeclaredModeKeys(long targetId);
}
```

当前实现 `DeviceInteractionModeCatalog`：现有设备（agent 回连目标）均声明 `shell`。TOB-361 Target 统一合入后由目标类型驱动：device 目标声明 shell，service 目标未声明——核心即不渲染其终端入口（验收 3）。

声明允许引用尚未注册的 key：查询 API 会跳过未注册项（向前兼容，声明先行不报错）。

## 查询 API

| 路由 | 说明 |
|---|---|
| `GET /api/interactions/modes` | 全量已注册模式 `[{ key, displayName, description }]` |
| `GET /api/devices/{deviceId}/interaction-modes` | 目标声明的入口（经注册表解析后的模式列表）；设备不存在 404 |

均走面板登录会话认证（`/api` 前缀由登录拦截统一把关）。

## 如何注册新模式

以未来的 MC 控制台模式为例（三期真实接入，此处仅示骨架）：

1. **实现模式契约**（元数据 + 你自己的交互链路）：

   ```csharp
   namespace DevicePanel.Web.Interactions;

   /// <summary>MC 控制台交互模式（示例骨架，真实接入见三期）。</summary>
   public sealed class McConsoleInteractionMode : IInteractionMode
   {
       public string Key => "mc-console";
       public string DisplayName => "MC 控制台";
       public string? Description => "面向 Minecraft 服务器的控制台交互。";
   }
   ```

2. **注册 DI**（一行，注册表自动收集）：

   ```csharp
   builder.Services.AddSingleton<IInteractionMode, McConsoleInteractionMode>();
   ```

3. **让目标声明该模式**：在 `IInteractionModeCatalog` 实现中为对应目标返回 `"mc-console"`（TOB-361 后按目标类型/配置驱动），或替换目录实现接你自己的声明来源。

4. **前端入口渲染**：在目标详情页的交互区按 `/api/devices/{id}/interaction-modes` 返回的 `key` 渲染入口。`shell` 入口跳 `/terminal?device=<id>`（终端页已支持 `?device` 深链）；新模式的入口组件按 `key` 接入（如 `mc-console` → 对应视图），未知 `key` 不渲染。

5. **测试**：仿照 `tests/DevicePanel.Web.Tests/InteractionModeRegistryTests.cs`（注册收集/查找/重复 key）与 `InteractionApiTests.cs`（清单与目标声明 API）补测试。

## 与既有模块的关系

- Web 终端（TOB-339）：shell 模式仅做登记，终端协议与功能不变；`docs/agent-channel.md` 的 `term.*` 契约仍是终端链路的唯一事实源。
- Target 统一（TOB-361）：合入后 `DeviceInteractionModeCatalog` 调整为按目标类型声明，本抽象的接口与注册表不变。
