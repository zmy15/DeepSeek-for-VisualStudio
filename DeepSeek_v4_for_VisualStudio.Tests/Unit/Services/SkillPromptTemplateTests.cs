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
    }
}
