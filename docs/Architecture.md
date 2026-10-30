# 架构设计 / Architecture

> 本文档承载 DeepSeek Chat for Visual Studio 的架构与内部设计说明。
> README 只保留概览与使用指引，深入的实现细节统一收敛到这里。
>
> This document holds the architecture and internal design notes. The README keeps only the overview and
> usage guidance; implementation depth lives here.

[English](#english) · [简体中文](#简体中文)

---

# English

## Table of Contents

- [Project layout](#project-layout)
- [Initialization sequence](#initialization-sequence)
- [Assembly resolution](#assembly-resolution)
- [Chat rendering pipeline](#chat-rendering-pipeline)
- [Dependency injection](#dependency-injection)
- [Provider layer](#provider-layer)
- [Prefix cache design](#prefix-cache-design)
- [Editing pipeline](#editing-pipeline)
- [Agent execution loop](#agent-execution-loop)
- [Code completion pipeline](#code-completion-pipeline)
- [Related documents](#related-documents)

## Project layout

```
DeepSeek-v4-for-VisualStudio/
├── DeepSeek_v4_for_VisualStudioPackage.cs   # AsyncPackage entry point (9-step init sequence)
├── DeepSeekExtension.cs                     # VisualStudio.Extensibility in-proc host
├── VSCommandTable.vsct                      # Menus, toolbar, key bindings
├── source.extension.vsixmanifest            # VSIX identity & assets
├── Commands/                                # ShowChatWindowCommand, InlineAiEditCommand
├── View/                                    # WPF UI
│   ├── DeepSeekChatControl.*.cs             # Partial classes: Agent, Sessions, Rendering, Theme, ...
│   ├── DeepSeekChatWindowPane.cs            # Tool window host
│   ├── InlineDiffHostControl.xaml(.cs)      # Inline diff overlay + toolbar
│   ├── Hosts/                               # IDiffHost, EditorDiffHost
│   ├── InlineEdit/                          # Inline edit bar + command filter
│   └── McpConfigDialog.xaml(.cs)            # MCP configuration
├── Services/                                # ~130 service files
│   ├── Agents/                              # AskAgent, ExploreAgent, PlanAgent, EditAgent, BuildAgent
│   ├── BuiltInTools/                        # 28 tool implementations
│   ├── EditTools/                           # Edit primitives, string matching, healing
│   ├── Editing/                             # ProposalCommitCoordinator, StagedEditWorkspace, commit targets
│   ├── Providers/                           # DeepSeekProvider, OpenAiCompatibleProvider
│   ├── Interfaces/                          # Service abstractions
│   ├── IdeContext/                          # Active file & Git context tracking
│   ├── Telemetry/                           # AgentMetricsCollector
│   └── Benchmark/                           # BenchmarkReportGenerator
├── Models/                                  # DTOs, agent/chat/tool models
├── Settings/                                # Options page, unified settings, API key store, migration
├── CodeCompletion/                          # Ghost text, prediction manager, command filter
├── Utils/                                   # Logging, i18n markup, tokenizers, encoding helpers
├── Resources/Locales/                       # en.json, zh-CN.json
├── .vsextension/                            # Extensibility metadata localization
├── docs/                                    # Design & analysis documents
├── benchmark/                               # Benchmark protocol and sample tasks
└── tools/                                   # Build & lint scripts
```

## Initialization sequence

`DeepSeek_v4_for_VisualStudioPackage.InitializeAsync` runs a deliberately ordered sequence, with each step
isolated in its own `try`/`catch` so a non-fatal failure cannot prevent the extension from loading:

1. `base.InitializeAsync` — package registration.
2. **Localization (auto-detect)** — so the options page builds its property descriptors in the right language.
3. **In-memory option defaults** — `GetDialogPage` is deliberately *not* called here, because restoring settings
   synchronously queries Unified Settings while VS is still waiting for this package, which deadlocks the UI
   thread. Persisted options load later in `LoadPersistedOptionsAsync`.
4. **Logger** initialization.
5. **Localization (user override)**.
6. **Settings-changed subscription**.
7. **Theme service + VS UI language capture** (on the UI thread).
8. **DI container** — `CompositionRoot.Build()`.
9. **Command registration** — `ShowChatWindowCommand` immediately; `InlineAiEditCommand` deferred to a
   background task to keep startup fast.

Background sweeps then run off the UI thread: expired backup sessions (>14 days), exported diagram SVGs, and
stale vision-image temp batches (>6 hours).

Startup auto-show of the chat window is **gated by a marker file**
(`%LocalAppData%\DeepSeekVS\chat-window-opened.flag`), written only after the user explicitly opens the window.
A blank or never-used instance therefore stays quiet instead of forcing the persisted-settings load path.

## Assembly resolution

The static constructor installs an `AppDomain.AssemblyResolve` handler and preloads critical assemblies. This
solves two real-world problems:

- **.NET Standard bridge assemblies** (`System.Memory`, `System.Buffers`, …) — Markdig was compiled against
  `System.Memory` 4.0.5.0 but a newer version is deployed; the handler redirects at runtime.
- **Third-party extension conflicts** — ReSharper preloads different `Microsoft.Web.WebView2.*` and `Markdig`
  versions. The extension therefore **always loads its own bundled copies** of these "critical" assemblies
  rather than reusing whatever is already in the AppDomain, which previously caused `XamlParseException` /
  `MissingMethodException` and a chat window that refused to open (issue #18).

An MSBuild target (`IncludeSystemAssembliesInVsix`) explicitly injects the `System.*` bridge assemblies into the
VSIX, because the VS SDK's `PkgdefProjectOutputGroup` filters `System.*.dll` by default.

Native OCR dependencies (PaddleInference, OpenCvSharp) land in `dll\x64\` because the runtime host is
`devenv.exe`, not the extension output directory. The `CopyNativeDlls` target copies them to the output root, and
`OcrService.EnsureNativeDllSearchPath()` pre-loads them from sub-directories at runtime.

## Chat rendering pipeline

```
User types ──▶ WPF InputTextBox
                     │  C# builds messages, calls agent
                     ▼
              BaseAgent tool loop ──▶ DeepSeek API (SSE stream)
                     │  chunks: [THINKING] / [TOOL_CALL] / [CACHE] / content
                     ▼
             ChatHtmlService (generates HTML/CSS/JS)
                     │  PostWebMessageAsString (JSON) or ExecuteScriptAsync
                     ▼
            WebView2CompositionControl ──▶ rendered transcript
                     │  window.__sendToHost(...)
                     ▼
        CoreWebView2_WebMessageReceived ──▶ actions (approve, apply, retry, …)
```

The transcript is a `WebView2CompositionControl` chosen specifically to avoid the ReSharper-related BAML parsing
failure noted above. `ChatHtmlService` emits a full HTML document (Markdig-rendered messages plus KaTeX,
highlight.js, and Mermaid from a CDN), and signals readiness with `__pageReady__`. Client-side updates are batched
with `requestAnimationFrame`; C#→JS messaging prefers `PostWebMessageAsString` and falls back to
`ExecuteScriptAsync` and finally a full re-render. JS→C# messages funnel through a single `__sendToHost` entry
point into `CoreWebView2_WebMessageReceived`, carrying actions such as approve, apply, retry, and navigate.

Streaming frames are throttled in two tiers: body snapshots on a 60 ms window — carried only when the body
actually changed — and reasoning deltas on a 30 ms window, so a reasoning-only tick no longer re-serializes a
body that grows with the tool timeline. A terminal render dropped while the page is being rebuilt (theme or
language switch, WebView recovery) is queued and replayed on `__pageReady__` instead of being lost.

## Dependency injection

`CompositionRoot` is a static service locator built once during package initialization; `AddDeepSeekServices()`
registers API services, file parsing, edit patching, context management, prefix caching, compression, skills,
web search, RAG, memory, MCP, build, test, built-in tools, persistence, notifications, and the agent factory.

## Provider layer

`DeepSeekApiService` is a thin compatibility facade. The real transport lives in
`OpenAiCompatibleProvider` (SSE streaming, message sanitization, retry policy), with `DeepSeekProvider`
overriding it for DeepSeek-native capabilities: FIM completion on `/beta/completions`, account balance via
`user/balance`, peak/off-peak pricing in CNY and USD, and reasoning-parameter shaping.

Streaming uses `IAsyncEnumerable<string>` with an internal prefixed pseudo-protocol to carry metadata alongside
content — `[THINKING]` for reasoning deltas, `[TOOL_CALL]` for tool-call deltas, `[CACHE]` for cache-hit
statistics. Content is buffered to 100 characters before yielding to reduce iteration overhead, and an
independent 120-second read timeout guards against a stalled stream (necessary because .NET Framework 4.7.2's
`ReadLineAsync` does not accept a cancellation token).

Before sending, messages pass through a sanitization stage operating on a **deep clone** so the caller's
`ConversationContextManager` state — and therefore the prefix cache — is never disturbed. The rules drop orphaned
`tool` messages, remove empty assistants, backfill `reasoning_content`, merge consecutive same-role messages,
and strip unpaired tool calls that would otherwise produce a 400.

Retries cap at 4 attempts with exponential backoff; 4xx responses other than 429 are not retried. A special case
detects a 400 caused by unsupported image data, strips the `image_url` blocks, and resends once — so a single
unreadable attachment does not break the whole turn.

Reasoning parameters are inferred per vendor dialect (`ReasoningCapabilityConfig.Infer`) covering DeepSeek
official, OpenRouter, SiliconFlow/ModelScope, Kimi, GLM, Qwen/DashScope, MiniMax, and OpenAI o-series/GPT-5.
Extended effort levels are clamped to the platform's legal enum rather than dropped. A separate guard
(`ReasoningLoopGuard`) detects degenerate reasoning repetition and enforces a length ceiling.

## Prefix cache design

DeepSeek's prefix cache only pays off if the message prefix is byte-stable across requests. The extension
defends that property deliberately:

- The system prompt is a **shared immutable prefix** (`AiPrompts.SharedImmutablePrefix`) reused by every agent.
- Volatile content (compression summaries, memory, IDE snapshots, search results, RAG) is confined to a
  **dynamic block at `messages[1]`** or appended at the tail — never inserted ahead of the stable prefix.
- Compression summaries therefore merge into the dynamic block rather than being emitted as their own system
  message.
- Tool schemas are **sorted by name** before serialization so registration order cannot perturb the prefix.
- **The full toolset is always sent** (built-in + MCP) with client-side whitelist enforcement, instead of
  filtering the `tools` array per agent — filtering would change the prefix on every handoff.
- Agent handoffs forward the source agent's final message list as the new prefix, keeping the cache warm across
  the Plan → Edit → Build boundary.

`PrefixCacheManager` computes a SHA-256 fingerprint over the system prompt plus normalized tool catalog and
distinguishes two drift severities: a system-prompt change is a real break requiring re-pinning, while a tool-set
change is treated as expected (and updated silently) because tool availability legitimately varies by agent role
and settings. `ConversationContextManager` additionally calibrates its token estimator against actual API usage
with an exponential moving average, and computes prefix hashes at several message boundaries so a cache break can
be located precisely.

## Editing pipeline

```
AI proposes edit
      │
      ▼
┌──────────────────┐
│ InlineDiffSession │  Created → Showing
└────────┬─────────┘
         │  user reviews hunks (inline or side-by-side)
         ▼
┌──────────────────────────┐
│ ProposalCommitCoordinator│  Preflight ALL files
└────────┬─────────────────┘
         │ any preflight failure → nothing is written
         ▼
┌──────────────────┐
│  Commit targets  │  OpenBuffer · File · NewFile · DeleteFile
└────────┬─────────┘
         │ on mid-batch failure → best-effort rollback
         ▼
   Committed / Dismissed / Conflicted / Failed
```

`InlineDiffSessionState` = `Created`, `Showing`, `Applying`, `Committed`, `Dismissed`, `Conflicted`, `Failed`,
`Disposed`. `InlineDiffSessionManager` keys sessions by `ITextBuffer`, enforcing **one session per document**.

| Target | Used when | Behaviour |
|---|---|---|
| `OpenBufferCommitTarget` | File is open in the editor | Applies changes through an `ITextEdit` inside a single `ITextUndoTransaction` named **"Apply AI Edit"** — so one <kbd>Ctrl</kbd>+<kbd>Z</kbd> undoes the whole AI edit. Changes apply end-to-start by offset. |
| `FileCommitTarget` | File exists on disk but is closed | SHA-256 preflight against the baseline hash → backup → write with encoding preservation → re-read and verify → clean up. Restores from backup on mismatch. |
| `NewFileCommitTarget` | New file | Rejects an existing file; creates parent directories, writes, then adds to the VS project via DTE. |
| `DeleteFileCommitTarget` | File deletion | Verifies the hash, backs up, then deletes; rollback restores the original path. |

For long agent runs, `StagedEditWorkspace` tracks per-file baseline content, hash, and last-write time. Edits
accumulate in the workspace and commit together at the end (**preview-then-commit**), with per-hunk
accept/restore and full baseline restore.

Preview rendering goes through `DiffViewerService` (read-only difference buffers via MEF) and `EditorDiffHost`
(an adorner layer) with a 250 ms refresh debounce, per-hunk keep/revert buttons, and synchronized zoom.
Destructive operations snapshot state via `BackupService`; backups older than 14 days are swept at startup.

## Agent execution loop

All agents share one tool loop, tuned per role:

- **Parallel tool execution.** Calls within a turn run concurrently via `Task.WhenAll`. Duplicate calls (same
  name + normalized arguments) are deduplicated and reuse the first result. File-mutating tools are serialized
  behind a single `SemaphoreSlim` so concurrent edits cannot interleave writes.
- **Per-tool timeouts.** `ToolTimeoutPolicy` assigns budgets by tool (`memory` 10 s, symbol/error tools 20 s,
  `fetch_webpage` 45 s, default 60 s including MCP). **Interactive tools are exempt** so an approval prompt
  cannot be cut short mid-wait.
- **Iteration and error limits.** Defaults: 200 tool rounds, abort after 5 identical calls, abort after 5
  consecutive errors. An execution guard adds wall time (≤7200 s), total tokens (≤2 M), tool calls (≤10,000),
  and nesting depth (3) budgets, plus rolling-fingerprint no-progress detection with a warning at 80 %.
- **Stream resilience.** Up to 4 attempts with exponential backoff; 4xx (except 429) is not retried. A dropped
  stream resumes from partial content rather than restarting the turn.
- **Early termination on success.** In Edit/Build, a fully successful build ends the loop immediately instead of
  letting the model call `get_errors` to "double-check".
- **Degraded parsing fallback.** If the provider returns no native `tool_calls` but the content contains leaked
  DSML/XML `<invoke>` markup or ReAct-style `Action:`/`Action Input:` text, the loop synthesizes tool calls from
  it instead of failing the turn.

A second, hard-coded line of defence sits inside the loop: for any agent that is not Edit or Build, the editing
tools are stripped from the whitelist at runtime — so a read-only agent cannot write files even if a prompt tried
to make it.

### Handoff vs. sub-agent

`request_handoff` is a **full control transfer**: the current agent ends and the target takes over. `runSubagent`
is a **delegation**: the caller waits for the result and continues. Handoffs carry `editSteps` (progress
rendering), `gitState` (avoiding repeated Git probing), `chainBack`, and `ForwardedMessages` (prefix preservation).
Sub-agents get an isolated context manager and file-read cache copy, run up to 3 in parallel behind a semaphore,
and are guarded by a watchdog timeout (default 900 s).

### Solution reload vs. solution switch

When the agent rewrites `.sln`/`.slnx`/`.csproj`/`Directory.Build.props`, Visual Studio closes and reopens the
same solution — an `OnAfterCloseSolution` → `OnAfterOpenSolution` pair. The chat window treats the two events
very differently from a real solution switch:

- **Same path ⇒ reload, not switch.** `_boundSolutionPath` records which solution the in-memory conversation
  belongs to; `SolutionIdentity.IsSame` compares normalized paths. A reload keeps `_messages`, `_tree` and the
  context manager untouched and only re-syncs persisted settings — no save, no clear, no disk reload.
- **Never mutate a running conversation.** While `_isGenerating` is true, solution events are recorded in
  `_pendingSolutionChange` and applied *after* the run finishes (`ApplyPendingSolutionChangeIfAnyAsync`).
  Saving mid-run would persist a half snapshot, and reloading would replace the live state the agent is still
  writing into.
- **Crash-safe run marker.** `ChatSession.ActiveRun` is written when a run starts and cleared on every exit
  path. A session that still carries the marker on load reports "previous run was interrupted" instead of
  silently presenting a truncated turn.
- **In-flight snapshots.** Every 5 s while a run is active, the received content/timeline/reasoning is written
  to `conversations/inflight/<project>/<session>.json` (kept apart from the authoritative session file). On load,
  a leftover snapshot becomes an `IsIncomplete` assistant message, so a hard kill shows the partial output and
  offers retry instead of looking like "the task never ran". The write stays synchronous on the UI thread, so the
  last snapshot can never race the delete performed after a normal completion.
- **Streaming writes are anchored to the message object.** `_agentStreamingMessage` (not just the index) is
  verified before every streamed write; if the list was replaced, the write is dropped instead of landing on an
  unrelated message.
- **Handoff buttons are single-use.** `HandoffJson` is cleared and saved as soon as execution starts, so a
  reload can never resurrect a clickable "start implementing" button that would launch a second concurrent run.

## Code completion pipeline

A MEF-exported `IVsTextViewCreationListener` (content type `code`, editable role) attaches an
`InlinePredictionManager`, a `GhostTextTagger`, and a `CommandFilter` to each code view. A `DispatcherTimer`
debounces edits; surrounding code is gathered by walking enclosing spans (up to 5 nesting levels, ±500-char
fallback); a FIM request goes to `/beta/completions` capped at 256 tokens at temperature 0 and is cached (10-entry
LRU). The suggestion renders as a grey `IntraTextAdornmentTag` using the editor's own font metrics. <kbd>Tab</kbd>
accepts and formats, <kbd>Esc</kbd> dismisses, <kbd>Enter</kbd> restarts the timer.

## Related documents

| Document | Contents |
|---|---|
| [`../README.md`](../README.md) | Project overview and usage |
| [`Performance-Analysis.md`](Performance-Analysis.md) | Cold-start and runtime performance work |
| [`ColdStart-Fix-Verification.md`](ColdStart-Fix-Verification.md) | Startup hang root-cause analysis |
| [`InlineDiffSession-Redesign-Plan.md`](InlineDiffSession-Redesign-Plan.md) | Diff session redesign |
| [`I18n-Implementation-Analysis.md`](I18n-Implementation-Analysis.md) | Localization implementation |
| [`Prompt-Inventory.md`](Prompt-Inventory.md) | System prompt inventory |
| [`Settings-UnifiedIntegration-Feasibility.md`](Settings-UnifiedIntegration-Feasibility.md) | Unified settings bridge |
| [`VsToolApiIntegration-Analysis.md`](VsToolApiIntegration-Analysis.md) | VS tool API integration |
| [`RunTests-Tool-Implementation-Report.md`](RunTests-Tool-Implementation-Report.md) | `run_tests` tool design |
| [`../benchmark/README.md`](../benchmark/README.md) | Benchmark protocol |

---

# 简体中文

## 目录

- [项目结构](#项目结构)
- [初始化序列](#初始化序列)
- [程序集解析](#程序集解析)
- [聊天渲染管线](#聊天渲染管线)
- [依赖注入](#依赖注入)
- [提供方分层](#提供方分层)
- [前缀缓存设计](#前缀缓存设计)
- [编辑管线](#编辑管线)
- [Agent 执行循环](#agent-执行循环)
- [代码补全管线](#代码补全管线)
- [相关文档](#相关文档)

## 项目结构

```
DeepSeek-v4-for-VisualStudio/
├── DeepSeek_v4_for_VisualStudioPackage.cs   # AsyncPackage 入口（9 步初始化序列）
├── DeepSeekExtension.cs                     # VisualStudio.Extensibility 进程内宿主
├── VSCommandTable.vsct                      # 菜单、工具栏与快捷键绑定
├── source.extension.vsixmanifest            # VSIX 标识与资产
├── Commands/                                # ShowChatWindowCommand、InlineAiEditCommand
├── View/                                    # WPF 界面
│   ├── DeepSeekChatControl.*.cs             # 分部类：Agent、Sessions、Rendering、Theme 等
│   ├── DeepSeekChatWindowPane.cs            # 工具窗口宿主
│   ├── InlineDiffHostControl.xaml(.cs)      # 内联差异覆盖层 + 工具栏
│   ├── Hosts/                               # IDiffHost、EditorDiffHost
│   ├── InlineEdit/                          # 内联编辑条 + 命令过滤器
│   └── McpConfigDialog.xaml(.cs)            # MCP 配置
├── Services/                                # 约 130 个服务文件
│   ├── Agents/                              # AskAgent、ExploreAgent、PlanAgent、EditAgent、BuildAgent
│   ├── BuiltInTools/                        # 28 个工具实现
│   ├── EditTools/                           # 编辑原语、字符串匹配、healing
│   ├── Editing/                             # ProposalCommitCoordinator、StagedEditWorkspace、提交目标
│   ├── Providers/                           # DeepSeekProvider、OpenAiCompatibleProvider
│   ├── Interfaces/                          # 服务抽象
│   ├── IdeContext/                          # 活动文件与 Git 上下文跟踪
│   ├── Telemetry/                           # AgentMetricsCollector
│   └── Benchmark/                           # BenchmarkReportGenerator
├── Models/                                  # DTO、Agent/聊天/工具模型
├── Settings/                                # 选项页、统一设置、密钥存储、迁移
├── CodeCompletion/                          # 幽灵文本、预测管理器、命令过滤器
├── Utils/                                   # 日志、i18n 标记扩展、分词器、编码辅助
├── Resources/Locales/                       # en.json、zh-CN.json
├── .vsextension/                            # Extensibility 元数据本地化
├── docs/                                    # 设计与分析文档
├── benchmark/                               # 基准测试规程与样例任务
└── tools/                                   # 构建与检查脚本
```

## 初始化序列

`DeepSeek_v4_for_VisualStudioPackage.InitializeAsync` 按严格顺序执行，每一步都独立 `try`/`catch`，
确保非致命失败不会阻止扩展加载：

1. `base.InitializeAsync` —— 包注册。
2. **国际化（自动检测）** —— 使选项页能以正确语言构建属性描述符。
3. **内存中的选项默认值** —— 刻意**不**在此调用 `GetDialogPage`：同步恢复设置会查询统一设置，
   而此时 VS 仍在等待本包初始化完成，会造成 UI 线程死锁。持久化选项稍后由 `LoadPersistedOptionsAsync` 加载。
4. **日志系统**初始化。
5. **国际化（用户覆盖）**。
6. **设置变更订阅**。
7. **主题服务 + 捕获 VS 界面语言**（在 UI 线程上）。
8. **DI 容器** —— `CompositionRoot.Build()`。
9. **命令注册** —— `ShowChatWindowCommand` 立即注册；`InlineAiEditCommand` 延迟到后台任务，以加快启动。

随后在非 UI 线程上执行后台清扫：过期备份会话（>14 天）、导出的图表 SVG，
以及残留的视觉图片临时批次（>6 小时）。

启动时的自动弹窗由**标记文件**（`%LocalAppData%\DeepSeekVS\chat-window-opened.flag`）门控，
只有用户显式打开过聊天窗口后才会写入。因此全新或从未使用过的实例会保持安静，
不会在启动时被迫走持久化设置加载链路。

## 程序集解析

静态构造函数安装 `AppDomain.AssemblyResolve` 处理器并预加载关键程序集，解决两类真实问题：

- **.NET Standard 桥接程序集**（`System.Memory`、`System.Buffers` 等）—— Markdig 编译时引用
  `System.Memory` 4.0.5.0，但实际部署的是更新版本，处理器在运行时完成重定向。
- **第三方扩展冲突** —— ReSharper 会预加载不同版本的 `Microsoft.Web.WebView2.*` 与 `Markdig`。
  因此扩展对这些「关键程序集」**始终加载自带的副本**，而不是复用 AppDomain 中已有的版本 ——
  此前复用会导致 `XamlParseException` / `MissingMethodException`，使聊天窗口无法打开（issue #18）。

另有一个 MSBuild 目标（`IncludeSystemAssembliesInVsix`）显式把 `System.*` 桥接程序集注入 VSIX，
因为 VS SDK 的 `PkgdefProjectOutputGroup` 默认会过滤掉 `System.*.dll`。

本地 OCR 依赖（PaddleInference、OpenCvSharp）会落到 `dll\x64\`，因为运行时宿主是 `devenv.exe`
而非扩展输出目录。`CopyNativeDlls` 目标把它们复制到输出根目录，
`OcrService.EnsureNativeDllSearchPath()` 在运行时从子目录预加载。

## 聊天渲染管线

```
用户输入 ──▶ WPF InputTextBox
                  │  C# 构建消息、调用 Agent
                  ▼
           BaseAgent 工具循环 ──▶ DeepSeek API（SSE 流）
                  │  分块：[THINKING] / [TOOL_CALL] / [CACHE] / 正文
                  ▼
          ChatHtmlService（生成 HTML/CSS/JS）
                  │  PostWebMessageAsString（JSON）或 ExecuteScriptAsync
                  ▼
         WebView2CompositionControl ──▶ 渲染后的对话
                  │  window.__sendToHost(...)
                  ▼
     CoreWebView2_WebMessageReceived ──▶ 各类动作（审批、应用、重试…）
```

正文区特意使用 `WebView2CompositionControl`，以规避上述与 ReSharper 相关的 BAML 解析失败问题。
`ChatHtmlService` 输出完整 HTML 文档（Markdig 渲染的消息，加上来自 CDN 的 KaTeX、highlight.js 与 Mermaid），
并通过 `__pageReady__` 通报就绪。客户端更新通过 `requestAnimationFrame` 批量处理；
C#→JS 消息优先用 `PostWebMessageAsString`，失败降级到 `ExecuteScriptAsync`，再失败才全量重渲染。
JS→C# 消息统一经 `__sendToHost` 进入 `CoreWebView2_WebMessageReceived`，
承载审批、应用、重试、导航等动作。

流式帧分两档节流：正文快照 60 毫秒一档，且仅在正文确实变化时随帧携带；思考增量 30 毫秒一档 ——
纯思考刷新不再重新序列化随工具时间线增长的整段正文。页面重建期间（主题/语言切换、WebView 恢复）
被丢弃的终态渲染会先入队，在 `__pageReady__` 后补发，而不是静默丢失。

## 依赖注入

`CompositionRoot` 是在包初始化期间构建一次的静态服务定位器；`AddDeepSeekServices()` 注册 API 服务、
文件解析、编辑补丁、上下文管理、前缀缓存、压缩、技能、联网搜索、RAG、记忆、MCP、构建、测试、
内置工具、持久化、通知与 Agent 工厂。

## 提供方分层

`DeepSeekApiService` 只是薄兼容层。真正的传输逻辑位于 `OpenAiCompatibleProvider`
（SSE 流式、消息清洗、重试策略），`DeepSeekProvider` 继承并覆写 DeepSeek 原生能力：
`/beta/completions` 的 FIM 补全、`user/balance` 的账户余额、人民币与美元双轨的峰谷定价，
以及推理参数塑形。

流式处理使用 `IAsyncEnumerable<string>`，并通过内部带前缀的伪协议在正文之外携带元数据 ——
`[THINKING]` 承载推理增量，`[TOOL_CALL]` 承载工具调用增量，`[CACHE]` 承载缓存命中统计。
正文累积到 100 字符才产出，以减少迭代开销；另有独立的 120 秒读超时防止流挂起
（这是必要的，因为 .NET Framework 4.7.2 的 `ReadLineAsync` 不接受取消令牌）。

发送前，消息会经过一道清洗流程，且**全程操作深克隆**，因此调用方的 `ConversationContextManager`
状态 —— 也就是前缀缓存 —— 不会被扰动。清洗规则会移除孤立的 `tool` 消息、删除空 assistant、
回填 `reasoning_content`、合并连续同角色消息，并剥离会导致 400 错误的未配对工具调用。

重试最多 4 次并指数退避；4xx（429 除外）不重试。有一处特例：检测到 400 且错误体涉及不支持的图片数据时，
会剥离 `image_url` 块后原样重发一次 —— 使单张无法读取的附件不至于打断整轮对话。

推理参数按各家方言推断（`ReasoningCapabilityConfig.Infer`），覆盖 DeepSeek 官方、OpenRouter、
SiliconFlow/ModelScope、Kimi、GLM、Qwen/DashScope、MiniMax 与 OpenAI o-series/GPT-5。
扩展档位会被**钳制**到平台合法枚举，而不是直接丢弃。另有 `ReasoningLoopGuard` 检测退化的推理重复并限制长度。

## 前缀缓存设计

DeepSeek 的前缀缓存只有在消息前缀跨请求保持字节级稳定时才有收益。扩展刻意守护这一点：

- 系统提示词是**共享不可变前缀**（`AiPrompts.SharedImmutablePrefix`），所有 Agent 复用。
- 易变内容（压缩摘要、记忆、IDE 快照、搜索结果、RAG）被限制在 **`messages[1]` 的动态块**或尾部追加，
  绝不插入到稳定前缀之前。
- 因此压缩摘要会合并进动态块，而不是单独作为一条 system 消息。
- 工具 schema 在序列化前**按名称排序**，使注册顺序无法扰动前缀。
- **始终发送完整工具集**（内置 + MCP），在客户端执行白名单拦截，而不是按 Agent 过滤 `tools` 数组 ——
  过滤会导致每次移交都改变前缀。
- Agent 移交时会把源 Agent 的最终消息列表作为新前缀转发，使缓存在 Plan → Edit → Build 边界上保持命中。

`PrefixCacheManager` 对系统提示词加规范化工具目录计算 SHA-256 指纹，并区分两种漂移严重程度：
系统提示词变化属于真实断裂，需要重新 pin；工具集变化则被视为正常（静默更新），
因为工具可用性确实会随 Agent 角色与设置而变化。`ConversationContextManager` 还会用指数移动平均
根据真实 API 用量校准 token 估算，并在多个消息边界计算前缀哈希，以便精确定位缓存断裂位置。

## 编辑管线

```
AI 提出修改
      │
      ▼
┌──────────────────┐
│  InlineDiffSession│  Created → Showing
└────────┬─────────┘
         │  用户逐块审阅（内联或并排）
         ▼
┌──────────────────────────┐
│ ProposalCommitCoordinator│  对所有文件预检
└────────┬─────────────────┘
         │ 任一预检失败 → 不写入任何文件
         ▼
┌──────────────────┐
│     提交目标      │  OpenBuffer · File · NewFile · DeleteFile
└────────┬─────────┘
         │ 批量提交中途失败 → 尽力回滚
         ▼
   Committed / Dismissed / Conflicted / Failed
```

`InlineDiffSessionState` 取值为 `Created`、`Showing`、`Applying`、`Committed`、`Dismissed`、`Conflicted`、
`Failed`、`Disposed`。`InlineDiffSessionManager` 以 `ITextBuffer` 为键管理会话，强制**单文档单会话**。

| 目标 | 适用场景 | 行为 |
|---|---|---|
| `OpenBufferCommitTarget` | 文件已在编辑器中打开 | 在单个名为 **"Apply AI Edit"** 的 `ITextUndoTransaction` 中通过 `ITextEdit` 应用改动 —— 因此一次 <kbd>Ctrl</kbd>+<kbd>Z</kbd> 即可撤销整次 AI 编辑。改动按偏移量从后向前应用。 |
| `FileCommitTarget` | 文件存在于磁盘但未打开 | 以 SHA-256 对基线哈希做预检 → 备份 → 保持编码写入 → 回读校验 → 清理备份。校验不符时从备份恢复。 |
| `NewFileCommitTarget` | 新建文件 | 文件已存在则拒绝；创建父目录、写入文件，随后通过 DTE 加入 VS 项目。 |
| `DeleteFileCommitTarget` | 删除文件 | 校验哈希、备份，然后删除；回滚时恢复到原路径。 |

对于长时间运行的 Agent 任务，`StagedEditWorkspace` 会记录每个文件的基线内容、哈希与最后写入时间。
改动先累积在工作区中，最后统一提交（**先预览后提交**），并支持逐块接受/恢复与整体基线还原。

预览渲染经由 `DiffViewerService`（通过 MEF 构建只读差异缓冲区）与 `EditorDiffHost`（装饰层），
带 250 毫秒刷新防抖、逐块保留/撤销按钮与缩放同步。破坏性操作前会通过 `BackupService` 快照状态；
超过 14 天的备份会在启动时清理。

## Agent 执行循环

所有 Agent 共用同一套工具循环，并按角色调整行为：

- **工具并行执行。** 同一轮内的工具调用通过 `Task.WhenAll` 并发执行。重复调用（同名 + 规范化参数相同）
  会被去重并复用首次结果。文件修改类工具随后被单个 `SemaphoreSlim` 串行化，确保并发编辑不会交叉写盘。
- **分档超时。** `ToolTimeoutPolicy` 按工具分配预算（`memory` 10 秒，符号/错误类 20 秒，
  `fetch_webpage` 45 秒，默认 60 秒含 MCP）。**交互式工具豁免超时**，避免审批等待被硬超时打断。
- **迭代与错误上限。** 默认：200 轮工具调用；连续 5 次完全相同的调用即中止；连续 5 次错误即中止。
  执行守卫另提供墙钟时间（≤7200 秒）、总 token（≤200 万）、工具调用数（≤10,000）与嵌套深度（3）预算，
  并通过滚动指纹检测「无进展」，在消耗达 80% 时告警。
- **流式容错。** 最多 4 次尝试并指数退避；4xx（429 除外）不重试。流中断时从已接收的部分内容续写，
  而不是重跑整轮。
- **成功后提前终止。** 在 Edit/Build 中，一旦构建完全成功，循环立即结束，
  不再让模型调用 `get_errors` 去「二次确认」。
- **降级解析兜底。** 若提供方未返回原生 `tool_calls`，但内容中泄露了 DSML/XML `<invoke>` 标记
  或 ReAct 风格的 `Action:`/`Action Input:` 文本，循环会据此合成工具调用，而不是让整轮失败。

循环内还设有第二道硬编码防线：对任何非 Edit/Build 的 Agent，编辑类工具会在运行时从白名单中剔除 ——
即便提示词试图让只读 Agent 写文件，也无法做到。

### 移交与子代理的区别

`request_handoff` 是**完整控制权移交**：当前 Agent 结束，目标 Agent 接管。`runSubagent` 是**委派**：
调用方等待结果后继续。移交会携带 `editSteps`（进度渲染）、`gitState`（免去重复 Git 探测）、
`chainBack` 与 `ForwardedMessages`（保持前缀命中）。子代理拥有独立的上下文管理器与文件读缓存副本，
经信号量限制最多 3 个并行，并受看门狗超时保护（默认 900 秒）。

### 解决方案重载 vs. 切换解决方案

Agent 改写 `.sln`/`.slnx`/`.csproj`/`Directory.Build.props` 时，Visual Studio 会先关闭再打开同一个解决方案
（`OnAfterCloseSolution` → `OnAfterOpenSolution`）。聊天窗口必须把这种情况与「切换到另一个解决方案」区分开：

- **路径相同 ⇒ 是重载，不是切换。** `_boundSolutionPath` 记录内存中的对话属于哪个解决方案，
  `SolutionIdentity.IsSame` 做规范化路径比较。重载时保留 `_messages`/`_tree`/上下文管理器，只重新同步配置——
  不保存、不清空、不从磁盘重载。
- **运行中的对话绝不被改写。** `_isGenerating` 为真时，解决方案事件只记入 `_pendingSolutionChange`，
  等任务收尾后再应用。运行中保存只会落一份半截快照，而重载会把 Agent 正在写入的活动状态整体替换掉。
- **可崩溃恢复的执行标记。** `ChatSession.ActiveRun` 在任务开始时写入、在所有退出路径上清除。
  加载时若标记仍在，会提示「上次执行被中断」，而不是把截断的轮次当成完整历史。
- **执行中快照。** 任务执行期间每 5 秒把已接收正文/时间线/推理写入
  `conversations/inflight/<project>/<session>.json`（与会话权威文件分离）。加载时若发现残留快照，
  会补成一条 `IsIncomplete` 助手消息——强杀后能看到已产出的部分并直接重试，而不是像「任务没执行」。
  写入在 UI 线程同步完成，保证「最后一次快照」不会与正常收尾后的删除竞争、复活陈旧快照。
- **流式写回锚定在消息对象上。** 每次写回前校验 `_agentStreamingMessage`（而不只是下标）；
  列表被整体替换时放弃本次写回，而不是把内容写到无关消息上。
- **Handoff 按钮是一次性的。** 开始执行即清除并持久化 `HandoffJson`，
  这样重载后不会复活一个可点击的「开始实现」，进而并发启动第二个任务。

## 代码补全管线

MEF 导出的 `IVsTextViewCreationListener`（内容类型 `code`，可编辑角色）为每个代码视图挂载
`InlinePredictionManager`、`GhostTextTagger` 与 `CommandFilter`。`DispatcherTimer` 对编辑做防抖；
通过逐层扩展包围区间收集上下文（最多 5 层嵌套，回退 ±500 字符）；向 `/beta/completions` 发送 FIM 请求，
上限 256 token、温度 0，并做缓存（10 条 LRU）。建议以灰色 `IntraTextAdornmentTag` 渲染，
并使用编辑器自身字体度量。<kbd>Tab</kbd> 接受并格式化，<kbd>Esc</kbd> 取消，<kbd>Enter</kbd> 重启定时器。

## 相关文档

| 文档 | 内容 |
|---|---|
| [`../README.md`](../README.md) | 项目概览与使用说明 |
| [`../README.zh-CN.md`](../README.zh-CN.md) | 中文使用说明 |
| [`Performance-Analysis.md`](Performance-Analysis.md) | 冷启动与运行时性能优化 |
| [`ColdStart-Fix-Verification.md`](ColdStart-Fix-Verification.md) | 启动卡死根因分析 |
| [`InlineDiffSession-Redesign-Plan.md`](InlineDiffSession-Redesign-Plan.md) | 差异会话重构方案 |
| [`I18n-Implementation-Analysis.md`](I18n-Implementation-Analysis.md) | 国际化实现分析 |
| [`Prompt-Inventory.md`](Prompt-Inventory.md) | 系统提示词清单 |
| [`Settings-UnifiedIntegration-Feasibility.md`](Settings-UnifiedIntegration-Feasibility.md) | 统一设置桥接 |
| [`VsToolApiIntegration-Analysis.md`](VsToolApiIntegration-Analysis.md) | VS 工具 API 集成 |
| [`RunTests-Tool-Implementation-Report.md`](RunTests-Tool-Implementation-Report.md) | `run_tests` 工具设计 |
| [`../benchmark/README.md`](../benchmark/README.md) | 基准测试规程 |