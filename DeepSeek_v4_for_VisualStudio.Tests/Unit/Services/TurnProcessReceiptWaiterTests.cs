using System;
using System.Threading;
using System.Threading.Tasks;
using DeepSeek_v4_for_VisualStudio.Services.WebAssets;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// <see cref="TurnProcessReceiptWaiter"/> 的行为测试。
/// 覆盖回执到达、超时、取消、重复回执与未登记回执等路径，
/// 确保「节点永远不会出现」的正常场景不会挂住等待方。
/// </summary>
public class TurnProcessReceiptWaiterTests
{
    [Fact]
    public async Task WaitAsync_CompletesWhenReceiptArrives()
    {
        using var waiter = new TurnProcessReceiptWaiter();
        waiter.Register("t1");

        Task<bool> wait = waiter.WaitAsync("t1", TimeSpan.FromSeconds(5), CancellationToken.None);
        waiter.Complete("t1");

        (await wait).Should().BeTrue();
    }

    [Fact]
    public async Task WaitAsync_ReturnsFalseOnTimeout()
    {
        using var waiter = new TurnProcessReceiptWaiter();
        waiter.Register("t1");

        bool ready = await waiter.WaitAsync("t1", TimeSpan.FromMilliseconds(50), CancellationToken.None);

        ready.Should().BeFalse();
    }

    [Fact]
    public async Task CancelAll_ReleasesPendingWaitImmediately()
    {
        using var waiter = new TurnProcessReceiptWaiter();
        waiter.Register("t1");

        Task<bool> wait = waiter.WaitAsync("t1", TimeSpan.FromSeconds(30), CancellationToken.None);
        waiter.CancelAll();

        // 取消后必须立即结束，而不是等满超时
        (await wait).Should().BeFalse();
    }

    [Fact]
    public void Complete_WithoutRegister_IsIgnored()
    {
        using var waiter = new TurnProcessReceiptWaiter();

        // 迟到的旧页面回执不应抛异常
        Action act = () => waiter.Complete("never-registered");

        act.Should().NotThrow();
    }

    [Fact]
    public async Task Complete_IsIdempotent()
    {
        using var waiter = new TurnProcessReceiptWaiter();
        waiter.Register("t1");

        Task<bool> wait = waiter.WaitAsync("t1", TimeSpan.FromSeconds(5), CancellationToken.None);
        waiter.Complete("t1");
        waiter.Complete("t1");

        (await wait).Should().BeTrue();
    }

    [Fact]
    public async Task WaitAsync_UnknownTurnId_ReturnsFalse()
    {
        using var waiter = new TurnProcessReceiptWaiter();

        bool ready = await waiter.WaitAsync("missing", TimeSpan.FromMilliseconds(50), CancellationToken.None);

        ready.Should().BeFalse();
    }

    [Fact]
    public async Task WaitAsync_CanceledToken_ReturnsFalse()
    {
        using var waiter = new TurnProcessReceiptWaiter();
        waiter.Register("t1");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        bool ready = await waiter.WaitAsync("t1", TimeSpan.FromSeconds(5), cts.Token);

        ready.Should().BeFalse();
    }

    [Fact]
    public async Task WaitAsync_AfterDispose_ReturnsFalse()
    {
        var waiter = new TurnProcessReceiptWaiter();
        waiter.Register("t1");
        waiter.Dispose();

        bool ready = await waiter.WaitAsync("t1", TimeSpan.FromMilliseconds(50), CancellationToken.None);

        ready.Should().BeFalse();
    }
}
