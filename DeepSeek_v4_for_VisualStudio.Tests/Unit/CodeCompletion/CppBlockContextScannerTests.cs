using DeepSeek_v4_for_VisualStudio.CodeCompletion;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.CodeCompletion;

/// <summary>
/// CppBlockContextScanner 单元测试 —— 验证 C/C++ 补全上下文能稳定拿到
/// "函数签名 → 右括号"的完整代码，且不受注释/字符串里的花括号干扰。
/// 该扫描器是纯文本实现（无 COM / 无语言服务），因此可以直接在单元测试中调用。
/// </summary>
public class CppBlockContextScannerTests
{
    #region 完整函数上下文

    [Fact]
    public void CaretInsideNestedBlock_IncludesWholeFunctionIncludingSignature()
    {
        var (text, caret) = WithCaret(
            "#include <cstdio>\n" +
            "\n" +
            "int Add(int a, int b)\n" +
            "{\n" +
            "    if (a > b)|\n" +
            "    {\n" +
            "        return a;\n" +
            "    }\n" +
            "    return a + b;\n" +
            "}\n" +
            "\n" +
            "void Other() { }\n");

        (int start, int end) = CppBlockContextScanner.GetContext(text, caret);

        string prefix = text.Substring(start, caret - start);
        string suffix = text.Substring(caret, end - caret);

        prefix.Should().Contain("int Add(int a, int b)");
        prefix.Should().Contain("#include <cstdio>");
        suffix.Should().Contain("return a + b;");
        suffix.TrimEnd().Should().EndWith("}");
        // 不能越界到下一个函数
        suffix.Should().NotContain("void Other");
    }

    [Fact]
    public void CaretInEmptyFunctionBody_StillIncludesSignature()
    {
        var (text, caret) = WithCaret("void F()\n{\n    |\n}\n\nvoid G() { }\n");

        (int start, int end) = CppBlockContextScanner.GetContext(text, caret);

        string prefix = text.Substring(start, caret - start);
        string suffix = text.Substring(caret, end - caret);

        prefix.Should().Contain("void F()");
        prefix.TrimEnd().Should().EndWith("{");
        // 空函数体：后缀至少要覆盖到右括号，不能为空
        suffix.Should().Contain("}");
        suffix.Should().NotContain("void G");
    }

    [Fact]
    public void CaretInClassMethod_IncludesClassHeaderAndMethodSignature()
    {
        var (text, caret) = WithCaret(
            "class Foo\n" +
            "{\n" +
            "public:\n" +
            "    void Method()\n" +
            "    {\n" +
            "        int a = 1;|\n" +
            "    }\n" +
            "};\n");

        (int start, int end) = CppBlockContextScanner.GetContext(text, caret);

        string prefix = text.Substring(start, caret - start);
        string suffix = text.Substring(caret, end - caret);

        prefix.Should().Contain("class Foo");
        prefix.Should().Contain("void Method()");
        // 后缀至少覆盖方法体右括号（小类还会顺带带上类体右括号）
        suffix.Should().Contain("}");
        suffix.Should().NotContain("public:");
    }

    [Fact]
    public void CaretAtClosingBrace_CountsAsInsideBlock()
    {
        var (text, caret) = WithCaret("void F()\n{\n    int x = 1;\n|}\n");

        (int start, int end) = CppBlockContextScanner.GetContext(text, caret);

        string prefix = text.Substring(start, caret - start);
        string suffix = text.Substring(caret, end - caret);

        prefix.Should().Contain("void F()");
        suffix.Should().StartWith("}");
    }

    [Fact]
    public void UnclosedFunctionBody_ExtendsToEndOfFile()
    {
        var (text, caret) = WithCaret("void F()\n{\n    int x = 1;\n|");

        (int start, int end) = CppBlockContextScanner.GetContext(text, caret);

        string prefix = text.Substring(start, caret - start);

        // 正在输入的函数体（右括号还没写）也必须能拿到签名
        prefix.Should().Contain("void F()");
        prefix.Should().Contain("int x = 1;");
        end.Should().Be(text.Length);
    }

    [Fact]
    public void LongFunction_DoesNotLeakPreviousFunctionIntoPrefix()
    {
        // 函数体足够长（> 最小前缀 600 字符），声明起点不会被最小前缀补足覆盖，
        // 因此前缀必须严格从当前函数签名开始。
        var builder = new System.Text.StringBuilder();
        builder.Append("void A()\n{\n    int a = 0;\n}\n\n");
        builder.Append("void B()\n{\n");
        for (int i = 0; i < 80; i++)
        {
            builder.Append("    total += ").Append(i).Append(";\n");
        }

        var (text, caret) = WithCaret(builder.Append("    |\n}\n").ToString());

        (int start, _) = CppBlockContextScanner.GetContext(text, caret);

        string prefix = text.Substring(start, caret - start).Trim();

        prefix.Should().StartWith("void B()");
        prefix.Should().NotContain("void A");
    }

    #endregion

    #region 非代码区域屏蔽

    [Fact]
    public void BracesInsideStringAndComment_AreIgnored()
    {
        var (text, caret) = WithCaret(
            "void F()\n" +
            "{\n" +
            "    const char* s = \"}{ {\";\n" +
            "    // } stray brace\n" +
            "    int x = 0;|\n" +
            "}\n" +
            "void G() { int y = 1; }\n");

        (_, int end) = CppBlockContextScanner.GetContext(text, caret);
        string suffix = text.Substring(caret, end - caret);

        // 若字符串/注释里的 } 被当成块闭合，后缀会被截断成空或越过 G
        suffix.Trim().Should().Be("}");
    }

    [Fact]
    public void BracesInsideRawString_AreIgnored()
    {
        var (text, caret) = WithCaret(
            "void F()\n" +
            "{\n" +
            "    auto s = R\"(}{ { )\";\n" +
            "    int x = 1;|\n" +
            "}\n");

        (_, int end) = CppBlockContextScanner.GetContext(text, caret);

        text.Substring(caret, end - caret).Trim().Should().Be("}");
    }

    [Fact]
    public void BracesInsideCharLiteral_AreIgnored()
    {
        var (text, caret) = WithCaret(
            "void F()\n" +
            "{\n" +
            "    char c = '{';\n" +
            "    int x = 0;|\n" +
            "}\n");

        (_, int end) = CppBlockContextScanner.GetContext(text, caret);

        text.Substring(caret, end - caret).Trim().Should().Be("}");
    }

    [Fact]
    public void MaskNonCode_MasksCommentsAndLiteralsButKeepsNewlines()
    {
        string source = "a // b {\n/* c } */\nd = \"e{\";\nchar q = '}';\n";

        string masked = CppBlockContextScanner.MaskNonCode(source);

        masked.Length.Should().Be(source.Length);
        masked.Should().NotContain("{");
        masked.Should().NotContain("}");
        masked.Count(c => c == '\n').Should().Be(source.Count(c => c == '\n'));
    }

    [Fact]
    public void MaskNonCode_KeepsDigitSeparatorUnmasked()
    {
        const string source = "long v = 1'000'000;";

        CppBlockContextScanner.MaskNonCode(source).Should().Be(source);
    }

    #endregion

    #region 预算与边界

    [Fact]
    public void TopLevelCaret_UsesDeclarationBounds()
    {
        var (text, caret) = WithCaret("int g = 1;\n\nvoid F()|\n{\n    g = 2;\n}\n");

        (int start, int end) = CppBlockContextScanner.GetContext(text, caret);

        string prefix = text.Substring(start, caret - start).Trim();
        string suffix = text.Substring(caret, end - caret);

        // 顶层光标：前缀止于当前声明（更早的顶层声明可被"最小前缀"补进来，属预期行为）
        prefix.Should().EndWith("void F()");
        suffix.Should().Contain("g = 2;");
    }

    [Fact]
    public void HugeFunction_PrefixIsTruncatedButNotBelowMinimum()
    {
        var builder = new System.Text.StringBuilder();
        builder.Append("void Big()\n{\n");
        for (int i = 0; i < 400; i++)
        {
            builder.Append("    counter += 1;\n");
        }

        var (text, caret) = WithCaret(builder.Append("    |\n}\n").ToString());

        (int start, _) = CppBlockContextScanner.GetContext(text, caret);

        (caret - start).Should().BeLessThanOrEqualTo(CppBlockContextScanner.DefaultMaxPrefixChars);
        (caret - start).Should().BeGreaterThanOrEqualTo(CppBlockContextScanner.DefaultMinPrefixChars);
        // 按行对齐：起点必须落在行首（0 或紧跟换行）
        if (start > 0)
        {
            text[start - 1].Should().Be('\n');
        }
    }

    [Fact]
    public void ShortBlock_PrefixPaddedToMinimumAroundCaret()
    {
        var builder = new System.Text.StringBuilder();
        for (int i = 0; i < 200; i++)
        {
            builder.Append("int v").Append(i).Append(" = ").Append(i).Append(";\n");
        }

        var (text, caret) = WithCaret(builder.Append("void F()\n{\n    |\n}\n").ToString());

        (int start, _) = CppBlockContextScanner.GetContext(text, caret);

        (caret - start).Should().BeGreaterThanOrEqualTo(CppBlockContextScanner.DefaultMinPrefixChars);
        if (start > 0)
        {
            text[start - 1].Should().Be('\n', "补足最小前缀后应落到行首");
        }
    }

    [Fact]
    public void EmptyText_ReturnsZeroRange()
    {
        CppBlockContextScanner.GetContext(string.Empty, 0).Should().Be((0, 0));
    }

    [Fact]
    public void CaretOutsideText_IsClamped()
    {
        const string text = "void F() { }\n";

        (int start, int end) = CppBlockContextScanner.GetContext(text, 9999);

        start.Should().BeGreaterThanOrEqualTo(0);
        start.Should().BeLessThanOrEqualTo(text.Length);
        end.Should().Be(text.Length);
    }

    [Fact]
    public void RandomCarets_AlwaysReturnValidRanges()
    {
        const string text =
            "#include <vector>\n" +
            "#include <string>\n" +
            "\n" +
            "namespace demo {\n" +
            "\n" +
            "struct Point\n" +
            "{\n" +
            "    int x = 0;\n" +
            "    int y = 0;\n" +
            "};\n" +
            "\n" +
            "int Sum(const std::vector<int>& values)\n" +
            "{\n" +
            "    int total = 0;\n" +
            "    for (int v : values)\n" +
            "    {\n" +
            "        total += v;\n" +
            "    }\n" +
            "    return total;\n" +
            "}\n" +
            "\n" +
            "};\n";

        var random = new Random(20261123);

        for (int i = 0; i < 400; i++)
        {
            int caret = random.Next(0, text.Length + 1);

            (int start, int end) = CppBlockContextScanner.GetContext(text, caret);

            start.Should().BeGreaterThanOrEqualTo(0);
            start.Should().BeLessThanOrEqualTo(caret);
            end.Should().BeGreaterThanOrEqualTo(caret);
            end.Should().BeLessThanOrEqualTo(text.Length);
            (caret - start).Should().BeLessThanOrEqualTo(CppBlockContextScanner.DefaultMaxPrefixChars);
            (end - caret).Should().BeLessThanOrEqualTo(CppBlockContextScanner.DefaultMaxSuffixChars);
        }
    }

    [Fact]
    public void CustomBudgets_AreRespected()
    {
        var (text, caret) = WithCaret(
            "void F()\n" +
            "{\n" +
            "    int a = 1;\n" +
            "    int b = 2;|\n" +
            "}\n");

        (int start, int end) = CppBlockContextScanner.GetContext(
            text, caret, minPrefixChars: 4, maxPrefixChars: 10, minSuffixChars: 2, maxSuffixChars: 6);

        (caret - start).Should().BeLessThanOrEqualTo(10);
        (end - caret).Should().BeLessThanOrEqualTo(6);
    }

    #endregion

    #region Helpers

    /// <summary>用 '|' 标记光标位置（测试夹具中不出现 C++ 的 '||' 运算符）。</summary>
    private static (string Text, int Caret) WithCaret(string template)
    {
        int caret = template.IndexOf('|');
        caret.Should().BeGreaterThanOrEqualTo(0, "夹具必须包含 '|' 光标标记");
        return (template.Remove(caret, 1), caret);
    }

    #endregion
}
