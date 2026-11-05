using DeepSeek_v4_for_VisualStudio.View;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.View;

public class EditContextIsolationTests
{
    [Fact]
    public void BuildResendPromptContent_EditResend_DoesNotReadConversationHistory()
    {
        bool historyRead = false;

        string content = DeepSeekChatControl.BuildResendPromptContent(
            "修改后的问题",
            isEditResend: true,
            () =>
            {
                historyRead = true;
                return "旧问题/旧回复";
            });

        content.Should().Be("修改后的问题");
        historyRead.Should().BeFalse("编辑消息必须从当前文本重新开始，不能读取旧上下文");
    }

    [Fact]
    public void BuildResendPromptContent_Retry_StillIncludesConversationHistory()
    {
        string content = DeepSeekChatControl.BuildResendPromptContent(
            "原始问题",
            isEditResend: false,
            () => "已完成的历史");

        content.Should().Contain("【对话历史】");
        content.Should().Contain("已完成的历史");
        content.Should().Contain("【当前修改后的问题】");
        content.Should().Contain("原始问题");
    }
}
