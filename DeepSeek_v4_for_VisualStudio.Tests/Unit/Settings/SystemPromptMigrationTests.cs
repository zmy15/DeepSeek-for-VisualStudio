using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Settings;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Settings;

public class SystemPromptMigrationTests
{
    [Fact]
    public void LegacyChineseDefaultPrompt_IsDetected()
    {
        const string legacy =
            "你是 DeepSeek Chat，一个深度集成在 Visual Studio 中的 AI 编程助手。你的核心能力包括：解释代码逻辑、定位并修复 Bug、重构优化代码、生成单元测试、回答各类技术问题。请遵循以下准则：\r\n" +
            "- 回答应简洁、准确、直接，优先给出可运行的代码方案。\r\n" +
            "- 涉及代码修改时，明确指出文件路径和具体行号。\r\n" +
            "- 优先使用用户项目已有的框架和库，不引入不必要的依赖。\r\n" +
            "- 如果用户的问题模糊不清，先追问澄清再给出建议。\r\n" +
            "- 使用中文回答，代码中的注释也使用中文。\r\n" +
            "- 当用户需要获取实时信息、操作文件系统或执行特定任务时，积极使用可用的工具（tools）来完成任务。\r\n";

        DeepSeekOptionsPage.IsLegacyDefaultSystemPrompt(legacy).Should().BeTrue();
    }

    [Fact]
    public void CurrentOrCustomPrompt_IsNotOverwritten()
    {
        DeepSeekOptionsPage.IsLegacyDefaultSystemPrompt(AiPrompts.DefaultSystemPrompt)
            .Should().BeFalse();
        DeepSeekOptionsPage.IsLegacyDefaultSystemPrompt("我的自定义提示词")
            .Should().BeFalse();
    }
}
