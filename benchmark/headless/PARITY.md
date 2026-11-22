# VS and Headless Parity

The benchmark intentionally reuses production Agent classes, prompts, handoff
logic, and `DeepSeekApiService`. It is not a pixel-for-pixel replay of the
Visual Studio process.

## Shared production code

- `DeepSeekApiService`
- `AgentFactory`
- `AskAgent`, `ExploreAgent`, `PlanAgent`, `EditAgent`, `BuildAgent`
- `BaseAgent.ExecuteAsync` and `ExecuteHandoffAsync`
- `BuiltInToolService` and production tool definitions
- `ConversationContextManager`
- `PrefixCacheManager`
- `GitContextProvider`
- production `SkillService`

## Deliberate substitutions

| Area | Visual Studio | Headless |
|---|---|---|
| IDE context | active file, selection, diagnostics, cursor, working set | workspace/solution plus Git context |
| Build | VS `BuildService` / DTE / VS installation environment | `HeadlessBuildService` using `dotnet build` or `cmake` only |
| Tests | VS `TestService` | `HeadlessTestService` using `dotnet test/run` or `ctest` only |
| MCP | user-configured MCP servers | disabled |
| Web search | user setting | disabled |
| Memory | user/session/repo memory | disabled |
| Permissions | interactive UI approval | workspace allowlist policy |
| Workflow | UI routing, chainback, plan panels, events | headless Agent/handoff loop |
| Thinking | user setting | profile `thinking_enabled` and `thinking_effort` |
| Clarifications | interactive user answer | fixed cases skip; simulated cases answer through `QuestionPolicy` |

Every new archive records these fields under
`manifest.execution_environment`. A result that requires MCP, memory, web
search, or active-editor context is not a direct VS-equivalent result.
