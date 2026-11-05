using DeepSeek_v4_for_VisualStudio.Services.EditTools;
using DeepSeek_v4_for_VisualStudio.Services.Editing;
using DeepSeek_v4_for_VisualStudio.Services;
using System.Text.Json;
using BuiltInApplyPatchTool = DeepSeek_v4_for_VisualStudio.Services.BuiltInTools.ApplyPatchTool;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// 验证 apply_patch 对同一 hunk 内多个分离编辑段的解析和重建。
/// </summary>
public class ApplyPatchToolTests
{
    [Fact]
    public void ApplyPatchTool_ExpectedRemainsRequiredInSchema()
    {
        var parameters = JsonSerializer.SerializeToElement(
            new BuiltInApplyPatchTool().GetDefinition().Function.Parameters);

        var required = parameters.GetProperty("required")
            .EnumerateArray()
            .Select(item => item.GetString());

        required.Should().Equal("patch", "expected");
    }

    [Fact]
    public async Task ApplyPatchTool_RejectsMissingExpected()
    {
        var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            JsonSerializer.Serialize(new { patch = "*** Begin Patch\n*** End Patch" }))!;

        string result = await new BuiltInApplyPatchTool()
            .ExecuteAsync(args, workspaceRoot: null);

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task ApplyPatchTool_DeleteUsesDeletedMarker()
    {
        string tempPath = Path.Combine(
            Path.GetTempPath(), $"apply-patch-delete-{Guid.NewGuid():N}.txt");
        File.WriteAllText(tempPath, "obsolete\n");

        try
        {
            string patch =
                "*** Begin Patch\n" +
                $"*** Delete File: {tempPath}\n" +
                "*** End Patch";
            var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                JsonSerializer.Serialize(new
                {
                    patch,
                    expected = "(deleted)",
                }))!;

            string result = await new BuiltInApplyPatchTool()
                .ExecuteAsync(args, workspaceRoot: null);

            result.Should().NotStartWith("Error: ");
            File.Exists(tempPath).Should().BeFalse();
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    [Fact]
    public async Task ApplyPatchTool_LocationAnchorChoosesNearestMatchingHunk()
    {
        string tempPath = Path.Combine(
            Path.GetTempPath(), $"apply-patch-location-anchor-{Guid.NewGuid():N}.cs");
        File.WriteAllText(
            tempPath,
            "class Sample\n" +
            "{\n" +
            "    void Target()\n" +
            "    {\n" +
            "        oldCall();\n" +
            "    }\n\n" +
            "    void Other()\n" +
            "    {\n" +
            "        oldCall();\n" +
            "    }\n" +
            "}\n");

        try
        {
            string patch =
                "*** Begin Patch\n" +
                $"*** Update File: {tempPath}\n" +
                "@@ void Other()\n" +
                "-        oldCall();\n" +
                "+        newCall();\n" +
                "*** End Patch";
            var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                JsonSerializer.Serialize(new
                {
                    patch,
                    expected = "@void Other()",
                }))!;

            string result = await new BuiltInApplyPatchTool()
                .ExecuteAsync(args, workspaceRoot: null);

            result.Should().NotStartWith("Error: ");
            string content = File.ReadAllText(tempPath);
            content.Should().Contain("void Target()\n    {\n        oldCall();");
            content.Should().Contain("void Other()\n    {\n        newCall();");
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    [Fact]
    public async Task ApplyPatchTool_AllowsFarMarkerAndFuzzyContextFallback()
    {
        string tempPath = Path.Combine(
            Path.GetTempPath(), $"apply-patch-far-marker-{Guid.NewGuid():N}.cs");
        string padding = string.Join(
            "\n",
            Enumerable.Range(1, 20).Select(i => $"        step{i}();"));
        File.WriteAllText(
            tempPath,
            "class Sample\n" +
            "{\n" +
            "    public void Save(string token)\n" +
            "    {\n" +
            padding + "\n" +
            "        var encrypted = Encrypt(token);\n" +
            "        File.WriteAllBytes(_filePath, encrypted);\n" +
            "    }\n" +
            "}\n");

        try
        {
            string patch =
                "*** Begin Patch\n" +
                $"*** Update File: {tempPath}\n" +
                "@@ public void Save(string token)\n" +
                "          var encrypted = Encrypt(token);\n" +
                "+        // persisted atomically for this user\n" +
                "          File.WriteAllBytes(_filePath, encrypted);\n" +
                "*** End Patch";
            var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                JsonSerializer.Serialize(new
                {
                    patch,
                    expected = "@public void Save(string token)",
                }))!;

            string result = await new BuiltInApplyPatchTool()
                .ExecuteAsync(args, workspaceRoot: null);

            result.Should().NotStartWith("Error: ");
            File.ReadAllText(tempPath).Should()
                .Contain("// persisted atomically for this user");
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    [Fact]
    public async Task ApplyPatchTool_LocationAnchorMustBeUniqueBeforeWriting()
    {
        string tempPath = Path.Combine(
            Path.GetTempPath(), $"apply-patch-anchor-unique-{Guid.NewGuid():N}.cs");
        string original =
            "class Sample\n" +
            "{\n" +
            "    void A() { oldCall(); }\n" +
            "    void B() { oldCall(); }\n" +
            "}\n";
        File.WriteAllText(tempPath, original);

        try
        {
            string patch =
                "*** Begin Patch\n" +
                $"*** Update File: {tempPath}\n" +
                "@@ void A()\n" +
                "-    void A() { oldCall(); }\n" +
                "+    void A() { newCall(); }\n" +
                "*** End Patch";
            var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                JsonSerializer.Serialize(new
                {
                    patch,
                    expected = "@oldCall();",
                }))!;

            string result = await new BuiltInApplyPatchTool()
                .ExecuteAsync(args, workspaceRoot: null);

            result.Should().StartWith("Error: ");
            File.ReadAllText(tempPath).Should().Be(original);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    [Fact]
    public async Task ApplyPatchTool_MapsMultipleLocationAnchorsToMultipleHunks()
    {
        string tempPath = Path.Combine(
            Path.GetTempPath(), $"apply-patch-multi-anchor-{Guid.NewGuid():N}.cs");
        File.WriteAllText(
            tempPath,
            "class Sample\n" +
            "{\n" +
            "    void A()\n" +
            "    {\n" +
            "        oldA();\n" +
            "    }\n\n" +
            "    void B()\n" +
            "    {\n" +
            "        oldB();\n" +
            "    }\n" +
            "}\n");

        try
        {
            string patch =
                "*** Begin Patch\n" +
                $"*** Update File: {tempPath}\n" +
                "@@ void A()\n" +
                "-        oldA();\n" +
                "+        newA();\n" +
                "@@ void B()\n" +
                "-        oldB();\n" +
                "+        newB();\n" +
                "*** End Patch";
            var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                JsonSerializer.Serialize(new
                {
                    patch,
                    expected = "@void A()\n@void B()",
                }))!;

            string result = await new BuiltInApplyPatchTool()
                .ExecuteAsync(args, workspaceRoot: null);

            result.Should().NotStartWith("Error: ");
            File.ReadAllText(tempPath).Should().Contain("newA();").And.Contain("newB();");
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    [Fact]
    public void ParsePatches_MultipleUpdateFilesInSingleBlock_ReturnsTwoPatches()
    {
        string patchText =
            "*** Begin Patch\n" +
            "*** Update File: C:\\Temp\\A.cs\n" +
            "@@ void A()\n" +
            "-        oldA();\n" +
            "+        newA();\n" +
            "*** Update File: C:\\Temp\\B.cs\n" +
            "@@ void B()\n" +
            "-        oldB();\n" +
            "+        newB();\n" +
            "*** End Patch";

        var patches = ApplyPatchTool.ParsePatches(patchText);

        patches.Should().HaveCount(2);
        patches[0].FilePath.Should().Be("C:\\Temp\\A.cs");
        patches[1].FilePath.Should().Be("C:\\Temp\\B.cs");
        patches[0].Hunks.Should().ContainSingle();
        patches[1].Hunks.Should().ContainSingle();
    }

    [Fact]
    public async Task ApplyPatchTool_AppliesMultipleFilesInOneCall()
    {
        string tempA = Path.Combine(
            Path.GetTempPath(), $"apply-patch-file-a-{Guid.NewGuid():N}.cs");
        string tempB = Path.Combine(
            Path.GetTempPath(), $"apply-patch-file-b-{Guid.NewGuid():N}.cs");
        File.WriteAllText(tempA, "class A\n{\n    void A()\n    {\n        oldA();\n    }\n}\n");
        File.WriteAllText(tempB, "class B\n{\n    void B()\n    {\n        oldB();\n    }\n}\n");

        try
        {
            string patch =
                "*** Begin Patch\n" +
                $"*** Update File: {tempA}\n" +
                "@@ void A()\n" +
                "-        oldA();\n" +
                "+        newA();\n" +
                $"*** Update File: {tempB}\n" +
                "@@ void B()\n" +
                "-        oldB();\n" +
                "+        newB();\n" +
                "*** End Patch";
            var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                JsonSerializer.Serialize(new
                {
                    patch,
                    expected = "@void A()\n@void B()",
                }))!;

            string result = await new BuiltInApplyPatchTool()
                .ExecuteAsync(args, workspaceRoot: null);

            result.Should().NotStartWith("Error: ");
            File.ReadAllText(tempA).Should().Contain("newA();");
            File.ReadAllText(tempB).Should().Contain("newB();");
        }
        finally
        {
            if (File.Exists(tempA)) File.Delete(tempA);
            if (File.Exists(tempB)) File.Delete(tempB);
        }
    }

    [Fact]
    public async Task ApplyPatchTool_CrossFileFailureRestoresWorkspaceFiles()
    {
        string tempA = Path.Combine(
            Path.GetTempPath(), $"apply-patch-rollback-a-{Guid.NewGuid():N}.cs");
        string tempB = Path.Combine(
            Path.GetTempPath(), $"apply-patch-rollback-b-{Guid.NewGuid():N}.cs");
        string originalA = "class A\n{\n    void A()\n    {\n        oldA();\n    }\n}\n";
        string originalB = "class B\n{\n    void B()\n    {\n        oldB();\n    }\n}\n";
        File.WriteAllText(tempA, originalA);
        File.WriteAllText(tempB, originalB);

        try
        {
            var tool = new BuiltInApplyPatchTool
            {
                Workspace = new StagedEditWorkspace(),
            };
            string patch =
                "*** Begin Patch\n" +
                $"*** Update File: {tempA}\n" +
                "@@ void A()\n" +
                "-        oldA();\n" +
                "+        newA();\n" +
                $"*** Update File: {tempB}\n" +
                "@@ void B()\n" +
                "-        wrongB();\n" +
                "+        newB();\n" +
                "*** End Patch";
            var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                JsonSerializer.Serialize(new
                {
                    patch,
                    expected = "@void A()\n@void B()",
                }))!;

            string result = await tool.ExecuteAsync(args, workspaceRoot: null);

            result.Should().Contain("Hunk");
            File.ReadAllText(tempA).Should().Be(originalA);
            File.ReadAllText(tempB).Should().Be(originalB);
        }
        finally
        {
            if (File.Exists(tempA)) File.Delete(tempA);
            if (File.Exists(tempB)) File.Delete(tempB);
        }
    }

    [Fact]
    public async Task ExecutePatchesAsync_CrossFileWorkspaceFailureRollsBackFirstFile()
    {
        string tempA = Path.Combine(
            Path.GetTempPath(), $"apply-patch-core-a-{Guid.NewGuid():N}.cs");
        string tempB = Path.Combine(
            Path.GetTempPath(), $"apply-patch-core-b-{Guid.NewGuid():N}.cs");
        string originalA = "class A\n{\n    void A()\n    {\n        oldA();\n    }\n}\n";
        string originalB = "class B\n{\n    void B()\n    {\n        oldB();\n    }\n}\n";
        File.WriteAllText(tempA, originalA);
        File.WriteAllText(tempB, originalB);

        try
        {
            var workspace = new StagedEditWorkspace();
            var tool = new ApplyPatchTool(
                new DeepSeekApiService(new System.Net.Http.HttpClient()),
                string.Empty)
            {
                Workspace = workspace,
            };
            var first = new PatchOperation
            {
                Action = PatchFileAction.Update,
                FilePath = tempA,
                Hunks =
                {
                    new PatchHunk
                    {
                        ExpectedAnchor = "void A()",
                        ContextMarkers = { "void A()" },
                        Lines =
                        {
                            new PatchLine { Type = '-', Text = "        oldA();" },
                            new PatchLine { Type = '+', Text = "        newA();" },
                        },
                    },
                },
            };
            var second = new PatchOperation
            {
                Action = PatchFileAction.Update,
                FilePath = tempB,
                Hunks =
                {
                    new PatchHunk
                    {
                        ExpectedAnchor = "void B()",
                        ContextMarkers = { "void B()" },
                        Lines =
                        {
                            new PatchLine { Type = '-', Text = "        wrongB();" },
                            new PatchLine { Type = '+', Text = "        newB();" },
                        },
                    },
                },
            };

            var results = await tool.ExecutePatchesAsync(
                new List<PatchOperation> { first, second },
                CancellationToken.None);

            results.Should().HaveCount(2);
            results[0].Success.Should().BeTrue();
            results[1].Success.Should().BeFalse();
            File.ReadAllText(tempA).Should().Be(originalA);
            File.ReadAllText(tempB).Should().Be(originalB);
        }
        finally
        {
            if (File.Exists(tempA)) File.Delete(tempA);
            if (File.Exists(tempB)) File.Delete(tempB);
        }
    }

    [Fact]
    public async Task ApplyPatchTool_StructureRegressionDoesNotWriteFile()
    {
        string tempPath = Path.Combine(
            Path.GetTempPath(), $"apply-patch-structure-rollback-{Guid.NewGuid():N}.cs");
        string original =
            "class Sample\n" +
            "{\n" +
            "    void A()\n" +
            "    {\n" +
            "    }\n" +
            "}\n";
        File.WriteAllText(tempPath, original);

        try
        {
            string patch =
                "*** Begin Patch\n" +
                $"*** Update File: {tempPath}\n" +
                "@@ void A()\n" +
                "     void A()\n" +
                "     {\n" +
                "+        {\n" +
                "     }\n" +
                "*** End Patch";
            var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                JsonSerializer.Serialize(new
                {
                    patch,
                    expected = "@void A()",
                }))!;

            string result = await new BuiltInApplyPatchTool()
                .ExecuteAsync(args, workspaceRoot: null);

            result.Should().StartWith("Error: ");
            File.ReadAllText(tempPath).Should().Be(original);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    [Fact]
    public void ValidateStructureDelta_AllowsNaturallyUnbalancedFile()
    {
        string[] before = { "void Broken() {" };
        string[] after = { "void Broken() {", "    // comment only" };

        var errors = ApplyPatchTool.ValidateStructureDelta(before, after);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void ValidateStructureDelta_AllowsExistingUnterminatedStringState()
    {
        string[] before = { "var text = \"unterminated" };
        string[] after = { "var text = \"unterminated", "// comment only" };

        var errors = ApplyPatchTool.ValidateStructureDelta(before, after);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void ValidateStructureDelta_IgnoresParenInsideCharLiteral()
    {
        string[] before =
        {
            "void A()",
            "{",
            "}",
        };
        string[] after =
        {
            "void A()",
            "{",
            "    if (a == '(')",
            "    {",
            "    }",
            "}",
        };

        var errors = ApplyPatchTool.ValidateStructureDelta(before, after);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void ValidateStructureDelta_RejectsNewUnbalancedDelimiter()
    {
        string[] before = { "class Sample", "{", "}" };
        string[] after = { "class Sample", "{", "{", "}" };

        var errors = ApplyPatchTool.ValidateStructureDelta(before, after);

        errors.Should().NotBeEmpty();
    }

    [Fact]
    public void ApplySinglePatch_ReportsFailedHunkNumberAndMarker()
    {
        string tempPath = Path.Combine(
            Path.GetTempPath(), $"apply-patch-failed-hunk-{Guid.NewGuid():N}.cs");
        string original =
            "class Sample\n" +
            "{\n" +
            "    void A()\n" +
            "    {\n" +
            "        first();\n" +
            "    }\n\n" +
            "    void B()\n" +
            "    {\n" +
            "        one();\n" +
            "        two();\n" +
            "    }\n" +
            "}\n";
        File.WriteAllText(tempPath, original);

        try
        {
            var first = new PatchHunk
            {
                ExpectedAnchor = "void A()",
                ContextMarkers = { "void A()" },
                Lines =
                {
                    new PatchLine { Type = '-', Text = "        first();" },
                    new PatchLine { Type = '+', Text = "        changedFirst();" },
                },
            };
            var second = new PatchHunk
            {
                ExpectedAnchor = "void B()",
                ContextMarkers = { "void B()" },
                Lines =
                {
                    new PatchLine { Type = '-', Text = "        two();" },
                    new PatchLine { Type = '-', Text = "        one();" },
                    new PatchLine { Type = '+', Text = "        changed();" },
                },
            };
            var patch = new PatchOperation
            {
                Action = PatchFileAction.Update,
                FilePath = tempPath,
                Hunks = { first, second },
            };

            var result = ApplyPatchTool.ApplySinglePatch(patch, tempPath, original);

            result.Success.Should().BeFalse();
            result.ErrorMessage.Should().Contain("#2");
            result.ErrorMessage.Should().Contain("void B()");
            File.ReadAllText(tempPath).Should().Be(original);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    [Fact]
    public void ApplySinglePatch_PureInsertionWithoutContextFails()
    {
        string tempPath = Path.Combine(
            Path.GetTempPath(), $"apply-patch-pure-insert-{Guid.NewGuid():N}.cs");
        string original =
            "class Sample\n" +
            "{\n" +
            "    private static int Build() => 0;\n" +
            "}\n";
        File.WriteAllText(tempPath, original);

        try
        {
            var patch = new PatchOperation
            {
                Action = PatchFileAction.Update,
                FilePath = tempPath,
                Hunks =
                {
                    new PatchHunk
                    {
                        ExpectedAnchor = "private static int Build()",
                        ContextMarkers = { "private static int Build()" },
                        Lines =
                        {
                            new PatchLine { Type = '+', Text = "    // inserted without context" },
                        },
                    },
                },
            };

            var result = ApplyPatchTool.ApplySinglePatch(patch, tempPath, original);

            result.Success.Should().BeFalse();
            (result.ErrorMessage!.Contains("上下文")
                || result.ErrorMessage.Contains("context", StringComparison.OrdinalIgnoreCase))
                .Should().BeTrue();
            File.ReadAllText(tempPath).Should().Be(original);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    [Fact]
    public void ApplySinglePatch_AlreadyApplied_IsSuccessfulNoOp()
    {
        string tempPath = Path.Combine(
            Path.GetTempPath(), $"apply-patch-idempotent-{Guid.NewGuid():N}.txt");
        string appliedContent =
            "public MainView()\n" +
            "{\n" +
            "    // comment\n" +
            "    InitializeComponent();\n" +
            "}\n";
        File.WriteAllText(tempPath, appliedContent);

        try
        {
            var hunk = new PatchHunk
            {
                ExpectedAnchor = "public MainView()",
                Lines =
                {
                    new PatchLine { Type = ' ', Text = "public MainView()" },
                    new PatchLine { Type = ' ', Text = "{" },
                    new PatchLine { Type = '+', Text = "    // comment" },
                    new PatchLine { Type = ' ', Text = "    InitializeComponent();" },
                    new PatchLine { Type = ' ', Text = "}" },
                },
            };
            var patch = new PatchOperation
            {
                Action = PatchFileAction.Update,
                FilePath = tempPath,
                Hunks = { hunk },
            };

            var result = ApplyPatchTool.ApplySinglePatch(patch, tempPath, appliedContent);

            result.Success.Should().BeTrue();
            result.AppliedEdits.Should().BeEmpty();
            result.FinalContent.Should().Be(
                EditStringMatcher.NormalizeToCrLf(appliedContent));
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    [Fact]
    public void HunkToChunk_SplitsSeparatedEditsIntoMultipleSegments()
    {
        var hunk = new PatchHunk
        {
            Lines =
            {
                new PatchLine { Type = ' ', Text = "void Run()" },
                new PatchLine { Type = ' ', Text = "{" },
                new PatchLine { Type = '-', Text = "    alpha();" },
                new PatchLine { Type = '+', Text = "    alpha1();" },
                new PatchLine { Type = ' ', Text = "    beta();" },
                new PatchLine { Type = '-', Text = "    gamma();" },
                new PatchLine { Type = '+', Text = "    gamma1();" },
                new PatchLine { Type = ' ', Text = "}" },
            },
        };

        var (chunk, contextLines) = ApplyPatchTool.HunkToChunk(hunk);

        chunk.Should().NotBeNull();
        chunk!.Segments.Should().HaveCount(2);
        chunk.Segments[0].Offset.Should().Be(2);
        chunk.Segments[0].DelLines.Should().Equal("    alpha();");
        chunk.Segments[0].InsLines.Should().Equal("    alpha1();");
        chunk.Segments[1].Offset.Should().Be(4);
        chunk.Segments[1].DelLines.Should().Equal("    gamma();");
        chunk.Segments[1].InsLines.Should().Equal("    gamma1();");
        contextLines.Should().Equal(
            "void Run()",
            "{",
            "    alpha();",
            "    beta();",
            "    gamma();",
            "}");
    }

    [Fact]
    public void ReconstructFile_AppliesSeparatedEditsInOrder()
    {
        string[] original =
        {
            "void Run()",
            "{",
            "    alpha();",
            "    beta();",
            "    gamma();",
            "}",
        };
        var chunks = new List<FileChunk>
        {
            new FileChunk
            {
                OrigIndex = 2,
                DelLines = { "    alpha();" },
                InsLines = { "    alpha1();" },
            },
            new FileChunk
            {
                OrigIndex = 4,
                DelLines = { "    gamma();" },
                InsLines = { "    gamma1();" },
            },
        };

        string result = ApplyPatchTool.ReconstructFile(original, chunks);

        result.Replace("\r\n", "\n").Should().Be(
            "void Run()\n{\n    alpha1();\n    beta();\n    gamma1();\n}");
    }

    [Fact]
    public void ApplySinglePatch_PreservesContextBetweenMultipleReplacements()
    {
        string tempPath = Path.Combine(
            Path.GetTempPath(), $"apply-patch-multi-{Guid.NewGuid():N}.txt");
        string original = "void Run()\n{\n    alpha();\n    beta();\n    gamma();\n}\n";
        File.WriteAllText(tempPath, original);

        try
        {
            var hunk = new PatchHunk
            {
                ExpectedAnchor = "void Run()",
                Lines =
                {
                    new PatchLine { Type = ' ', Text = "void Run()" },
                    new PatchLine { Type = ' ', Text = "{" },
                    new PatchLine { Type = '-', Text = "    alpha();" },
                    new PatchLine { Type = '+', Text = "    alpha1();" },
                    new PatchLine { Type = ' ', Text = "    beta();" },
                    new PatchLine { Type = '-', Text = "    gamma();" },
                    new PatchLine { Type = '+', Text = "    gamma1();" },
                    new PatchLine { Type = ' ', Text = "}" },
                },
            };
            var patch = new PatchOperation
            {
                Action = PatchFileAction.Update,
                FilePath = tempPath,
                Hunks = { hunk },
            };

            var result = ApplyPatchTool.ApplySinglePatch(patch, tempPath, original);

            result.Success.Should().BeTrue();
            result.AppliedEdits.Should().HaveCount(2);
            result.AppliedEdits.Select(e => e.StartLine).Should().Equal(2, 4);
            result.FinalContent.Should().NotBeNull();
            result.FinalContent!.Replace("\r\n", "\n").Should().Be(
                "void Run()\n{\n    alpha1();\n    beta();\n    gamma1();\n}\n");
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    [Fact]
    public void ApplySinglePatch_PreservesClosingTokenInsertedBeforeMatchingContext()
    {
        string tempPath = Path.Combine(
            Path.GetTempPath(), $"apply-patch-closing-token-{Guid.NewGuid():N}.txt");
        string original = "config.IsVision.Should().BeTrue();\n}\n";
        File.WriteAllText(tempPath, original);

        try
        {
            var hunk = new PatchHunk
            {
                ExpectedAnchor = "config.IsVision.Should().BeTrue();",
                Lines =
                {
                    new PatchLine { Type = ' ', Text = "config.IsVision.Should().BeTrue();" },
                    new PatchLine { Type = '+', Text = "    }" },
                    new PatchLine { Type = ' ', Text = "}" },
                },
            };
            var patch = new PatchOperation
            {
                Action = PatchFileAction.Update,
                FilePath = tempPath,
                Hunks = { hunk },
            };

            var result = ApplyPatchTool.ApplySinglePatch(patch, tempPath, original);

            result.Success.Should().BeTrue();
            result.AppliedEdits.Should().HaveCount(1);
            result.FinalContent.Should().NotBeNull();
            result.FinalContent!.Replace("\r\n", "\n").Should().Be(
                "config.IsVision.Should().BeTrue();\n    }\n}\n");
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    [Fact]
    public void TrimTrailingDuplicateClosingTokens_PreservesInsertionContainingOnlyClosingToken()
    {
        var segment = new FileChunkSegment
        {
            Offset = 0,
            InsLines = { "    }" },
        };
        string[] fileLines = { "}" };

        ApplyPatchTool.TrimTrailingDuplicateClosingTokens(segment, 0, fileLines);

        segment.InsLines.Should().Equal("    }");
    }

    [Fact]
    public void TrimTrailingDuplicateClosingTokens_RemovesDuplicateAfterInsertedCode()
    {
        var segment = new FileChunkSegment
        {
            Offset = 1,
            InsLines = { "    newStatement();", "}" },
        };
        string[] fileLines = { "    existing();", "}" };

        ApplyPatchTool.TrimTrailingDuplicateClosingTokens(segment, 1, fileLines);

        segment.InsLines.Should().Equal("    newStatement();");
    }
}
