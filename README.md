# DeepSeek Chat for Visual Studio

> A DeepSeek AI coding assistant deeply integrated into Visual Studio 2022 (17.14+) and Visual Studio 2026 —
> combining conversational Q&A, multi-agent autonomous coding, inline code completion, and
> hunk-by-hunk reviewable diff editing.

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![VSIX](https://img.shields.io/badge/VSIX-1.3.1-success.svg)](source.extension.vsixmanifest)
[![Visual Studio](https://img.shields.io/badge/Visual%20Studio-2022%20%7C%202026-5C2D91.svg)](https://visualstudio.microsoft.com/)
[![.NET Framework](https://img.shields.io/badge/.NET%20Framework-4.7.2-512BD4.svg)](https://dotnet.microsoft.com/)
[![Stars](https://img.shields.io/github/stars/zmy15/DeepSeek-for-VisualStudio?style=flat&logo=github&label=Stars)](https://github.com/zmy15/DeepSeek-for-VisualStudio/stargazers)
[![Forks](https://img.shields.io/github/forks/zmy15/DeepSeek-for-VisualStudio?style=flat&logo=github&label=Forks)](https://github.com/zmy15/DeepSeek-for-VisualStudio/network/members)
[![Issues](https://img.shields.io/github/issues/zmy15/DeepSeek-for-VisualStudio?style=flat&logo=github&label=Issues)](https://github.com/zmy15/DeepSeek-for-VisualStudio/issues)

**English** · [简体中文](README.zh-CN.md)

---

## Table of Contents

- [Overview](#overview)
- [Screenshots](#screenshots)
- [Features](#features)
- [Requirements](#requirements)
- [Installation](#installation)
- [Getting Started](#getting-started)
- [Multi-Agent System](#multi-agent-system)
- [Built-in Tools](#built-in-tools)
- [Inline Code Completion](#inline-code-completion)
- [Inline Edit (Ctrl+I)](#inline-edit-ctrli)
- [Diff Review](#diff-review)
- [MCP Support](#mcp-support)
- [Memory & Skills](#memory--skills)
- [Settings Reference](#settings-reference)
- [Keyboard Shortcuts](#keyboard-shortcuts)
- [Building & Testing](#building--testing)
- [Troubleshooting](#troubleshooting)
- [Contributing](#contributing)
- [Star History](#star-history)
- [License](#license)

---

## Overview

**DeepSeek Chat for Visual Studio** is a VSIX extension that brings a full agentic coding assistant into the
Visual Studio IDE. It is not just a chat sidebar — it can read and search your solution, modify files, run builds
and tests, operate Git, and drive a browser, all under an approval model you control.

> If this extension saves you time, a ⭐ helps more people discover it — and it is real motivation to keep
> maintaining it.

| | |
|---|---|
| **Version** | 1.3.1 |
| **Target** | Visual Studio 2022 / 2026, `amd64`, `[17.14, )` |
| **Runtime** | .NET Framework 4.7.2, C# 14 |
| **License** | MIT |

The chat surface is a WPF shell hosting a WebView2 transcript renderer, with the HTML/CSS/JS generated entirely
from C#. It renders Markdown, syntax highlighting, KaTeX math, and Mermaid diagrams, and follows the Visual
Studio theme automatically.

---

## Screenshots

### Chat window — welcome state

![Chat window welcome state](https://raw.githubusercontent.com/zmy15/DeepSeek-for-VisualStudio/master/docs/images/en_us/1.png)

Empty conversation state: the DeepSeek greeting, noting it can explain code, fix bugs, refactor, generate tests,
and answer technical questions.

### Input area and status bar

![Input area and status bar](https://raw.githubusercontent.com/zmy15/DeepSeek-for-VisualStudio/master/docs/images/en_us/2.png)

The bottom input area and status bar: **MCP connected** (1 tool available), model picker
(`DeepSeek-V4.1-Flash`), the **Deep Think** toggle, reasoning effort (`high`), web search
(`DeepSeek Official`), a clear button, plus **Approval Mode** and **Balance**.

### Conversation with thinking process and tool calls

![Conversation with thinking process](https://raw.githubusercontent.com/zmy15/DeepSeek-for-VisualStudio/master/docs/images/en_us/3.png)

A collapsible **Thinking Process** block (8 tool calls executed, expandable to inspect the process), rendered
Markdown tables, inline code and code blocks, and a streaming answer.

### Context and cache statistics

![Context and cache statistics](https://raw.githubusercontent.com/zmy15/DeepSeek-for-VisualStudio/master/docs/images/en_us/4.png)

The statistics bar at the end of an answer: **92.2 %** prefix-cache hit rate (306,176 hits / 26,034 misses),
plus Prompt and Completion token usage.

### Session history

![Session history](https://raw.githubusercontent.com/zmy15/DeepSeek-for-VisualStudio/master/docs/images/en_us/10.png)

The session history list: titles are generated automatically by AI (e.g. "Demo handoff verification to Plan
agent", "DeepSeek VSIX Project Structure Analysis"), sorted newest first, each removable.

---

## Features

**Conversational** — streaming chat with collapsible *Thinking Process* blocks for reasoning-capable models;
Markdown, tables, syntax highlighting, KaTeX math, Mermaid diagrams (SVG export); multiple sessions with
AI-generated titles; message retry/edit/copy plus version and branch navigation; direct image viewing on
vision models.

**Agentic** — five specialised agents (Ask / Explore / Plan / Edit / Build) that hand work off to each other;
28 built-in tools plus any number of MCP tools; an autonomous tool loop that reads, searches, edits, builds,
tests, and iterates; up to 3 parallel Explore sub-agents; persistent memory; reusable skills.

**Editing** — inline code completion (ghost text) via a FIM endpoint; **Inline Edit (Ctrl+I)** to issue
natural-language edits directly; every AI change presented as a hunk-by-hunk reviewable diff; exact-string
replacement, multi-replace, whole-file rewrite, and `apply_patch`; automatic backups before destructive
operations.

**IDE integration** — a native dockable tool window following the VS theme; build and test through the VS SDK;
Error List integration; new files added to the project automatically; toast notifications.

**Platform** — a fully bilingual UI with 2,100+ localized strings (including system prompts); DeepSeek official
or any OpenAI-compatible endpoint with automatic detection of each vendor's reasoning-parameter dialect; local
OCR via PaddleOCR or the Windows built-in OCR engine.

---

## Requirements

| Component | Requirement |
|---|---|
| Visual Studio | 2022 version **17.14** or later, or Visual Studio 2026 |
| Edition | Community / Professional / Enterprise |
| Architecture | `amd64` (x64) |
| Workload | *.NET desktop development* or any workload including the **Visual Studio core editor** |
| OS | Windows 10 / 11 (x64) |
| Runtime | .NET Framework 4.7.2 (installed with Visual Studio) |
| WebView2 | Evergreen Runtime — a fixed-version runtime is bundled as a fallback |

> [!NOTE]
> Not available for Visual Studio for Mac or VS Code.

The default provider requires a DeepSeek API key ([platform.deepseek.com](https://platform.deepseek.com/));
a custom OpenAI-compatible endpoint can be configured instead.

---

## Installation

**From a release (recommended)**

1. Download the latest `DeepSeek_v4_for_VisualStudio-v<version>.vsix` from
   [Releases](https://github.com/zmy15/DeepSeek-for-VisualStudio/releases).
2. **Close Visual Studio.**
3. Double-click the `.vsix` and follow the VSIX Installer prompts.
4. Launch Visual Studio and press <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>D</kbd> to open the chat window.

> [!TIP]
> A **No-Local-OCR** variant is published for environments where the bundled PaddleOCR/OpenCV native libraries
> are undesirable. It is functionally identical apart from local OCR.

---

## Getting Started

1. **Set your API key and endpoint.** Open `Tools → Options → DeepSeek Chat → General` and choose one of the
   two connection modes under the **Model Settings** category:
   - **DeepSeek official models (default):** fill in `01. DeepSeek API Key` only and leave
     `02. API Endpoint (Base URL)` empty — the model list is fetched from the official catalog automatically.
   - **Custom OpenAI-compatible endpoint:** enter the endpoint URL in `02. API Endpoint (Base URL)`
     (for example `https://api.example.com/v1`) and its key in `03. API Key (Custom Endpoint)`, then add
     models under `05. Model Settings` (**+ Add model** → **Fetch from endpoint...**, or type the model names
     manually) and pick an entry suffixed with **(Custom)** in `06. Selected Model` or the chat dropdown.
     Setting the endpoint enables custom mode by default, so requests use that endpoint's key and models
     (you can still switch back to an official model in the chat dropdown).

   Use `04. Test Connection` to verify whichever endpoint, model, and key are currently active.
   Both keys are stored in **Visual Studio Credential Storage**, not in plain text in the settings store.
2. **Open the chat window.** Press <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>D</kbd>, or use
   `View → Other Windows → DeepSeek Chat`, or the toolbar button.
3. **Pick a model** from the dropdown at the bottom of the chat window — official models, or custom
   endpoint models suffixed with **(Custom)**.
4. **Ask something.** For example:
   - `Explain the architecture of this solution`
   - `Find all implementations of IDiffHost and describe how they differ`
   - `Add null-argument validation to every public method in Services/Utils, then build`
5. **Review the changes.** When the agent edits files, a diff preview appears — confirm before keeping or
   reverting.

Prefix a message with an `@` mention to bypass automatic routing:

```
@ask   Why does this method allocate on every call?
@plan  Migrate the data layer from Dapper to EF Core
@edit  Rename GetUserById to GetUserByIdAsync across the solution
@build Fix the 4 compile errors in the Reporting project
```

Typing `@` opens the **agent suggestion list**; typing `/` opens the **skill suggestion list**.

> **Screenshot placeholder** — first-run flow
> `docs/images/first-run.gif`
> Record: open Options → configure endpoint and key → Test Connection → open chat → first answer.

---

## Multi-Agent System

All requests enter through the **Ask Agent**, which hands off via the `request_handoff` tool whenever a task
exceeds its remit.

| Agent | Responsibility | Write access | Typical triggers |
|---|---|---|---|
| **Ask** | Technical Q&A, code explanation, design discussion. Default entry point and fallback. | ❌ Read-only | "how does X work", "explain this error" |
| **Explore** | Deep read-only codebase research, called by other agents, usually in parallel. | ❌ Read-only | Internal sub-agent, not user-routed |
| **Plan** | Research then produce a reviewable step-by-step implementation plan. | ❌ Read-only | Multi-file changes, architectural work, ≥3 files |
| **Edit** | Execute the plan: modify code step by step. | ✅ Full | Explicit code changes, git writes, terminal commands |
| **Build** | Diagnose and fix compilation/build errors after edits. | ✅ Full | Build failures, link errors |

```
User request
    │
    ▼
┌─────────┐   research    ┌───────────┐  (parallel, up to 3)
│  Ask    │──────────────▶│  Explore  │
│ (router)│               └───────────┘
└────┬────┘
     │ @plan / complex task
     ▼
┌─────────┐   plan ready    ┌─────────┐   step-by-step    ┌─────────┐
│  Plan   │───────────────▶│  Edit   │─────────────────▶│  Build  │
└─────────┘   (handoff)     └─────────┘   (verify)       └────┬────┘
                                                              │ build OK
                                                              ▼
                                                        ┌─────────┐
                                                        │  Ask    │  final summary
                                                        └─────────┘
```

### Plan confirmation and execution

![Plan confirmation and start](https://raw.githubusercontent.com/zmy15/DeepSeek-for-VisualStudio/master/docs/images/en_us/11.jpg)

The Plan Agent produces a step-by-step plan and waits for confirmation: each step names the files and key points
involved, and the plan is also written to `%LocalAppData%\DeepSeekVS\plans\plan.md`. Clicking
**Start Implementation** hands off to the Edit Agent, while the **steps pending** panel at the bottom tracks
progress (0/2) live.

The Ask Agent can also surface a structured question card when multiple-choice options are not enough to pin
down requirements:

![Structured question card](https://raw.githubusercontent.com/zmy15/DeepSeek-for-VisualStudio/master/docs/images/en_us/8.png)

---

## Built-in Tools

28 built-in tools. `🔒` marks tools available only to Edit/Build agents — read-only agents are blocked at
runtime, not merely by prompt.

| Group | Tools |
|---|---|
| **Read & explore** | `list_dir`, `read_file`, `file_search`, `grep_search`, `symbol_search`, `get_file_symbols`, `get_errors` |
| **Write & edit** 🔒 | `replace_string_in_file`, `multi_replace_string_in_file`, `create_file`, `delete_file`, `apply_patch`, `create_directory` |
| **Build, test, terminal** | `build_solution`, `run_tests`, `run_in_terminal`, `get_terminal_output` |
| **Web & vision** | `web_search`, `fetch_webpage`, `capture_window` 🔍, `capture_webpage` 🔍 |
| **Git** | `git` |
| **Memory, skills, interaction** | `memory`, `load_skill`, `read_skill_resource`, `runSubagent`, `request_handoff`, `VisualStudio_askQuestions` |

🔍 = vision models only. Highlights:

- **`symbol_search`** — the preferred tool for named symbols; searches the VS symbol index across the whole
  solution and beats text search for classes, methods, and properties.
- **`run_tests`** — builds **and** runs tests (.NET via VSTest, C++ via CMake/CTest) and returns structured
  results, so "run the tests" alone is a complete verification step.
- **`apply_patch`** — a custom diff format (`*** Begin Patch` / `*** End Patch`) supporting cross-file changes,
  with AI-assisted healing when a hunk cannot be applied cleanly.
- **`git`** — a broad surface; read-only operations are auto-approved, while dangerous ones (`push`,
  `reset --hard`, `branch --force --delete`) require approval.

### Approval model

| Mode | Behaviour |
|---|---|
| **AllowAll** | Auto-approve everything, no prompts. *Use with caution.* |
| **SmartBlock** *(default)* | Only dangerous commands require approval; safe operations pass through. |
| **BlockAll** | Every terminal command and file operation requires explicit approval. |

Terminal commands, `git` writes, file deletion, **access outside the workspace**, and edits to **project files**
(`.csproj`, `.vcxproj`, `.sln`, `.props`, `.targets`, `CMakeLists.txt`, `Makefile`, …) always route through
approval. Approval cards are presented serially — only one at a time — with an 800 ms watchdog.

![Terminal command approval card](https://raw.githubusercontent.com/zmy15/DeepSeek-for-VisualStudio/master/docs/images/en_us/9.png)

The card states the **purpose**, shows the exact command the AI wants to run, and offers **Allow** and **Skip** —
in the example above, a read-only command was judged safe, while commands that change system state prompt for
confirmation.

---

## Inline Code Completion

Ghost-text completion is attached to every code view by a MEF-exported `IVsTextViewCreationListener`. Edits are
debounced, context is gathered by walking enclosing spans outward, and a **fill-in-the-middle (FIM)** request is
sent to the `/beta/completions` endpoint (256 tokens, temperature 0) with a small LRU cache. Suggestions render
as grey ghost text using the editor's own font metrics: <kbd>Tab</kbd> accepts, <kbd>Esc</kbd> dismisses,
<kbd>Enter</kbd> restarts the timer.

![Inline code completion](https://raw.githubusercontent.com/zmy15/DeepSeek-for-VisualStudio/master/docs/images/en_us/13.gif)

---

## Inline Edit (Ctrl+I)

Place the caret in the editor, press <kbd>Ctrl</kbd>+<kbd>I</kbd>, and type an instruction in the floating bar.
The extension generates an edit, previews it as a diff, applies it with <kbd>Enter</kbd>, or cancels with
<kbd>Esc</kbd>. The binding is scoped to the text editor and does not interfere with the system incremental
search.

![Inline Edit](https://raw.githubusercontent.com/zmy15/DeepSeek-for-VisualStudio/master/docs/images/en_us/12.gif)

---

## Diff Review

Every AI change is a **proposal**, not a fait accompli. A single preflight pass runs across all files first —
if any file fails, nothing is written — then the batch commits sequentially with best-effort rollback on
mid-batch failure.

| Commit target | Used when | Behaviour |
|---|---|---|
| Open buffer | File is open in the editor | Wrapped in **one** `ITextUndoTransaction` named "Apply AI Edit", so a single <kbd>Ctrl</kbd>+<kbd>Z</kbd> undoes the whole edit. |
| Disk file | File is not open | SHA-256 preflight → backup → write preserving encoding → read back and verify. |
| New file | Newly created | Rejects an existing file, then adds it to the VS project. |
| Delete file | Deletion | Verifies the hash, backs up, then deletes — rollback restores the original. |

For long agent runs, changes accumulate in a staged workspace and commit together at the end
(**preview-then-commit**), with per-hunk accept/restore. Backups older than 14 days are swept at startup.

The change summary panel lists the files involved with added/removed line counts and offers
**Apply All** / **Revert All**:

![Change summary](https://raw.githubusercontent.com/zmy15/DeepSeek-for-VisualStudio/master/docs/images/en_us/5.png)

Once the diff preview is open you can switch between **Inline** and **Side by Side** views. The toolbar shows
added/removed statistics (e.g. +59 lines added / -0 lines removed), supports **Previous / Next** navigation
between hunks, and lets you decide **Keep Block** / **Revert Block** individually — or **Revert** / **Confirm**
the whole change:

![Inline diff review](https://raw.githubusercontent.com/zmy15/DeepSeek-for-VisualStudio/master/docs/images/en_us/6.png)

---

## MCP Support

The extension is an **MCP (Model Context Protocol) client**, so external tool servers can extend the agent.

- **Transports:** `stdio` (spawn a local process) and `http` (Streamable HTTP with SSE and `Last-Event-ID`
  resumption).
- **Configuration UI:** add/remove servers, set name, command, arguments, environment variables, enable/disable,
  and test connectivity.
- **Paste-JSON import:** auto-detects Claude Desktop, VS Code, and Cursor config formats.
- **Config storage:** `%LocalAppData%\DeepSeekVS\mcp_servers.json`.
- **Tool merging:** MCP and built-in tools merge into one toolset; on a name collision **MCP wins**.
- **Role-aware:** MCP tools are classified read/write by name pattern, so **Explore** receives read-like tools
  and **Edit**/**Build** receive write-like ones.
- **Resilience:** a single failed server is logged and skipped rather than aborting startup.

![MCP Support](https://raw.githubusercontent.com/zmy15/DeepSeek-for-VisualStudio/master/docs/images/en_us/14.png)

---

## Memory & Skills

**Memory** spans three scopes, accessed through the `memory` tool:

| Scope | Path | Lifetime | Use for |
|---|---|---|---|
| **User** | `/memories/` | Across all workspaces and sessions | Preferences, style, recurring patterns, corrections |
| **Session** | `/memories/session/` | Current conversation only | Task plans, in-progress notes, cross-turn state |
| **Repo** | `/memories/repo/` | Current solution | Build/test commands, conventions, architecture decisions |

**Skills** are reusable, model-invocable procedure packs — a directory containing `SKILL.md` plus optional
`scripts/`, `references/`, and `assets/`. Invoke one explicitly with `/skill-name`, let the model choose when
**Auto Skill Routing** is enabled, or accept a proactive suggestion. A `code-review` skill ships built in;
`/help` lists skills, `/refresh-skills` rescans, and `/create-skill` scaffolds a new one. Skills are discovered
from project-level `.github/skills/`, `.agents/skills/`, `.claude/skills/`, user-level directories, and the
extension's own `BuiltInSkills/`. `read_skill_resource` is path-traversal protected.

---

## Settings Reference

Access via `Tools → Options → DeepSeek Chat`. On VS 2026, non-sensitive settings also appear in the unified
settings UI, kept in two-way sync.

| Group | Key settings |
|---|---|
| **Connection** | API Key (in Credential Storage), Custom Endpoint Key, API Base URL, Model Catalog, Custom Model Name, Test Connection |
| **Model** | Selected Model, Enable Deep Thinking, Reasoning Effort (`low`/`high`/`max`), LLM Timeout |
| **Web & vision** | Enable Web Search, Search Provider, Web Fetch Mode, Baidu/Bing keys, OCR Engine |
| **Editing** | Show Diff Markers, Enable Autocomplete, Autocomplete Delay, Continue After Accept |
| **Context** | Token Budget (%), Auto Compression, Compression Threshold, Preserve Recent Turns, RAG, RAG Top K, Context Stats, IDE Context Injection |
| **Agent & safety** | Approval Mode, Max Tool Call Rounds, Max Repeated Same Call, Max Consecutive Errors, wall-clock/token/tool-call/depth budgets, No-Progress Rounds, Auto Build, Auto Skill Routing, Append Message Mode |
| **Interface** | Language, Theme Mode, Input Box Height, Bottom Area Scale, System Prompt, Restore Defaults, API Debug Dump |

![Settings page — Agent group](https://raw.githubusercontent.com/zmy15/DeepSeek-for-VisualStudio/master/docs/images/en_us/7.png)

The settings page is organized into collapsible groups (Agent, DeepSeek settings, DeepSeek autocomplete,
Language, OCR, Editor, About, Developer debugging, Web search, and more), each entry carrying a description.
The example above shows Agent budget settings (total tool-call cap, max wall-clock time, tool-call round limit,
sub-agent watchdog timeout, and so on).

---

## Keyboard Shortcuts

| Shortcut | Scope | Action |
|---|---|---|
| <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>D</kbd> | Global | Open / focus the DeepSeek Chat window |
| <kbd>Ctrl</kbd>+<kbd>I</kbd> | Text editor | Inline AI Edit |
| <kbd>Tab</kbd> / <kbd>Esc</kbd> | Text editor (with suggestion) | Accept / dismiss ghost-text completion |
| <kbd>Enter</kbd> / <kbd>Esc</kbd> | Inline edit bar | Apply / cancel the edit |

All bindings can be remapped under `Tools → Options → Environment → Keyboard` (search for `DeepSeek`).

---

## Building & Testing

**Prerequisites:** Visual Studio 2022 (17.14+) with the *Visual Studio extension development* workload,
.NET SDK 8.0, and Git.

```powershell
git clone https://github.com/zmy15/DeepSeek-for-VisualStudio.git
cd DeepSeek-for-VisualStudio

dotnet restore
msbuild DeepSeek_v4_for_VisualStudio.csproj /t:Build /p:Configuration=Release /p:DeployExtension=false
```

The VSIX is produced at `bin\Release\net472\DeepSeek_v4_for_VisualStudio.vsix`. Pressing F5 launches the
Visual Studio experimental instance (`devenv.exe /RootSuffix Exp`) with the extension deployed automatically.

**Tests** — 160+ test files covering models, services, editing, settings, and i18n:

```powershell
dotnet test DeepSeek_v4_for_VisualStudio.Tests\DeepSeek_v4_for_VisualStudio.Tests.csproj --configuration Release

# With coverage
dotnet test DeepSeek_v4_for_VisualStudio.Tests\DeepSeek_v4_for_VisualStudio.Tests.csproj `
  --configuration Release /p:CollectCoverage=true `
  /p:CoverletOutputFormat=cobertura /p:CoverletOutput=TestResults\coverage.cobertura.xml
```

**CI** — GitHub Actions runs restore → build (Release) → test with coverage → upload artifact → CJK string
check, plus release packaging (standard and No-Local-OCR) and merged-branch cleanup workflows.

---

## Troubleshooting

**The chat window will not open** — most often a WebView2 or Markdig version conflict with another extension
(notably ReSharper), surfacing as `XamlParseException` or `MissingMethodException`. The extension mitigates this
by loading its own bundled copies. Try: update the extension, confirm the WebView2 Evergreen Runtime is
installed, and check the `[DeepSeek AR]` entries under `%LocalAppData%\DeepSeekVS\`.

**Settings not applying** — the options page and the VS 2026 unified settings are kept in sync through a bridge.
If a change does not take effect, close and reopen the chat window; most settings are read at the start of each
request.

### FAQ

**Where is my API key stored?** In **Visual Studio Credential Storage**, not the exportable settings store.
Legacy plain-text keys migrate automatically on first run.

**Can I use a local model?** Yes, if it exposes an OpenAI-compatible API — set the API Base URL and the custom
endpoint key.

**Which models support vision?** Visual capability is determined authoritatively from the configured
vision-model list, not guessed. Screenshot tools are offered only to vision models.

**Supported on VS Code or Visual Studio for Mac?** No — this is a Visual Studio (Windows) extension.

**How do I write my own skill?** Use `/create-skill`, or create a directory containing a `SKILL.md` with `name`,
`description`, and procedure sections. `/refresh-skills` rescans without a restart.

---

## Contributing

Contributions are welcome. Please read the flow below before starting.

### Branches and Workflow

- The default branch is `master`; day-to-day development happens on `dev`. Please do not push directly to `master`.
- Branch off the latest `dev`, naming it `feature/xxx`, `fix/xxx`, or `docs/xxx`, and open a Pull Request against
  `dev` when done.
- Only the maintainer merges `dev` into `master`, and only when releasing.

### Before Submitting a Pull Request

1. **Create an Issue first**: every change must start with an Issue in
   [Issues](https://github.com/zmy15/DeepSeek-for-VisualStudio/issues/new/choose) describing the background, the
   expected behaviour, and your rough implementation approach. Wait for the maintainer to confirm the direction
   before you start coding. Your PR description must link the corresponding Issue with `Fixes #123` or
   `Closes #123`; PRs without a corresponding Issue will be asked to add one first.
2. **Sync before you submit**: rebase or merge onto the latest `dev` to avoid conflicts and meaningless merge
   commits.
3. **Make it compile**: build the whole solution in the Release configuration and make sure no new warnings or
   errors are introduced.
4. **Make the tests pass**: run
   `dotnet test DeepSeek_v4_for_VisualStudio.Tests\DeepSeek_v4_for_VisualStudio.Tests.csproj --configuration Release`.
   Add tests for new logic, and do not lower the existing coverage.
5. **Respect the i18n constraint**: user-facing text must go through localization resources. Do not add hard-coded
   Chinese string literals in `Services/` or `View/`.
6. **Keep the change focused**: one PR solves one thing. Do not mix in unrelated formatting, renames, or
   dependency upgrades.

### Pull Request Requirements

- **Title**: follow Conventional Commits, e.g. `feat(chat): support session export` or
  `fix(edit): fix patch matching out of bounds`.
- **Description**: explain the background and motivation, the concrete changes, the scope of impact (whether it
  affects settings, shortcuts, `SKILL.md`, or MCP behaviour), and how you verified it.
- **Linked Issue**: reference the relevant Issue with `Fixes #123` or `Closes #123`.
- **UI changes**: attach a screenshot or screen recording so reviewers can confirm the visual result.
- **Compatibility notes**: call out anything involving localization, the official DeepSeek API, compatible
  endpoints, or other platforms.
- **Do not commit**: API keys, tokens, internal paths, company code, or build artifacts such as `bin/`, `obj/`,
  and `TestResults/`.

### CI and Review

A PR automatically triggers the **Build & Test** workflow (compile, unit tests with an uploaded coverage report,
and the CJK hard-coded string check); everything must pass before it can be merged. Please address review comments
with follow-up commits rather than force-pushing over existing discussion.

---

## Star History

[![Star History Chart](https://api.star-history.com/svg?repos=zmy15/DeepSeek-for-VisualStudio&type=Date)](https://star-history.com/#zmy15/DeepSeek-for-VisualStudio&Date)

<!--
  Fallback if the star-history service is unavailable:
  ![Stars over time](docs/images/star-history.png)
  Generate at https://star-history.com/#zmy15/DeepSeek-for-VisualStudio&Date
-->

---

## License

[MIT](LICENSE) © 2026 zmy15

Not affiliated with or endorsed by DeepSeek. DeepSeek is a trademark of its respective owner.
Visual Studio is a trademark of Microsoft Corporation.