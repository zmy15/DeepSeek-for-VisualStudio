using DeepSeek_v4_for_VisualStudio.Services;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

public class SkillPromptTemplateTests
{
    [Fact]
    public void BuildSkillSystemPromptFragment_InjectsContextWithoutFormattingLiteralBraces()
    {
        const string discoveryContext = "skill-a\nskill-b";

        var result = AiPrompts.BuildSkillSystemPromptFragment(discoveryContext);

        result.Should().Contain(discoveryContext);
        result.Should().Contain("{");
        result.Should().NotContain("{0}");
        result.Should().Contain("技能执行边界");
        result.Should().Contain("1-2 处");
    }
}
