using System;
using System.Linq;
using DeepSeek_v4_for_VisualStudio.Models;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Models
{
    /// <summary>
    /// IdeContextSnapshot 格式化器单元测试（P1-A）。
    /// </summary>
    public class IdeContextModelsTests
    {
        private static IdeContextSnapshot Snapshot(Action<IdeContextSnapshot>? setup = null)
        {
            var s = new IdeContextSnapshot { FilePath = @"C:\repo\src\Renderer.cpp", CursorLine = 10, CursorColumn = 5 };
            setup?.Invoke(s);
            return s;
        }

        // ──────────────── 空态 ────────────────

        [Fact]
        public void ToPromptBlock_EmptySnapshot_ReturnsNull()
        {
            var s = new IdeContextSnapshot();
            s.HasContent.Should().BeFalse();
            s.ToPromptBlock().Should().BeNull();
        }

        [Fact]
        public void HasSelection_False_WhenWhitespaceOnly()
        {
            var s = Snapshot(x => x.SelectionText = "   ");
            s.HasSelection.Should().BeFalse();
        }

        [Fact]
        public void HasContent_True_WithFilePathOnly()
        {
            var s = new IdeContextSnapshot { FilePath = @"C:\x\a.cs" };
            s.HasContent.Should().BeTrue();
        }

        // ──────────────── 基本格式 ────────────────

        [Fact]
        public void ToPromptBlock_FileOnly_ContainsHeaderCursorAndPath()
        {
            var block = Snapshot().ToPromptBlock();

            block.Should().NotBeNull();
            block!.Should().StartWith("[IDE Context]");
            block.Should().Contain("Active File: C:\\repo\\src\\Renderer.cpp");
            block.Should().Contain("Cursor: line 10, col 5");
            block.Should().NotContain("Symbol:");
            block.Should().NotContain("Selection (");
            block.Should().NotContain("Diagnostics:");
        }

        [Fact]
        public void ToPromptBlock_RelativePath_WhenWorkspaceRootMatches()
        {
            var block = Snapshot().ToPromptBlock(@"C:\Repo");
            block.Should().Contain("Active File: src\\Renderer.cpp");
            block.Should().NotContain("C:\\Repo");
        }

        [Fact]
        public void ToPromptBlock_AbsolutePath_WhenOutsideWorkspaceRoot()
        {
            var block = Snapshot().ToPromptBlock(@"D:\other");
            block.Should().Contain(@"C:\repo\src\Renderer.cpp");
        }

        // ──────────────── 符号与当前行 ────────────────

        [Fact]
        public void ToPromptBlock_Symbol_IncludedWithCurrentLine()
        {
            var block = Snapshot(s =>
            {
                s.SymbolAtCursor = "Draw";
                s.SymbolLineText = "    void Renderer::Draw()   ";
            }).ToPromptBlock();

            block.Should().Contain("Symbol: Draw");
            block.Should().Contain("Current Line: void Renderer::Draw()");
        }

        [Fact]
        public void SymbolLineText_TruncatedToLimit()
        {
            var block = Snapshot(s =>
            {
                s.SymbolAtCursor = "X";
                s.SymbolLineText = new string('a', 250);
            }).ToPromptBlock();

            block!.Should().Contain(new string('a', 200));      // 截断到上限 200
            block.Should().NotContain(new string('a', 201));
            block.Should().Contain("…");
        }

        [Fact]
        public void SymbolLineText_ShortLine_KeptAsIs()
        {
            var block = Snapshot(s => { s.SymbolAtCursor = "X"; s.SymbolLineText = "short line"; })
                .ToPromptBlock();
            block!.Should().Contain("Current Line: short line");
        }

        // ──────────────── 选区 ────────────────

        [Fact]
        public void ToPromptBlock_Selection_SingleLineLabel_AndFenceLanguage()
        {
            var block = Snapshot(s =>
            {
                s.SelectionText = "foo->Update();";
                s.SelectionStartLine = 7;
                s.SelectionEndLine = 7;
            }).ToPromptBlock();

            block!.Should().Contain("Selection (line 7):");
            block.Should().Contain("```cpp");
            block.Should().Contain("foo->Update();");
        }

        [Fact]
        public void ToPromptBlock_Selection_RangeLabel()
        {
            var block = Snapshot(s =>
            {
                s.SelectionText = "a\nb\nc";
                s.SelectionStartLine = 3;
                s.SelectionEndLine = 5;
            }).ToPromptBlock();

            block!.Should().Contain("Selection (lines 3-5):");
        }

        [Fact]
        public void Selection_TruncatedByLineCount_AppendsNote()
        {
            var text = string.Join("\n", Enumerable.Range(1, 100).Select(i => $"line-{i}"));
            var block = Snapshot(s =>
            {
                s.SelectionText = text;
                s.SelectionStartLine = 1;
                s.SelectionEndLine = 100;
            }).ToPromptBlock();

            block!.Should().Contain("(selection truncated)");
            block.Should().Contain("line-40");      // 保留前 40 行
            block.Should().NotContain("line-41");   // 第 41 行被裁掉
        }

        [Fact]
        public void Selection_TruncatedByCharBudget_AppendsNoteAndEllipsis()
        {
            var longLine = new string('x', 3000); // 单行超字符预算
            var block = Snapshot(s =>
            {
                s.SelectionText = longLine;
                s.SelectionStartLine = 1;
                s.SelectionEndLine = 1;
            }).ToPromptBlock();

            block!.Should().Contain("…");
            block.Should().Contain("(selection truncated)");
        }

        // ──────────────── 诊断 ────────────────

        [Fact]
        public void Diagnostics_CountsFormatted_WithSingularPlural()
        {
            var block = Snapshot(s =>
            {
                s.Diagnostics.Add(new IdeDiagnosticItem { Severity = "error", Line = 12, Message = "E1" });
                s.Diagnostics.Add(new IdeDiagnosticItem { Severity = "error", Line = 13, Message = "E2" });
                s.Diagnostics.Add(new IdeDiagnosticItem { Severity = "warning", Line = 14, Message = "W1" });
            }).ToPromptBlock();

            block!.Should().Contain("Diagnostics: 2 errors / 1 warning");
            block.Should().Contain("- error line 12: E1");
            block.Should().Contain("- warning line 14: W1");
        }

        [Fact]
        public void Diagnostics_SingularForms()
        {
            var block = Snapshot(s =>
                s.Diagnostics.Add(new IdeDiagnosticItem { Severity = "error", Line = 1, Message = "only" })
            ).ToPromptBlock();

            block!.Should().Contain("Diagnostics: 1 error / 0 warnings");
        }

        [Fact]
        public void Diagnostics_ErrorsListedFirst_AndCappedAtSix()
        {
            var block = Snapshot(s =>
            {
                for (int i = 0; i < 8; i++)
                    s.Diagnostics.Add(new IdeDiagnosticItem { Severity = "warning", Line = i + 1, Message = $"W{i}" });
                s.Diagnostics.Add(new IdeDiagnosticItem { Severity = "error", Line = 99, Message = "CRITICAL" });
            }).ToPromptBlock();

            block!.Should().Contain("- error line 99: CRITICAL");       // 错误排最前
            block.Should().Match(b => System.Text.RegularExpressions.Regex.IsMatch(b!, @"\(\+3 more\)")); // 9 条只展示 6 条
            block.Should().NotContain("W6");                            // 被裁掉
        }

        [Fact]
        public void Diagnostics_MessageTruncatedTo120()
        {
            var msg = new string('m', 300);
            var block = Snapshot(s =>
                s.Diagnostics.Add(new IdeDiagnosticItem { Severity = "error", Line = 2, Message = msg })
            ).ToPromptBlock();

            block!.Length.Should().BeLessThan(msg.Length); // 截断生效
            block.Should().Contain(new string('m', 120));
            block.Should().NotContain(new string('m', 121));
        }

        // ──────────────── 围栏语言映射 ────────────────

        [Theory]
        [InlineData("a.cs", "csharp")]
        [InlineData("b.cpp", "cpp")]
        [InlineData("c.H", "cpp")]
        [InlineData("d.py", "python")]
        [InlineData("e.unknown", "")]
        public void GetFenceLanguage_MapsCommonExtensions(string file, string expected)
        {
            IdeContextSnapshot.GetFenceLanguage(file).Should().Be(expected);
        }

        // ──────────────── 符号提取（Case B 验收核心） ────────────────

        // 列号语义：0-based，指向光标左侧字符；光标停在词尾后一格时左偏好归属前词
        [Theory]
        [InlineData("void Renderer::Draw()", 7, "Renderer")]    // 词中
        [InlineData("void Renderer::Draw()", 13, "Renderer")]   // 词尾后一格
        [InlineData("foo->Update();", 6, "Update")]             // 词首
        public void ExtractIdentifierAt_InsideOrAdjacentWord_ReturnsWord(string line, int col, string expected)
        {
            IdeContextSnapshot.ExtractIdentifierAt(line, col).Should().Be(expected);
        }

        [Fact]
        public void ExtractIdentifierAt_OnSeparatorWithoutLeftWord_ReturnsNull()
        {
            IdeContextSnapshot.ExtractIdentifierAt("a + b", 2).Should().BeNull();       // 光标在 '+' 上，左侧非词
            IdeContextSnapshot.ExtractIdentifierAt("::Draw()", 1).Should().BeNull();    // 第二个 ':' 上
        }

        [Fact]
        public void ExtractIdentifierAt_SpaceAfterWord_PrefersLeftWord()
        {
            // 设计行为（与主流编辑器一致）：两词之间的空格位归属左侧词
            IdeContextSnapshot.ExtractIdentifierAt("foo bar", 3).Should().Be("foo");
        }

        [Theory]
        [InlineData("_count123", 0, "_count123")]   // 下划线/数字词
        [InlineData("x", 0, null)]                  // 单字符视为噪音
        public void ExtractIdentifierAt_WordShapeRules(string line, int col, string? expected)
        {
            IdeContextSnapshot.ExtractIdentifierAt(line, col).Should().Be(expected);
        }

        [Fact]
        public void ExtractIdentifierAt_ColumnBeyondEol_ClampsToLastChar()
        {
            IdeContextSnapshot.ExtractIdentifierAt("abc", 999).Should().Be("abc");
            IdeContextSnapshot.ExtractIdentifierAt("abc ", 999).Should().Be("abc"); // 收敛到空格后回退
        }

        [Fact]
        public void ExtractIdentifierAt_EmptyLine_ReturnsNull()
        {
            IdeContextSnapshot.ExtractIdentifierAt("", 0).Should().BeNull();
            IdeContextSnapshot.ExtractIdentifierAt("   ", 1).Should().BeNull();
        }
    }
}
