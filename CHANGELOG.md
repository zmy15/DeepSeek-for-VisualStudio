# 更新日志

## [1.2.0] — 2026-08-23（Phase 1.5：Context & Evaluation Driven Refinement）

> 基线 `644068a` (v1.1.14) → 本版本共 **34 个提交**；含上游 `feature/v1.1.15` 全量合入。
> 详细报告：`docs/Phase1.5-Delivery-Report.md`、`docs/Phase1.5-BuildTest-Report.md`

### 新增
- **会话遥测**：每次 Agent 会话自动导出 JSON 指标
  （TTFT / 轮次 / 工具调用明细与耗时 / Token / Cache 命中率 / 失败四分类 /
  终止原因 / context_debug 上下文构成快照）至 `%LocalAppData%\DeepSeekVS\telemetry\`
- **IDE 实时上下文注入**：每条消息自动携带活动文件、光标、选区、光标符号、
  当前文件错误/警告摘要（volatile 注入，不影响 Prefix Cache；可关闭）
- **编辑器 Inline Edit**：选区 → Ctrl+I / 右键菜单 → 指令条 → 单次 LLM 直改 →
  复用 InlineDiffSession 预览管线（Accept/Reject/Esc 取消/原地重试）
- **Context Debugger 抽屉**：聊天窗右上角折叠面板，展示 Token 预算占用、
  注入块标志（IDE/Search/RAG）、Working Set Top-N、活动文件/选区/符号/诊断计数
- **审批模式条目去 emoji**，底部审批行纳入主题适配
- **工具超时分档**：memory 10s / 诊断类 20s / 抓取 45s / 默认 60s；
  新增 LLM 请求超时设置项（默认 300s）
- **跨实例设置迁移**：新 hive 无 ApiKey 时自动从同机其他 VS 实例导入
  （RegLoadAppKey 只读挂载 + 类型前缀解码；一次性执行防覆盖）
- **设置热更新**：选项页保存后 ApiKey/模型/思考模式即时生效，无需重启
- **Benchmark 脚手架**：报告生成器（C#）、标注/报告脚本（PS）、24 任务规程与 schema

### 变更
- 主题不再提供独立切换开关，始终跟随 IDE（VSColorTheme 实时订阅）
- 工具行图标全部替换为 Segoe Fluent Icons 字形；聊天内容渲染层将
  Emoji 映射为 Fluent 图标或移除（发给模型的数据字节不变）
- 会话历史交互对齐 Copilot：时钟按钮打开历史浮层（当前会话钉顶 +
  相对时间 + 逐条删除），替代常驻下拉框
- 输入区结构修正：上下文按钮移出文本框悬浮位，内边距规范化

### 合入上游 (feature/v1.1.15)
- `capture_window` 窗口截图工具（GraphicsCaptureService）
- PDF 直传视觉模型（PdfRenderService）
- `fetch_webpage` 图片直传视觉 + 递归抓取/长度修复
- FIM 补全在视觉模型下回退 deepseek-v4-flash

### 质量
- 单元测试 942 个全绿（真实 VS2022 vstest 运行）
- VSIX 整包构建通过（VS2026 Roslyn + 合并式 Sdks 工具链，
  见 `tools/build-vs26.ps1`）

### 已知事项
- Unified Settings（新设置 UI）接入完成可行性调研与 Provider 原型，
  正式注册推荐采用 VisualStudio.Extensibility SettingCategory 声明式模型
  （详见 `docs/Settings-UnifiedIntegration-Feasibility.md` §七）
- RAG 冻结中：待 Benchmark cross_file 类 Context 失败数据触发 BM25 立项
