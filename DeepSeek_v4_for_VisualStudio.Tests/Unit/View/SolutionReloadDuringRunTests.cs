using System.Reflection;
using System.Threading;
using DeepSeek_v4_for_VisualStudio.View;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.View;

/// <summary>
/// 回归测试：AI 改写解决方案文件 → VS 重载解决方案时，聊天窗口不得回滚到「任务未执行」状态。
/// <para>
/// 缺陷背景：VS 在 .sln/.slnx/.csproj 被外部修改后先 close 再 open 同一个解决方案。
/// 旧实现不比较路径，一律按「切换解决方案」处理：保存 → 清空 <c>_messages</c>/<c>_tree</c>/
/// <c>_contextManager</c> → 从磁盘重载。于是界面回到任务执行前的快照，而后台 Agent
/// 仍在往已被替换的状态里写——最终答复丢失，且恢复出的「开始实现」按钮会诱导用户
/// 再启动第二个并发任务。
/// </para>
/// <para>
/// 修复要点（本测试覆盖前两点）：
/// 1) 任务执行中的解决方案事件只记录意图，绝不保存/清空对话；
/// 2) 流式气泡以消息对象为锚点，消息列表被整体替换后写回必须失败而不是改错消息。
/// </para>
/// </summary>
public class SolutionReloadDuringRunTests
{
    /// <summary>
    /// 在专用 STA 线程上构造控件、执行测试体，并必定释放控件
    /// （控件会向进程级 LocalizationService 订阅事件，不释放会毒化后续测试）。
    /// </summary>
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
                try { control?.Dispose(); } catch { /* 释放异常不影响测试结论 */ }
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
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

    private static object? Invoke(DeepSeekChatControl control, string methodName, params object?[] args)
    {
        var method = typeof(DeepSeekChatControl).GetMethod(
            methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull($"方法 {methodName} 应存在于 DeepSeekChatControl");
        return method!.Invoke(control, args);
    }

    private static bool IsRunInProgress(DeepSeekChatControl control)
        => (bool)Invoke(control, "IsRunInProgress")!;

    private static int ResolveAgentStreamingIndex(DeepSeekChatControl control)
        => (int)Invoke(control, "ResolveAgentStreamingIndex")!;

    [Fact]
    public void SolutionClosed_WhileRunInProgress_KeepsConversationAndDefersOnly()
    {
        RunOnStaThread(control =>
        {
            var messages = GetField<List<ChatMessage>>(control, "_messages");
            messages.Add(new ChatMessage { Role = "user", Content = "开始实现" });
            messages.Add(new ChatMessage { Role = "assistant", Content = "正在执行步骤 1/7" });

            // 后台任务进行中
            SetField(control, "_isGenerating", true);
            IsRunInProgress(control).Should().BeTrue();

            Invoke(control, "OnSolutionClosed");

            // 关键契约：运行中的对话是唯一权威副本，关闭事件不允许动它
            messages.Should().HaveCount(2, "任务执行中不得清空消息列表");
            messages[1].Content.Should().Be("正在执行步骤 1/7", "流式内容不得被磁盘快照覆盖");

            GetField<object>(control, "_pendingSolutionChange").ToString()
                .Should().Be("Closed", "关闭意图应被延后到任务收尾再应用");
        });
    }

    [Fact]
    public void SolutionOpened_WhileRunInProgress_DefersAndKeepsConversation()
    {
        RunOnStaThread(control =>
        {
            var messages = GetField<List<ChatMessage>>(control, "_messages");
            messages.Add(new ChatMessage { Role = "assistant", Content = "后台仍在执行" });

            SetField(control, "_isGenerating", true);
            SetField(control, "_boundSolutionPath", @"F:\CSharp\exp1\exp1.slnx");

            // 该路径在测试宿主里解析不到解决方案（无 IVsSolution），
            // 但运行中分支必须在解析结果之后、任何保存/清空之前就返回。
            Invoke(control, "OnSolutionOpened");

            messages.Should().ContainSingle("任务执行中不得清空/重载对话");
            GetField<object>(control, "_pendingSolutionChange").ToString()
                .Should().Be("Opened");
        });
    }

    [Fact]
    public void StreamingAnchor_SurvivesReorder_ButIsInvalidatedByReload()
    {
        RunOnStaThread(control =>
        {
            var messages = GetField<List<ChatMessage>>(control, "_messages");

            var streaming = new ChatMessage { Role = "assistant", Content = "流式输出中" };
            messages.Add(streaming);
            Invoke(control, "SetAgentStreamingTarget", 0);
            ResolveAgentStreamingIndex(control).Should().Be(0, "锚点仍在列表中原位置时应解析出原下标");

            // 同一个对象被重新排序（如从树重建列表）→ 仍应能跟随锚点定位
            messages.Insert(0, new ChatMessage { Role = "user", Content = "前插消息" });
            ResolveAgentStreamingIndex(control).Should().Be(1, "锚点跟随的是消息对象，不是下标");

            // 模拟「从磁盘重载」：列表被整体换成新对象（旧对象的身份丢失）
            messages.Clear();
            messages.Add(new ChatMessage { Role = "assistant", Content = "重载后的旧消息" });

            ResolveAgentStreamingIndex(control).Should().Be(-1,
                "锚点已脱离列表时必须放弃写回，否则会把别的消息改成最终答复");
        });
    }

    [Fact]
    public void ClearAgentStreamingTarget_ResetsAnchor()
    {
        RunOnStaThread(control =>
        {
            var messages = GetField<List<ChatMessage>>(control, "_messages");
            messages.Add(new ChatMessage { Role = "assistant", Content = "x" });
            Invoke(control, "SetAgentStreamingTarget", 0);
            ResolveAgentStreamingIndex(control).Should().Be(0);

            Invoke(control, "ClearAgentStreamingTarget");

            GetField<int>(control, "_agentStreamingMsgIndex").Should().Be(-1);
            GetField<ChatMessage?>(control, "_agentStreamingMessage").Should().BeNull();
            ResolveAgentStreamingIndex(control).Should().Be(-1);
        });
    }
}
