// 测试项目 ImplicitUsings 的默认集合不含 System.IO，测试代码大量使用 Path/File/Directory/MemoryStream，在此全局补充
global using System.IO;
global using Xunit;
global using FluentAssertions;
global using Moq;
global using DeepSeek_v4_for_VisualStudio.Models;
global using DeepSeek_v4_for_VisualStudio.Services;

[assembly: Xunit.TestFramework("DeepSeek_v4_for_VisualStudio.Tests.TestFrameworkWithLoggingDisabled", "DeepSeek_v4_for_VisualStudio.Tests")]
