using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Services.BuiltInTools;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.Agents
{
    /// <summary>
    /// <see cref="BaseAgent"/> 的 Explore 子代理注册 分部实现。
    /// 承载该职责域的成员，使主文件聚焦于 Agent 执行编排。
    /// </summary>
    public abstract partial class BaseAgent
    {
        /// <summary>
        /// 注册 ExploreAgent 子代理，自动管理日志和文件变更事件转发。
        /// EditAgent / PlanAgent 共享使用，消除重复的样板代码。
        /// </summary>
        protected void RegisterExploreAgent(ExploreAgent? exploreAgent, ref ExploreAgent? field)
        {
            if (field != null)
            {
                field.LogEntryAdded -= OnExploreLog;
                field.FileChangeNotified -= OnExploreFileChange;
                field.PermissionRequested -= OnExplorePermissionRequested;
                field.QuestionsRequested -= OnExploreQuestionsRequested;
            }
            field = exploreAgent;
            if (field != null)
            {
                field.LogEntryAdded += OnExploreLog;
                field.FileChangeNotified += OnExploreFileChange;
                field.PermissionRequested += OnExplorePermissionRequested;
                field.QuestionsRequested += OnExploreQuestionsRequested;
            }
        }

        private void OnExploreLog(AgentLogEntry entry)
        {
            // 转发 ExploreAgent 日志到父 Agent，触发 LogEntryAdded 以便 UI 实时刷新进度
            // 由于 RegisterExploreAgent 只订阅一次（构造函数中），不会出现并行重复订阅问题
            // 注意：直接转发原始消息（不加前缀），确保 FormatLogForThinking 的 emoji 匹配正常工作
            RaiseLogEntryAdded(entry);
        }

        /// <summary>
        /// 确保 ExploreAgent 的日志、权限请求和提问请求转发到当前 Agent。
        /// 供 AgentFactory 在注入 ExploreAgent 引用后调用（AgentFactory 无法访问 protected RegisterExploreAgent）。
        /// </summary>
        public void WireExploreLogs()
        {
            if (ExploreAgent != null)
            {
                // 先移除防止重复订阅，再重新订阅
                ExploreAgent.LogEntryAdded -= OnExploreLog;
                ExploreAgent.LogEntryAdded += OnExploreLog;
                ExploreAgent.PermissionRequested -= OnExplorePermissionRequested;
                ExploreAgent.PermissionRequested += OnExplorePermissionRequested;
                ExploreAgent.QuestionsRequested -= OnExploreQuestionsRequested;
                ExploreAgent.QuestionsRequested += OnExploreQuestionsRequested;
            }
        }

        /// <summary>
        /// 转发 ExploreAgent 的权限请求到父 Agent（v1.1.10 修复）。
        /// 子代理执行 git/终端等操作需要审批时，弹窗需通过父 Agent 的事件链到达 UI。
        /// </summary>
        private void OnExplorePermissionRequested(AgentPermissionRequest request)
        {
            // 临时 ExploreAgent 不在 AgentFactory 中，UI 的响应会回退到父 Agent。
            // 将同一个请求对象登记到父 Agent，确保批准/拒绝能完成子代理共享的 TCS。
            _pendingPermissions[request.RequestId] = request;
            _ = request.ResponseTcs.Task.ContinueWith(
                _completed => _pendingPermissions.TryRemove(request.RequestId, out _),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            PermissionRequested?.Invoke(request);
            AddLog("INFO", $"[Explore→{Definition.Name}] 转发权限请求: {request.Title}");
        }

        /// <summary>
        /// 转发 ExploreAgent 的提问请求到父 Agent（v1.1.10 修复）。
        /// </summary>
        private void OnExploreQuestionsRequested(AgentQuestionRequest request)
        {
            _pendingQuestions[request.RequestId] = request;
            _ = request.ResponseTcs.Task.ContinueWith(
                _completed => _pendingQuestions.TryRemove(request.RequestId, out _),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            QuestionsRequested?.Invoke(request);
            AddLog("INFO", $"[Explore→{Definition.Name}] 转发提问请求: {request.Questions.Count} 个问题");
        }

        private void OnExploreFileChange(AgentFileChangeEventArgs args)
        {
            NotifyFileChange(args.PlanId, args.ChangeType, args.FilePath, args.Detail);
        }
    }
}
