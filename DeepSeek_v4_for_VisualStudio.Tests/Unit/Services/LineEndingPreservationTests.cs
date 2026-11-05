using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Services.BuiltInTools;
using DeepSeek_v4_for_VisualStudio.Services.Editing;
using DeepSeek_v4_for_VisualStudio.Utils;
using System.Text;
using System.Text.Json;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// AI 编辑链路换行风格保持的回归测试。
///
/// 修复前：各编辑链路各自把内容硬编码成 CRLF（NormalizeToCrLf / Replace("\n","\r\n")），
/// 编辑一个 LF 文件会产生「整个文件都改了」的 diff。
/// 修复后：统一走 <see cref="LineEndingHelper"/>，沿用目标文件原本的风格；
/// 探测不到（新建文件）则回退系统默认 <see cref="Environment.NewLine"/>。
/// </summary>
public class LineEndingPreservationTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _backupRoot;

    public LineEndingPreservationTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"le_preserve_{Guid.NewGuid():N}");
        _backupRoot = Path.Combine(Path.GetTempPath(), $"le_backup_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_backupRoot);

        // 测试隔离：备份根指向临时目录，绝不触碰真实 %LOCALAPPDATA%\DeepSeekVS\backups
        BackupService.BaseDirOverride = _backupRoot;
        BackupService.EndSession();
    }

    public void Dispose()
    {
        BackupService.EndSession();
        BackupService.BaseDirOverride = null;
        try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, true); } catch { }
        try { if (Directory.Exists(_backupRoot)) Directory.Delete(_backupRoot, true); } catch { }
    }

    private string NewPath(string name) => Path.Combine(_tempRoot, name);

    /// <summary>File.WriteAllText 不做换行转换，可精确控制磁盘上的行尾风格。</summary>
    private string WriteSource(string name, string content)
    {
        var path = NewPath(name);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    private static Dictionary<string, JsonElement> ParseArgs(string json)
        => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)
           ?? new Dictionary<string, JsonElement>();

    #region 写入原语（所有编辑链路的公共出口）

    [Fact]
    public void WriteAllText_ExistingLfFile_StaysLf()
    {
        var file = WriteSource("primitive-lf.txt", "a\nb\n");

        FileEncodingHelper.WriteAllText(file, "x\r\ny\r\n", workspaceRoot: null);

        File.ReadAllText(file).Should().Be("x\ny\n");
    }

    [Fact]
    public void WriteAllText_ExistingCrlfFile_StaysCrlf()
    {
        var file = WriteSource("primitive-crlf.txt", "a\r\nb\r\n");

        FileEncodingHelper.WriteAllText(file, "x\ny\n", workspaceRoot: null);

        File.ReadAllText(file).Should().Be("x\r\ny\r\n");
    }

    [Fact]
    public void WriteAllText_NewFile_UsesSystemDefault()
    {
        var file = NewPath("primitive-new.txt");

        FileEncodingHelper.WriteAllText(file, "x\ny", workspaceRoot: null);

        File.ReadAllText(file).Should().Be("x" + Environment.NewLine + "y");
    }

    #endregion

    #region replace_string_in_file

    [Fact]
    public async Task ReplaceStringInFile_LfFile_StaysLf()
    {
        var file = WriteSource("replace-lf.txt", "alpha\nbeta\ngamma\n");

        var args = ParseArgs(JsonSerializer.Serialize(new
        {
            filePath = file,
            oldString = "beta",
            newString = "BETA",
        }));

        var result = await new ReplaceStringInFileTool().ExecuteAsync(args, workspaceRoot: null);

        result.Should().NotStartWith("Error: ");
        File.ReadAllText(file).Should().Be("alpha\nBETA\ngamma\n");
        File.ReadAllText(file).Should().NotContain("\r", "LF 文件不应被改写成 CRLF");
    }

    [Fact]
    public async Task ReplaceStringInFile_CrlfFile_StaysCrlf()
    {
        var file = WriteSource("replace-crlf.txt", "alpha\r\nbeta\r\ngamma\r\n");

        var args = ParseArgs(JsonSerializer.Serialize(new
        {
            filePath = file,
            oldString = "beta",
            newString = "BETA",
        }));

        var result = await new ReplaceStringInFileTool().ExecuteAsync(args, workspaceRoot: null);

        result.Should().NotStartWith("Error: ");
        File.ReadAllText(file).Should().Be("alpha\r\nBETA\r\ngamma\r\n");
    }

    #endregion

    #region create_file

    [Fact]
    public async Task CreateFile_NewFile_UsesSystemDefault()
    {
        var file = NewPath("created-new.cs");

        var args = ParseArgs(JsonSerializer.Serialize(new
        {
            filePath = file,
            content = "class Sample\n{\n}\n",
        }));

        var result = await new CreateFileTool().ExecuteAsync(args, workspaceRoot: null);

        result.Should().NotStartWith("Error: ");
        string expected = "class Sample" + Environment.NewLine
                        + "{" + Environment.NewLine
                        + "}" + Environment.NewLine;
        File.ReadAllText(file).Should().Be(expected);
    }

    [Fact]
    public async Task CreateFile_OverwriteExistingLfFile_StaysLf()
    {
        var file = WriteSource("overwrite.cs", "class Old\n{\n}\n");

        var args = ParseArgs(JsonSerializer.Serialize(new
        {
            filePath = file,
            content = "class New\r\n{\r\n}\r\n",
        }));

        var result = await new CreateFileTool().ExecuteAsync(args, workspaceRoot: null);

        result.Should().NotStartWith("Error: ");
        File.ReadAllText(file).Should().Be("class New\n{\n}\n", "覆盖已有 LF 文件时应沿用其原有风格");
    }

    #endregion

    #region StagedEditWorkspace 写穿（含 VS 编辑器 buffer 路径的统一收口点）

    [Fact]
    public void StagedWorkspace_WriteFile_LfFile_StaysLf()
    {
        var file = WriteSource("staged-lf.txt", "a\nb\n");

        var ws = new StagedEditWorkspace();
        ws.WriteFile(file, "a\r\nB\r\n");

        File.ReadAllText(file).Should().Be("a\nB\n");
    }

    [Fact]
    public void StagedWorkspace_WriteFile_CrlfFile_StaysCrlf()
    {
        var file = WriteSource("staged-crlf.txt", "a\r\nb\r\n");

        var ws = new StagedEditWorkspace();
        ws.WriteFile(file, "a\nB\n");

        File.ReadAllText(file).Should().Be("a\r\nB\r\n");
    }

    [Fact]
    public void StagedWorkspace_WriteFile_NewFile_UsesSystemDefault()
    {
        var file = NewPath("staged-new.txt");

        var ws = new StagedEditWorkspace();
        ws.WriteFile(file, "a\nb");

        File.ReadAllText(file).Should().Be("a" + Environment.NewLine + "b");
    }

    [Fact]
    public void StagedWorkspace_RepeatedWrites_StayStable()
    {
        // 首次写入定下风格后，后续多次写入不应发生风格漂移
        var file = WriteSource("staged-stable.txt", "a\nb\n");

        var ws = new StagedEditWorkspace();
        ws.WriteFile(file, "a\r\nB\r\n");
        ws.WriteFile(file, "a\nB\nC\n");
        ws.WriteFile(file, "a\r\nB\r\nC\r\nD\r\n");

        File.ReadAllText(file).Should().Be("a\nB\nC\nD\n");
        File.ReadAllText(file).Should().NotContain("\r");
    }

    #endregion
}
