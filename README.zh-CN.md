# DeepSeek Chat for Visual Studio

> 一款深度集成在 Visual Studio 2022（17.14+）与 Visual Studio 2026 中的 DeepSeek AI 编程助手 ——
> 融合对话问答、多 Agent 自主编码、内联代码补全，以及可逐块审阅的差异式编辑。

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![VSIX](https://img.shields.io/badge/VSIX-1.3.1-success.svg)](source.extension.vsixmanifest)
[![Visual Studio](https://img.shields.io/badge/Visual%20Studio-2022%20%7C%202026-5C2D91.svg)](https://visualstudio.microsoft.com/)
[![.NET Framework](https://img.shields.io/badge/.NET%20Framework-4.7.2-512BD4.svg)](https://dotnet.microsoft.com/)
[![Stars](https://img.shields.io/github/stars/zmy15/DeepSeek-for-VisualStudio?style=flat&logo=github&label=Stars)](https://github.com/zmy15/DeepSeek-for-VisualStudio/stargazers)
[![Forks](https://img.shields.io/github/forks/zmy15/DeepSeek-for-VisualStudio?style=flat&logo=github&label=Forks)](https://github.com/zmy15/DeepSeek-for-VisualStudio/network/members)
[![Issues](https://img.shields.io/github/issues/zmy15/DeepSeek-for-VisualStudio?style=flat&logo=github&label=Issues)](https://github.com/zmy15/DeepSeek-for-VisualStudio/issues)

[English](README.md) · **简体中文**

---

## 目录

- [项目简介](#项目简介)
- [界面截图](#界面截图)
- [功能特性](#功能特性)
- [环境要求](#环境要求)
- [安装](#安装)
- [快速上手](#快速上手)
- [多 Agent 系统](#多-agent-系统)
- [内置工具](#内置工具)
- [内联代码补全](#内联代码补全)
- [内联编辑（Ctrl+I）](#内联编辑ctrli)
- [差异审阅](#差异审阅)
- [MCP 支持](#mcp-支持)
- [记忆与技能](#记忆与技能)
- [设置项参考](#设置项参考)
- [快捷键](#快捷键)
- [构建与测试](#构建与测试)
- [故障排查](#故障排查)
- [参与贡献](#参与贡献)
- [Star 历史](#star-历史)
- [开源协议](#开源协议)

---

## 项目简介

**DeepSeek Chat for Visual Studio** 是一个把完整 Agent 式编程助手带入 Visual Studio IDE 的 VSIX 扩展。
它不只是一个聊天侧边栏 —— 它能读取和检索你的解决方案、修改文件、执行构建与测试、操作 Git、驱动浏览器，
而这一切都在你可控的审批模型之下进行。

> 如果这个扩展帮你省下了时间，一个 ⭐ 能让更多人发现它，也是持续维护的真实动力。

| | |
|---|---|
| **版本** | 1.3.0 |
| **目标平台** | Visual Studio 2022 / 2026，`amd64`，`[17.14, )` |
| **运行时** | .NET Framework 4.7.2，C# 14 |
| **开源协议** | MIT |

聊天界面采用 WPF 外壳 + WebView2 正文渲染器，正文的 HTML/CSS/JS 全部由 C# 生成，
可呈现 Markdown、语法高亮、KaTeX 数学公式与 Mermaid 图表，并自动跟随 Visual Studio 主题。

---

## 界面截图

### 聊天窗口 —— 欢迎状态

![聊天窗口欢迎状态](https://raw.githubusercontent.com/zmy15/DeepSeek-for-VisualStudio/master/docs/images/zh_cn/1.png)

空会话状态：DeepSeek 欢迎语，提示可以解释代码、修复 Bug、重构代码、生成测试、回答技术问题。

### 输入区与状态栏

![输入区与状态栏](https://raw.githubusercontent.com/zmy15/DeepSeek-for-VisualStudio/master/docs/images/zh_cn/2.png)

底部输入区与状态栏：**MCP 已连接**、模型选择器（`DeepSeek-V4.1-Flash`）、**深度思考**开关、
推理强度（`high`）、联网搜索（`DeepSeek 官方`）、清空按钮，以及**审批模式**（智能拦截）与**余额**。

### 带思考过程与工具调用的对话

![带思考过程的对话](https://raw.githubusercontent.com/zmy15/DeepSeek-for-VisualStudio/master/docs/images/zh_cn/3.png)

可折叠的**思考过程**区块（已执行 8 次工具调用，可点击展开查看过程）、渲染后的 Markdown 表格、
行内代码与代码块，以及流式回答。

### 上下文与缓存统计

![上下文与缓存统计](https://raw.githubusercontent.com/zmy15/DeepSeek-for-VisualStudio/master/docs/images/zh_cn/4.png)

回答末尾的统计条：**92.4%** 前缀缓存命中率（337,408 命中 / 27,866 未命中），
以及 Prompt 与 Completion 的 token 用量。

### 会话历史

![会话历史](https://raw.githubusercontent.com/zmy15/DeepSeek-for-VisualStudio/master/docs/images/zh_cn/10.png)

历史会话列表：标题由 AI 自动生成（如「VSIX扩展项目结构分析」「1747个单元测试全部通过」），
按时间倒序排列，支持逐条删除。

---

## 功能特性

**对话能力** —— 流式对话，对支持推理的模型提供可折叠的 *Thinking Process* 区块；Markdown、表格、
语法高亮、KaTeX 数学公式、Mermaid 图表（可导出 SVG）；多会话管理，标题由 AI 自动生成；
消息重试/编辑/复制与版本、分支导航；视觉模型下可直接查看图片。

**Agent 能力** —— 五种专职 Agent（Ask / Explore / Plan / Edit / Build）彼此移交任务；28 个内置工具
外加任意数量的 MCP 工具；自主工具循环，可读取、检索、编辑、构建、测试并持续迭代；最多 3 个 Explore
子代理并行；持久化记忆；可复用技能。

**编辑能力** —— 基于 FIM 端点的内联代码补全（幽灵文本）；**内联编辑（Ctrl+I）** 用自然语言直接下达修改指令；
每次 AI 修改都以可逐块审阅的差异呈现；支持精确字符串替换、批量替换、整文件重写与 `apply_patch` 补丁；
破坏性操作前自动备份。

**IDE 集成** —— 原生可停靠工具窗口，跟随 VS 主题；通过 VS SDK 完成构建与测试；集成错误列表；
新建文件自动加入项目；Toast 通知。

**平台特性** —— 完整中英双语界面，2,100+ 条本地化字符串（含系统提示词）；DeepSeek 官方或任意
OpenAI 兼容端点，自动识别各家的推理参数方言；通过 PaddleOCR 或 Windows 内置 OCR 引擎提供本地 OCR。

---

## 环境要求

| 组件 | 要求 |
|---|---|
| Visual Studio | 2022 版本 **17.14** 或更高，或 Visual Studio 2026 |
| 版本 | Community / Professional / Enterprise |
| 架构 | `amd64`（x64） |
| 工作负载 | *.NET 桌面开发* 或任何包含 **Visual Studio 核心编辑器** 的工作负载 |
| 操作系统 | Windows 10 / 11（x64） |
| 运行时 | .NET Framework 4.7.2（随 Visual Studio 安装） |
| WebView2 | Evergreen 运行时 —— 已内置固定版本运行时作为兜底 |

> [!NOTE]
> 不支持 Visual Studio for Mac，也不支持 VS Code。

默认提供方需要 DeepSeek API 密钥（[platform.deepseek.com](https://platform.deepseek.com/)）；
也可以改为配置自定义的 OpenAI 兼容端点。

---

## 安装

**从 Release 安装（推荐）**

1. 从 [Releases](https://github.com/zmy15/DeepSeek-for-VisualStudio/releases) 下载最新的
   `DeepSeek_v4_for_VisualStudio-v<版本号>.vsix`。
2. **关闭 Visual Studio。**
3. 双击 `.vsix`，按 VSIX Installer 提示完成安装。
4. 启动 Visual Studio，按 <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>D</kbd> 打开聊天窗口。

> [!TIP]
> 若环境不适合捆绑 PaddleOCR/OpenCV 原生库，可选用 **No-Local-OCR** 版本，除本地 OCR 外功能完全一致。

---

## 快速上手

1. **配置 API 密钥。** 打开 `工具 → 选项 → DeepSeek Chat → General`，粘贴密钥并用**测试连接**验证。
   密钥保存在 **Visual Studio 凭据存储**中，不会以明文写入设置存储。
2. **打开聊天窗口。** 按 <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>D</kbd>，或使用
   `视图 → 其他窗口 → DeepSeek Chat`，也可点击工具栏按钮。
3. **选择模型。** 在聊天窗口底部的下拉框中选择。
4. **开始提问。** 例如：
   - `解释一下这个解决方案的架构`
   - `找出 IDiffHost 的所有实现并说明它们的差异`
   - `给 Services/Utils 下所有公开方法补上空参数校验，然后构建`
5. **审阅改动。** Agent 修改文件时会弹出差异预览，确认后再保留或撤销。

在消息前加 `@` 提及即可绕过自动路由：

```
@ask   为什么这个方法每次调用都会分配内存？
@plan  把数据层从 Dapper 迁移到 EF Core
@edit  在整个解决方案中把 GetUserById 重命名为 GetUserByIdAsync
@build 修复 Reporting 项目中的 4 个编译错误
```

输入 `@` 会弹出 **Agent 建议列表**；输入 `/` 会弹出**技能建议列表**。

> **截图占位** —— 首次使用流程
> `docs/images/first-run.gif`
> 录制：打开选项页 → 粘贴密钥 → 测试连接 → 打开聊天窗口 → 首次回答。

---

## 多 Agent 系统

所有请求都从 **Ask Agent** 进入，当任务超出其职责边界时，由它通过 `request_handoff` 工具进行移交。

| Agent | 职责 | 写权限 | 典型触发场景 |
|---|---|---|---|
| **Ask** | 技术问答、代码解释、方案讨论。默认入口与兜底。 | ❌ 只读 | “X 是怎么工作的”、“解释这个报错” |
| **Explore** | 深度只读代码库检索，由其他 Agent 调用，通常并行执行。 | ❌ 只读 | 内部子代理，不接受用户直接路由 |
| **Plan** | 调研后产出可审阅的分步实施计划。 | ❌ 只读 | 多文件改动、架构性工作、涉及 ≥3 个文件 |
| **Edit** | 按计划逐步执行代码修改。 | ✅ 完整 | 明确的代码修改、Git 写操作、终端命令 |
| **Build** | 修改后诊断并修复编译/构建错误。 | ✅ 完整 | 构建失败、链接错误 |

```
用户请求
    │
    ▼
┌─────────┐   调研        ┌───────────┐  （并行，最多 3 个）
│  Ask    │──────────────▶│  Explore  │
│ （路由）│               └───────────┘
└────┬────┘
     │ @plan / 复杂任务
     ▼
┌─────────┐   计划就绪      ┌─────────┐   逐步执行      ┌─────────┐
│  Plan   │──────────────▶│  Edit   │──────────────▶ │  Build  │
└─────────┘   （移交）      └─────────┘   （验证）      └────┬────┘
                                                            │ 构建成功
                                                            ▼
                                                      ┌─────────┐
                                                      │  Ask    │  最终总结
                                                      └─────────┘
```


### 计划确认与执行

![计划确认与开始实现](https://raw.githubusercontent.com/zmy15/DeepSeek-for-VisualStudio/master/docs/images/zh_cn/11.jpg)

Plan Agent 产出分步计划并等待用户确认：每条步骤标明改动文件与要点，
计划同时落盘到 `%LocalAppData%\DeepSeekVS\plans\plan.md`；点击**开始实现**即移交给 Edit Agent，
底部**步骤执行**面板实时跟踪 0/7 的进度。

Ask Agent 也可以在多选题不足以决策时，弹出结构化提问卡片收敛需求：

![结构化提问卡片](https://raw.githubusercontent.com/zmy15/DeepSeek-for-VisualStudio/master/docs/images/zh_cn/8.png)

---

## 内置工具

共 28 个内置工具。`🔒` 标记仅 Edit/Build Agent 可用的工具 —— 只读 Agent 是在运行时被拦截，而非仅靠提示词约束。

| 分组 | 工具 |
|---|---|
| **读取与探索** | `list_dir`、`read_file`、`file_search`、`grep_search`、`symbol_search`、`get_file_symbols`、`get_errors` |
| **写入与编辑** 🔒 | `replace_string_in_file`、`multi_replace_string_in_file`、`create_file`、`delete_file`、`apply_patch`、`create_directory` |
| **构建、测试、终端** | `build_solution`、`run_tests`、`run_in_terminal`、`get_terminal_output` |
| **网页与视觉** | `web_search`、`fetch_webpage`、`capture_window` 🔍、`capture_webpage` 🔍 |
| **Git** | `git` |
| **记忆、技能、交互** | `memory`、`load_skill`、`read_skill_resource`、`runSubagent`、`request_handoff`、`VisualStudio_askQuestions` |

🔍 = 仅视觉模型可用。重点说明：

- **`symbol_search`** —— 查找具名符号的首选工具，基于 VS 符号索引在整个解决方案中搜索，
  对类、方法、属性的查找优于文本搜索。
- **`run_tests`** —— **构建并**运行测试（.NET 走 VSTest，C++ 走 CMake/CTest），返回结构化结果，
  因此「跑测试」本身就是完整的验证步骤。
- **`apply_patch`** —— 自定义 diff 格式（`*** Begin Patch` / `*** End Patch`），支持跨文件修改，
  并在差异块无法干净应用时提供 AI 辅助 healing。
- **`git`** —— 覆盖面较广；只读操作自动放行，危险操作（`push`、`reset --hard`、
  `branch --force --delete`）需审批。

### 审批模型

| 模式 | 行为 |
|---|---|
| **AllowAll（全部放行）** | 自动批准一切，不弹提示。*请谨慎使用。* |
| **SmartBlock（智能拦截，默认）** | 仅危险命令需要审批，安全操作直接放行。 |
| **BlockAll（全部拦截）** | 所有终端命令与文件操作都需要显式审批。 |

终端命令、`git` 写操作、文件删除、**工作区外访问**，以及对**项目文件**
（`.csproj`、`.vcxproj`、`.sln`、`.props`、`.targets`、`CMakeLists.txt`、`Makefile` 等）的修改
始终走审批流程。审批卡片串行呈现，同一时刻只显示一张，并配有 800 毫秒看门狗。

![终端命令审批卡片](https://raw.githubusercontent.com/zmy15/DeepSeek-for-VisualStudio/master/docs/images/zh_cn/9.png)

审批卡片会说明**目的**、展示 AI 请求执行的具体命令，并给出**允许**与**跳过**选项 ——
上例中的只读命令即被判定为无需拦截，而会修改系统状态的命令才提示确认。

---

## 内联代码补全

幽灵文本补全由 MEF 导出的 `IVsTextViewCreationListener` 挂载到每个代码视图。编辑操作先做防抖，
通过逐层扩展包围区间收集上下文，再向 `/beta/completions` 端点发送**中间填充（FIM）**请求
（256 token，温度 0），并带有小型 LRU 缓存。建议以灰色幽灵文本渲染，使用编辑器自身字体度量：
<kbd>Tab</kbd> 接受，<kbd>Esc</kbd> 取消，<kbd>Enter</kbd> 重启定时器。

![内联代码补全](https://raw.githubusercontent.com/zmy15/DeepSeek-for-VisualStudio/master/docs/images/zh_cn/13.gif)

---

## 内联编辑（Ctrl+I）

把光标放在编辑器中，按 <kbd>Ctrl</kbd>+<kbd>I</kbd>，在浮现的输入条中键入指令。
扩展会生成修改并以差异形式预览，按 <kbd>Enter</kbd> 应用，按 <kbd>Esc</kbd> 取消。
该快捷键限定在文本编辑器作用域内，不会影响系统增量搜索。

![内联编辑](https://raw.githubusercontent.com/zmy15/DeepSeek-for-VisualStudio/master/docs/images/zh_cn/12.gif)

---

## 差异审阅

每一次 AI 修改都是**提案**，而不是既成事实。提交前先对所有文件做一次预检 —— 任一文件失败则不写入任何内容 ——
随后顺序提交，中途失败时尽力回滚。

| 提交目标 | 适用场景 | 行为 |
|---|---|---|
| 已打开缓冲区 | 文件已在编辑器中打开 | 包裹在**单个**名为 "Apply AI Edit" 的 `ITextUndoTransaction` 中，因此一次 <kbd>Ctrl</kbd>+<kbd>Z</kbd> 即可撤销整次编辑。 |
| 磁盘文件 | 文件未打开 | SHA-256 预检 → 备份 → 保持编码写入 → 回读校验。 |
| 新建文件 | 新建 | 文件已存在则拒绝，随后加入 VS 项目。 |
| 删除文件 | 删除 | 校验哈希、备份，然后删除 —— 回滚可恢复原文件。 |

对于长时间运行的 Agent 任务，改动会先累积在暂存工作区中，最后统一提交（**先预览后提交**），
并支持逐块接受/恢复。超过 14 天的备份会在启动时清理。

改动摘要面板会汇总本次涉及的文件与增删行数，并提供**全部应用** / **全部撤销**：

![改动摘要](https://raw.githubusercontent.com/zmy15/DeepSeek-for-VisualStudio/master/docs/images/zh_cn/5.png)

打开差异预览后可在**内联**与**并排**两种视图间切换，工具栏给出增删统计
（如 +111 / −15），支持**上一个 / 下一个**跳转差异，以及**保留此块 / 撤销此块**逐块决策：

![内联差异审阅](https://raw.githubusercontent.com/zmy15/DeepSeek-for-VisualStudio/master/docs/images/zh_cn/6.png)

---

## MCP 支持

扩展是一个 **MCP（Model Context Protocol）客户端**，可用外部工具服务器为 Agent 扩展能力。

- **传输方式：** `stdio`（启动本地进程）与 `http`（Streamable HTTP，支持 SSE 与 `Last-Event-ID` 断线续传）。
- **配置界面：** 增删服务器，设置名称、命令、参数、环境变量，启用/停用，以及测试连接。
- **粘贴 JSON 导入：** 自动识别 Claude Desktop、VS Code 与 Cursor 配置格式。
- **配置存储：** `%LocalAppData%\DeepSeekVS\mcp_servers.json`。
- **工具合并：** MCP 工具与内置工具合并为同一工具集；名称冲突时 **MCP 优先**。
- **按角色区分：** MCP 工具按名称模式分为读/写两类，因此 **Explore** 获得读类工具，
  **Edit**/**Build** 获得写类工具。
- **健壮性：** 单个服务器连接失败只记录日志并跳过，不会中断启动。

![MCP 支持](https://raw.githubusercontent.com/zmy15/DeepSeek-for-VisualStudio/master/docs/images/zh_cn/14.png)

---

## 记忆与技能

**记忆**分三个作用域，通过 `memory` 工具访问：

| 作用域 | 路径 | 生命周期 | 用途 |
|---|---|---|---|
| **用户（User）** | `/memories/` | 跨所有工作区与会话 | 偏好、风格、反复出现的模式、纠正记录 |
| **会话（Session）** | `/memories/session/` | 仅当前对话 | 任务计划、进行中的笔记、跨轮次状态 |
| **仓库（Repo）** | `/memories/repo/` | 当前解决方案 | 构建/测试命令、代码约定、架构决策 |

**技能（Skills）** 是可复用、可被模型调用的流程包 —— 一个包含 `SKILL.md` 的目录，
外加可选的 `scripts/`、`references/` 与 `assets/`。可用 `/技能名` 显式调用，
在开启**自动技能路由**时由模型自行选择，或接受模型主动建议。扩展自带 `code-review` 技能；
`/help` 列出技能，`/refresh-skills` 重新扫描，`/create-skill` 生成新技能骨架。
技能发现范围包括项目级的 `.github/skills/`、`.agents/skills/`、`.claude/skills/`、
用户级目录，以及扩展自带的 `BuiltInSkills/`。`read_skill_resource` 带路径穿越防护。

---

## 设置项参考

通过 `工具 → 选项 → DeepSeek Chat` 访问。在 VS 2026 中，非敏感设置也会出现在统一设置界面中，并保持双向同步。

| 分组 | 主要设置 |
|---|---|
| **连接** | API 密钥（存于凭据存储）、自定义端点密钥、API 端点（Base URL）、模型目录、自定义模型名、测试连接 |
| **模型** | 选择模型、启用深度思考、推理强度（`low`/`high`/`max`）、LLM 超时 |
| **网页与视觉** | 启用联网搜索、搜索引擎、网页抓取方式、百度/Bing 密钥、OCR 引擎 |
| **编辑** | 显示差异标记、启用自动补全、自动补全延迟、接受后继续补全 |
| **上下文** | Token 预算（%）、自动压缩、压缩阈值、保留最近轮数、RAG、RAG Top K、上下文统计、IDE 上下文注入 |
| **Agent 与安全** | 审批模式、最大工具调用轮数、最大重复调用次数、最大连续错误数、墙钟/token/工具调用/深度预算、无进展轮数、自动构建、自动技能路由、追加消息模式 |
| **界面** | 语言、主题模式、输入框高度、底部区域缩放、系统提示词、恢复默认设置、API 报文转储 |

![设置页 —— Agent 分组](https://raw.githubusercontent.com/zmy15/DeepSeek-for-VisualStudio/master/docs/images/zh_cn/7.png)

设置页按分组折叠展开（Agent、DeepSeek 设置、DeepSeek 自动补全、Language / 语言、OCR 设置、编辑器、
关于、开发者调试、联网搜索等），每一项均带中文说明。上例展示了 Agent 预算类设置
（工具调用总上限、最长墙钟时间、工具调用轮数上限、子 Agent 看门狗超时等）。

---

## 快捷键

| 快捷键 | 作用域 | 动作 |
|---|---|---|
| <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>D</kbd> | 全局 | 打开 / 聚焦 DeepSeek Chat 窗口 |
| <kbd>Ctrl</kbd>+<kbd>I</kbd> | 文本编辑器 | 内联 AI 编辑 |
| <kbd>Tab</kbd> / <kbd>Esc</kbd> | 文本编辑器（有建议时） | 接受 / 取消幽灵文本补全 |
| <kbd>Enter</kbd> / <kbd>Esc</kbd> | 内联编辑条 | 应用 / 取消修改 |

所有快捷键均可在 `工具 → 选项 → 环境 → 键盘` 中重新绑定（搜索 `DeepSeek`）。

---

## 构建与测试

**前置条件：** Visual Studio 2022（17.14+）并安装 *Visual Studio 扩展开发* 工作负载、
.NET SDK 8.0，以及 Git。

```powershell
git clone https://github.com/zmy15/DeepSeek-for-VisualStudio.git
cd DeepSeek-for-VisualStudio

dotnet restore
msbuild DeepSeek_v4_for_VisualStudio.csproj /t:Build /p:Configuration=Release /p:DeployExtension=false
```

VSIX 产物位于 `bin\Release\net472\DeepSeek_v4_for_VisualStudio.vsix`。
按 F5 会启动 Visual Studio 实验实例（`devenv.exe /RootSuffix Exp`）并自动部署扩展。

**测试** —— 160+ 个测试文件，覆盖模型、服务、编辑、设置与 i18n：

```powershell
dotnet test DeepSeek_v4_for_VisualStudio.Tests\DeepSeek_v4_for_VisualStudio.Tests.csproj --configuration Release

# 带覆盖率
dotnet test DeepSeek_v4_for_VisualStudio.Tests\DeepSeek_v4_for_VisualStudio.Tests.csproj `
  --configuration Release /p:CollectCoverage=true `
  /p:CoverletOutputFormat=cobertura /p:CoverletOutput=TestResults\coverage.cobertura.xml
```

**持续集成** —— GitHub Actions 依次执行还原 → 构建（Release）→ 带覆盖率测试 → 上传产物 → CJK 检查，
另有发布打包（标准版与 No-Local-OCR 版）与已合并分支清理工作流。

---

## 故障排查

**聊天窗口打不开** —— 最常见原因是 WebView2 或 Markdig 与其他扩展（尤其是 ReSharper）的版本冲突，
表现为 `XamlParseException` 或 `MissingMethodException`。扩展通过加载自带副本缓解此问题。
可尝试：升级扩展、确认已安装 WebView2 Evergreen 运行时、检查 `%LocalAppData%\DeepSeekVS\` 下的
`[DeepSeek AR]` 日志。

**设置不生效** —— 选项页与 VS 2026 统一设置通过桥接保持同步。若某项修改未生效，
请关闭并重新打开聊天窗口；大多数设置会在每次请求开始时读取。

### 常见问题

**API 密钥存在哪里？** 保存在 **Visual Studio 凭据存储**中，而非可导出的设置存储。
历史明文密钥会在首次运行时自动迁移。

**可以使用本地模型吗？** 可以，只要它暴露 OpenAI 兼容 API —— 设置 API 端点与自定义端点密钥即可。

**哪些模型支持视觉？** 视觉能力根据已配置的视觉模型名单**权威判定**，而非猜测。
截图类工具只会提供给视觉模型。

**支持 VS Code 或 Visual Studio for Mac 吗？** 不支持 —— 这是 Visual Studio（Windows）扩展。

**如何编写自己的技能？** 使用 `/create-skill`，或创建一个包含 `SKILL.md` 的目录，
其中写明 `name`、`description` 与流程章节。`/refresh-skills` 可在不重启的情况下重新扫描。

---

## 参与贡献

欢迎贡献。开始前请先阅读以下流程。

### 分支与流程

- 默认分支是 `master`，日常开发在 `dev` 分支进行，请不要直接向 `master` 推送。
- 从最新的 `dev` 切出特性分支，命名建议为 `feature/xxx`、`fix/xxx`、`docs/xxx`，完成后向 `dev` 发起 Pull Request。
- 只有发布时才由维护者把 `dev` 合并到 `master`。

### 提交 Pull Request 前

1. **先创建 Issue**：任何改动都要先在 [Issues](https://github.com/zmy15/DeepSeek-for-VisualStudio/issues/new/choose)
   建一个 Issue，说明背景、期望行为和大致的实现思路，等维护者确认方向后再开始编码。PR 描述中必须用
   `Fixes #123` 或 `Closes #123` 关联对应 Issue；没有对应 Issue 的 PR 会被要求先补 Issue。
2. **先同步再提交**：基于最新的 `dev` 变基或合并，避免出现冲突和无意义的合并提交。
3. **编译通过**：在 Release 配置下编译整个解决方案，确保没有新增警告或错误。
4. **跑通测试**：执行
   `dotnet test DeepSeek_v4_for_VisualStudio.Tests\DeepSeek_v4_for_VisualStudio.Tests.csproj --configuration Release`，
   新增逻辑请补充对应测试，不要降低现有覆盖率。
5. **遵守 i18n 约束**：面向用户的文案必须走本地化资源，不要在 `Services/` 和 `View/` 中新增硬编码中文字符串。
6. **保持改动聚焦**：一个 PR 只解决一件事，不要混入无关的格式化、重命名或依赖升级。

### PR 内容要求

- **标题**：遵循 Conventional Commits，例如 `feat(chat): 支持会话导出`、`fix(edit): 修复补丁匹配越界`。
- **描述**：说明背景与动机、具体改动、影响范围（是否影响设置项、快捷键、`SKILL.md` 或 MCP 行为），以及验证方式。
- **关联 Issue**：使用 `Fixes #123` 或 `Closes #123` 关联相关 Issue。
- **界面改动**：请附上截图或录屏，便于评审确认视觉效果。
- **兼容性说明**：涉及本地化、DeepSeek 官方 API、兼容端点或其他平台时应单独说明。
- **不要提交**：API Key、令牌、内部路径、公司代码以及 `bin/`、`obj/`、`TestResults/` 等构建产物。

### CI 与评审

PR 会自动触发 **Build & Test** 工作流（编译、单元测试并上传覆盖率报告、CJK 硬编码字符串检查），必须全部
通过才能合并。评审意见请通过追加提交处理，避免在评审过程中强制推送覆盖已有讨论。

---

## Star 历史

[![Star History Chart](https://api.star-history.com/svg?repos=zmy15/DeepSeek-for-VisualStudio&type=Date)](https://star-history.com/#zmy15/DeepSeek-for-VisualStudio&Date)

<!--
  star-history 服务不可用时的兜底方案：
  ![Star 增长曲线](docs/images/star-history.png)
  可在 https://star-history.com/#zmy15/DeepSeek-for-VisualStudio&Date 生成
-->

---

## 开源协议

[MIT](LICENSE) © 2026 zmy15

本项目与 DeepSeek 无隶属关系，也未获得其背书。DeepSeek 是其各自所有者的商标。
Visual Studio 是 Microsoft Corporation 的商标。