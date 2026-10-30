namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// 执行中快照的读写：崩溃/强杀后能否把「已接收到的输出」找回来。
/// <para>
/// 背景：会话文件只在任务开始与收尾时各落盘一次。执行中的正文、过程时间线、推理内容
/// 都只存在于内存；VS 被强杀或工具窗口在任务中被重建时，磁盘上只剩用户消息，
/// 恢复出来的界面看起来就是「任务没执行」。快照文件补上这段缺口。
/// </para>
/// </summary>
public class InflightTurnSnapshotServiceTests
{
    private const string TestSolutionPath = @"F:\TestData\InflightSnapshot\Inflight.sln";

    private static InflightTurnSnapshot CreateSnapshot(string sessionId) => new()
    {
        SessionId = sessionId,
        TurnId = "a1b2c3d4",
        AgentType = "Edit",
        Content = "已完成的正文片段",
        TimelineContent = "步骤 1/7: 创建项目骨架",
        ReasoningContent = "先在脑内拆解一下步骤",
        CapturedAt = new DateTime(2026, 10, 30, 15, 40, 0, DateTimeKind.Local),
    };

    [Fact]
    public void SaveThenLoad_RoundTripsAllFields()
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var snapshot = CreateSnapshot(sessionId);

        try
        {
            InflightTurnSnapshotService.Save(TestSolutionPath, snapshot);

            File.Exists(InflightTurnSnapshotService.GetSnapshotPath(TestSolutionPath, sessionId))
                .Should().BeTrue("快照应落在 inflight 子目录下");

            var loaded = InflightTurnSnapshotService.Load(TestSolutionPath, sessionId);

            loaded.Should().NotBeNull();
            loaded!.SessionId.Should().Be(sessionId);
            loaded.TurnId.Should().Be("a1b2c3d4");
            loaded.AgentType.Should().Be("Edit");
            loaded.Content.Should().Be("已完成的正文片段");
            loaded.TimelineContent.Should().Be("步骤 1/7: 创建项目骨架");
            loaded.ReasoningContent.Should().Be("先在脑内拆解一下步骤");
            loaded.CapturedAt.Should().Be(snapshot.CapturedAt);
        }
        finally
        {
            InflightTurnSnapshotService.Delete(TestSolutionPath, sessionId);
        }
    }

    [Fact]
    public void SaveTwice_OverwritesWithoutLeavingTempFile()
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var first = CreateSnapshot(sessionId);
        var second = CreateSnapshot(sessionId);
        second.Content = "第二轮覆盖内容";

        try
        {
            InflightTurnSnapshotService.Save(TestSolutionPath, first);
            InflightTurnSnapshotService.Save(TestSolutionPath, second);

            var path = InflightTurnSnapshotService.GetSnapshotPath(TestSolutionPath, sessionId);
            File.Exists(path + ".tmp").Should().BeFalse("临时文件必须被替换掉，不能残留");

            InflightTurnSnapshotService.Load(TestSolutionPath, sessionId)!
                .Content.Should().Be("第二轮覆盖内容");
        }
        finally
        {
            InflightTurnSnapshotService.Delete(TestSolutionPath, sessionId);
        }
    }

    [Fact]
    public void Load_MissingFile_ReturnsNull()
    {
        InflightTurnSnapshotService.Load(TestSolutionPath, Guid.NewGuid().ToString("N"))
            .Should().BeNull();
    }

    [Fact]
    public void Load_CorruptFile_ReturnsNullAndRemovesIt()
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var path = InflightTurnSnapshotService.GetSnapshotPath(TestSolutionPath, sessionId);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{ not valid json");

            InflightTurnSnapshotService.Load(TestSolutionPath, sessionId).Should().BeNull(
                "损坏的快照不能拖垮会话加载");
            File.Exists(path).Should().BeFalse("损坏的快照应被清理，避免每次加载都重试");
        }
        finally
        {
            InflightTurnSnapshotService.Delete(TestSolutionPath, sessionId);
        }
    }

    [Fact]
    public void Delete_RemovesFileAndIsIdempotent()
    {
        var sessionId = Guid.NewGuid().ToString("N");
        InflightTurnSnapshotService.Save(TestSolutionPath, CreateSnapshot(sessionId));

        InflightTurnSnapshotService.Delete(TestSolutionPath, sessionId);
        InflightTurnSnapshotService.Delete(TestSolutionPath, sessionId); // 再删一次不应抛

        File.Exists(InflightTurnSnapshotService.GetSnapshotPath(TestSolutionPath, sessionId))
            .Should().BeFalse();
    }

    [Fact]
    public void SnapshotPath_DifferentSolutions_DoNotCollide()
    {
        var sessionId = Guid.NewGuid().ToString("N");

        var a = InflightTurnSnapshotService.GetSnapshotPath(@"F:\A\A.sln", sessionId);
        var b = InflightTurnSnapshotService.GetSnapshotPath(@"F:\B\B.sln", sessionId);

        a.Should().NotBe(b, "快照按解决方案隔离，避免同名会话互相覆盖");
    }

    [Fact]
    public void SnapshotPath_UnsanitizedSessionId_CannotEscapeDirectory()
    {
        var path = InflightTurnSnapshotService.GetSnapshotPath(TestSolutionPath, @"..\..\evil");

        path.Should().NotContain(@"..\");
        Path.GetFileName(path).Should().EndWith(".json");
    }
}
