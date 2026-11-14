using DeepSeek_v4_for_VisualStudio.View;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.View;

/// <summary>
/// 图表导出契约：点击 Mermaid 图后把已渲染的 SVG 写盘并用系统默认程序打开。
/// 只放行单张 &lt;svg&gt; 图，避免把任意 HTML 交给默认浏览器。
/// </summary>
public class DiagramExportTests
{
    [Fact]
    public void BuildDiagramSaveFileName_UsesSvgExtensionAndTimestamp()
    {
        DeepSeekChatControl.BuildDiagramSaveFileName(
            new DateTime(2026, 11, 15, 13, 14, 15))
            .Should().Be("diagram_20261115_131415.svg");
    }

    [Fact]
    public void IsExportableDiagramSvg_MermaidSvg_ReturnsTrue()
    {
        const string svg = "<svg id=\"mermaid-svg-1\" viewBox=\"0 0 100 50\" "
            + "xmlns=\"http://www.w3.org/2000/svg\"><g><text>a</text></g></svg>";

        DeepSeekChatControl.IsExportableDiagramSvg(svg).Should().BeTrue();
    }

    [Fact]
    public void IsExportableDiagramSvg_LeadingWhitespaceAndUppercase_ReturnsTrue()
    {
        DeepSeekChatControl.IsExportableDiagramSvg("  \n<SVG viewBox=\"0 0 1 1\"></SVG>")
            .Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<html><body>hi</body></html>")]
    [InlineData("<div>not a diagram</div>")]
    [InlineData("plain text")]
    public void IsExportableDiagramSvg_NonDiagramContent_ReturnsFalse(string? content)
    {
        DeepSeekChatControl.IsExportableDiagramSvg(content).Should().BeFalse();
    }

    [Fact]
    public void IsExportableDiagramSvg_EmbeddedScript_ReturnsFalse()
    {
        const string svg = "<svg viewBox=\"0 0 1 1\"><script>alert(1)</script></svg>";

        DeepSeekChatControl.IsExportableDiagramSvg(svg).Should().BeFalse();
    }

    [Fact]
    public void IsExportableDiagramSvg_OversizedPayload_ReturnsFalse()
    {
        string huge = "<svg viewBox=\"0 0 1 1\">" + new string('x', 4 * 1024 * 1024) + "</svg>";

        DeepSeekChatControl.IsExportableDiagramSvg(huge).Should().BeFalse();
    }

    [Fact]
    public void DiagramExportDirectory_IsUnderLocalAppData()
    {
        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeepSeekVS", "diagrams");

        DeepSeekChatControl.DiagramExportDirectory.Should().Be(expected);
    }

    /// <summary>
    /// 真实缺陷复现：Mermaid 把节点文字放进 foreignObject 的 XHTML 里，
    /// HTML 序列化会把换行写成裸 &lt;br&gt;，独立 .svg 文件按 XML 解析时报
    /// "Opening and ending tag mismatch: br ... span"。
    /// </summary>
    private const string MermaidSvgWithHtmlBr =
        "<svg id=\"mermaid-svg-1\" aria-roledescription=\"flowchart-v2\" "
        + "viewBox=\"0 0 878 1081\" style=\"max-width: none; height: auto;\">"
        + "<g><foreignObject width=\"130\" height=\"51\">"
        + "<div xmlns=\"http://www.w3.org/1999/xhtml\" style=\"display: inline-block;\">"
        + "<span class=\"nodeLabel\">需要跨多轮<br>操作工具 / 仓库？</span></div></foreignObject></g></svg>";

    [Fact]
    public void IsWellFormedXml_MermaidForeignObjectWithHtmlBr_ReturnsFalse()
    {
        DeepSeekChatControl.IsWellFormedXml(MermaidSvgWithHtmlBr).Should().BeFalse();
    }

    [Fact]
    public void NormalizeDiagramSvg_HtmlBrValue_ProducesWellFormedXml()
    {
        string normalized = DeepSeekChatControl.NormalizeDiagramSvg(MermaidSvgWithHtmlBr);

        normalized.Should().StartWith("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        normalized.Should().Contain("xmlns=\"http://www.w3.org/2000/svg\"");
        normalized.Should().Contain("<br />");
        normalized.Should().NotContain("<br>");
        DeepSeekChatControl.IsWellFormedXml(normalized).Should().BeTrue();
    }

    [Fact]
    public void NormalizeDiagramSvg_VoidElementWithAttributes_SelfCloses()
    {
        DeepSeekChatControl.NormalizeDiagramSvg("<svg><g><img src=\"a.png\"><hr></g></svg>")
            .Should().Contain("<img src=\"a.png\" />").And.Contain("<hr />");
    }

    [Fact]
    public void NormalizeDiagramSvg_ExistingXmlDeclaration_NotDuplicated()
    {
        string source = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
            + "<svg xmlns=\"http://www.w3.org/2000/svg\"><g></g></svg>";

        string normalized = DeepSeekChatControl.NormalizeDiagramSvg(source);

        normalized.Split(new[] { "<?xml" }, StringSplitOptions.None).Should().HaveCount(2);
        normalized.Should().NotContain("xmlns=\"http://www.w3.org/2000/svg\" xmlns=");
        DeepSeekChatControl.IsWellFormedXml(normalized).Should().BeTrue();
    }

    [Fact]
    public void NormalizeDiagramSvg_WellFormedSvg_LeftSemanticallyIntact()
    {
        const string source = "<svg viewBox=\"0 0 10 10\"><g><text>a</text></g></svg>";

        string normalized = DeepSeekChatControl.NormalizeDiagramSvg(source);

        normalized.Should().Contain("viewBox=\"0 0 10 10\"");
        normalized.Should().Contain("<text>a</text>");
        DeepSeekChatControl.IsWellFormedXml(normalized).Should().BeTrue();
    }

    [Fact]
    public void IsExportableDiagramSvg_XmlDeclarationPrefixedSvg_ReturnsTrue()
    {
        const string source = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
            + "<svg viewBox=\"0 0 1 1\"></svg>";

        DeepSeekChatControl.IsExportableDiagramSvg(source).Should().BeTrue();
    }

    // ── 重复点击去重 ──
    // Mermaid 每次渲染都换 id（mermaid-svg-{时间戳}-{序号}），
    // 若不归一化，同一张图重新渲染后内容不同，去重会失效、文件越堆越多。

    private static string MermaidSvgWithId(string id) =>
        "<svg id=\"" + id + "\" viewBox=\"0 0 10 10\"><style>#" + id
        + " .node{fill:#1f2020;}</style><g><marker id=\"" + id
        + "_flowchart-pointEnd\" marker-end=\"url(#" + id + "_flowchart-pointEnd)\"></marker></g></svg>";

    [Fact]
    public void NormalizeDiagramIdentity_DifferentRenderIds_ProduceSameText()
    {
        string first = MermaidSvgWithId("mermaid-svg-1792252615344-3");
        string second = MermaidSvgWithId("mermaid-svg-1799999999999-12");

        string a = DeepSeekChatControl.NormalizeDiagramIdentity(first);
        string b = DeepSeekChatControl.NormalizeDiagramIdentity(second);

        a.Should().Be(b);
        a.Should().Contain("mermaid-diagram");
        a.Should().NotContain("1792252615344");
        a.Should().NotContain("1799999999999");
    }

    [Fact]
    public void NormalizeDiagramIdentity_RewritesEveryIdReference()
    {
        string normalized = DeepSeekChatControl.NormalizeDiagramIdentity(
            MermaidSvgWithId("mermaid-svg-123-4"));

        // 根 id、样式选择器、marker id、url(#...) 引用都必须同步替换，
        // 否则箭头等引用会指向不存在的 id。
        normalized.Should().NotContain("mermaid-svg-123-4");
        normalized.Should().Contain("id=\"mermaid-diagram\"");
        normalized.Should().Contain("#mermaid-diagram .node");
        normalized.Should().Contain("url(#mermaid-diagram_flowchart-pointEnd)");
    }

    [Fact]
    public void ComputeDiagramHash_SameDiagramRenderedTwice_IsStable()
    {
        string first = DeepSeekChatControl.NormalizeDiagramSvg(
            MermaidSvgWithId("mermaid-svg-1792252615344-3"));
        string second = DeepSeekChatControl.NormalizeDiagramSvg(
            MermaidSvgWithId("mermaid-svg-1799999999999-12"));

        DeepSeekChatControl.ComputeDiagramHash(first)
            .Should().Be(DeepSeekChatControl.ComputeDiagramHash(second));
    }

    [Fact]
    public void ComputeDiagramHash_DifferentDiagrams_Differ()
    {
        string a = DeepSeekChatControl.NormalizeDiagramSvg(
            "<svg viewBox=\"0 0 10 10\"><text>模型 A</text></svg>");
        string b = DeepSeekChatControl.NormalizeDiagramSvg(
            "<svg viewBox=\"0 0 10 10\"><text>模型 B</text></svg>");

        DeepSeekChatControl.ComputeDiagramHash(a)
            .Should().NotBe(DeepSeekChatControl.ComputeDiagramHash(b));
    }

    [Fact]
    public void ComputeDiagramHash_Returns16HexChars()
    {
        DeepSeekChatControl.ComputeDiagramHash("<svg></svg>")
            .Should().MatchRegex("^[0-9A-F]{16}$");
    }

    [Fact]
    public void NormalizeDiagramSvg_StampsStableId()
    {
        string normalized = DeepSeekChatControl.NormalizeDiagramSvg(MermaidSvgWithId("mermaid-svg-1-1"));

        normalized.Should().Contain("mermaid-diagram");
        DeepSeekChatControl.IsWellFormedXml(normalized).Should().BeTrue();
    }

    // ── 过期清扫 ──
    // 图表是一次性辅助产物，按保留期清理；同时只允许删除自己的 diagram_*.svg，
    // 同目录下的日志/会话文件绝不能被牵连。

    private static string CreateDiagramTempDir() =>
        Path.Combine(Path.GetTempPath(), $"diagrams_{Guid.NewGuid():N}");

    private static string WriteDiagramFile(string dir, string name, DateTime lastWrite)
    {
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, "<svg/>");
        File.SetLastWriteTime(path, lastWrite);
        return path;
    }

    [Fact]
    public void CleanupExpiredDiagrams_RemovesOnlyExpiredFiles()
    {
        string dir = CreateDiagramTempDir();
        Directory.CreateDirectory(dir);

        string oldFile = WriteDiagramFile(dir, "diagram_old.svg", DateTime.Now.AddDays(-30));
        string freshFile = WriteDiagramFile(dir, "diagram_fresh.svg", DateTime.Now.AddDays(-1));

        try
        {
            int removed = DeepSeekChatControl.CleanupExpiredDiagrams(7, 200, dir);

            removed.Should().Be(1);
            File.Exists(oldFile).Should().BeFalse("超过保留期的图表应被清理");
            File.Exists(freshFile).Should().BeTrue("保留期内的图表应保留");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void CleanupExpiredDiagrams_RetentionBoundary_KeepsExactlyAtCutoff()
    {
        string dir = CreateDiagramTempDir();
        Directory.CreateDirectory(dir);

        // 保留 7 天：第 6 天保留，第 8 天清理（避开边界抖动）
        string insideFile = WriteDiagramFile(dir, "diagram_inside.svg", DateTime.Now.AddDays(-6));
        string outsideFile = WriteDiagramFile(dir, "diagram_outside.svg", DateTime.Now.AddDays(-8));

        try
        {
            int removed = DeepSeekChatControl.CleanupExpiredDiagrams(7, 200, dir);

            removed.Should().Be(1);
            File.Exists(insideFile).Should().BeTrue();
            File.Exists(outsideFile).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void CleanupExpiredDiagrams_IgnoresUnrelatedFiles()
    {
        string dir = CreateDiagramTempDir();
        Directory.CreateDirectory(dir);

        // 同目录下还有其他类型的文件（日志、会话、用户自建文件）
        string logFile = WriteDiagramFile(dir, "diagnostic-2020-01-01.log", DateTime.Now.AddDays(-900));
        string conversation = WriteDiagramFile(dir, "conversations.json", DateTime.Now.AddDays(-900));
        string handwritten = WriteDiagramFile(dir, "my-sketch.svg", DateTime.Now.AddDays(-900));
        string expiredDiagram = WriteDiagramFile(dir, "diagram_expired.svg", DateTime.Now.AddDays(-900));

        try
        {
            int removed = DeepSeekChatControl.CleanupExpiredDiagrams(7, 200, dir);

            removed.Should().Be(1);
            File.Exists(logFile).Should().BeTrue("日志文件不属于图表清理范围");
            File.Exists(conversation).Should().BeTrue("会话文件不属于图表清理范围");
            File.Exists(handwritten).Should().BeTrue("非 diagram_ 前缀的 SVG 不应被删除");
            File.Exists(expiredDiagram).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void CleanupExpiredDiagrams_OverCountLimit_RemovesOldestFirst()
    {
        string dir = CreateDiagramTempDir();
        Directory.CreateDirectory(dir);

        // 都在保留期内，但超出数量上限 → 只删最旧的
        var files = new List<string>();
        for (int i = 0; i < 5; i++)
        {
            files.Add(WriteDiagramFile(dir, $"diagram_{i}.svg", DateTime.Now.AddHours(-i - 1)));
        }

        try
        {
            int removed = DeepSeekChatControl.CleanupExpiredDiagrams(7, 3, dir);

            removed.Should().Be(2);
            File.Exists(files[0]).Should().BeTrue("最新的一张必须保留");
            File.Exists(files[4]).Should().BeFalse("最旧的应先被清理");
            Directory.GetFiles(dir, "diagram_*.svg").Length.Should().Be(3);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void CleanupExpiredDiagrams_MissingDirectory_DoesNotThrow()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"diagrams_missing_{Guid.NewGuid():N}");

        int removed = DeepSeekChatControl.CleanupExpiredDiagrams(7, 200, dir);

        removed.Should().Be(0);
    }

    [Fact]
    public void CleanupExpiredDiagrams_LockedFile_SkipsAndKeepsGoing()
    {
        string dir = CreateDiagramTempDir();
        Directory.CreateDirectory(dir);

        string lockedFile = WriteDiagramFile(dir, "diagram_locked.svg", DateTime.Now.AddDays(-30));
        string otherFile = WriteDiagramFile(dir, "diagram_other.svg", DateTime.Now.AddDays(-30));

        try
        {
            // 打开句柄模拟"文件正在被查看器占用"，此时删除会抛共享冲突
            using (var hold = new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                int removed = DeepSeekChatControl.CleanupExpiredDiagrams(7, 200, dir);

                removed.Should().Be(1, "被占用的文件跳过，其余过期文件仍应清理");
                File.Exists(lockedFile).Should().BeTrue();
                File.Exists(otherFile).Should().BeFalse();
            }
        }
        finally
        {
            // 句柄释放后再删目录，否则会因占用失败
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void CleanupDefaults_AreSaneValues()
    {
        DeepSeekChatControl.DiagramRetentionDays.Should().Be(7);
        DeepSeekChatControl.DiagramMaxFileCount.Should().Be(200);
    }
}
