using BenchmarkDotNet.Running;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;

namespace Cljr.Benchmarks;

public class Program
{
    public static void Main(string[] args)
    {
        // Use InProcess for .NET 10 preview compatibility
        var config = DefaultConfig.Instance
            .AddJob(Job.ShortRun
                .WithToolchain(InProcessNoEmitToolchain.Instance));

        if (args.Length == 0)
        {
            // Run all benchmarks
            BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
        }
        else
        {
            BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
        }
    }
}
