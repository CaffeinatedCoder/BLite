using System.Linq.Expressions;
using System.Reflection;
using BLite.Bson;
using BLite.Core;
using BLite.Core.Collections;
using BLite.Core.Query;
using BLite.Shared;

namespace BLite.Tests;

/// <summary>
/// LINQ <c>Sum</c>, <c>Average</c>, <c>Min</c> and <c>Max</c> computed from index keys or raw BSON
/// return exactly what LINQ returns over the materialised entities, or throw the same exception.
/// </summary>
public class LinqAggregateTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"agg_{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (var f in new[] { _dbPath, Path.ChangeExtension(_dbPath, ".wal") })
            try { if (File.Exists(f)) File.Delete(f); } catch { /* best-effort */ }
    }

    private static readonly string[] Operators = ["Sum", "Average", "Min", "Max"];
    private static readonly string[] Indexed = ["Count", "Total", "MaybeCount"];
    private static readonly string[] Properties =
        ["Count", "Total", "Units", "Ratio", "Weight", "Price", "MaybeCount", "MaybeTotal", "MaybeRatio", "MaybeWeight", "MaybePrice"];

    private static Reading Make(int i) => new()
    {
        Tag = "abc"[i % 3].ToString(),
        Count = (i * 7) % 13 - 4,
        Total = i * 1_000_000_007L - 5_000_000_000L,
        Units = i * 3 - 50,
        Ratio = Math.Sin(i) * 100 / 3,
        Weight = (float)(i / 7.0),
        // Same values at different scales: 1.5 / 1.50 / 1.500.
        Price = (i % 3) switch { 0 => i * 1.5m, 1 => i * 1.50m, _ => i * 1.500m },
        MaybeCount = i % 4 == 0 ? null : i - 20,
        MaybeTotal = i % 4 == 1 ? null : i * 3L,
        MaybeRatio = i % 4 == 2 ? null : 1.0 / (i + 1),
        MaybeWeight = i % 4 == 3 ? null : (float)(i * 0.1),
        MaybePrice = i % 4 == 0 ? null : i * 0.10m,
        Level = i,
        Score = 100 + i,
    };

    private static readonly (string Name, Expression<Func<Reading, bool>>? Where)[] Filters =
    [
        ("none", null),
        ("tag", r => r.Tag == "b"),
        ("count range", r => r.Count >= -1 && r.Count < 5),
        ("count range and tag", r => r.Count > 0 && r.Tag == "c"),
        ("no match", r => r.Tag == "zzz"),
        ("empty count range", r => r.Count > 1000),
    ];

    /// <summary>Whether the aggregate should be computed without materialising the entities.</summary>
    private static bool ExpectPushedDown(string property, string filter) => filter switch
    {
        "none" or "tag" or "no match" => true,               // index keys, or raw BSON without/with a predicate
        "count range" or "empty count range" => property == "Count", // the Count index covers the filter
        _ => false,                                             // another index serves the filter
    };

    [Fact]
    public async Task EveryOperatorPropertyAndFilter_MatchesLinqOverTheEntities()
    {
        using var db = new AggregateDbContext(_dbPath);
        for (int i = 0; i < 40; i++)
            await db.Readings.InsertAsync(Make(i));
        var entities = db.Readings.AsQueryable().ToList();

        var failures = new List<string>();
        foreach (var op in Operators)
        foreach (var property in Properties)
        foreach (var (filter, where) in Filters)
        {
            var selector = Selector(property);
            var label = $"{op}({property}) where {filter}";

            // LINQ over the matching entities, in the order the collection returns them for the filter
            // (primary-key order, or index order when an index serves it).
            var matches = where == null ? entities : db.Readings.AsQueryable().Where((Expression<Func<Reading, bool>>)where).ToList();
            var expected = Run(() => Execute(matches.AsQueryable(), op, selector, null));
            var actual = Run(() => Execute(db.Readings.AsQueryable(), op, selector, where));
            if (!Same(expected, actual))
                failures.Add($"{label}: expected {Show(expected)}, got {Show(actual)}");

            var resultType = AggregateCall(entities.AsQueryable(), op, selector, where).Type;
            var direct = await RunAsync(() => db.Readings.TryAggregateAsync(Enum.Parse<AggregateOperator>(op), selector, where, resultType));
            bool handled = direct.Error != null || direct.Value.Handled;
            if (handled != ExpectPushedDown(property, filter))
                failures.Add($"{label}: pushed down = {handled}, expected {ExpectPushedDown(property, filter)}");
            else if (handled && !Same(expected, (direct.Value.Value, direct.Error)))
                failures.Add($"{label} (pushed down): expected {Show(expected)}, got {Show((direct.Value.Value, direct.Error))}");
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Theory]
    [InlineData(new[] { 0.0, -0.0, double.NaN, 1.0, -0.0 })]
    [InlineData(new[] { -0.0, 0.0, 2.5, double.NaN })]
    [InlineData(new[] { double.NaN, double.NaN })]
    [InlineData(new[] { double.PositiveInfinity, double.NegativeInfinity, 1.0 })]
    [InlineData(new[] { 1e308, 1e308, -1e308 })]
    [InlineData(new[] { 447711.6875, 7923489.5, 16777216.0 })] // float Average: (float)(sum / n) != (float)sum / n
    public async Task FloatingPointEdgeCases_MatchLinq(double[] values)
    {
        using var db = new AggregateDbContext(_dbPath);
        foreach (var v in values)
            await db.Readings.InsertAsync(new Reading { Ratio = v, MaybeRatio = v, Weight = (float)v, MaybeWeight = (float)v });
        var entities = db.Readings.AsQueryable().ToList();

        foreach (var op in Operators)
        foreach (var property in new[] { "Ratio", "MaybeRatio", "Weight", "MaybeWeight" })
        {
            var selector = Selector(property);
            var expected = Run(() => Execute(entities.AsQueryable(), op, selector, null));
            var direct = await db.Readings.TryAggregateAsync(Enum.Parse<AggregateOperator>(op), selector, null, AggregateCall(entities.AsQueryable(), op, selector, null).Type);

            Assert.True(direct.Handled, $"{op}({property})");
            Assert.True(Same(expected, (direct.Value, null)), $"{op}({property}): expected {Show(expected)}, got {Show((direct.Value, null))}");
        }
    }

    [Fact]
    public async Task DecimalScale_MatchesLinq()
    {
        using var db = new AggregateDbContext(_dbPath);
        foreach (var price in new[] { 1.00m, 1.0m, 2.50m, 1m })
            await db.Readings.InsertAsync(new Reading { Price = price, MaybePrice = price });

        var entities = db.Readings.AsQueryable().ToList();
        string Text(decimal? d) => d?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null";

        Assert.Equal(Text(entities.Sum(r => r.Price)), Text(db.Readings.AsQueryable().Sum(r => r.Price)));
        Assert.Equal(Text(entities.Min(r => r.Price)), Text(db.Readings.AsQueryable().Min(r => r.Price))); // first of the equal minima
        Assert.Equal(Text(entities.Max(r => r.MaybePrice)), Text(db.Readings.AsQueryable().Max(r => r.MaybePrice)));
        Assert.Equal(Text(entities.Average(r => r.MaybePrice)), Text(db.Readings.AsQueryable().Average(r => r.MaybePrice)));
    }

    [Fact]
    public async Task IntegerOverflow_ThrowsLikeLinq()
    {
        using var db = new AggregateDbContext(_dbPath);
        await db.Readings.InsertAsync(new Reading { Units = int.MaxValue, Count = int.MaxValue, Total = long.MaxValue });
        await db.Readings.InsertAsync(new Reading { Units = 1, Count = 1, Total = 1 });

        Assert.Throws<OverflowException>(() => db.Readings.AsQueryable().Sum(r => r.Units)); // raw BSON
        Assert.Throws<OverflowException>(() => db.Readings.AsQueryable().Sum(r => r.Count)); // index keys
        Assert.Throws<OverflowException>(() => db.Readings.AsQueryable().Sum(r => r.Total));
        Assert.Throws<OverflowException>(() => db.Readings.AsQueryable().Average(r => r.Total));
        Assert.Equal((int.MaxValue + 1.0) / 2, db.Readings.AsQueryable().Average(r => r.Units));
        Assert.Equal((int.MaxValue + 1.0) / 2, db.Readings.AsQueryable().Average(r => r.Count));
    }

    [Fact]
    public async Task IndexKeySums_CheckOnlyTheTotalForOverflow()
    {
        // Index keys come in value order, not document order, so an index sum cannot reproduce an
        // overflow of a running total that LINQ would hit before a later negative value: it returns
        // the exact total when that fits, and throws when it does not.
        using var db = new AggregateDbContext(_dbPath);
        foreach (var count in new[] { int.MaxValue, 1, -2 })
            await db.Readings.InsertAsync(new Reading { Count = count, Units = count });

        // Whether LINQ overflows depends on the document order (int.MaxValue + 1 before the -2).
        var entities = db.Readings.AsQueryable().ToList();
        Assert.True(Same(Run(() => entities.Sum(r => r.Units)), Run(() => db.Readings.AsQueryable().Sum(r => r.Units)))); // raw BSON, document order
        Assert.Equal(int.MaxValue - 1, db.Readings.AsQueryable().Sum(r => r.Count));                                         // index keys: the exact total
    }

    [Fact]
    public async Task DocumentsWithoutTheField_UseTheEntityValue()
    {
        using (var v1 = new AggregateDbContext(_dbPath))
        {
            for (int i = 0; i < 5; i++)
                await v1.Readings.InsertAsync(Make(i));
        }

        // Whatever value the mapper gives a missing property, the aggregate must use the same one.
        using var v2 = new AggregateV2DbContext(_dbPath);
        var entities = v2.Readings.AsQueryable().ToList();

        Assert.False((await v2.Readings.TryAggregateAsync(AggregateOperator.Sum, (Expression<Func<ReadingV2, int>>)(r => r.Bonus), null, typeof(int))).Handled);
        Assert.False((await v2.Readings.TryAggregateAsync(AggregateOperator.Max, (Expression<Func<ReadingV2, double?>>)(r => r.Factor), null, typeof(double?))).Handled);
        Assert.Equal(entities.Sum(r => r.Bonus), v2.Readings.AsQueryable().Sum(r => r.Bonus));
        Assert.Equal(entities.Max(r => r.Factor), v2.Readings.AsQueryable().Max(r => r.Factor));
        Assert.Equal(entities.Sum(r => r.Count), v2.Readings.AsQueryable().Sum(r => r.Count));
    }

    [Theory]
    [InlineData("units", "null")]   // null in a non-nullable int
    [InlineData("units", "int64")]  // an Int64 in an int field
    [InlineData("ratio", "null")]
    [InlineData("ratio", "int32")]  // an Int32 in a double field
    public async Task FieldsWithAnotherBsonType_UseTheEntityValue(string field, string kind)
    {
        using (var db = new AggregateDbContext(_dbPath))
        {
            for (int i = 0; i < 4; i++)
                await db.Readings.InsertAsync(Make(i));
        }

        // Written through the schemaless API, which stores whatever it is given.
        using (var engine = new BLiteEngine(_dbPath))
        {
            var col = engine.GetOrCreateCollection("readings");
            await col.InsertAsync(col.CreateDocument(["_id", "tag", field], b =>
            {
                b.AddObjectId("_id", ObjectId.NewObjectId()).AddString("tag", "x");
                switch (kind)
                {
                    case "null": b.AddNull(field); break;
                    case "int64": b.AddInt64(field, 5L); break;
                    default: b.AddInt32(field, 2); break;
                }
            }));
        }

        using var reopened = new AggregateDbContext(_dbPath);
        var property = field == "units" ? "Units" : "Ratio";
        var resultType = field == "units" ? typeof(int) : typeof(double);

        // A null the entity cannot hold makes materialising throw; the aggregate must throw alike.
        var entities = Run(() => reopened.Readings.AsQueryable().ToList());
        var expected = entities.Error != null
            ? entities
            : Run(() => Execute(((List<Reading>)entities.Value!).AsQueryable(), "Sum", Selector(property), null));

        Assert.False((await reopened.Readings.TryAggregateAsync(AggregateOperator.Sum, Selector(property), null, resultType)).Handled);
        Assert.True(Same(expected, Run(() => Execute(reopened.Readings.AsQueryable(), "Sum", Selector(property), null))), Show(expected));
    }

    [Fact]
    public async Task RenamedProperty_IsNotReadFromAnotherField()
    {
        using var db = new AggregateDbContext(_dbPath);
        for (int i = 0; i < 5; i++)
            await db.Readings.InsertAsync(Make(i));

        Assert.False((await db.Readings.TryAggregateAsync(AggregateOperator.Sum, Selector("Level"), null, typeof(int))).Handled);
        Assert.Equal(10, db.Readings.AsQueryable().Sum(r => r.Level));
        Assert.Equal(510, db.Readings.AsQueryable().Sum(r => r.Score));
    }

    [Fact]
    public async Task OtherOperatorsBeforeTheAggregate_AreApplied()
    {
        using var db = new AggregateDbContext(_dbPath);
        for (int i = 0; i < 20; i++)
            await db.Readings.InsertAsync(Make(i));
        var entities = db.Readings.AsQueryable().ToList();
        var q = db.Readings.AsQueryable();

        Assert.Equal(entities.Take(3).Sum(r => r.Count), q.Take(3).Sum(r => r.Count));
        Assert.Equal(entities.Skip(5).Max(r => r.Count), q.Skip(5).Max(r => r.Count));
        Assert.Equal(entities.OrderBy(r => r.Ratio).Take(4).Sum(r => r.Ratio), q.OrderBy(r => r.Ratio).Take(4).Sum(r => r.Ratio));
        Assert.Equal(entities.Where(r => r.Tag == "a").Where(r => r.Units > -40).Sum(r => r.Units),
                     q.Where(r => r.Tag == "a").Where(r => r.Units > -40).Sum(r => r.Units));
        Assert.Equal(entities.Where(r => r.Count > 0).Sum(r => r.Count), await q.Where(r => r.Count > 0).SumAsync(r => r.Count));
        Assert.Equal(entities.Average(r => r.Units), await q.AverageAsync(r => r.Units));
    }

    [Fact]
    public async Task IndexAggregates_FollowUpdatesAndDeletes()
    {
        using var db = new AggregateDbContext(_dbPath);
        var readings = Enumerable.Range(0, 10).Select(Make).ToList();
        foreach (var r in readings) await db.Readings.InsertAsync(r);

        readings[3].Count = 500;
        await db.Readings.UpdateAsync(readings[3]);
        await db.Readings.DeleteAsync(readings[4].Id);
        readings.RemoveAt(4);

        Assert.Equal(readings.Sum(r => r.Count), db.Readings.AsQueryable().Sum(r => r.Count));
        Assert.Equal(500, db.Readings.AsQueryable().Max(r => r.Count));
        Assert.Equal(readings.Min(r => r.Count), db.Readings.AsQueryable().Min(r => r.Count));
        Assert.Equal(readings.Where(r => r.Count < 500).Max(r => r.Count), db.Readings.AsQueryable().Where(r => r.Count < 500).Max(r => r.Count));
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static LambdaExpression Selector(string property)
    {
        var p = Expression.Parameter(typeof(Reading), "r");
        return Expression.Lambda(Expression.Property(p, property), p);
    }

    private static MethodCallExpression AggregateCall<TEntity>(IQueryable<TEntity> source, string op, LambdaExpression selector, LambdaExpression? where)
    {
        var src = where == null
            ? source.Expression
            : Expression.Call(typeof(Queryable), nameof(Queryable.Where), [typeof(TEntity)], source.Expression, Expression.Quote(where));
        Type[] typeArgs = op is "Min" or "Max" ? [typeof(TEntity), selector.ReturnType] : [typeof(TEntity)];
        return Expression.Call(typeof(Queryable), op, typeArgs, src, Expression.Quote(selector));
    }

    private static readonly MethodInfo s_execute = typeof(IQueryProvider).GetMethods().Single(m => m.Name == "Execute" && m.IsGenericMethodDefinition);

    private static object? Execute<TEntity>(IQueryable<TEntity> source, string op, LambdaExpression selector, LambdaExpression? where)
    {
        var call = AggregateCall(source, op, selector, where);
        return s_execute.MakeGenericMethod(call.Type).Invoke(source.Provider, [call]);
    }

    private static (object? Value, Type? Error) Run(Func<object?> action)
    {
        try { return (action(), null); }
        catch (Exception ex) { return (null, Unwrap(ex).GetType()); }
    }

    private static async Task<((bool Handled, object? Value) Value, Type? Error)> RunAsync(Func<Task<(bool, object?)>> action)
    {
        try { return (await action(), null); }
        catch (Exception ex) { return (default, Unwrap(ex).GetType()); }
    }

    private static Exception Unwrap(Exception ex)
    {
        while (ex is TargetInvocationException or AggregateException && ex.InnerException != null)
            ex = ex.InnerException!;
        return ex;
    }

    /// <summary>Same exception type, or values of the same type with identical bits.</summary>
    private static bool Same((object? Value, Type? Error) expected, (object? Value, Type? Error) actual)
    {
        if (expected.Error != null || actual.Error != null) return expected.Error == actual.Error;
        return (expected.Value, actual.Value) switch
        {
            (null, null) => true,
            (double x, double y) => BitConverter.DoubleToInt64Bits(x) == BitConverter.DoubleToInt64Bits(y),
            (float x, float y) => BitConverter.SingleToInt32Bits(x) == BitConverter.SingleToInt32Bits(y),
            (decimal x, decimal y) => decimal.GetBits(x).SequenceEqual(decimal.GetBits(y)),
            var (x, y) => x?.GetType() == y?.GetType() && Equals(x, y),
        };
    }

    private static string Show((object? Value, Type? Error) result)
        => result.Error != null ? result.Error.Name
         : result.Value switch
         {
             null => "null",
             double d => d.ToString("R") + (BitConverter.DoubleToInt64Bits(d) < 0 ? " (neg)" : ""),
             float f => f.ToString("R"),
             var v => $"{v} ({v.GetType().Name})",
         };
}
