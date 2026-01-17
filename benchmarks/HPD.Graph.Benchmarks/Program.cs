using BenchmarkDotNet.Running;
using HPD.Graph.Benchmarks;

Console.WriteLine("HPD.Graph -Performance Benchmarks");
Console.WriteLine("======================================================");
Console.WriteLine();
Console.WriteLine("Testing Cloning Performance:");
Console.WriteLine("  - Payload sizes: 1KB, 10KB, 100KB, 500KB");
Console.WriteLine("  - Methods: Source-gen JSON, Reflection JSON, MessagePack");
Console.WriteLine("  - Target: <5ms for 100KB payload (95th percentile)");
Console.WriteLine("  - Circular reference handling validation");
Console.WriteLine();
Console.WriteLine("Running benchmarks...");
Console.WriteLine();

var summary = BenchmarkRunner.Run<CloningBenchmarks>();

Console.WriteLine();
Console.WriteLine("======================================================");
Console.WriteLine("Benchmark Results Summary:");
Console.WriteLine();

// Check if 100KB source-gen JSON meets the <5ms target
var criticalBenchmark = summary.Reports
    .FirstOrDefault(r => r.BenchmarkCase.Descriptor.WorkloadMethodDisplayInfo.Contains("100KB - Source-gen JSON"));

if (criticalBenchmark != null)
{
    var meanMs = criticalBenchmark.ResultStatistics?.Mean / 1_000_000; // Convert ns to ms

    Console.WriteLine($"CRITICAL BENCHMARK: 100KB Source-gen JSON");
    Console.WriteLine($"  Mean: {meanMs:F2} ms");
    Console.WriteLine($"  Target: <5ms");

    if (meanMs < 5)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"  ✓ PASSED - Target met ({meanMs:F2}ms < 5ms)");
        Console.ResetColor();
        Console.WriteLine();
        Console.WriteLine("Decision: PROCEED with source-generated JSON cloning ");
    }
    else
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"  ✗ FAILED - Target not met ({meanMs:F2}ms >= 5ms)");
        Console.ResetColor();
        Console.WriteLine();
        Console.WriteLine("Decision: EVALUATE MessagePack alternative before proceeding");
    }
}
else
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("Warning: Could not find 100KB source-gen JSON benchmark result");
    Console.ResetColor();
}

Console.WriteLine();
Console.WriteLine("Full benchmark results saved to: BenchmarkDotNet.Artifacts/results/");
