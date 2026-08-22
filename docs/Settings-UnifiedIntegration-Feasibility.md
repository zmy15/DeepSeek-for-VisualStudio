# 设置体系接入方式调查 与 Unified Settings（新版设置 UI）接入可行性报告

> 日期：2026-08-23　对应提交：`353ca3a`
> 调研对象：当前扩展的设置接入链路 + VS2026「工具→设置」(Unified Settings) 的官方扩展点

---

## 一、当前接入方式（现状链路）

```
[ProvideOptionPage(typeof(DeepSeekOptionsPage), "DeepSeek Chat", "General", true)]
        │  （传统 DialogPage；无 ProvideProfile → 不参与漫游/导入导出）
        ▼
Tools→选项 页面 UI ──OK──► SaveSettingsToStorage()
        │                     ├─ ApiKey/BaiduKey/BingKey → DPAPI 加密
        ▼                     └─ base 写入实例私有存储（Dev17=bin / Dev18=file+bin 混合，已实测）
DeepSeekOptionsPage.Instance（单例 = 唯一事实源）
        ├─ LoadPersistedOptionsAsync 启动装载（含跨实例迁移，一次性标记防回写）
        ├─ SettingsChanged 静态事件 → 热更新
        │     ├─ OnOcrSettingsChanged（OCR/Web搜索/模型下拉刷新）
        │     └─ OnCoreSettingsChanged（ApiKey/Model/思考模式 → _apiService 即时生效）★本轮新增
        └─ 各消费点直读 Instance 属性（TokenBudget/压缩阈值等在构造期捕获的除外）
```

**已确认的现状缺口**：
1. 无 `ProvideProfile` —— 设置不随 VS 账户漫游、不参与导入导出；
2. 构造期捕获型消费点（如 ContextManager 的 TokenBudget）改动后需重启才生效；
3. 在 Dev18 新版设置界面中不可见/不可搜（见下）。

## 二、VS2026 新版设置体系（Unified Settings）

- 官方命名空间：`Microsoft.VisualStudio.Utilities.UnifiedSettings`，
  **包 Microsoft.VisualStudio.Utilities v17.14.40264 —— 已被本工程 SDK 元包引用**，17.14+/18 双端可用。
- 入口：`SVsUnifiedSettingsManager` → `ISettingsManager.GetReader()/GetWriter(callerId)`。
  Writer 采用 Enqueue→RequestCommit 两段式，支持作用域与用户审批。
- **面向扩展的展示/读写接入点：`IExternalSettingsProvider`**
  「Controls a single region of external settings. Unified Settings will query for this object when the external settings region is shown in the UI.」
  - 需实现：`GetValueAsync<T>(id)` / `SetValueAsync<T>(id, value)` / 可选 `GetEnumChoicesAsync`（动态枚举）、
    `OpenBackingStoreAsync`（点击“托管于 xxx”链接打开真实存储）。
  - 可选增强：`ICachingExternalSettingsProvider`（realtimeNotifications=false 时内存缓存+Commit）、
    `ISuspendableExternalSettingsProvider`。
  - 反向通知：`SettingValuesChanged` / `ErrorConditionResolved` / 动态消息文本变更事件。
  - 注册形态：文档明确"registration 包含区域 id、backing store 字符串资源 id、realtimeNotifications 标志"，
    但注册属性/接口未出现在 Utilities.xml 中 —— 需对 Dev18 实测确定（MEF Export+元数据 或 Shell 侧 attribute）。
- 另有新一代 `VisualStudio.Extensibility` 的 `SettingCategory` 声明式模型（类型化/校验/生成观察者），
  但面向新 Extensibility 包模型，经典 AsyncPackage 接入属重写级。

## 三、可行性矩阵

| 方案 | 做法 | 工作量 | 风险 | 收益 |
|------|------|:----:|------|------|
| C 维持现状 | 依赖 Dev18 兼容模型：VS2022 扩展免改直接运行于 VS2026，旧 Options 页继续可用 | 0 | 低（官方兼容承诺） | 不进新设置 UI，不可搜索 |
| **A 外部区域桥接 ⭐** | 实现 `DeepSeekExternalSettingsProvider : IExternalSettingsProvider`。GetValue/SetValue 直接桥接 `DeepSeekOptionsPage.Instance`（单一事实源），SetValue 后触发既有 SettingsChanged 热更新链路；枚举项映射审批模式三态；`SettingsValuesChanged` 由现有 SettingsChanged 事件转发。MEF 导出 + 注册元数据原型迭代 | 1–2 天原型 | 注册元数据需 Dev18 实测一次；敏感字段排除策略（见 §四） | 设置进入新 UI：可搜索、即时生效、主题一致 |
| B 全面迁移 SettingCategory | 迁到 VisualStudio.Extensibility 声明式模型 | 大（包模型重构/双栈并存） | 高 | 类型安全、校验、观察者、workspace scope |

## 四、关键约束：敏感字段必须排除

Unified Settings 具备云同步与 JSON 导出能力。ApiKey/BaiduApiKey/BingKey 属 DPAPI 本机加密凭据，
**不应进入 Unified 存储**（会随同步/导出泄漏）。方案 A 的区域定义仅收录非敏感子集：

```
SelectedModel / IsThinkingEnabled / ReasoningEffort / EnableWebSearch /
SearchProvider / ApprovalMode / TokenBudget / CompressionThreshold /
PreserveRecentTurns / ShowContextStats / EnableTelemetryExport / EnableIdeContextInjection
```

ApiKey 保持现状：旧页编辑 + DPAPI 私有存储；新 UI 对应条目可展示为只读状态 + “在旧页修改”跳转链接
（利用 OpenBackingStoreAsync 打开旧 Options 页命令）。

## 五、建议路线

1. **Step 1（原型验证，约 1–2 天）**：方案 A 最小实现 —— 仅挂 SelectedModel/Thinking/Effort/审批模式 四项，
   在 Dev18 实测注册元数据形态并跑通 GetValue/SetValue/枚举/事件闭环。
2. **Step 2（补全）**：按 §四 子集全量接入；旧 Options 页保留为高级入口，页头加“在新设置中打开”跳转。
3. **Step 3（远期评估）**：VisualStudio.Extensibility SettingCategory 待其 API 稳定且支持场景完备后再评估迁移；
   同时关注 IArraySettingMigrator 是否可用于把历史 DialogPage 存储自动搬入 Unified 作用域。

## 六、附：本次核实的关键证据

| 断言 | 证据 |
|------|------|
| API 已在本机 SDK 内 | `~/.nuget/packages/microsoft.visualstudio.utilities/17.14.40264/` 存在（与工程元包同版本线）|
| 官方定位 | IExternalSettingsProvider doc：「Unified Settings will query for this object when the external settings region is shown in the UI」|
| 入口服务 | ISettingsManager doc：「available as a VS service (via SVsUnifiedSettingsManager)」，Guid 2f26e586-… |
| 兼容承诺 | DevBlog《Modernizing Visual Studio Extension Compatibility》：VS2022 扩展免改运行于 VS2026 |

*后续若立项 Step1，建议同时把 §一 缺口 2 的构造期捕获点改为订阅式刷新，一并收口。*
