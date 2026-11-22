# Hidden Oracles

Oracle projects live outside every prepared Agent workspace. They receive only
the final workspace path and a case id.

## OrderDesk

`OrderDesk/OrderDesk.Oracle.csproj` references the final workspace's
`OrderDesk.Core.csproj`. `Program.cs` uses compile-time public types and
reflection for methods that the Agent must add.

## LineController

`LineController/CMakeLists.txt` accepts `WORKSPACE_ROOT`, compiles the final
workspace sources, and links them into `oracle_tests.cpp`.

## Adding an oracle case

1. Add the case id to the relevant suite's `*.oracle.v1.json`.
2. Declare `read_only`, `allowed_changes`, and `forbidden_changes`.
3. Add deterministic behavior checks to the oracle program.
4. Verify the unmodified fixture fails and a known-good implementation passes.

Do not copy oracle checks into the Agent workspace.
