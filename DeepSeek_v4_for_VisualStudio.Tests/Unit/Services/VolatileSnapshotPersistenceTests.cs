using System.Collections.Generic;
using System.Linq;
using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using FluentAssertions;
using Xunit;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// 回归：易变上下文块（IDE Context / 工作区快照）应在「用户发起一次提问」时固化进 _entries，
/// 且同一轮内只保留一份。
///
/// 时序依据（UI 先追加 user，再进入 Agent 执行）：
///   View/DeepSeekChatControl.Messaging.cs:375  AddUserMessage
///   View/DeepSeekChatControl.Messaging.cs:485  RunAgentWorkflowAsync
///   Services/Agents/AskAgent.cs:130            useSessionHistory = !contextManager.IsEmpty
///   Services/Agents/AskAgent.cs:142            persistVolatileToHistory: useSessionHistory
///
/// 因此「首轮」在 Agent 读取状态时 IsEmpty 已为 false，固化路径本就处于启用状态；
/// 本测试锁定该行为，防止后续把 IsEmpty 判定提前而静默退回非持久化的临时注入。
/// </summary>
public class VolatileSnapshotPersistenceTests
{
    [Fact]
    public void FirstTurn_UserMessageAlreadyAdded_EnablesVolatilePersistence()
    {
        var mgr = new ConversationContextManager();

        // 会话刚创建、用户尚未提问
        mgr.IsEmpty.Should().BeTrue("会话初始应无 user 消息");

        // UI 层先追加本次用户消息，随后 Agent 才读取状态
        mgr.AddUserMessage("第一个问题");

        mgr.IsEmpty.Should().BeFalse("追加 user 后不再判为空");
        // 由 AskAgent.cs:130/142 推导：首轮 useSessionHistory 实为 true
        (!mgr.IsEmpty).Should().BeTrue("首轮 persistVolatileToHistory 应已启用");
    }

    [Fact]
    public void FirstTurn_PersistsIdeContextIntoEntries()
    {
        var mgr = new ConversationContextManager();
        mgr.SetSolutionPath(@"C:\Projects\MyApp\MyApp.sln");
        mgr.SetIdeContext("[IDE Context] Active File: Test.cs");
        mgr.AddUserMessage("第一个问题");

        mgr.PersistCurrentVolatileSnapshot().Should().BeTrue("首轮应能成功固化 volatile 快照");

        var full = mgr.GetFullContext();
        full.Should().Contain(m => m.Role == "system" && m.Content!.Contains("[IDE Context]"),
            "首轮固化后 IDE Context 应进入 _entries，从而可持久化并在重启后恢复");

        int ideIdx = full.FindIndex(m => m.Content != null && m.Content.Contains("[IDE Context]"));
        int userIdx = full.FindIndex(m => m.Role == "user");
        ideIdx.Should().BeGreaterThanOrEqualTo(0);
        ideIdx.Should().BeLessThan(userIdx, "volatile 快照应插在本轮 user 之前");
    }

    [Fact]
    public void SameTurn_RepeatPersist_IsIdempotent()
    {
        var mgr = new ConversationContextManager();
        mgr.SetIdeContext("[IDE Context] Active File: Test.cs");
        mgr.AddUserMessage("第一个问题");

        mgr.PersistCurrentVolatileSnapshot().Should().BeTrue();
        mgr.PersistCurrentVolatileSnapshot().Should().BeFalse("同一轮次内不得重复固化");

        mgr.GetFullContext()
            .Count(m => m.Content != null && m.Content.Contains("[IDE Context]"))
            .Should().Be(1, "同一轮内 IDE Context 只应存在一份，避免与 Handoff 注入叠加成重复块");
    }

    [Fact]
    public void NextTurn_PersistsNewSnapshot_BothSnapshotsRetained()
    {
        var mgr = new ConversationContextManager();
        mgr.SetIdeContext("[IDE Context] Old");
        mgr.AddUserMessage("Q1");
        mgr.PersistCurrentVolatileSnapshot().Should().BeTrue();

        mgr.AddAssistantMessage("A1");
        mgr.SetIdeContext("[IDE Context] New");
        mgr.AddUserMessage("Q2");
        mgr.PersistCurrentVolatileSnapshot().Should().BeTrue("新一轮应能再固化一份新快照");

        var full = mgr.GetFullContext();
        full.Should().Contain(m => m.Content != null && m.Content.Contains("[IDE Context] Old"));
        full.Should().Contain(m => m.Content != null && m.Content.Contains("[IDE Context] New"));

        // 旧快照必须留在原位（Q1 之前），新快照位于 Q2 之前，保证前缀缓存不被击穿
        int oldIdx = full.FindIndex(m => m.Content != null && m.Content.Contains("[IDE Context] Old"));
        int q1Idx = full.FindIndex(m => m.Role == "user" && m.Content == "Q1");
        int newIdx = full.FindIndex(m => m.Content != null && m.Content.Contains("[IDE Context] New"));
        int q2Idx = full.FindIndex(m => m.Role == "user" && m.Content == "Q2");

        oldIdx.Should().BeLessThan(q1Idx);
        newIdx.Should().BeGreaterThan(q1Idx);
        newIdx.Should().BeLessThan(q2Idx);
    }

    /// <summary>
    /// @agent 显式路由场景：用户消息同样由 Messaging.cs:375 先行写入 ContextManager，
    /// 因此目标 Agent 读到 IsEmpty=false，固化路径同样处于启用状态。
    /// 注意 AskAgent 会切走（Agent.cs:907 SwitchActiveAgent），但消息序列化与否
    /// 只取决于 _entries 中是否存在该 user —— 与目标 Agent 类型无关。
    /// </summary>
    [Fact]
    public void ExplicitRoute_UserMessageStillAdded_EnablesVolatilePersistence()
    {
        var mgr = new ConversationContextManager();

        // @agent 解析发生在 AddUserMessage 之后，故 user 已入 _entries
        mgr.AddUserMessage("@Edit 修复这个 bug");
        mgr.SetIdeContext("[IDE Context] Active File: Test.cs");

        mgr.IsEmpty.Should().BeFalse("@agent 消息同样是 user，应使上下文非空");

        // 目标 Agent 读取到的 useSessionHistory 亦为 true
        mgr.PersistCurrentVolatileSnapshot().Should().BeTrue(
            "@agent 路由下固化路径同样启用，IDE Context 应进入 _entries");

        mgr.GetFullContext()
            .Should().Contain(m => m.Role == "system" && m.Content!.Contains("[IDE Context]"));
    }
}