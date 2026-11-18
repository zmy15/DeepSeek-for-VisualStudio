using System;
using System.Collections.Generic;
using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.View;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.View;

/// <summary>
/// 删除当前会话后「接替会话」的选择契约。
/// <para>
/// 历史缺陷：删除当前会话后取 <c>Sessions.FirstOrDefault()</c>。但 <c>Sessions</c>
/// 是按创建顺序追加的（最旧在前），而历史浮层按 <c>LastActiveAt</c> 降序展示，
/// 因此用户删除后会被切到**最旧**的会话，与列表中看到的顺序相反。
/// </para>
/// <para>修复：改为取最近活跃者，与浮层展示顺序一致。本测试锁定该契约。</para>
/// </summary>
public class SessionFallbackSelectionTests
{
    private static ChatSession Session(string id, DateTime lastActive, DateTime? created = null) => new()
    {
        Id = id,
        Title = id,
        LastActiveAt = lastActive,
        CreatedAt = created ?? lastActive,
    };

    [Fact]
    public void SelectFallbackSession_PicksMostRecentlyActive_NotFirstInList()
    {
        var oldest = Session("old", new DateTime(2026, 1, 1));
        var newest = Session("new", new DateTime(2026, 10, 1));
        var middle = Session("mid", new DateTime(2026, 5, 1));

        // 列表按创建顺序（最旧在前）——正是 FirstOrDefault 会选错的排列
        var sessions = new List<ChatSession> { oldest, middle, newest };

        var picked = DeepSeekChatControl.SelectFallbackSession(sessions);

        picked.Should().BeSameAs(newest, "接替的应是最近活跃的会话，而非列表首元素");
        picked.Should().NotBeSameAs(oldest);
    }

    [Fact]
    public void SelectFallbackSession_TieOnLastActive_UsesCreatedAtDescending()
    {
        var same = new DateTime(2026, 6, 1);
        var createdEarlier = Session("earlier", same, new DateTime(2026, 1, 1));
        var createdLater = Session("later", same, new DateTime(2026, 5, 1));

        var picked = DeepSeekChatControl.SelectFallbackSession(
            new List<ChatSession> { createdEarlier, createdLater });

        picked.Should().BeSameAs(createdLater, "活跃时间相同时用 CreatedAt 保证结果确定");
    }

    [Fact]
    public void SelectFallbackSession_SingleSession_ReturnsIt()
    {
        var only = Session("only", DateTime.Now);

        DeepSeekChatControl.SelectFallbackSession(new List<ChatSession> { only })
            .Should().BeSameAs(only);
    }

    [Fact]
    public void SelectFallbackSession_EmptyList_ReturnsNull()
    {
        DeepSeekChatControl.SelectFallbackSession(new List<ChatSession>())
            .Should().BeNull();
    }

    [Fact]
    public void SelectFallbackSession_NullInput_ReturnsNull()
    {
        DeepSeekChatControl.SelectFallbackSession(null).Should().BeNull();
    }

    [Fact]
    public void SelectFallbackSession_ResultIsHeadOfHistoryOrder()
    {
        // 与 RefreshHistoryUI 的排序（LastActiveAt 降序）保持一致：
        // 选出的会话应当正是浮层中排在最上面的那一个。
        var sessions = new List<ChatSession>
        {
            Session("a", new DateTime(2026, 3, 1)),
            Session("b", new DateTime(2026, 9, 1)),
            Session("c", new DateTime(2026, 1, 1)),
        };

        var uiOrder = sessions
            .OrderByDescending(s => s.LastActiveAt)
            .ThenByDescending(s => s.CreatedAt)
            .ToList();

        DeepSeekChatControl.SelectFallbackSession(sessions)
            .Should().BeSameAs(uiOrder[0]);
    }

    [Fact]
    public void ResolveActiveSession_UsesContainerActiveSession()
    {
        var active = Session("active", new DateTime(2026, 3, 1));
        var newer = Session("newer", new DateTime(2026, 9, 1));
        var container = new SessionsContainer
        {
            Sessions = new List<ChatSession> { active, newer },
            ActiveSessionId = active.Id,
        };

        DeepSeekChatControl.ResolveActiveSession(container).Should().BeSameAs(active);
    }

    [Fact]
    public void ResolveActiveSession_EmptyContainer_ReturnsNull()
    {
        DeepSeekChatControl.ResolveActiveSession(new SessionsContainer())
            .Should().BeNull("解决方案切换时不能沿用旧容器的活跃会话");
    }

    [Fact]
    public void EnsureSessionTracked_AddsSessionMissingFromContainer()
    {
        var container = new SessionsContainer();
        var session = Session("active", DateTime.Now);

        bool added = DeepSeekChatControl.EnsureSessionTracked(container, session);

        added.Should().BeTrue();
        container.Sessions.Should().ContainSingle().Which.Should().BeSameAs(session);
    }
}
