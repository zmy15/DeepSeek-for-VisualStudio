using DeepSeek_v4_for_VisualStudio.Services.BuiltInTools;
using DeepSeek_v4_for_VisualStudio.Utils;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Utils;

/// <summary>
/// 文件编码「读写一致」回归测试。
///
/// 修复前的缺陷：读侧用 <see cref="Encoding.UTF8"/>（替换回退）读取，非法字节被替换成 U+FFFD 乱码，
/// 而 catch(DecoderFallbackException) 的回退分支永不触发；写侧 FileEncodingHelper 只认 BOM，
/// 无 BOM 的 GBK 文件被当作 UTF-8 写回 → 编码被悄悄改变。
///
/// 修复后：读写共用同一套判定（BOM → 无 BOM UTF-16 启发式 → 严格 UTF-8 校验 → 系统 ANSI 回退），
/// 本测试锁定该契约。
/// </summary>
public class FileEncodingHelperTests : IDisposable
{
    /// <summary>含中文的源码样本（同时覆盖 ASCII 与多字节字符）。</summary>
    private const string SampleSource =
        "using System;\r\n" +
        "\r\n" +
        "// 中文注释：读写文件编码必须一致\r\n" +
        "namespace Demo\r\n" +
        "{\r\n" +
        "    public class 示例\r\n" +
        "    {\r\n" +
        "    }\r\n" +
        "}\r\n";

    private readonly string _tempDir;

    public FileEncodingHelperTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ds-encoding-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    /// <summary>
    /// 仅在中文 ANSI 代码页（GBK/936）下执行的测试。
    /// ANSI 回退编码取系统代码页，非中文区域设置（如 CI 默认的 1252）无法表示中文，
    /// 此时中文用例无意义 → 标记为 Skip 而不是失败。
    /// </summary>
    private sealed class ChineseAnsiFactAttribute : FactAttribute
    {
        public ChineseAnsiFactAttribute()
        {
            if (Encoding.Default.CodePage != 936)
                Skip = "仅在中文 ANSI 代码页（GBK/936）下有意义";
        }
    }

    private string NewPath(string extension = ".cs")
        => Path.Combine(_tempDir, $"sample-{Guid.NewGuid():N}{extension}");

    /// <summary>按指定编码写入文件，并在编码带 BOM 时写出 BOM（Encoding.GetBytes 本身不含 BOM）。</summary>
    private static void WriteBytes(string filePath, Encoding encoding, string content)
    {
        byte[] bytes = encoding.GetBytes(content);
        byte[] preamble = encoding.GetPreamble();
        if (preamble.Length == 0)
        {
            File.WriteAllBytes(filePath, bytes);
            return;
        }

        byte[] withPreamble = new byte[preamble.Length + bytes.Length];
        Buffer.BlockCopy(preamble, 0, withPreamble, 0, preamble.Length);
        Buffer.BlockCopy(bytes, 0, withPreamble, preamble.Length, bytes.Length);
        File.WriteAllBytes(filePath, withPreamble);
    }

    private static bool BytesEqual(byte[] left, byte[] right) => left.SequenceEqual(right);

    #region 读取：编码判定

    [Fact]
    public void ReadAllText_Utf8WithoutBom_ReadsContent()
    {
        string path = NewPath();
        WriteBytes(path, new UTF8Encoding(false), SampleSource);

        FileEncodingHelper.ReadAllText(path).Should().Be(SampleSource);
        BytesEqual(FileEncodingHelper.DetectFileEncoding(path).GetPreamble(), Array.Empty<byte>())
            .Should().BeTrue("无 BOM 的 UTF-8 文件写回时不应引入 BOM");
    }

    [Fact]
    public void ReadAllText_Utf8WithBom_StripsBom()
    {
        string path = NewPath();
        WriteBytes(path, new UTF8Encoding(true), SampleSource);

        string content = FileEncodingHelper.ReadAllText(path);

        content.Should().Be(SampleSource, "BOM 必须在读取时被剥离，不能混入内容");
        content.Should().NotStartWith("\uFEFF");
        BytesEqual(FileEncodingHelper.DetectFileEncoding(path).GetPreamble(), new byte[] { 0xEF, 0xBB, 0xBF })
            .Should().BeTrue();
    }

    [Fact]
    public void ReadAllText_Utf16LittleEndianWithBom_ReadsContent()
    {
        string path = NewPath();
        WriteBytes(path, new UnicodeEncoding(false, true), SampleSource);

        FileEncodingHelper.ReadAllText(path).Should().Be(SampleSource);
        FileEncodingHelper.DetectFileEncoding(path).CodePage.Should().Be(1200);
    }

    [Fact]
    public void ReadAllText_Utf16BigEndianWithoutBom_ReadsContent()
    {
        // 无 BOM 的 UTF-16 字节本身是合法 UTF-8（NUL 是合法字节），必须靠零字节启发式识别
        string path = NewPath();
        WriteBytes(path, new UnicodeEncoding(true, false), SampleSource);

        FileEncodingHelper.ReadAllText(path).Should().Be(SampleSource);
    }

    [Fact]
    public void ReadAllText_Utf16LittleEndianWithoutBom_DoesNotGainBomOnWriteBack()
    {
        string path = NewPath();
        WriteBytes(path, new UnicodeEncoding(false, false), SampleSource);

        FileEncodingHelper.ReadAllText(path).Should().Be(SampleSource);

        // 写回：保持「无 BOM 的 UTF-16LE」，不得凭空添加 BOM
        FileEncodingHelper.WriteAllText(path, SampleSource, workspaceRoot: null);

        byte[] written = File.ReadAllBytes(path);
        bool hasUtf16LeBom = written.Length >= 2 && written[0] == 0xFF && written[1] == 0xFE;
        hasUtf16LeBom.Should().BeFalse("无 BOM 的 UTF-16 文件写回时不应引入 BOM");
        new UnicodeEncoding(false, false).GetString(written).Should().Be(SampleSource);
    }

    [Fact]
    public void ReadAllText_NonUtf8Bytes_FallsBackToSystemAnsi()
    {
        string path = NewPath();
        // 0xD6 0xD0 0xCE 0xC4 是 GBK 的「中文」，作为 UTF-8 非法
        byte[] gbkBytes = { 0xD6, 0xD0, 0xCE, 0xC4 };
        File.WriteAllBytes(path, gbkBytes);

        string expected = Encoding.Default.GetString(gbkBytes);
        string content = FileEncodingHelper.ReadAllText(path);

        content.Should().Be(expected, "无法按 UTF-8 解码时应回退系统 ANSI，而不是产出 U+FFFD 替换字符");
        content.Should().NotContain("\uFFFD");
    }

    [ChineseAnsiFact]
    public void ReadAllText_GbkChineseSource_DecodesWithoutMojibake()
    {
        string path = NewPath();
        WriteBytes(path, Encoding.GetEncoding(936), SampleSource);

        string content = FileEncodingHelper.ReadAllText(path);

        content.Should().Be(SampleSource);
        content.Should().NotContain("\uFFFD");
        content.Should().Contain("中文注释");
    }

    [Fact]
    public void ReadAllText_EmptyFile_ReturnsEmpty()
    {
        string path = NewPath();
        File.WriteAllBytes(path, Array.Empty<byte>());

        FileEncodingHelper.ReadAllText(path).Should().BeEmpty();
        FileEncodingHelper.ReadAllLines(path).Should().BeEmpty();
    }

    [Fact]
    public void ReadAllText_MissingFile_ThrowsLikeFileReadAllText()
    {
        string path = Path.Combine(_tempDir, $"missing-{Guid.NewGuid():N}.cs");

        Action act = () => FileEncodingHelper.ReadAllText(path);

        act.Should().Throw<IOException>("与 File.ReadAllText 的失败语义保持一致");
    }

    #endregion

    #region 读取：按行切分语义

    [ChineseAnsiFact]
    public void ReadAllLines_GbkFile_SplitsLikeFileReadAllLines()
    {
        string gbkPath = NewPath();
        string utf8Path = NewPath();
        WriteBytes(gbkPath, Encoding.GetEncoding(936), SampleSource);
        WriteBytes(utf8Path, new UTF8Encoding(false), SampleSource);

        FileEncodingHelper.ReadAllLines(gbkPath).Should().Equal(File.ReadAllLines(utf8Path));
    }

    [Theory]
    [InlineData("a\r\nb\r\n", new[] { "a", "b" })]
    [InlineData("a\nb", new[] { "a", "b" })]
    [InlineData("a\n\n", new[] { "a", "" })]
    [InlineData("single", new[] { "single" })]
    public void ReadAllLines_MatchesFileReadAllLinesSemantics(string content, string[] expected)
    {
        string path = NewPath(".txt");
        WriteBytes(path, new UTF8Encoding(false), content);

        FileEncodingHelper.ReadAllLines(path).Should().Equal(expected);
        FileEncodingHelper.ReadAllLines(path).Should().Equal(File.ReadAllLines(path));
    }

    #endregion

    #region 写回：保持文件原有编码

    [ChineseAnsiFact]
    public void WriteAllText_GbkFile_KeepsGbkEncoding()
    {
        Encoding gbk = Encoding.GetEncoding(936);
        string path = NewPath();
        WriteBytes(path, gbk, SampleSource);

        // 模拟编辑工具：读取 → 修改 → 写回
        string content = FileEncodingHelper.ReadAllText(path);
        string updated = content.Replace("读写文件编码必须一致", "改成了新的中文说明");
        FileEncodingHelper.WriteAllText(path, updated, workspaceRoot: null);

        byte[] written = File.ReadAllBytes(path);
        gbk.GetString(written).Should().Be(updated, "GBK 文件写回后仍必须是 GBK");

        byte[] asUtf8 = new UTF8Encoding(false).GetBytes(updated);
        BytesEqual(written, asUtf8).Should().BeFalse("GBK 文件不得被悄悄转成 UTF-8");
        FileEncodingHelper.ReadAllText(path).Should().Be(updated);
    }

    [Fact]
    public void WriteAllText_Utf8File_StaysUtf8WithoutBom()
    {
        string path = NewPath();
        WriteBytes(path, new UTF8Encoding(false), SampleSource);

        FileEncodingHelper.WriteAllText(path, SampleSource, workspaceRoot: null);

        BytesEqual(File.ReadAllBytes(path), new UTF8Encoding(false).GetBytes(SampleSource)).Should().BeTrue();
    }

    #endregion

    #region read_file 工具端到端

    [ChineseAnsiFact]
    public async Task ReadFileTool_GbkFile_ReturnsChineseContentWithoutReplacementChars()
    {
        string path = NewPath();
        WriteBytes(path, Encoding.GetEncoding(936), SampleSource);

        string result = await ExecuteReadFileAsync(path);

        result.Should().Be(SampleSource, "read_file 必须按文件真实编码解码");
        result.Should().NotContain("\uFFFD");
        result.Should().Contain("中文注释");
    }

    [Fact]
    public async Task ReadFileTool_Utf8File_Unchanged()
    {
        string path = NewPath();
        WriteBytes(path, new UTF8Encoding(false), SampleSource);

        string result = await ExecuteReadFileAsync(path);

        result.Should().Be(SampleSource);
    }

    private static async Task<string> ExecuteReadFileAsync(string filePath)
    {
        var tool = new ReadFileTool(new ConcurrentDictionary<string, FileReadCacheEntry>());
        var args = new Dictionary<string, JsonElement>();
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { filePath }));
        foreach (var property in doc.RootElement.EnumerateObject())
            args[property.Name] = property.Value.Clone();

        return await tool.ExecuteAsync(args, workspaceRoot: null);
    }

    #endregion
}
