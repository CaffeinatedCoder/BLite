using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BLite.Shared;
using System.IO;

namespace BLite.Benchmark;

/// <summary>
/// LINQ <c>Sum</c> / <c>Average</c> / <c>Max</c> over 10 000 <see cref="Reading"/> documents,
/// against materialising the entities and aggregating them with LINQ to Objects (what every
/// aggregate with a selector did before). Covers each push-down path:
/// <list type="bullet">
///   <item>index keys: <c>int</c> property with a B-Tree index, with and without a range filter;</item>
///   <item>index boundary: <c>Max</c> of an indexed <c>int</c> reads one entry;</item>
///   <item>raw BSON: <c>double</c> and <c>decimal</c> properties without an index, with and
///         without a filter on a non-indexed field.</item>
/// </list>
/// </summary>
[SimpleJob]
[InProcess]
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class LinqAggregateBenchmarks
{
    private const int DocCount = 10_000;

    private string _dbPath = null!;
    private AggregateDbContext _ctx = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _dbPath = Path.Combine(AppContext.BaseDirectory, $"linq_agg_{Guid.NewGuid():N}.db");
        _ctx = new AggregateDbContext(_dbPath);
        await _ctx.Readings.InsertBulkAsync(Enumerable.Range(0, DocCount).Select(i => new Reading
        {
            Tag = "abcd"[i % 4].ToString(),
            Count = (i * 7919) % 1000,
            Total = i * 1_000L,
            Ratio = Math.Sin(i) * 100,
            Weight = i / 3f,
            Price = i * 1.25m,
        }));
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _ctx?.Dispose();
        foreach (var f in new[] { _dbPath, Path.ChangeExtension(_dbPath, ".wal") })
            if (File.Exists(f)) File.Delete(f);
    }

    // ── int property with an index ──────────────────────────────────────────

    [Benchmark(Baseline = true, Description = "Sum(int) – materialise + LINQ to Objects")]
    [BenchmarkCategory("IndexedInt")]
    public int SumIndexed_Materialised() => _ctx.Readings.AsQueryable().ToList().Sum(r => r.Count);

    [Benchmark(Description = "Sum(int) – index keys")]
    [BenchmarkCategory("IndexedInt")]
    public int SumIndexed() => _ctx.Readings.AsQueryable().Sum(r => r.Count);

    [Benchmark(Description = "Where(range).Sum(int) – index keys")]
    [BenchmarkCategory("IndexedInt")]
    public int SumIndexedRange() => _ctx.Readings.AsQueryable().Where(r => r.Count >= 250 && r.Count < 500).Sum(r => r.Count);

    [Benchmark(Description = "Max(int) – index boundary")]
    [BenchmarkCategory("IndexedInt")]
    public int MaxIndexed() => _ctx.Readings.AsQueryable().Max(r => r.Count);

    // ── properties without an index ─────────────────────────────────────────

    [Benchmark(Baseline = true, Description = "Sum(double) – materialise + LINQ to Objects")]
    [BenchmarkCategory("RawBson")]
    public double SumDouble_Materialised() => _ctx.Readings.AsQueryable().ToList().Sum(r => r.Ratio);

    [Benchmark(Description = "Sum(double) – raw BSON")]
    [BenchmarkCategory("RawBson")]
    public double SumDouble() => _ctx.Readings.AsQueryable().Sum(r => r.Ratio);

    [Benchmark(Description = "Average(decimal) – raw BSON")]
    [BenchmarkCategory("RawBson")]
    public decimal AverageDecimal() => _ctx.Readings.AsQueryable().Average(r => r.Price);

    [Benchmark(Description = "Where(tag).Sum(double) – raw BSON")]
    [BenchmarkCategory("RawBson")]
    public double SumDoubleFiltered() => _ctx.Readings.AsQueryable().Where(r => r.Tag == "b").Sum(r => r.Ratio);
}
