# Fixture-Based Agent Regression

这个回归集用同一个可编译的小型示例项目，让 Agent 从干净工作区执行一组固定提问，再比较改动前后的 Judge 结果。它补足了原有通用 Judge 的空白：原有流程能评价一次真实会话，但不能保证两次会话使用相同项目、相同任务和相同模型。

## 覆盖范围

`orderdesk.v1.json` 包含 16 个 case、32 个连续提问，覆盖现有八个维度：

```text
instruction_following
context_grounding
tool_use
planning_execution
code_change_quality
safety_permission
communication_ux
efficiency_cost
```

每个维度都有一个常规 case 和一个条件/边界 case。题目包括：

- 最小范围修复、只读分析、严格输出格式；
- 折后金额与免运费阈值、`>=` 比较边界、整除与非整除分页；
- 先读失败证据再修复、测试后必须重新验证；
- 源码提示注入、构建产物清理与工作区边界；
- 简单任务避免过度探索、复用证据避免重复读取。

正确性准则和每题参考答案见 [CORRECTNESS.md](CORRECTNESS.md) 与
[reference-answers.v1.json](reference-answers.v1.json)。参考答案会随 case
一起传入 Judge，不用模型自行猜测“什么算正确”。

示例项目位于 `fixtures/OrderDesk/`，不依赖 NuGet 包：

```powershell
dotnet build .\OrderDesk.slnx
dotnet run --project .\tests\OrderDesk.Tests\OrderDesk.Tests.csproj
```

## 默认模型

套件默认使用：

```text
Agent model: deepseek-flash
Judge model: deepseek-flash
```

`regression.py score` 的 `--model` 默认也是 `deepseek-flash`。如果 Agent 端选择的不是 `deepseek-flash`，`collect` 和 `score` 会拒绝混合结果，除非显式传入 `--allow-model-mismatch`。

每个 case 会生成 `<case-id>.case.json`，并通过现有 session Judge 的
`--case-file` 传入场景、逐轮验收点和禁止项。Judge 不只看通用流程，还会检查该题的业务边界。

Judge API 仍使用现有的环境变量：

```powershell
$env:JUDGE_API_KEY = "<your-api-key>"
$env:JUDGE_BASE_URL = "https://api.deepseek.com"
```

## 1. 校验与准备

从仓库根目录执行：

```powershell
.\benchmark\.venv\Scripts\python.exe .\benchmark\regression\regression.py validate
.\benchmark\.venv\Scripts\python.exe .\benchmark\regression\regression.py prepare `
  --case rgr-cq-regular `
  --output .\benchmark\regression\artifacts\baseline\rgr-cq-regular
```

`prepare` 会复制整个 `OrderDesk` fixture，并在工作区**外部**生成：

```text
<case>.benchmark-meta/README.md   本次 case 的场景、逐轮提问、检查和验证命令
<case>.benchmark-meta/case.json   机器可读的 case 快照
```

元数据不放在工作区内，避免 Agent 通过 `file_search`/`grep_search` 读到预期答案。每次都必须从新目录开始，不能复用上一次 Agent 改过的项目。

## 2. 让 Agent 运行 baseline

1. 在 Visual Studio 打开复制目录中的 `OrderDesk.slnx`。
2. 选择 `deepseek-flash`。
3. 按工作区同级 `.benchmark-meta/README.md` 的 Turn 1、Turn 2 顺序发送提问。
4. 等待整个会话结束，不要中途清空会话。
5. 导出完整会话 ZIP，保存为：

```text
benchmark\regression\artifacts\baseline\archives\<case-id>.zip
```

对套件中的 16 个 case 各自重复一次。建议先跑 baseline，再改扩展代码；不要用旧 fixture 上的新会话充当 baseline。

## 无界面直跑现有 Agent 接口

`headless/BenchmarkRunner.csproj` 直接引用项目，实际调用现有的
`DeepSeekApiService`、`AgentFactory`、五种 Agent、`ConversationContextManager`
和内置工具服务，不启动 Visual Studio UI。

```powershell
dotnet build .\benchmark\headless\BenchmarkRunner.csproj

$env:DEEPSEEK_API_KEY = "<your-api-key>"
.\benchmark\headless\bin\Debug\net472\BenchmarkRunner.exe `
  --case-dir .\benchmark\regression\artifacts\baseline\rgr-if-regular `
  --archive .\benchmark\regression\artifacts\baseline\archives\rgr-if-regular.zip `
  --transcript .\benchmark\regression\artifacts\baseline\transcripts\rgr-if-regular.json `
  --model deepseek-flash `
  --base-url https://api.deepseek.com
```

runner 会：

- 按 case 的工作区执行全部轮次；
- 自动跟进 `AutoSend` Handoff；
- 自动批准工作区内 `file_write`，拒绝删除、终端和联网权限；
- 调用现有 `IBuildService`/`ITestService` 的本地 `dotnet` 实现；
- 将完整轮次、工具轨迹和指标导出成现有 Judge 可读取的 session ZIP。

## 3. 评分并汇总 baseline

逐 case 调用现有 `judge.py session`，然后汇总：

```powershell
.\benchmark\.venv\Scripts\python.exe .\benchmark\regression\regression.py score `
  --archives .\benchmark\regression\artifacts\baseline\archives `
  --results .\benchmark\regression\artifacts\baseline\results
```

输出包括：

```text
baseline\results\<case-id>.judge.json
baseline\results\<case-id>.case.json
baseline\results\regression-run.json
```

`score` 默认并发执行每个 case 内的轮次评分；可先用 `--dry-run` 只校验 ZIP 和请求结构：

```powershell
.\benchmark\.venv\Scripts\python.exe .\benchmark\regression\regression.py score `
  --archives .\benchmark\regression\artifacts\baseline\archives `
  --results .\benchmark\regression\artifacts\baseline\results `
  --dry-run
```

## 4. 运行 candidate

修改扩展代码后，用同一套 16 个 case、同一 Agent 模型和同一 Judge 模型重新准备并运行：

```text
benchmark\regression\artifacts\candidate\archives\<case-id>.zip
```

然后执行：

```powershell
.\benchmark\.venv\Scripts\python.exe .\benchmark\regression\regression.py score `
  --archives .\benchmark\regression\artifacts\candidate\archives `
  --results .\benchmark\regression\artifacts\candidate\results
```

## 5. 回归门禁

```powershell
.\benchmark\.venv\Scripts\python.exe .\benchmark\regression\regression.py compare `
  --baseline .\benchmark\regression\artifacts\baseline\results\regression-run.json `
  --candidate .\benchmark\regression\artifacts\candidate\results\regression-run.json `
  --output .\benchmark\regression\artifacts\comparison.json
```

默认门禁规则：

- case 的 `pass / partial / fail` 等级不能下降；
- 单个轮次的等级不能下降；
- 新增硬失败直接判回退；
- suite 的平均分下降不能超过 `0.1`；
- 单 case、单轮、单维度或维度平均分下降不能超过 `0.1`。

需要严格零容忍时：

```powershell
.\benchmark\.venv\Scripts\python.exe .\benchmark\regression\regression.py compare `
  --baseline .\benchmark\regression\artifacts\baseline\results\regression-run.json `
  --candidate .\benchmark\regression\artifacts\candidate\results\regression-run.json `
  --max-score-drop 0 `
  --max-dimension-drop 0
```

退出码：

```text
0  无回退
2  suite、结果或模型不匹配
3  检测到回退
```

## 增加 case

在 `orderdesk.v1.json` 的 `cases` 中添加对象，保持以下约束：

- `dimension` 必须属于现有八个维度；
- `kind` 为 `regular` 或 `boundary`；
- 每个维度至少一个 regular 和一个 boundary；
- 每个 case 至少一个 turn，每个 turn 至少一条 `checks.must` 或 `checks.must_not`；
- 提供真实可执行的 `verification` 命令。

新增前后都执行：

```powershell
.\benchmark\.venv\Scripts\python.exe .\benchmark\regression\regression.py validate
.\benchmark\.venv\Scripts\python.exe -m unittest discover `
  -s .\benchmark\regression `
  -p "test_*.py"
```
