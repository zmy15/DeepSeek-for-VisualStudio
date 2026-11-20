# VS-Agent Evaluation

当前评测采用 **LLM-as-a-Judge** 路线，不再依赖预先编写 24 个 Fixture 任务。

主要文件：

- `judge/cases.v1.json`：24 个按八个质量维度组织的评测场景。
- `judge/rubrics.v1.json`：1-5 分 rubric、硬失败和输出契约。
- `judge/judge.py`：Python 校验与 LLM Judge 运行器。
- `judge/judge-prompt.md`：Python 调用 Judge 模型时使用的系统提示词。
- `judge/README.md`：从运行 Agent 到导出 JSON、再到 Judge 评分的完整流程。
- `test_judge.py`：不调用模型的 Python 单元测试。

聊天界面中的每条已完成助手回复提供 **“导出评测”** 按钮。导出文件默认位于：

```text
%LocalAppData%\DeepSeekVS\evaluations\
```

完整规程见 [judge/README.md](judge/README.md)。
