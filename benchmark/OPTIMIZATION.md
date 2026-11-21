# Benchmark Cost and Cache Optimization

## Observed baseline

2026-11-21 的完整测评产生了约：

```text
Input total:        16,000,572
Cache hit input:     7,949,824  (49.7%)
Cache miss input:    6,327,444  (39.5%)
Output:              1,723,304
```

Agent 会话本身的缓存并不差：16 个 OrderDesk session 的平均缓存命中率为
**84.9%**，范围约 **81.1% - 90.6%**。低命中主要来自重复执行 Judge：

- 每次重评都会重新发送大量证据；
- turn Judge 和 session Judge 中间又经历了修复和重复重试；
- session Judge 原先把完整 round flow、manifest 和轮次结果重复序列化；
- DeepSeek Judge 请求默认可进入 thinking，单次 session 输出曾达到 **16,817 tokens**。

## Optimization

### 1. Use low Judge thinking by default

DeepSeek Judge 默认发送：

```json
{"thinking":{"type":"enabled"},"reasoning_effort":"low"}
```

需要更强多步推理时可使用 `--thinking-effort high` 或 `max`。
需要最低成本时使用 `--no-thinking`。

### 2. Compact evidence

Turn Judge 不再接收以下重复字段：

```text
conversation.full_api_history
conversation.before_turn
conversation.turn_messages
```

保留：

```text
current turn user/assistant
tool_trace with bounded results
context_debug tokens
metrics / session telemetry
flow analysis
```

Session Judge 只接收压缩后的轮次结果、flow summary、completion、permissions、
loops、cost 和 cache。

### 3. Cache-friendly scheduling

- `regression.py score` 默认 `--parallel 1`；
- `score` 先用 2 个极小请求预热 turn/session 两类稳定 system+rubric 前缀；
- 同一 case 的第 1 轮先建立稳定前缀，第 2 轮复用前缀；
- 只有在大规模批量测评、更看重吞吐时才提高并行数。

### 4. Avoid redundant evaluation

- Judge 规则变化时，通过 `--skip-existing` 复用已完成结果；
- Judge 结果使用内容寻址缓存；archive、case/参考答案、Judge 提示词/rubric、
  `judge.py` 和模型都不变时，即使换输出目录也不会再次调用 API；
- Agent 代码未变化时，只重评已有 session archive，不重跑 Agent；
- 先用 `validate` / `--dry-run`，再调用模型；
- 成本结果写入 `regression-run.json` 的 `agent_usage` 和 `judge_usage`。

## Measured result on one representative case

Case: `rgr-cg-regular`

| Metric | Before | After | Change |
|---|---:|---:|---:|
| Judge prompt tokens | 62,568 | 32,157 | -48.6% |
| Judge completion tokens | 27,839 | 5,982 | -78.5% |
| Judge total tokens | 90,407 | 38,139 | -57.8% |

`rgr-if-regular` 的两轮 Judge 请求字符数从 **86,928** 降到 **45,527**，
减少约 **47.6%**。

启用 prefix warmup 后，`rgr-cg-boundary` 的实测结果：

| Metric | Before | After | Change |
|---|---:|---:|---:|
| Judge prompt tokens | 52,967 | 31,368 | -40.8% |
| Judge completion tokens | 40,420 | 16,740 | -58.6% |
| Judge total tokens | 93,387 | 48,108 | -48.5% |
| Turn 1 cache hit rate | 约 20% | 43.6% | +23.6pp |
| Turn 2 cache hit rate | 约 20% | 98.0% | +78pp |

预热调用本身每次只请求最多 32 output tokens。即使加上 warmup，总成本仍显著
低于旧流程。

以上数据使用默认的 `thinking=low`。若临时使用 `--no-thinking`，同一 case
可进一步降到约 **36,963 tokens**（-60.4%），但默认按产品要求保留 low 档位思考。

## Recommended operating policy

```powershell
.\benchmark\.venv\Scripts\python.exe .\benchmark\regression\regression.py score `
  --archives <archives> `
  --results <results> `
  --model deepseek-flash `
  --parallel 1 `
  --skip-existing
```

只有 Agent runtime、系统提示词、工具行为或模型发生变化时，才需要重新运行
Agent。仅调整 rubric、参考正确答案或 Judge 逻辑时，应复用 archive 重新 Judge。
