using System.Reflection;
using System.Text;
using System.Threading;
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

    [Fact]
    public void RebuildFromTree_RecreatesUserBubbleHtml()
    {
        RunOnStaThread(control =>
        {
            var tree = new ConversationTree();
            tree.AddChildMessage(new ChatMessage
            {
                Role = "user",
                Content = "修改后的用户问题",
            });
            tree.AddChildMessage(new ChatMessage
            {
                Role = "assistant",
                Content = "思考中",
                IsStreaming = true,
            });

            SetField(control, "_tree", tree);
            InvokePrivate(control, "RebuildFromTree");

            string html = GetField<StringBuilder>(control, "_messagesHtml").ToString();
            html.Should().Contain("msg-wrapper user");
            html.Should().Contain("修改后的用户问题");
            html.Should().Contain("思考中");
        });
    }

    private static void RunOnStaThread(Action<DeepSeekChatControl> testBody)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            DeepSeekChatControl? control = null;
            try
            {
                control = new DeepSeekChatControl();
                testBody(control);
            }
            catch (Exception ex)
            {
                captured = ex;
            }
            finally
            {
                try { control?.Dispose(); }
                catch { }
            }
        })
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        thread.Join(TimeSpan.FromSeconds(30)).Should().BeTrue("STA 测试线程应在超时前完成");
        if (captured != null)
            throw new InvalidOperationException("STA 线程内断言失败", captured);
    }

    private static T GetField<T>(DeepSeekChatControl control, string name)
    {
        var field = typeof(DeepSeekChatControl).GetField(
            name, BindingFlags.Instance | BindingFlags.NonPublic);
        field.Should().NotBeNull($"字段 {name} 应存在于 DeepSeekChatControl");
        return (T)field!.GetValue(control)!;
    }

    private static void SetField(DeepSeekChatControl control, string name, object? value)
    {
        var field = typeof(DeepSeekChatControl).GetField(
            name, BindingFlags.Instance | BindingFlags.NonPublic);
        field.Should().NotBeNull($"字段 {name} 应存在于 DeepSeekChatControl");
        field!.SetValue(control, value);
    }

    private static void InvokePrivate(DeepSeekChatControl control, string methodName)
    {
        var method = typeof(DeepSeekChatControl).GetMethod(
            methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull($"方法 {methodName} 应存在于 DeepSeekChatControl");
        method!.Invoke(control, null);
    }
}
