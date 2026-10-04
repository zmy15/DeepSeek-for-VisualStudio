<div align="center">


# DeepSeek for Visual Studio

**An AI coding assistant for Visual Studio 2022 and 2026**

[![License](https://img.shields.io/badge/license-MIT-blue)](https://github.com/zmy15/DeepSeek-for-VisualStudio/blob/master/LICENSE)
[![Visual Studio](https://img.shields.io/badge/Visual%20Studio-2022%20%7C%202026-purple)]()
[![.NET](https://img.shields.io/badge/.NET%20Framework-4.7.2-blueviolet)]()
[![Platform](https://img.shields.io/badge/platform-Windows%20x64%20%2F%20ARM64-lightgrey)]()
[![Version](https://img.shields.io/badge/version-1.2.6-blue)]()
[![GitHub Stars](https://img.shields.io/github/stars/zmy15/DeepSeek-for-VisualStudio?style=social)](https://github.com/zmy15/DeepSeek-for-VisualStudio)

[简体中文](https://github.com/zmy15/DeepSeek-for-VisualStudio/blob/master/README.zh-CN.md)

</div>

## Overview

DeepSeek for Visual Studio brings AI chat, code editing, solution-aware tools, terminal execution, and multimodal understanding directly into the IDE. It is designed for Visual Studio 2022 (17.14 or later) and Visual Studio 2026. Beyond the official DeepSeek API, it also supports any OpenAI-compatible `chat/completions` endpoint configured with a custom Base URL.

The extension combines a native-grade WebView2 chat experience with five cooperating agents, reusable Skills, MCP tool servers, Ghost Text completion, and persistent project memory.

> ⭐If this project helps you, please consider giving it a Star — it helps more developers discover it.

## Screenshots

<table align="center">
  <tr>
    <td align="center" valign="top" width="50%">
      <img src="https://raw.githubusercontent.com/zmy15/DeepSeek-for-VisualStudio/master/docs/images/1.png" width="360" alt="DeepSeek Chat panel" />
      <br />
      <sub>Chat panel — model selection, Deep Think, approval mode, and MCP status</sub>
    </td>
    <td align="center" valign="top" width="50%">
      <img src="https://raw.githubusercontent.com/zmy15/DeepSeek-for-VisualStudio/master/docs/images/3.png" width="360" alt="Thinking process and tool calls" />
      <br />
      <sub>Deep reasoning, tool calls, and live session context usage</sub>
    </td>
  </tr>
</table>

<p align="center">
  <img src="https://raw.githubusercontent.com/zmy15/DeepSeek-for-VisualStudio/master/docs/images/5.png" width="720" alt="Inline AI edit" />
  <br />
  <sub>Inline AI edit — Enter to apply / Esc to cancel</sub>
</p>

## Project Highlights

| Highlight | Why it matters |
|---|---|
| **Five-agent workflow** | Ask, Explore, Plan, Edit, and Build collaborate through the Handoff protocol instead of forcing manual mode switching. |
| **IDE-native interaction** | Chat, inline editing, diff preview, build diagnostics, terminal commands, and Git operations stay inside Visual Studio. |
| **Reliable code changes** | Patch editing, four-tier matching, Healing repair, and per-hunk diff confirmation reduce accidental edits. |
| **Long-context reasoning** | A 900K token budget, Deep Reasoning, and automatic LLM summarization support large solutions. |
| **Multimodal understanding** | Images, screenshots, PDFs, webpage content, and OCR results can be reasoned about directly. |
| **Extensible tools** | Markdown-based Skills and MCP servers add custom workflows and external tools without changing the extension core. |
| **Safety controls** | Terminal approval modes and user/session/repository memory keep automation useful and controllable. |

## Core Features

- **Chat and reasoning**: streaming responses, Deep Reasoning, Pro/Flash models, resumable streaming, and Prefix Cache.
- **OpenAI-compatible endpoints**: connect any `chat/completions`-compatible service through a custom Base URL.
- **Code editing**: inline AI edit, exact/multi replacement, `apply_patch`, file creation, diff preview, and Ghost Text completion.
- **Project tools**: solution exploration, file parsing, terminal commands, Git operations, and build verification.
- **Skills system**: reusable workflows defined in `SKILL.md`, triggered by `/skillname` or semantic matching.
- **MCP integration**: connect HTTP and stdio tool servers and classify their tools for agents.
- **Multimodal AI**: image understanding, window screenshots, page-by-page PDF reading, web image reading, and OCR.
- **Search**: Baidu Qianfan and DuckDuckGo with automatic fallback.
- **Memory**: persistent user, session, and repository notes injected into new conversations.
- **Internationalization**: automatic Chinese/English UI switching with custom translation overrides.

## Requirements

| Component | Requirement |
|---|---|
| Visual Studio | **2022 (17.14+)** or **2026** |
| Operating system | Windows 10/11 |
| Architectures | x64; ARM64 for the No-Local-OCR package |

## Installation

Download the latest `.vsix` from [Releases](https://github.com/zmy15/DeepSeek-for-VisualStudio/releases), close Visual Studio, and install it.

- **Full package**: x64 only, includes local OCR.
- **No-Local-OCR package**: x64 and ARM64; local OCR is removed and other features remain available.

To build from source, install the **.NET Framework 4.7.2 SDK** and the **Visual Studio extension development** workload:

```powershell
git clone https://github.com/zmy15/DeepSeek-for-VisualStudio.git
```

Open `.slnx` in Visual Studio, build the solution, and press `F5` to launch the experimental instance.

## Quick Start

1. Get an API key from [platform.deepseek.com/api_keys](https://platform.deepseek.com/api_keys).
2. Open `Tools > Options > DeepSeek Chat`, enter the API key, and select a model.
3. Open `View > Other Windows > DeepSeek Chat`.
4. Ask a question, select code and press `Ctrl+I` for inline editing, or dispatch an agent command.

Recommended starting settings:

| Setting | Value |
|---|---|
| Model | Choose from the list returned by `/models` |
| Deep Reasoning | Enabled, effort `high` |
| Token budget | `90%` |
| Vision models | Mark multimodal models in the Vision Models picker |

| Shortcut | Action |
|---|---|
| `Ctrl+Shift+D` | Open or focus the chat window |
| `Ctrl+I` | Inline AI edit |
| `Enter` | Send chat message |
| `Ctrl+Enter` | Insert a newline |

## Agents and Skills

| Agent | Responsibility |
|---|---|
| **Ask** | Answer questions, explain code, and summarize results. |
| **Explore** | Search the repository and analyze project structure. |
| **Plan** | Decompose tasks, design changes, and coordinate subagents. |
| **Edit** | Apply code and file changes with confirmation and review. |
| **Build** | Run builds, inspect diagnostics, and repair errors. |

Use `@ask`, `@plan`, `@edit`, or `@build` to select an agent explicitly, or let Handoff route the task automatically. Skills are loaded from project-level directories, user-level directories, and built-in skills.

## TODO

| Item | Description | Priority |
|---|---|---|
| **Code knowledge graph** | AST-based relationships and semantic navigation across classes and methods. | Medium |
| **More built-in skills** | Debug analyzer, SQL optimizer, API design, and other workflows. | Medium |
| **Session export** | Export conversations as Markdown, PDF, or HTML. | Low |
| **More UI languages** | Japanese, Korean, and additional locales. | Medium |

## Contributing

### Branches and workflow

- The default branch is `master`; day-to-day development happens on `dev`, so please do not push to `master` directly.
- Branch off the latest `dev` using a `feature/xxx`, `fix/xxx`, or `docs/xxx` name, then open a Pull Request against `dev`.
- `dev` is merged into `master` by the maintainers at release time only.

### Before opening a Pull Request

1. **Open an Issue first**: every change should start with an [Issue](https://github.com/zmy15/DeepSeek-for-VisualStudio/issues/new/choose) describing the background, expected behavior, and rough implementation approach. Wait for the maintainers to confirm the direction before you start coding. The PR description must link the Issue with `Fixes #123` or `Closes #123`; PRs without a corresponding Issue will be asked to file one first.
2. **Sync before you submit**: rebase or merge onto the latest `dev` to avoid conflicts and meaningless merge commits.
3. **Build cleanly**: build the whole solution in the Release configuration and make sure you add no new warnings or errors.
4. **Pass the tests**: run `dotnet test DeepSeek_v4_for_VisualStudio.Tests\DeepSeek_v4_for_VisualStudio.Tests.csproj --configuration Release`. Add tests for new logic and do not lower existing coverage.
5. **Respect the i18n constraint**: user-facing text must go through localization resources; do not add hardcoded Chinese string literals in `Services/` or `View/`.
6. **Keep the change focused**: one PR solves one problem — do not mix in unrelated formatting, renames, or dependency upgrades.

### What a PR should contain

- **Title**: follow Conventional Commits, e.g. `feat(chat): support session export` or `fix(edit): fix out-of-range patch matching`.
- **Description**: explain the background and motivation, the concrete changes, the blast radius (whether settings, shortcuts, `SKILL.md`, or MCP behavior are affected), and how you verified it.
- **Linked Issue**: reference the Issue with `Fixes #123` or `Closes #123`.
- **UI changes**: attach a screenshot or screen recording so reviewers can confirm the visual result.
- **Compatibility notes**: call out anything involving localization, the official DeepSeek API, compatible endpoints, or other platforms.
- **Do not commit**: API keys, tokens, internal paths, proprietary code, or build output such as `bin/`, `obj/`, and `TestResults/`.

### CI and review

Pull Requests automatically trigger the **Build & Test** workflow (build, unit tests with an uploaded coverage report, and the hardcoded-CJK string check), and all checks must pass before merging. Address review feedback with follow-up commits rather than force-pushing over existing discussion.

## Support

- **Usage questions and discussions**: [Discussions](https://github.com/zmy15/DeepSeek-for-VisualStudio/discussions)
- **Bug reports and feature requests**: [Issues](https://github.com/zmy15/DeepSeek-for-VisualStudio/issues)

## Contributors

<a href="https://github.com/zmy15/DeepSeek-for-VisualStudio/graphs/contributors">
  <img src="https://contrib.rocks/image?repo=zmy15/DeepSeek-for-VisualStudio" />
</a>

## Star History

<a href="https://www.star-history.com/?type=date&repos=zmy15%2FDeepSeek-for-VisualStudio">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="https://api.star-history.com/chart?repos=zmy15/DeepSeek-for-VisualStudio&type=date&theme=dark&legend=top-left" />
    <source media="(prefers-color-scheme: light)" srcset="https://api.star-history.com/chart?repos=zmy15/DeepSeek-for-VisualStudio&type=date&theme=light&legend=top-left" />
    <img alt="Star History Chart" src="https://api.star-history.com/chart?repos=zmy15/DeepSeek-for-VisualStudio&type=date&legend=top-left" />
  </picture>
</a>

## License

[MIT](https://github.com/zmy15/DeepSeek-for-VisualStudio/blob/master/LICENSE) © 2026 zmy15
