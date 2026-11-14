using System.Runtime.Loader;
using Xunit.Runners;

var assembly = args.FirstOrDefault() ?? throw new ArgumentException("Path to the xUnit test assembly is required.");
if (!File.Exists(assembly)) throw new FileNotFoundException("Test assembly not found.", assembly);
var testDirectory = Path.GetDirectoryName(Path.GetFullPath(assembly))!;
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    var candidate = Path.Combine(testDirectory, name.Name + ".dll");
    return File.Exists(candidate) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate) : null;
};
var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
using var runner = AssemblyRunner.WithoutAppDomain(Path.GetFullPath(assembly));
runner.OnTestFailed = info => Console.WriteLine($"FAIL {info.TestDisplayName}: {info.ExceptionMessage}");
runner.OnTestPassed = info => Console.WriteLine($"PASS {info.TestDisplayName}");
runner.OnTestSkipped = info => Console.WriteLine($"SKIP {info.TestDisplayName}");
runner.OnExecutionComplete = info => { Console.WriteLine($"xUnit: total={info.TotalTests}, failed={info.TestsFailed}, skipped={info.TestsSkipped}"); completion.TrySetResult(info.TestsFailed == 0 ? 0 : 1); };
runner.Start(AssemblyRunnerStartOptions.Empty);
return await completion.Task;
