using System;
using System.Threading;
using System.Threading.Tasks;

namespace BenchmarkRunner
{
    internal static class HeadlessToolchainSelfTest
    {
        public static async Task<int> RunAsync(string[] args)
        {
            if (args.Length != 2 || !string.Equals(
                args[0],
                "--case-dir",
                StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine(
                    "Usage: BenchmarkRunner --self-test-toolchain --case-dir <workspace>");
                return 2;
            }

            string caseDir = args[1];
            var buildService = new HeadlessBuildService(caseDir);
            string build = await buildService.BuildAsync(
                null,
                CancellationToken.None);
            Console.WriteLine("[self-test] BUILD");
            Console.WriteLine(build);

            var testService = new HeadlessTestService(caseDir, buildService);
            var test = await testService.RunTestsAsync(
                null,
                null,
                null,
                null,
                CancellationToken.None);
            Console.WriteLine("[self-test] TEST");
            Console.WriteLine(
                $"kind={test.Kind} error={test.Error} total={test.Total} "
                + $"passed={test.Passed} failed={test.Failed} skipped={test.Skipped}");
            if (!string.IsNullOrWhiteSpace(test.OutputTail))
                Console.WriteLine(test.OutputTail);

            return test.AllPassed ? 0 : 1;
        }
    }
}
