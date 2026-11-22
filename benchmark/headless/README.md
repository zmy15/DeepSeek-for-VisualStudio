# Headless Harness Runner

`BenchmarkRunner.exe` runs the existing Agent services without Visual Studio.
The runner is split by responsibility:

| File | Responsibility |
|---|---|
| `Program.cs` | Session orchestration and existing Agent service wiring |
| `HarnessProfile.cs` | Initial Agent, handoff depth, and task-size profile |
| `WorkspaceSnapshot.cs` | Final workspace snapshot embedded in the session ZIP |
| `UserSimulation.cs` | Dynamic multi-turn user messages for underspecified cases |
| `ExecutionEnvironment.cs` | Runtime parity inventory recorded in each archive |
| `HeadlessBuildService.cs` | CLI-only dotnet/CMake build backend |
| `HeadlessTestService.cs` | CLI-only dotnet/CTest test backend |
| `ToolchainProcess.cs` | Shared process execution and timeout layer |
| `QuestionPolicy.cs` | Clarification handling without fabricated user answers |
| `HeadlessToolAudit.cs` | End-to-end smoke checks for registered built-in tools |
| `ToolAuditWorkspace.cs` | Isolated workspace and fixtures for destructive checks |
| `ToolAuditReport.cs` | Markdown/JSON tool compatibility report |
| `HeadlessToolProxyClient.cs` | In-process MCP client used only by the benchmark |
| `HeadlessToolRouter.cs` | Benchmark tool override plus production fallback |
| `HeadlessToolBridge.cs` | Benchmark-owned tool dispatch |
| `HeadlessFileToolBridge.cs` | Benchmark-owned file, patch, symbol, and error tools |
| `HeadlessTerminalToolBridge.cs` | Benchmark-owned terminal execution and output |

Visual Studio and Headless share production Agent, API, prompt, handoff, tool
registry, context manager, prefix-cache manager, and Git-context code. There
are still deliberate service substitutions; see [PARITY.md](PARITY.md).

## Basic run

```powershell
.\benchmark\headless\bin\Debug\net472\BenchmarkRunner.exe `
  --case-dir .\benchmark\regression\artifacts\baseline\rgr-if-regular `
  --archive .\benchmark\regression\artifacts\baseline\archives\rgr-if-regular.zip `
  --transcript .\benchmark\regression\artifacts\baseline\transcripts\rgr-if-regular.json `
  --model deepseek-flash `
  --harness-profile .\benchmark\headless\profiles\ask-auto.json
```

Every new archive contains `workspace/` files. They are used by the hidden
oracle verifier; the transcript remains under `manifest.json` and `turns/`.

The Headless build and test implementations do not access DTE, VS Shell,
VS Error List, `BuildService`, or `TestService`. They invoke `dotnet`,
`cmake`, and `ctest` directly. The tool names exposed to the Agent remain
`build_solution` and `run_tests`, but their backend is CLI-only in Headless.

The extension source is not modified for benchmarking. Headless registers an
in-process MCP tool proxy before execution. It overrides VS-coupled operations
such as `get_errors`, symbol/comment tools, file editing, patches, terminal
execution, and `fetch_webpage`; other tools continue through the production
`BuiltInToolService`. Product schemas and Agent/loop behavior remain shared.

Self-check without an API key:

```powershell
.\benchmark\headless\bin\Debug\net472\BenchmarkRunner.exe `
  --self-test-toolchain `
  --case-dir .\benchmark\regression\artifacts\baseline\rgr-if-regular
```

Audit every built-in tool in an isolated copy:

```powershell
.\benchmark\headless\bin\Debug\net472\BenchmarkRunner.exe `
  --audit-tools `
  --case-dir .\benchmark\regression\artifacts\baseline\rgr-if-regular `
  --output .\benchmark\regression\artifacts\tool-audit.json
```

The audit runs search, read, symbol/comment fallbacks, skills, Git, terminal,
file edits, patches, build, tests, and an intentional compile failure followed
by `get_errors`. It classifies each tool as `works`, `headless_adapted`,
`requires_vs`, `requires_model`, `requires_desktop`, `disabled`, or `failed`.
The source case is never modified; destructive checks run in a temporary copy.

## Clarification requests

Fixed-script cases never auto-select a question option. If the Agent calls
`VisualStudio_askQuestions`, every answer is returned as `skipped=true`, and
an automatic Edit/Build handoff is blocked for that turn.

Dynamic cases with `user_simulation` use the separate simulator model to answer
from the public objective and persona only. The transcript records the
clarification policy summary under `turn.clarification`.

## Dynamic multi-turn cases

Prepared case metadata can include `user_simulation`:

```json
{
  "objective": "Converge on the user's actual requirement.",
  "persona": "A concise developer who answers focused questions.",
  "max_turns": 5
}
```

When present, the first prompt comes from the case. Later prompts are generated
by a separate model call that sees only the public objective and prior visible
messages, not the checks or reference answer:

```powershell
--user-sim-model deepseek-flash --max-turns 5
```

## Harness profiles

Profiles make the runner configuration an explicit experimental variable:

- `profiles/ask-auto.json`: Ask plus automatic handoffs.
- `profiles/ask-no-handoff.json`: Ask without automatic handoffs.
- `profiles/edit-direct.json`: Edit as the initial Agent.
- `profiles/ask-shallow-handoff.json`: Ask with a handoff depth limit of two.
