# VS-Agent Evaluation

当前评测采用 **LLM-as-a-Judge** 路线，不再依赖预先编写 24 个 Fixture 任务。

主要文件：

- `judge/general-case.json`：默认通用总体 case。
- `judge/general-rubric.v1.json`：12 维完整流程 rubric。
- `judge/general-prompt.md`：分阶段流程 Judge 提示词。
- `judge/general-session-case.json`：多轮会话综合 case。
- `judge/session-rubric.v1.json`：会话整体评分标准。
- `judge/session-prompt.md`：会话综合 Judge 提示词。
- `judge/cases.v1.json`：24 个按八个质量维度组织的评测场景。
- `judge/rubrics.v1.json`：1-5 分 rubric、硬失败和输出契约。
- `judge/judge.py`：Python 校验与 LLM Judge 运行器。
- `judge/judge-prompt.md`：Python 调用 Judge 模型时使用的系统提示词。
- `judge/README.md`：从运行 Agent 到导出 JSON、再到 Judge 评分的完整流程。
- `test_judge.py`：不调用模型的 Python 单元测试。
- `regression/`：带示例项目、多轮提问、baseline/candidate 对比的回归测评。
- `headless/`：直接调用现有 Agent/API/工具服务、无需 Visual Studio UI 的 runner。

回归测评默认 Agent 与 Judge 均使用 `deepseek-flash`，包含 16 个 case、32 个连续提问，八个现有维度各覆盖一个常规任务和一个条件/边界任务。完整流程见
[regression/README.md](regression/README.md)。

C++ 方向另外提供 `LineController` CMake fixture：8 个 case 覆盖八个现有维度，
支持 CMake/Ninja 构建与 CTest，参考答案见
`regression/line-controller-reference-answers.v1.json`。

聊天窗口顶部会话工具栏提供 **“导出会话评测”** 图标。导出 ZIP 默认位于：

```text
%LocalAppData%\DeepSeekVS\evaluations\
```

完整规程见 [judge/README.md](judge/README.md)。

默认评测命令：

```powershell
.\benchmark\.venv\Scripts\python.exe .\benchmark\judge\judge.py session `
  --archive "$env:LOCALAPPDATA\DeepSeekVS\evaluations\<session>.zip"
```

回归对比命令：

```powershell
.\benchmark\.venv\Scripts\python.exe .\benchmark\regression\regression.py compare `
  --baseline .\benchmark\regression\artifacts\baseline\results\regression-run.json `
  --candidate .\benchmark\regression\artifacts\candidate\results\regression-run.json
```
