using System.IO;
using System.Text;
using DeepSeek_v4_for_VisualStudio.View;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.View;

/// <summary>
/// 「粘贴超长文本自动转存为附件」核心写入逻辑单元测试。
/// 覆盖 <see cref="DeepSeekChatControl.SavePastedTextToFile"/> 的文件命名、UTF-8 无 BOM 编码、
/// 目录自动创建与失败回退（返回 null）契约，以及转存阈值常量。
/// 交互层（DataObject.Pasting 处理器 / TryPasteLargeTextAsAttachment）依赖控件实例状态，
/// 属于 UI 集成范畴，不在本测试覆盖范围内。
/// </summary>
public class ClipboardPasteTextTests : IDisposable
{
    /// <summary>测试专用临时目录（每个测试实例独立，避免并行冲突）。</summary>
    private readonly string _testDir;

    public ClipboardPasteTextTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "DeepSeekVS_PasteTextTests_" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, recursive: true);
            }
        }
        catch
        {
            // 清理失败不影响测试结论（临时目录由系统回收）
        }
    }

    [Fact]
    public void PasteTextToFileThreshold_ShouldBe5000()
    {
        // 需求契约：长度 ≥ 5000 字符（含换行，按 UTF-16 string.Length 计数）的粘贴文本转存为附件
        DeepSeekChatControl.PasteTextToFileThreshold.Should().Be(5000);
    }

    [Fact]
    public void SavePastedTextToFile_WritesUtf8NoBomWithTimestampedName()
    {
        string content = "第一行\n第二行\r\n第三行 🚀 with ASCII and 中文混排";

        string? path = DeepSeekChatControl.SavePastedTextToFile(content, _testDir);

        path.Should().NotBeNull();
        string savedPath = path!;
        File.Exists(savedPath).Should().BeTrue();
        Path.GetDirectoryName(savedPath).Should().Be(_testDir);
        Path.GetFileName(savedPath).Should().MatchRegex(@"^paste_\d{8}_\d{6}_\d{3}\.txt$");

        byte[] bytes = File.ReadAllBytes(savedPath);
        bool hasUtf8Bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        hasUtf8Bom.Should().BeFalse("转存文件必须为 UTF-8 无 BOM 编码");
        Encoding.UTF8.GetString(bytes).Should().Be(content);
    }

    [Fact]
    public void SavePastedTextToFile_CreatesMissingTargetDirectory()
    {
        string nestedDir = Path.Combine(_testDir, "a", "b", "c");
        Directory.Exists(nestedDir).Should().BeFalse();

        string? path = DeepSeekChatControl.SavePastedTextToFile("payload", nestedDir);

        path.Should().NotBeNull();
        Directory.Exists(nestedDir).Should().BeTrue();
        File.ReadAllText(path!).Should().Be("payload");
    }

    [Fact]
    public void SavePastedTextToFile_ReturnsNull_WhenTargetDirIsExistingFile()
    {
        Directory.CreateDirectory(_testDir);
        string blockingFile = Path.Combine(_testDir, "blocking.txt");
        File.WriteAllText(blockingFile, "occupied");

        // 以已存在文件充当目标目录 → Directory.CreateDirectory 抛 IOException → 被捕获返回 null（调用方回退为默认粘贴）
        string? path = DeepSeekChatControl.SavePastedTextToFile("payload", blockingFile);

        path.Should().BeNull();
    }
}
