using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.VisualStudio.Shell;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// C++ 测试支持 — 通过 CMake / CTest 运行测试工程。
    ///
    /// 设计取舍：
    /// - 只认 CMake 工程：C++ 没有统一的"测试项目文件"，CMakeLists.txt 里的
    ///   enable_testing / add_test / gtest_discover_tests 是跨框架（GoogleTest / Catch2 /
    ///   doctest / Qt Test）最稳定的共性特征。
    /// - 沿用已有构建目录：优先使用 CMakePresets.json 里 binaryDir 指向、且已经配置过
    ///   （含 CMakeCache.txt）的目录，避免与 VS / 用户命令行的构建状态互相踩踏。
    /// - 自带构建：与 dotnet test 对齐，跑测试前先 cmake --build（增量），
    ///   构建失败则直接判定失败，不再进入 ctest。
    /// - 结构化结果：ctest --output-junit 的 JUnit XML，语义等价于 .NET 侧的 TRX。
    ///   仅在 CMake &lt; 3.21（无 --output-junit）时退回解析 ctest 的汇总文本。
    /// - MSVC 环境：Ninja/cl.exe 与 AddressSanitizer 都依赖 vcvars64.bat 提供的
    ///   PATH/INCLUDE/LIB（ASan 运行时 clang_rt.asan_dynamic-*.dll 就在 MSVC bin 目录）
    ///   ，因此在检测到 cl.exe 或 /fsanitize=address 时按需注入。
    /// </summary>
    public partial class TestService
    {
        /// <summary>CMake 配置（configure）超时上限。</summary>
        private static readonly TimeSpan ConfigureTimeout = TimeSpan.FromMinutes(10);

        /// <summary>CMake 构建超时上限。</summary>
        private static readonly TimeSpan CmakeBuildTimeout = TimeSpan.FromMinutes(15);

        /// <summary>JUnit XML 出现前 ctest 的临时输出文件名。</summary>
        private const string CtestJUnitFilePrefix = "deepseek-ctest-";

        /// <summary>
        /// 扫描 CMake 工程时跳过的目录名：构建产物与依赖源码，避免把
        /// _deps/googletest-build 里的 CMakeLists.txt 或增量中间文件当成独立测试工程。
        /// </summary>
        private static readonly string[] CmakeSkippedDirectorySegments =
        {
            "bin", "obj", "out", "build", "cmake-build-debug", "cmake-build-release",
            "CMakeFiles", "_deps", "third_party", "node_modules", ".git", ".vs",
        };

        /// <summary>
        /// 判定 CMakeLists.txt 是否注册了测试。
        /// 覆盖 CMake 原生 add_test、CTest 模块与主流 C++ 测试框架的发现函数。
        /// </summary>
        private static readonly string[] CmakeTestMarkers =
        {
            "enable_testing(",
            "include(ctest)",
            "add_test(",
            "gtest_discover_tests(",
            "catch_discover_tests(",
            "doctest_discover_tests(",
            "qt_add_test",
        };

        #region C++ Test Project Discovery

        /// <inheritdoc />
        public IReadOnlyList<string> FindCppTestProjects(string? solutionPath)
        {
            string? root = StoragePaths.NormalizeToDirectory(solutionPath);
            if (root == null || !Directory.Exists(root))
                return Array.Empty<string>();

            var results = new List<string>();
            try
            {
                foreach (string cmakeLists in Directory.GetFiles(
                    root, "CMakeLists.txt", SearchOption.AllDirectories))
                {
                    if (IsUnderCmakeBuildOutput(cmakeLists, root))
                        continue;
                    if (!IsCmakeTestProjectFile(cmakeLists))
                        continue;

                    string? dir = Path.GetDirectoryName(cmakeLists);
                    if (!string.IsNullOrEmpty(dir))
                        results.Add(dir!);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[TestService] Scanning CMake test projects failed: {ex.Message}");
            }

            // 子目录工程会与根工程重复（根 CMakeLists 通常用 add_subdirectory 引入它们），
            // 去掉被其它候选目录包含的路径，只保留最外层的工程根。
            var distinct = results
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            results = distinct
                .Where(candidate => !distinct.Any(other =>
                    !string.Equals(other, candidate, StringComparison.OrdinalIgnoreCase) &&
                    candidate.StartsWith(
                        other.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                             + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase)))
                .ToList();

            Logger.Info($"[TestService] Found {results.Count} CMake test project(s) under {root}");
            return results;
        }

        /// <summary>
        /// 判断 CMakeLists.txt 是否注册了测试（按内容特征匹配，不解析 CMake 脚本）。
        /// </summary>
        internal static bool IsCmakeTestProjectFile(string cmakeListsPath)
        {
            try
            {
                string lowered = File.ReadAllText(cmakeListsPath).ToLowerInvariant();
                return CmakeTestMarkers.Any(marker => lowered.Contains(marker));
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 判断路径是否位于构建产物 / 依赖目录下（扫描 CMakeLists.txt 时排除）。
        /// </summary>
        private static bool IsUnderCmakeBuildOutput(string path, string root)
        {
            try
            {
                string rootFull = Path.GetFullPath(root)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                string pathFull = Path.GetFullPath(path);

                string relative = pathFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)
                    ? pathFull.Substring(rootFull.Length)
                    : pathFull;

                // 只看目录部分：文件名本身（CMakeLists.txt）不参与匹配
                string? directory = Path.GetDirectoryName(relative);
                if (string.IsNullOrEmpty(directory))
                    return false;

                string[] segments = directory.Split(
                    new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                    StringSplitOptions.RemoveEmptyEntries);

                return segments.Any(s => CmakeSkippedDirectorySegments.Any(skipped =>
                    s.Equals(skipped, StringComparison.OrdinalIgnoreCase) ||
                    s.StartsWith("cmake-build-", StringComparison.OrdinalIgnoreCase)));
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region C++ Project Resolution

        /// <summary>
        /// 判断显式传入的 project 参数是否指向 C++ 工程
        /// （CMakeLists.txt 文件、含 CMakeLists.txt 的工程目录、或构建目录里的 CMakeCache.txt）。
        /// </summary>
        internal static bool IsCppProjectRequest(string? projectPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath))
                return false;

            try
            {
                if (Directory.Exists(projectPath))
                    return File.Exists(Path.Combine(projectPath!, "CMakeLists.txt"));

                string name = Path.GetFileName(projectPath!.TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

                return name.Equals("CMakeLists.txt", StringComparison.OrdinalIgnoreCase) ||
                       name.Equals("CMakePresets.json", StringComparison.OrdinalIgnoreCase) ||
                       name.Equals("CMakeCache.txt", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 把 C++ 工程入参归一化为工程根目录：
        /// - CMakeLists.txt → 所在目录
        /// - CMakePresets.json → 所在目录
        /// - 已配置的构建目录（含 CMakeCache.txt）→ 向上回溯到含 CMakeLists.txt 的工程根
        /// - 其他目录 → 原样返回
        /// </summary>
        internal static string ResolveCppProjectDirectory(string projectPath, string? solutionPath)
        {
            try
            {
                if (Directory.Exists(projectPath))
                    return NormalizeBuildDirToProjectRoot(Path.GetFullPath(projectPath));

                if (File.Exists(projectPath))
                {
                    string? dir = Path.GetDirectoryName(projectPath);
                    if (!string.IsNullOrEmpty(dir))
                        return NormalizeBuildDirToProjectRoot(Path.GetFullPath(dir));
                }
            }
            catch { }

            string? root = StoragePaths.NormalizeToDirectory(solutionPath);
            return root ?? Path.GetFullPath(projectPath);
        }

        /// <summary>
        /// 若目录本身是 CMake 构建目录（有 CMakeCache.txt 但没有 CMakeLists.txt），
        /// 向上回溯到工程根；否则原样返回。
        /// </summary>
        private static string NormalizeBuildDirToProjectRoot(string directory)
        {
            if (File.Exists(Path.Combine(directory, "CMakeLists.txt")))
                return directory;

            string? current = directory;
            for (int depth = 0; depth < 8 && current != null; depth++)
            {
                if (File.Exists(Path.Combine(current, "CMakeLists.txt")))
                    return current;

                current = Path.GetDirectoryName(current?.TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) ?? string.Empty);
            }

            return directory;
        }

        /// <summary>
        /// 在工作区内选择唯一的 CMake/CTest 测试工程；多个候选时填入 CandidateProjects。
        /// </summary>
        private string? ResolveCppTestProject(string? solutionPath, TestRunResult result)
        {
            var projects = FindCppTestProjects(solutionPath);
            if (projects.Count == 0)
                return null;

            if (projects.Count > 1)
            {
                Logger.Info($"[TestService] Multiple CMake test projects found, project must be specified: {projects.Count}");
                result.Kind = TestProjectKind.Cpp;
                result.Error = TestRunErrorKind.MultipleProjects;
                result.CandidateProjects.AddRange(projects);
                return null;
            }

            return projects[0];
        }

        #endregion

        #region CMake Preset / Build Directory Resolution

        /// <summary>CMakePresets.json 中单个 configurePreset 的解析结果。</summary>
        internal sealed class CmakePresetInfo
        {
            public string Name { get; set; } = string.Empty;
            public bool Hidden { get; set; }

            /// <summary>未展开宏的 binaryDir 原值（继承合并时按子 preset 名重新展开）。</summary>
            public string RawBinaryDir { get; set; } = string.Empty;

            /// <summary>已展开宏（${sourceDir} 等）的构建目录绝对路径。</summary>
            public string BinaryDir { get; set; } = string.Empty;

            public string Generator { get; set; } = string.Empty;

            /// <summary>继承链合并后的 cacheVariables（子覆盖父）。</summary>
            public Dictionary<string, string> CacheVariables { get; } =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            /// <summary>直接父 preset 名（用于选择构建配置时向上回溯）。</summary>
            public List<string> Inherits { get; } = new List<string>();
        }

        /// <summary>
        /// 解析 CMakePresets.json 的 configurePresets，展开 inherits 继承链与路径宏。
        /// 不依赖 CMake 可执行文件，因此可在无 cmake 环境下单独测试。
        /// </summary>
        internal static List<CmakePresetInfo> ParseCmakeConfigurePresets(
            string presetsJson, string sourceDir, string? fileDir = null)
        {
            var presets = new Dictionary<string, CmakePresetInfo>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();

            using (var doc = JsonDocument.Parse(presetsJson))
            {
                if (!doc.RootElement.TryGetProperty("configurePresets", out var array) ||
                    array.ValueKind != JsonValueKind.Array)
                {
                    return new List<CmakePresetInfo>();
                }

                foreach (var element in array.EnumerateArray())
                {
                    var preset = new CmakePresetInfo
                    {
                        Name = ReadString(element, "name"),
                        Hidden = ReadBool(element, "hidden"),
                        RawBinaryDir = ReadString(element, "binaryDir"),
                        Generator = ReadString(element, "generator"),
                    };

                    if (string.IsNullOrEmpty(preset.Name))
                        continue;

                    ReadInherits(element, preset.Inherits);

                    if (element.TryGetProperty("cacheVariables", out var cache) &&
                        cache.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var item in cache.EnumerateObject())
                        {
                            if (item.Value.ValueKind == JsonValueKind.String)
                                preset.CacheVariables[item.Name] = item.Value.GetString() ?? string.Empty;
                        }
                    }

                    if (!presets.ContainsKey(preset.Name))
                        order.Add(preset.Name);
                    presets[preset.Name] = preset;
                }
            }

            // ── 继承链合并：把父 preset 的 binaryDir / generator / cacheVariables 补进子 preset ──
            string effectiveFileDir = fileDir ?? sourceDir;
            var resolved = new List<CmakePresetInfo>();
            foreach (string name in order)
            {
                var merged = ResolvePreset(presets, name, sourceDir, effectiveFileDir, new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase));
                if (merged != null)
                    resolved.Add(merged);
            }

            return resolved;
        }

        /// <summary>
        /// 展开单个 preset 的继承链：子 preset 显式给出的字段优先，其余从父 preset 继承。
        /// visited 用于阻断循环 inherits。
        /// </summary>
        private static string ReadString(JsonElement element, string property)
        {
            return element.TryGetProperty(property, out var value) &&
                   value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
        }

        private static bool ReadBool(JsonElement element, string property)
        {
            return element.TryGetProperty(property, out var value) &&
                   (value.ValueKind == JsonValueKind.True ||
                    (value.ValueKind == JsonValueKind.String &&
                     bool.TryParse(value.GetString(), out bool parsed) && parsed));
        }

        /// <summary>读取 inherits，兼容字符串与字符串数组两种写法。</summary>
        private static void ReadInherits(JsonElement element, List<string> target)
        {
            if (!element.TryGetProperty("inherits", out var inherits))
                return;

            if (inherits.ValueKind == JsonValueKind.String)
            {
                string value = inherits.GetString() ?? string.Empty;
                if (value.Length > 0)
                    target.Add(value);
            }
            else if (inherits.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in inherits.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        string value = item.GetString() ?? string.Empty;
                        if (value.Length > 0)
                            target.Add(value);
                    }
                }
            }
        }

        /// <summary>
        /// 展开单个 preset 的继承链：子 preset 显式给出的字段优先，其余从父 preset 继承。
        /// visited 用于阻断循环 inherits。
        /// </summary>
        private static CmakePresetInfo? ResolvePreset(
            Dictionary<string, CmakePresetInfo> presets,
            string name,
            string sourceDir,
            string fileDir,
            HashSet<string> visited)
        {
            if (!presets.TryGetValue(name, out var preset) || !visited.Add(name))
                return null;

            var result = new CmakePresetInfo
            {
                Name = preset.Name,
                Hidden = preset.Hidden,
                RawBinaryDir = preset.RawBinaryDir,
                Generator = preset.Generator,
            };
            result.Inherits.AddRange(preset.Inherits);

            foreach (string parentName in preset.Inherits)
            {
                var parent = ResolvePreset(presets, parentName, sourceDir, fileDir, visited);
                if (parent == null)
                    continue;

                if (string.IsNullOrEmpty(result.RawBinaryDir))
                    // 注意：继承来的 binaryDir 里可能含 ${presetName}，
                    // 必须用「子 preset 的名字」重新展开，否则会落到父 preset 的目录。
                    result.RawBinaryDir = parent.RawBinaryDir;
                if (string.IsNullOrEmpty(result.Generator))
                    result.Generator = parent.Generator;

                foreach (var kvp in parent.CacheVariables)
                {
                    if (!result.CacheVariables.ContainsKey(kvp.Key))
                        result.CacheVariables[kvp.Key] = kvp.Value;
                }

            }

            // 子 preset 自己的 cacheVariables 覆盖继承值
            foreach (var kvp in preset.CacheVariables)
                result.CacheVariables[kvp.Key] = kvp.Value;

            result.BinaryDir = ExpandCmakeMacros(result.RawBinaryDir, sourceDir, fileDir, name);
            return result;
        }

        /// <summary>
        /// 展开 CMakePresets 路径宏。只实现与构建目录定位相关的常见宏。
        /// </summary>
        internal static string ExpandCmakeMacros(string value, string sourceDir, string fileDir, string presetName)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            string expanded = value
                .Replace("${sourceDir}", sourceDir)
                .Replace("${sourceParentDir}", Path.GetDirectoryName(
                    sourceDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                    ?? sourceDir)
                .Replace("${fileDir}", fileDir)
                .Replace("${presetName}", presetName)
                .Replace("${hostSystemName}", "Windows")
                .Replace("${dollar}", "$");

            try
            {
                if (!Path.IsPathRooted(expanded))
                    expanded = Path.GetFullPath(Path.Combine(sourceDir, expanded));
                else
                    expanded = Path.GetFullPath(expanded);
            }
            catch
            {
                // 保留原值，交由调用方判断目录是否存在
            }

            return expanded;
        }

        /// <summary>本次选定的 CMake 构建目标（预设、构建目录、配置名）。</summary>
        internal sealed class CmakeBuildTarget
        {
            /// <summary>要传给 cmake --preset 的预设名；为空表示直接 -S/-B 配置。</summary>
            public string? PresetName { get; set; }

            /// <summary>已解析的构建目录绝对路径。</summary>
            public string BuildDirectory { get; set; } = string.Empty;

            /// <summary>多配置生成器（Visual Studio 等）需要的 -C/--config 值；单配置生成器为空。</summary>
            public string? Configuration { get; set; }

            /// <summary>该构建目录是否已经配置过（含 CMakeCache.txt）。</summary>
            public bool IsConfigured { get; set; }

            /// <summary>所选预设的 cacheVariables（用于判断是否需要 MSVC 环境）。</summary>
            public Dictionary<string, string> CacheVariables { get; } =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 决定本次 CTest 使用哪个构建目录。
        ///
        /// 优先级：
        /// 1. configuration 参数显式点名的 preset（同时支持把 Debug/Release 当作配置名）
        /// 2. CMakePresets.json 中「已配置过」的非 hidden preset（含 CMakeCache.txt）
        /// 3. CMakePresets.json 中首个非 hidden preset
        /// 4. 工作区内既有的 build / out/build/* / cmake-build-* 目录
        /// 5. 兜底：&lt;工程根&gt;/build（由调用方触发一次 configure）
        /// </summary>
        internal static CmakeBuildTarget ResolveCmakeBuildTarget(string projectDir, string? configuration)
        {
            string presetsPath = Path.Combine(projectDir, "CMakePresets.json");
            var candidates = new List<CmakePresetInfo>();
            if (File.Exists(presetsPath))
            {
                try
                {
                    candidates = ParseCmakeConfigurePresets(
                        File.ReadAllText(presetsPath), projectDir, projectDir);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[TestService] Parsing CMakePresets.json failed: {ex.Message}");
                }
            }

            var visible = candidates.Where(p => !p.Hidden).ToList();

            // ── 1. 显式指定 preset 名 ──
            if (!string.IsNullOrWhiteSpace(configuration))
            {
                var explicitPreset = visible.FirstOrDefault(p =>
                    p.Name.Equals(configuration, StringComparison.OrdinalIgnoreCase));
                if (explicitPreset != null)
                    return BuildTargetFromPreset(explicitPreset, configuration!.Trim());
            }

            // ── 2. 首选已配置过的预设 ──
            var configured = visible.FirstOrDefault(p =>
                !string.IsNullOrEmpty(p.BinaryDir) &&
                File.Exists(Path.Combine(p.BinaryDir, "CMakeCache.txt")));
            if (configured != null)
                return BuildTargetFromPreset(configured, configuration);

            // ── 3. 首个非 hidden 预设 ──
            var first = visible.FirstOrDefault();
            if (first != null && !string.IsNullOrEmpty(first.BinaryDir))
                return BuildTargetFromPreset(first, configuration);

            // ── 4. 既有构建目录 ──
            string? existing = FindExistingCmakeBuildDirectory(projectDir);
            if (existing != null)
            {
                return new CmakeBuildTarget
                {
                    BuildDirectory = existing,
                    Configuration = configuration,
                    IsConfigured = File.Exists(Path.Combine(existing, "CMakeCache.txt")),
                };
            }

            // ── 5. 兜底默认目录 ──
            return new CmakeBuildTarget
            {
                BuildDirectory = Path.Combine(projectDir, "build"),
                Configuration = configuration,
                IsConfigured = false,
            };
        }

        private static CmakeBuildTarget BuildTargetFromPreset(
            CmakePresetInfo preset, string? configuration)
        {
            var target = new CmakeBuildTarget
            {
                PresetName = preset.Name,
                BuildDirectory = preset.BinaryDir,
                IsConfigured = !string.IsNullOrEmpty(preset.BinaryDir) &&
                               File.Exists(Path.Combine(preset.BinaryDir, "CMakeCache.txt")),
            };

            foreach (var kvp in preset.CacheVariables)
                target.CacheVariables[kvp.Key] = kvp.Value;

            // 单配置生成器（Ninja 等）用 CMAKE_BUILD_TYPE 表达配置，
            // 多配置生成器（Visual Studio 等）才需要 -C / --config。
            bool multiConfig = preset.Generator.IndexOf("Visual Studio", StringComparison.OrdinalIgnoreCase) >= 0 ||
                               preset.Generator.IndexOf("Xcode", StringComparison.OrdinalIgnoreCase) >= 0;
            if (multiConfig)
            {
                target.Configuration = configuration;
            }
            else if (preset.CacheVariables.TryGetValue("CMAKE_BUILD_TYPE", out string? buildType) &&
                     !string.IsNullOrEmpty(buildType))
            {
                // 让 ctest -C 与构建类型保持一致，避免多配置语义下的错配
                target.Configuration = null;
                Logger.Info($"[TestService] CMake single-config generator, CMAKE_BUILD_TYPE={buildType}");
            }

            return target;
        }

        /// <summary>
        /// 在既有目录里查找可用于 ctest 的构建目录（含 CTestTestfile.cmake）。
        /// </summary>
        private static string? FindExistingCmakeBuildDirectory(string projectDir)
        {
            var roots = new List<string> { Path.Combine(projectDir, "build"), Path.Combine(projectDir, "out", "build") };

            foreach (string root in roots)
            {
                if (!Directory.Exists(root))
                    continue;

                if (HasCtestFile(root))
                    return root;

                try
                {
                    foreach (string sub in Directory.GetDirectories(root))
                    {
                        if (HasCtestFile(sub))
                            return sub;
                    }
                }
                catch { }
            }

            return null;
        }

        private static bool HasCtestFile(string directory)
        {
            try
            {
                return File.Exists(Path.Combine(directory, "CTestTestfile.cmake")) ||
                       File.Exists(Path.Combine(directory, "CTestTestfile.cmake.in"));
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region CTest Execution

        /// <summary>
        /// 运行 C++/CMake 测试：可选的 configure → cmake --build → ctest。
        /// 结果从 JUnit XML 解析；不支持 --output-junit 的老 ctest 退回解析汇总文本。
        /// </summary>
        private async Task RunCmakeTestsAsync(
            string projectDir,
            string? filter,
            string? configuration,
            TestRunResult result,
            CancellationToken ct)
        {
            result.Kind = TestProjectKind.Cpp;
            result.ProjectPath = projectDir;
            result.Filter = filter ?? string.Empty;
            // 走到这里说明前面的 .NET 探测已经失败（通常是 NoTestProject），
            // 技术栈以 C++ 路径的结论为准，避免把上一段的错误状态带出去。
            result.Error = TestRunErrorKind.None;
            result.CandidateProjects.Clear();

            // VS 安装根目录只需解析一次，cmake / ctest / vcvars 都复用它，
            // 避免重复跨线程访问 SVsShell。
            string? vsRoot = await TryGetVisualStudioRootAsync();

            string? cmake = FindCmakeExecutable(vsRoot);
            if (cmake == null)
            {
                Logger.Warn("[TestService] cmake executable not found");
                result.Error = TestRunErrorKind.RunnerNotFound;
                return;
            }

            string? ctest = FindCtestExecutable(cmake);
            if (ctest == null)
            {
                Logger.Warn("[TestService] ctest executable not found");
                result.Error = TestRunErrorKind.RunnerNotFound;
                return;
            }

            var target = ResolveCmakeBuildTarget(projectDir, configuration);
            Logger.Info(
                $"[TestService] CMake target: preset={target.PresetName ?? "(none)"}, " +
                $"buildDir={target.BuildDirectory}, configured={target.IsConfigured}");

            // MSVC 环境按需注入：Ninja/cl.exe 与 ASan 运行时 DLL 都依赖 vcvars 的 PATH/INCLUDE/LIB。
            var environment = TryGetMsvcEnvironment(target, vsRoot);

            // ── 1. 未配置过则先 configure ──
            if (!target.IsConfigured)
            {
                string configureArgs = string.IsNullOrEmpty(target.PresetName)
                    ? $"-S \"{projectDir}\" -B \"{target.BuildDirectory}\""
                    : $"--preset \"{target.PresetName}\"";

                var configure = await RunProcessAsync(
                    cmake, configureArgs, projectDir, environment, ConfigureTimeout, ct);

                if (configure.ExitCode != 0)
                {
                    Logger.Warn($"[TestService] CMake configure failed with exit code {configure.ExitCode}");
                    result.Error = TestRunErrorKind.BuildFailed;
                    result.OutputTail = BuildOutputTail(configure.Output);
                    return;
                }
            }

            // ── 2. 自带构建（增量）──
            string buildArgs = $"--build \"{target.BuildDirectory}\"";
            if (!string.IsNullOrWhiteSpace(target.Configuration))
                buildArgs += $" --config {target.Configuration}";

            var build = await RunProcessAsync(
                cmake, buildArgs, projectDir, environment, CmakeBuildTimeout, ct);

            if (build.ExitCode != 0)
            {
                Logger.Warn($"[TestService] CMake build failed with exit code {build.ExitCode}");
                result.Error = TestRunErrorKind.BuildFailed;
                result.OutputTail = BuildOutputTail(build.Output);
                return;
            }

            // ── 3. 运行 ctest ──
            string junitPath = Path.Combine(
                Path.GetTempPath(),
                $"{CtestJUnitFilePrefix}{Guid.NewGuid():N}.xml");

            bool supportsJUnit = CtestSupportsJUnitOutput(ctest);

            var args = new StringBuilder("--output-on-failure");
            if (supportsJUnit)
                args.Append(" --output-junit \"").Append(junitPath).Append('"');
            if (!string.IsNullOrWhiteSpace(filter))
                args.Append(" -R \"").Append(filter).Append('"');
            if (!string.IsNullOrWhiteSpace(target.Configuration))
                args.Append(" -C ").Append(target.Configuration);

            try
            {
                var run = await RunProcessAsync(
                    ctest, args.ToString(), target.BuildDirectory, environment, RunTimeout, ct);

                result.Duration = run.Elapsed;
                result.OutputTail = BuildOutputTail(run.Output);

                Logger.Info($"[TestService] ctest exit code: {run.ExitCode}, elapsed {run.Elapsed.TotalSeconds:F1}s");

                if (supportsJUnit && File.Exists(junitPath))
                {
                    try
                    {
                        ParseJUnitResult(junitPath, result);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"[TestService] Parsing ctest JUnit failed: {ex.Message}");
                        ParseCtestTextSummary(run.Output, result);
                    }
                }
                else
                {
                    ParseCtestTextSummary(run.Output, result);
                }

                // JUnit 写出 0 条（例如过滤器没匹配到用例）时，用文本汇总兜底，
                // 避免模型把「没有测试被执行」当成测试通过。
                if (result.Total == 0 && result.Error == TestRunErrorKind.None)
                    ParseCtestTextSummary(run.Output, result);
            }
            catch (OperationCanceledException)
            {
                result.Error = TestRunErrorKind.Timeout;
            }
            finally
            {
                TryDeleteFile(junitPath);
            }
        }

        /// <summary>
        /// 探测 ctest 是否支持 --output-junit（CMake 3.21+）。结果按可执行文件路径缓存。
        /// 探测失败时按"支持"处理，交由上层在解析失败时退回文本汇总。
        /// </summary>
        private static bool CtestSupportsJUnitOutput(string ctestPath)
        {
            lock (CtestHelpCache)
            {
                if (CtestHelpCache.TryGetValue(ctestPath, out bool cached))
                    return cached;

                bool supported = true;
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = ctestPath,
                        Arguments = "--help",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };

                    using var process = Process.Start(psi);
                    if (process != null)
                    {
                        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                        process.WaitForExit(5000);
                        supported = output.IndexOf("--output-junit", StringComparison.OrdinalIgnoreCase) >= 0;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[TestService] Probing ctest --output-junit failed: {ex.Message}");
                }

                if (!supported)
                    Logger.Info("[TestService] ctest does not support --output-junit; falling back to text summary");

                CtestHelpCache[ctestPath] = supported;
                return supported;
            }
        }

        private static readonly Dictionary<string, bool> CtestHelpCache =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        #endregion

        #region Process Execution & MSVC Environment

        /// <summary>子进程执行结果。</summary>
        private sealed class ProcessOutcome
        {
            public int ExitCode { get; set; } = -1;
            public string Output { get; set; } = string.Empty;
            public TimeSpan Elapsed { get; set; }
        }

        /// <summary>
        /// 启动一个子进程并等待结束。超时/取消时清理进程树并抛出 OperationCanceledException，
        /// 由调用方映射为 Timeout 错误分类。
        /// </summary>
        private static async Task<ProcessOutcome> RunProcessAsync(
            string fileName,
            string arguments,
            string workingDirectory,
            IReadOnlyDictionary<string, string>? environment,
            TimeSpan timeout,
            CancellationToken ct)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory,
            };

            if (environment != null)
            {
                foreach (var kvp in environment)
                    psi.EnvironmentVariables[kvp.Key] = kvp.Value;
            }

            Logger.Info($"[TestService] Executing: {fileName} {arguments}");
            var started = Stopwatch.StartNew();

            using var process = Process.Start(psi);
            if (process == null)
                throw new InvalidOperationException($"Failed to start process: {fileName}");

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            try
            {
                await Task.Run(() => process.WaitForExit(), cts.Token);
            }
            catch (OperationCanceledException)
            {
                Logger.Info($"[TestService] {Path.GetFileName(fileName)} timed out or was cancelled, killing process...");
                ProcessTreeKiller.Kill(process, "[TestService]");
                throw;
            }

            string stdout = await stdoutTask;
            string stderr = await stderrTask;
            started.Stop();

            return new ProcessOutcome
            {
                ExitCode = process.ExitCode,
                Output = stdout + "\n" + stderr,
                Elapsed = started.Elapsed,
            };
        }

        /// <summary>
        /// 判断是否需要 MSVC 开发环境，需要时捕获 vcvars64.bat 展开后的环境变量。
        /// 触发条件：构建缓存 / 预设使用 cl.exe，或开启了 /fsanitize=address（ASan 运行时 DLL 在 MSVC bin 下）。
        /// </summary>
        private static Dictionary<string, string>? TryGetMsvcEnvironment(
            CmakeBuildTarget target, string? vsRoot)
        {
            if (!NeedsMsvcEnvironment(target))
                return null;

            string? vcvars = FindVcvarsBat(vsRoot);
            if (vcvars == null)
            {
                Logger.Warn("[TestService] MSVC environment needed but vcvars64.bat was not found");
                return null;
            }

            var environment = CaptureMsvcEnvironment(vcvars);
            if (environment == null)
                Logger.Warn("[TestService] Capturing MSVC environment failed; continuing without it");
            else
                Logger.Info($"[TestService] Injected MSVC environment from {vcvars}");

            return environment;
        }

        /// <summary>
        /// 判断构建是否依赖 MSVC 开发环境。
        /// 已配置的目录看 CMakeCache.txt；未配置的目录看预设的 cacheVariables。
        /// </summary>
        internal static bool NeedsMsvcEnvironment(CmakeBuildTarget target)
        {
            string cachePath = Path.Combine(target.BuildDirectory, "CMakeCache.txt");
            if (File.Exists(cachePath))
            {
                try
                {
                    foreach (string line in File.ReadLines(cachePath))
                    {
                        if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal) ||
                            line.StartsWith("#", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        if (line.IndexOf("fsanitize=address", StringComparison.OrdinalIgnoreCase) >= 0)
                            return true;

                        if ((line.StartsWith("CMAKE_C_COMPILER", StringComparison.Ordinal) ||
                             line.StartsWith("CMAKE_CXX_COMPILER", StringComparison.Ordinal)) &&
                            line.IndexOf("cl.exe", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[TestService] Reading CMakeCache.txt failed: {ex.Message}");
                }

                return false;
            }

            foreach (string key in new[] { "CMAKE_C_COMPILER", "CMAKE_CXX_COMPILER" })
            {
                if (target.CacheVariables.TryGetValue(key, out string? compiler) &&
                    compiler.IndexOf("cl", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 查找 vcvars64.bat：只在 VS 安装根目录下定位，避免额外启动进程。
        /// </summary>
        internal static string? FindVcvarsBat(string? vsRoot)
        {
            if (string.IsNullOrEmpty(vsRoot))
                return null;

            try
            {
                string candidate = Path.Combine(vsRoot!, "VC", "Auxiliary", "Build", "vcvars64.bat");
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[TestService] Locating vcvars64.bat failed: {ex.Message}");
            }

            return null;
        }

        /// <summary>通过 vswhere 查找带 C++ 工具的 VS 安装根目录。</summary>
        private static async Task<string?> FindVisualStudioRootFromVswhereAsync()
        {
            try
            {
                string vswhere = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    "Microsoft Visual Studio", "Installer", "vswhere.exe");

                if (!File.Exists(vswhere))
                    return null;

                foreach (string extraArgs in new[]
                {
                    "-requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64",
                    string.Empty,
                })
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = vswhere,
                        Arguments = $"-latest -products * -property installationPath {extraArgs}".Trim(),
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };

                    using var process = Process.Start(psi);
                    if (process == null)
                        continue;

                    string output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                    process.WaitForExit(5000);

                    string? path = output
                        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(l => l.Trim())
                        .FirstOrDefault(l => l.Length > 0 && Directory.Exists(l));

                    if (path != null)
                        return path;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[TestService] Locating Visual Studio via vswhere failed: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// 执行 vcvars64.bat 并捕获展开后的环境变量。
        /// 用 `chcp 65001` 保证 set 输出按 UTF-8 编码，避免中文用户名/路径乱码。
        /// </summary>
        internal static Dictionary<string, string>? CaptureMsvcEnvironment(string vcvarsBat)
        {
            try
            {
                string comspec = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
                var psi = new ProcessStartInfo
                {
                    FileName = comspec,
                    // 注意：这里必须用单层引号包路径（不是 ""...""），
                    // cmd /s /c 下把 call 的路径写成双写引号会让整条命令静默失败、输出为空。
                    Arguments = "/s /c \"chcp 65001 >nul && call \"" + vcvarsBat + "\" >nul 2>&1 && set\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using var process = Process.Start(psi);
                if (process == null)
                    return null;

                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(30000);

                var environment = ParseEnvironmentBlock(output);
                return environment.Count > 0 ? environment : null;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[TestService] Capturing MSVC environment failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 解析 set 输出的环境变量块。跳过 cmd 的伪变量（以 '=' 开头，如 =C:=C:\path）。
        /// </summary>
        internal static Dictionary<string, string> ParseEnvironmentBlock(string output)
        {
            var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(output))
                return environment;

            foreach (string rawLine in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = rawLine.TrimEnd();
                int separator = line.IndexOf('=');
                if (separator <= 0)
                    continue;

                string name = line.Substring(0, separator);
                if (name.Length == 0 || name.StartsWith("=", StringComparison.Ordinal))
                    continue;
                // chcp 之类的提示行不含合法变量名（含有空格等）时跳过
                if (name.IndexOf(' ') >= 0)
                    continue;

                environment[name] = line.Substring(separator + 1);
            }

            return environment;
        }

        #endregion

        #region Runner Discovery

        /// <summary>
        /// 查找 cmake 可执行文件：PATH → VS 自带 CMake → 常见安装路径。
        /// </summary>
        internal static string? FindCmakeExecutable(string? vsRoot)
        {
            string? fromPath = FindExecutableInPath("cmake.exe");
            if (fromPath != null)
                return fromPath;

            if (vsRoot != null)
            {
                string vsCmake = Path.Combine(vsRoot, "Common7", "IDE", "CommonExtensions",
                    "Microsoft", "CMake", "CMake", "bin", "cmake.exe");
                if (File.Exists(vsCmake))
                {
                    Logger.Info($"[TestService] Using VS bundled CMake: {vsCmake}");
                    return vsCmake;
                }
            }

            string progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string progFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            foreach (string candidate in new[]
            {
                Path.Combine(progFiles, "CMake", "bin", "cmake.exe"),
                Path.Combine(progFilesX86, "CMake", "bin", "cmake.exe"),
            })
            {
                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }

        /// <summary>
        /// 查找 ctest：优先与 cmake 同目录，其次 PATH。
        /// </summary>
        internal static string? FindCtestExecutable(string cmakePath)
        {
            try
            {
                string? directory = Path.GetDirectoryName(cmakePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    string sibling = Path.Combine(directory!, "ctest.exe");
                    if (File.Exists(sibling))
                        return sibling;
                }
            }
            catch { }

            return FindExecutableInPath("ctest.exe");
        }

        /// <summary>在 PATH 中查找可执行文件，命中时返回绝对路径。</summary>
        private static string? FindExecutableInPath(string exeName)
        {
            string? pathVar = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(pathVar))
                return null;

            foreach (string dir in pathVar.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir))
                    continue;

                try
                {
                    string candidate = Path.Combine(dir.Trim(), exeName);
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch { }
            }

            return null;
        }

        /// <summary>
        /// 获取 VS 安装根目录：
        /// 1. 已经在 UI 线程上时直接问 SVsShell（拿到的是当前运行的 VS 实例，工具链最匹配）
        /// 2. 否则切到 UI 线程再问
        /// 3. 都不行（或不在 VS 宿主里）退回 vswhere 查询最新安装
        /// </summary>
        private static async Task<string?> TryGetVisualStudioRootAsync()
        {
            try
            {
                if (ThreadHelper.CheckAccess())
                {
                    string? path = BuildService.GetVisualStudioInstallPath();
                    if (path != null)
                        return path;
                }
                else
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    string? path = BuildService.GetVisualStudioInstallPath();
                    if (path != null)
                        return path;
                }
            }
            catch (Exception ex)
            {
                Logger.Info($"[TestService] Resolving VS install path via VS shell failed: {ex.Message}");
            }

            return await FindVisualStudioRootFromVswhereAsync().ConfigureAwait(false);
        }

        #endregion

        #region ctest text summary fallback

        /// <summary>
        /// 解析 ctest 的汇总文本（CMake &lt; 3.21 无 JUnit 输出时兜底）。
        /// ctest 的输出不随语言本地化，因此这里的匹配是稳定的。
        /// </summary>
        internal static void ParseCtestTextSummary(string output, TestRunResult result)
        {
            if (string.IsNullOrWhiteSpace(output))
                return;

            var summary = Regex.Match(
                output, @"(\d+)%\s+tests\s+passed,\s+(\d+)\s+tests?\s+failed\s+out\s+of\s+(\d+)",
                RegexOptions.IgnoreCase);

            if (summary.Success)
            {
                int failed = int.Parse(summary.Groups[2].Value);
                int total = int.Parse(summary.Groups[3].Value);
                result.Total = total;
                result.Failed = failed;
                result.Passed = Math.Max(0, total - failed);
            }

            // 失败用例清单：\t 12 - Suite.Name (Failed)
            var failedBlock = Regex.Match(
                output,
                @"The following tests FAILED:\s*(?<body>(?:\s*\d+\s*-\s*.+\r?\n?)+)",
                RegexOptions.IgnoreCase);

            if (failedBlock.Success)
            {
                foreach (Match line in Regex.Matches(
                    failedBlock.Groups["body"].Value, @"^\s*\d+\s*-\s*(?<name>.+?)\s*\((?<status>[^)]*)\)\s*$",
                    RegexOptions.Multiline))
                {
                    if (result.FailedTests.Count >= MaxFailedTests)
                        break;

                    result.FailedTests.Add(new FailedTestInfo
                    {
                        Name = line.Groups["name"].Value.Trim(),
                        Message = line.Groups["status"].Value.Trim(),
                    });
                }
            }

            // 用例被跳过 / 未运行时的汇总行
            var notRun = Regex.Match(
                output, @"The following tests did not run:\s*(?<body>(?:\s*\d+\s*-\s*.+\r?\n?)+)",
                RegexOptions.IgnoreCase);
            if (notRun.Success)
            {
                result.Skipped += Regex.Matches(notRun.Groups["body"].Value, @"^\s*\d+\s*-\s*", RegexOptions.Multiline).Count;
            }

            Logger.Info(
                $"[TestService] ctest text summary: total {result.Total}, passed {result.Passed}, " +
                $"failed {result.Failed}, skipped {result.Skipped}");
        }

        #endregion
    }
}
