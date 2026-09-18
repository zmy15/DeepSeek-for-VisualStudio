using DeepSeek_v4_for_VisualStudio.Models;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Models;

public class AppendMessageModelsTests
{
    [Fact]
    public void TakeGuidance_ReturnsOnlyGuidanceItemsInOrder()
    {
        var store = new PendingAppendMessageStore();
        store.Add("queue-1", "model-a", AppendMessageMode.Queue);
        store.Add("guide-1", "model-a", AppendMessageMode.Guidance);
        store.Add("guide-2", "model-b", AppendMessageMode.Guidance);

        store.TakeNextGuidance()!.Text.Should().Be("guide-1");
        store.TakeNextGuidance()!.Text.Should().Be("guide-2");
        store.TakeNextGuidance().Should().BeNull();
        store.Snapshot().Should().ContainSingle(item => item.Text == "queue-1");
    }

    [Fact]
    public void ToggleMode_ChangesOnlyTheSelectedMessage()
    {
        var store = new PendingAppendMessageStore();
        var first = store.Add("first", "model-a", AppendMessageMode.Queue);
        var second = store.Add("second", "model-a", AppendMessageMode.Queue);

        store.ToggleMode(first.Id).Should().BeTrue();

        var snapshot = store.Snapshot();
        snapshot.Single(item => item.Id == first.Id).Mode
            .Should().Be(AppendMessageMode.Guidance);
        snapshot.Single(item => item.Id == second.Id).Mode
            .Should().Be(AppendMessageMode.Queue);
    }

    [Fact]
    public void TakeNextQueued_UsesFifoAndLeavesGuidancePending()
    {
        var store = new PendingAppendMessageStore();
        store.Add("guide", "model-a", AppendMessageMode.Guidance);
        var firstQueue = store.Add("queue-1", "model-a", AppendMessageMode.Queue);
        store.Add("queue-2", "model-a", AppendMessageMode.Queue);

        store.TakeNextQueued()!.Id.Should().Be(firstQueue.Id);
        store.TakeNextQueued()!.Text.Should().Be("queue-2");
        store.Snapshot().Should().ContainSingle(item =>
            item.Mode == AppendMessageMode.Guidance
            && item.State == AppendMessageState.Pending);
    }
}
