using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// 审批卡片串行显示队列测试。
///
/// 背景：一轮工具调用并行执行（BaseAgent 用 Task.WhenAll），多个终端/git/删除审批会同时到达，
/// 此前每个请求各自注入卡片导致聊天区审批堆叠。本队列保证同一时刻只暴露一个请求，
/// 当前请求处理完才交出下一个，并跳过排队期间已失效（取消/已响应）的请求。
/// </summary>
public class ApprovalRequestQueueTests
{
    private static AgentPermissionRequest CreateRequest(string? requestId = null)
    {
        return new AgentPermissionRequest
        {
            RequestId = requestId ?? Guid.NewGuid().ToString("N"),
            Title = "测试审批",
            Command = "dotnet build",
            ActionType = "terminal_command",
            ResponseTcs = new TaskCompletionSource<bool>(),
        };
    }

    [Fact]
    public void TakeNext_WithMultipleQueued_ReturnsOnlyOneAtATime()
    {
        var queue = new ApprovalRequestQueue();
        var first = CreateRequest();
        var second = CreateRequest();
        var third = CreateRequest();

        queue.Enqueue(first).Should().BeTrue();
        queue.Enqueue(second).Should().BeTrue();
        queue.Enqueue(third).Should().BeTrue();

        queue.TakeNext().Should().BeSameAs(first);
        queue.Current.Should().BeSameAs(first);
        queue.PendingCount.Should().Be(2);

        // 当前项未结束 → 不再交出下一个（核心：不堆叠显示）
        queue.TakeNext().Should().BeNull();
        queue.PendingCount.Should().Be(2);
    }

    [Fact]
    public void Complete_CurrentRequest_AllowsNextRequestToBeShown()
    {
        var queue = new ApprovalRequestQueue();
        var first = CreateRequest();
        var second = CreateRequest();
        queue.Enqueue(first);
        queue.Enqueue(second);
        queue.TakeNext();

        queue.Complete(first.RequestId).Should().BeSameAs(first);
        queue.Current.Should().BeNull();

        queue.TakeNext().Should().BeSameAs(second);
        queue.PendingCount.Should().Be(0);
    }

    [Fact]
    public void Complete_QueuedButNotDisplayedRequest_ReturnsNull()
    {
        var queue = new ApprovalRequestQueue();
        var first = CreateRequest();
        var second = CreateRequest();
        queue.Enqueue(first);
        queue.Enqueue(second);
        queue.TakeNext();

        queue.Complete(second.RequestId).Should().BeNull("未显示的排队项不属于当前卡片");
        queue.Current.Should().BeSameAs(first);
    }

    [Fact]
    public void TakeNext_SkipsRequestsResolvedWhileQueued()
    {
        var queue = new ApprovalRequestQueue();
        var displayed = CreateRequest();
        var cancelledWhileQueued = CreateRequest();
        var next = CreateRequest();

        queue.Enqueue(displayed);
        queue.Enqueue(cancelledWhileQueued);
        queue.Enqueue(next);

        queue.TakeNext().Should().BeSameAs(displayed);

        // 排队期间该请求被取消/响应（例如用户点了停止）→ 不得再显示僵尸卡片
        cancelledWhileQueued.ResponseTcs!.TrySetResult(false);

        queue.Complete(displayed.RequestId);
        queue.TakeNext().Should().BeSameAs(next, "已失效的排队项应被跳过");
        queue.PendingCount.Should().Be(0);
    }

    [Fact]
    public void TakeNext_AllQueuedRequestsInvalid_ReturnsNull()
    {
        var queue = new ApprovalRequestQueue();
        var request = CreateRequest();
        queue.Enqueue(request);
        request.ResponseTcs!.TrySetResult(true);

        queue.TakeNext().Should().BeNull();
        queue.Current.Should().BeNull();
        queue.PendingCount.Should().Be(0);
    }

    [Fact]
    public void Enqueue_DuplicateRequestId_IsIgnored()
    {
        var queue = new ApprovalRequestQueue();
        var request = CreateRequest("dup-1");
        var duplicate = CreateRequest("dup-1");

        queue.Enqueue(request).Should().BeTrue();
        queue.Enqueue(duplicate).Should().BeFalse();

        queue.TakeNext().Should().BeSameAs(request);
        queue.TakeNext().Should().BeNull();

        // 已显示的请求再次入队也应被忽略（防止重复卡片）
        queue.Enqueue(duplicate).Should().BeFalse();
    }

    [Fact]
    public void Reset_ClearsQueueAndReturnsDisplayedRequest()
    {
        var queue = new ApprovalRequestQueue();
        var displayed = CreateRequest();
        var queued = CreateRequest();
        queue.Enqueue(displayed);
        queue.Enqueue(queued);
        queue.TakeNext();

        queue.Reset().Should().BeSameAs(displayed, "调用方需要据此移除残留卡片");
        queue.Current.Should().BeNull();
        queue.PendingCount.Should().Be(0);
        queue.TakeNext().Should().BeNull();
    }

    [Fact]
    public void Reset_WithoutDisplayedRequest_ReturnsNull()
    {
        var queue = new ApprovalRequestQueue();
        queue.Enqueue(CreateRequest());

        queue.Reset().Should().BeNull();
        queue.PendingCount.Should().Be(0);
    }

    [Fact]
    public void IsStillPending_TracksResponseTcsCompletion()
    {
        var pending = CreateRequest();
        ApprovalRequestQueue.IsStillPending(pending).Should().BeTrue();

        pending.ResponseTcs!.TrySetResult(true);
        ApprovalRequestQueue.IsStillPending(pending).Should().BeFalse();

        // 无 TCS 的请求按「仍在等待」处理，避免误跳过
        var noTcs = new AgentPermissionRequest();
        ApprovalRequestQueue.IsStillPending(noTcs).Should().BeTrue();
    }
}
