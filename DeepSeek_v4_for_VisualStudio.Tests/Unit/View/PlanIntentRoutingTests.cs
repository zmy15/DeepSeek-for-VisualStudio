using DeepSeek_v4_for_VisualStudio.View;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.View;

/// <summary>
/// 待执行计划存在时的调整意图识别测试。
/// </summary>
public class PlanIntentRoutingTests
{
    /// <summary>
    /// 明确的计划调整请求必须直接命中 Plan Agent，而不是先经过 Ask。
    /// </summary>
    [Theory]
    [InlineData("在添加一步")]
    [InlineData("再添加一步")]
    [InlineData("把第二步拆成两个步骤")]
    [InlineData("第 3 步前面插入配置检查")]
    [InlineData("计划里去掉缓存预热")]
    [InlineData("调整一下顺序，先跑测试")]
    [InlineData("这个方案太复杂了")]
    [InlineData("步骤顺序不对")]
    [InlineData("帮我补充一个验证步骤")]
    [InlineData("Can you add a verification step to the plan?")]
    public void IsPlanModificationIntent_RecognizesAdjustmentRequests(string input)
    {
        DeepSeekChatControl.IsPlanModificationIntent(input).Should().BeTrue();
    }

    /// <summary>
    /// 执行命令、计划提问和普通对话不能被误判为调整计划。
    /// </summary>
    [Theory]
    [InlineData("开始执行")]
    [InlineData("第三步是什么意思")]
    [InlineData("为什么计划里要删除缓存")]
    [InlineData("解释一下这个方案")]
    [InlineData("你好")]
    public void IsPlanModificationIntent_DoesNotTreatCommandsQuestionsOrChatterAsAdjustments(string input)
    {
        DeepSeekChatControl.IsPlanModificationIntent(input).Should().BeFalse();
    }
}
