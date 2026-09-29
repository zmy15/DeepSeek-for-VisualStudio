namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// 单测：ConversationContextManager.BuildApiMessagesWithSubTaskPrompt ——
/// 「完整上下文快照 + 子任务尾部（[user 提示][system 指令]）」的统一组装。
/// </summary>
public class ConversationContextManagerSubTaskPromptTests
{
    [Fact]
    public void BuildApiMessagesWithSubTaskPrompt_TailIsSystem_ReplacesTailAndKeepsSignature()
    {
        var manager = new ConversationContextManager();
        manager.AddUserMessage("你好");
        manager.AddCustomMessage("system", "尾部 system 提示");

        var before = manager.BuildApiMessages();
        // net472 无 System.Index，不能用 ^n 索引语法，改用显式 Count 索引
        before[before.Count - 1].Role.Should().Be("system");

        var result = manager.BuildApiMessagesWithSubTaskPrompt("子任务提示", "子任务指令");

        // 原末尾 system 被替换：长度 +1，尾部两条恒为 [user][system]
        result.Should().HaveCount(before.Count + 1);
        result[result.Count - 2].Role.Should().Be("user");
        result[result.Count - 2].Content.Should().Be("子任务提示");
        result[result.Count - 1].Role.Should().Be("system");
        result[result.Count - 1].Content.Should().Be("子任务指令");

        // 被替换的原末尾 system 不再存在；其余原消息完整保留且顺序不变
        result.Should().NotContain(m => m.Content == "尾部 system 提示");
        for (int i = 0; i < before.Count - 1; i++)
        {
            result[i].Role.Should().Be(before[i].Role);
            result[i].Content.Should().Be(before[i].Content);
        }
    }

    [Fact]
    public void BuildApiMessagesWithSubTaskPrompt_TailIsNotSystem_AppendsUserAndSystem()
    {
        var manager = new ConversationContextManager();
        manager.AddUserMessage("你好");
        manager.AddAssistantMessage("你好，有什么可以帮助你的？");

        var before = manager.BuildApiMessages();
        before[before.Count - 1].Role.Should().Be("assistant");

        var result = manager.BuildApiMessagesWithSubTaskPrompt("子任务提示", "子任务指令");

        // 末尾非 system：追加两条，尾部签名恒为 [user][system]
        result.Should().HaveCount(before.Count + 2);
        result[result.Count - 2].Role.Should().Be("user");
        result[result.Count - 2].Content.Should().Be("子任务提示");
        result[result.Count - 1].Role.Should().Be("system");
        result[result.Count - 1].Content.Should().Be("子任务指令");

        // 原上下文所有消息完整保留且顺序不变（快照未被副作用污染）
        for (int i = 0; i < before.Count; i++)
        {
            result[i].Role.Should().Be(before[i].Role);
            result[i].Content.Should().Be(before[i].Content);
        }
    }
}
