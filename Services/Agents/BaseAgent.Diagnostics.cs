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
    /// <see cref="BaseAgent"/> 的 文件变更通知与日志 分部实现。
    /// 承载该职责域的成员，使主文件聚焦于 Agent 执行编排。
    /// </summary>
    public abstract partial class BaseAgent
    {
        /// <summary>
        /// 通知文件变更（用于实时推送到 WebView）。
        /// </summary>
        /// <param name="planId">关联的计划 ID</param>
        /// <param name="changeType">变更类型: modify, create, delete</param>
        /// <param name="filePath">文件绝对路径</param>
        /// <param name="detail">变更详情</param>
        protected void NotifyFileChange(string planId, string changeType, string filePath, string detail)
        {
            try
            {
                FileChangeNotified?.Invoke(new AgentFileChangeEventArgs
                {
                    PlanId = planId,
                    ChangeType = changeType,
                    FilePath = filePath,
                    Detail = detail,
                });
            }
            catch { }
        }

        #region Event Helpers for Derived Classes

        /// <summary>
        /// 供派生类触发 LogEntryAdded 事件（不写日志文件，仅转发到订阅者）。
        /// </summary>
        protected void RaiseLogEntryAdded(AgentLogEntry entry)
        {
            try { LogEntryAdded?.Invoke(entry); } catch { }
        }

        #endregion

        #region Logging

        protected void AddLog(string level, string message)
        {
            var entry = new AgentLogEntry { Level = level, Message = message };
            _logs.Add(entry);
            try { LogEntryAdded?.Invoke(entry); } catch { }

            if (level == "ERROR") Logger.Error($"[{Definition.Name}] {message}");
            else if (level == "WARN") Logger.Warn($"[{Definition.Name}] {message}");
            else Logger.Info($"[{Definition.Name}] {message}");
        }

        /// <summary>
        /// 把进入 Agent 的消息（用户请求或 Handoff 提示词）压缩成一行摘要，供**写日志文件**使用。
        ///
        /// 这类消息常达数 KB，且尾部会挂上给模型看的脚手架（例如「优先复用对话历史中已读取的文件内容…」）。
        /// 它只能进日志文件，**不能进 <see cref="AddLog"/> 的时间线**——时间线会渲染进 UI，
        /// 用户会看到本该给 AI 的提示词（Build/Plan/Explore 曾因此把整段 Handoff prompt 显示给用户）。
        /// 时间线请改用各 Agent 的 *Short 文案键（不含 {0} 占位符）。
        /// </summary>
        /// <param name="message">原始用户消息或 Handoff 提示词。</param>
        protected static string SummarizeForLog(string? message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return "(empty)";

            string normalized = message.Replace("\r\n", "\n").Trim();
            int newlineIdx = normalized.IndexOf('\n');
            string firstLine = newlineIdx >= 0 ? normalized.Substring(0, newlineIdx).Trim() : normalized;

            const int maxFirstLine = 120;
            if (firstLine.Length > maxFirstLine)
                firstLine = firstLine.Substring(0, maxFirstLine) + "…";

            int totalLines = normalized.Split('\n').Length;
            return totalLines > 1
                ? $"{firstLine} …(共 {normalized.Length} 字符 / {totalLines} 行)"
                : firstLine;
        }

        #endregion
    }
}
