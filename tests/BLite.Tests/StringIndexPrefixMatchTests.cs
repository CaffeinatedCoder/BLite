using System.Linq.Expressions;
using BLite.Bson;
using BLite.Core;
using BLite.Core.Indexing;
using BLite.Core.Query;
using BLite.Core.Query.Blql;
using BLite.Shared;

namespace BLite.Tests;

/// <summary>
/// Regression tests for issue #206: an equality query on an indexed string property returned
/// every value starting with the searched one ("p7" also matched "p70".."p79").
/// Typed indexes store <c>[user key][document id]</c>; the upper range bound for a user key was
/// <c>[user key][0xFF..]</c>, which also covers the entries of every longer value starting with it.
/// Each query is compared with LINQ-to-Objects over the same data.
/// </summary>
public class StringIndexPrefixMatchTests : IDisposable
{
    private const string IndexName = "idx_name";

    // Enough keys for the index to span several B-tree leaves.
    private const int KeyCount = 2000;

    // Probe values: present ones whose longer neighbours exist ("p7" / "p70"), the missing "p5"
    // (with "p50".. present), the first and last keys, and values outside the stored range.
    private static readonly string[] Probes =
        ["", "o", "p", "p0", "p1", "p19", "p199", "p1999", "p5", "p50", "p7", "p70", "p999", "p9999", "q"];

    private static readonly string?[] Bounds = [null, "p", "p1", "p19", "p5", "p7", "p70", "q"];

    private readonly string _dbPath;
    private readonly string _dynamicDbPath;
    private readonly TestDbContext _db;

    public StringIndexPrefixMatchTests()
    {
        var id = Guid.NewGuid();
        _dbPath = Path.Combine(Path.GetTempPath(), $"string_index_prefix_{id}.db");
        _dynamicDbPath = Path.Combine(Path.GetTempPath(), $"string_index_prefix_dyn_{id}.db");
        _db = new TestDbContext(_dbPath);
    }

    public void Dispose()
    {
        _db.Dispose();
        foreach (var path in new[] { _dbPath, _dynamicDbPath })
        {
            if (File.Exists(path)) File.Delete(path);
            var wal = Path.ChangeExtension(path, ".wal");
            if (File.Exists(wal)) File.Delete(wal);
        }
    }

    /// <summary>"p0".."p1999" without "p5"; a non-unique index also gets a second document for every 7th value.</summary>
    private static List<string> Names(bool unique)
    {
        var names = new List<string>();
        for (int i = 0; i < KeyCount; i++)
        {
            if (i == 5) continue;
            names.Add($"p{i}");
            if (!unique && i % 7 == 0) names.Add($"p{i}");
        }
        return names;
    }

    private async Task<List<string>> SeedPeopleV2(bool unique)
    {
        var names = Names(unique);
        await _db.PeopleV2.CreateIndexAsync(p => p.Name, IndexName, unique);
        await _db.PeopleV2.InsertBulkAsync(names.Select((n, i) => new PersonV2 { Name = n, Age = i }));
        return names;
    }

    private static bool InRange(string value, string? min, string? max, bool minInclusive = true, bool maxInclusive = true)
    {
        if (min != null)
        {
            int c = string.CompareOrdinal(value, min);
            if (c < 0 || (c == 0 && !minInclusive)) return false;
        }
        if (max != null)
        {
            int c = string.CompareOrdinal(value, max);
            if (c > 0 || (c == 0 && !maxInclusive)) return false;
        }
        return true;
    }

    private static List<string> Expected(IEnumerable<string> names, Func<string, bool> predicate, bool ascending = true)
    {
        var list = names.Where(predicate).OrderBy(n => n, StringComparer.Ordinal).ToList();
        if (!ascending) list.Reverse();
        return list;
    }

    private static string Name(BsonDocument doc)
        => doc.TryGetString("name", out var name) ? name! : throw new InvalidOperationException("name missing");

    private static List<string> Sorted(IEnumerable<string> names)
        => names.OrderBy(n => n, StringComparer.Ordinal).ToList();

    // ── Typed collection: LINQ ────────────────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Linq_Equality_Returns_Only_Exact_Matches(bool unique)
    {
        var names = await SeedPeopleV2(unique);

        foreach (var probe in Probes)
        {
            var expected = Expected(names, n => n == probe);

            var actual = _db.PeopleV2.AsQueryable().Where(p => p.Name == probe).ToList();
            Assert.True(expected.SequenceEqual(Sorted(actual.Select(p => p.Name))), $"Where(Name == \"{probe}\")");

            var count = _db.PeopleV2.AsQueryable().Where(p => p.Name == probe).Count();
            Assert.True(expected.Count == count, $"Count(Name == \"{probe}\"): expected {expected.Count}, got {count}");

            var one = await _db.PeopleV2.FindOneAsync(p => p.Name == probe);
            Assert.True(expected.Count == 0 ? one == null : one?.Name == probe,
                $"FindOne(Name == \"{probe}\") returned \"{one?.Name}\"");
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Linq_StartsWith_Returns_Exactly_The_Prefixed_Values(bool unique)
    {
        var names = await SeedPeopleV2(unique);

        foreach (var prefix in new[] { "o", "p", "p1", "p19", "p1999", "p5", "p7", "p70", "q" })
        {
            var expected = Expected(names, n => n.StartsWith(prefix, StringComparison.Ordinal));

            // Built with a constant argument: the index optimizer only uses the index for a literal prefix.
            var startsWith = StartsWithConstant(prefix);

            var actual = _db.PeopleV2.AsQueryable().Where(startsWith).ToList();
            Assert.True(expected.SequenceEqual(Sorted(actual.Select(p => p.Name))),
                $"Where(Name.StartsWith(\"{prefix}\")): expected {expected.Count}, got {actual.Count}");

            var count = _db.PeopleV2.AsQueryable().Where(startsWith).Count();
            Assert.True(expected.Count == count, $"Count(Name.StartsWith(\"{prefix}\")): expected {expected.Count}, got {count}");
        }
    }

    /// <summary><c>p => p.Name.StartsWith("prefix")</c> as the compiler builds it for a string literal.</summary>
    private static Expression<Func<PersonV2, bool>> StartsWithConstant(string prefix)
    {
        var p = Expression.Parameter(typeof(PersonV2), "p");
        var call = Expression.Call(
            Expression.Property(p, nameof(PersonV2.Name)),
            typeof(string).GetMethod(nameof(string.StartsWith), [typeof(string)])!,
            Expression.Constant(prefix));
        return Expression.Lambda<Func<PersonV2, bool>>(call, p);
    }

    // ── Typed collection: QueryIndexAsync / index range bounds ────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task QueryIndexAsync_Bounds_Are_Exact(bool unique)
    {
        var names = await SeedPeopleV2(unique);

        foreach (var ascending in new[] { true, false })
        foreach (var min in Bounds)
        foreach (var max in Bounds)
        {
            var expected = Expected(names, n => InRange(n, min, max), ascending);
            var actual = await _db.PeopleV2.QueryIndexAsync(IndexName, min, max, ascending).ToListAsync();
            Assert.True(expected.SequenceEqual(actual.Select(p => p.Name)),
                $"QueryIndexAsync(\"{min}\", \"{max}\", ascending: {ascending}): expected {expected.Count}, got {actual.Count}");
        }

        foreach (var probe in Probes)
        {
            var expected = Expected(names, n => n == probe);
            var actual = await _db.PeopleV2.QueryIndexAsync(IndexName, probe, probe).ToListAsync();
            Assert.True(expected.SequenceEqual(actual.Select(p => p.Name)),
                $"QueryIndexAsync(\"{probe}\", \"{probe}\"): expected {expected.Count}, got {actual.Count}");
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CountRange_Inclusive_And_Exclusive_Bounds_Are_Exact(bool unique)
    {
        var names = await SeedPeopleV2(unique);
        var index = _db.PeopleV2.IndexManager.GetIndex(IndexName)!;

        foreach (var probe in Probes)
        {
            // ==, <=, <, >=, >
            Assert.Equal(names.Count(n => n == probe), index.CountRange(probe, probe, true, true));
            Assert.Equal(names.Count(n => InRange(n, null, probe)), index.CountRange(null, probe, true, true));
            Assert.Equal(names.Count(n => InRange(n, null, probe, maxInclusive: false)), index.CountRange(null, probe, true, false));
            Assert.Equal(names.Count(n => InRange(n, probe, null)), index.CountRange(probe, null, true, true));
            Assert.Equal(names.Count(n => InRange(n, probe, null, minInclusive: false)), index.CountRange(probe, null, false, true));
        }

        foreach (var min in Bounds)
        foreach (var max in Bounds)
        foreach (var minInclusive in new[] { true, false })
        foreach (var maxInclusive in new[] { true, false })
        {
            var expected = names.Count(n => InRange(n, min, max, minInclusive, maxInclusive));
            var actual = index.CountRange(min, max, minInclusive, maxInclusive);
            Assert.True(expected == actual,
                $"CountRange(\"{min}\", \"{max}\", {minInclusive}, {maxInclusive}): expected {expected}, got {actual}");
        }
    }

    /// <summary>The plan shapes the generated <c>{Entity}Filter</c> methods build for an indexed property.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IndexQueryPlan_Eq_Lt_Lte_Gt_Gte_Between_Are_Exact(bool unique)
    {
        var names = await SeedPeopleV2(unique);
        var builder = new IndexPlanBuilder(IndexName);

        async Task Check(IndexQueryPlan plan, Func<string, bool> predicate, string description)
        {
            var expected = Expected(names, predicate);
            var actual = Sorted((await _db.PeopleV2.ScanAsync(plan).ToListAsync()).Select(p => p.Name));
            Assert.True(expected.SequenceEqual(actual), $"{description}: expected {expected.Count}, got {actual.Count}");
        }

        foreach (var v in Probes)
        {
            var key = IndexKey.Create(v);
            await Check(builder.Exact(key), n => n == v, $"Eq(\"{v}\")");
            await Check(builder.Range(IndexKey.MinKey, key), n => InRange(n, null, v), $"Lte(\"{v}\")");
            await Check(builder.Range(IndexKey.MinKey, key).And(BsonPredicateBuilder.Lt("name", v)),
                n => InRange(n, null, v, maxInclusive: false), $"Lt(\"{v}\")");
            await Check(builder.Range(key, IndexKey.MaxKey), n => InRange(n, v, null), $"Gte(\"{v}\")");
            await Check(builder.Range(key, IndexKey.MaxKey).And(BsonPredicateBuilder.Gt("name", v)),
                n => InRange(n, v, null, minInclusive: false), $"Gt(\"{v}\")");
        }

        foreach (var lo in Probes)
        foreach (var hi in new[] { "p1", "p5", "p7", "p70", "q" })
            await Check(builder.Range(IndexKey.Create(lo), IndexKey.Create(hi)), n => InRange(n, lo, hi), $"Between(\"{lo}\", \"{hi}\")");
    }

    // ── Typed collections with other id types ─────────────────────────────────

    [Fact]
    public async Task Int_Id_Collection_Equality_And_Range_Are_Exact()
    {
        var names = Names(unique: false);
        await _db.People.CreateIndexAsync(p => p.Name, IndexName);
        await _db.People.InsertBulkAsync(names.Select((n, i) => new Person { Id = i + 1, Name = n, Age = i }));

        foreach (var probe in Probes)
        {
            var eq = _db.People.AsQueryable().Where(p => p.Name == probe).ToList();
            Assert.True(Expected(names, n => n == probe).SequenceEqual(Sorted(eq.Select(p => p.Name))), $"Where(Name == \"{probe}\")");

            var lte = await _db.People.QueryIndexAsync(IndexName, null, probe).ToListAsync();
            Assert.True(Expected(names, n => InRange(n, null, probe)).SequenceEqual(lte.Select(p => p.Name)), $"QueryIndexAsync(null, \"{probe}\")");
        }
    }

    [Fact]
    public async Task String_Id_Collection_Equality_And_Range_Are_Exact()
    {
        var names = Names(unique: false);
        await _db.StringEntities.CreateIndexAsync(e => e.Value, IndexName);
        await _db.StringEntities.InsertBulkAsync(names.Select((n, i) => new StringEntity { Id = $"id{i}", Value = n }));

        foreach (var probe in Probes)
        {
            var eq = _db.StringEntities.AsQueryable().Where(e => e.Value == probe).ToList();
            Assert.True(Expected(names, n => n == probe).SequenceEqual(Sorted(eq.Select(e => e.Value!))), $"Where(Value == \"{probe}\")");

            var lte = await _db.StringEntities.QueryIndexAsync(IndexName, null, probe).ToListAsync();
            Assert.True(Expected(names, n => InRange(n, null, probe)).SequenceEqual(lte.Select(e => e.Value!)), $"QueryIndexAsync(null, \"{probe}\")");
        }
    }

    [Fact]
    public async Task Byte_Array_Key_Equality_And_Range_Are_Exact()
    {
        byte[][] values =
        [
            [0x10, 0x20], [0x10, 0x20, 0x30], [0x10, 0x20, 0x30, 0x40], [0x10, 0x20, 0x31], [0x10, 0x21], [0x11],
        ];
        await _db.BinaryEntities.CreateIndexAsync(e => e.Data, IndexName);
        foreach (var v in values)
            await _db.BinaryEntities.InsertAsync(new BinaryEntity { Label = Convert.ToHexString(v), Data = v });

        static int Compare(byte[] a, byte[] b) => a.AsSpan().SequenceCompareTo(b);

        foreach (var probe in values)
        {
            var eq = await _db.BinaryEntities.QueryIndexAsync(IndexName, probe, probe).ToListAsync();
            Assert.Equal([Convert.ToHexString(probe)], eq.Select(e => e.Label));

            var lte = await _db.BinaryEntities.QueryIndexAsync(IndexName, null, probe).ToListAsync();
            Assert.Equal(values.Where(v => Compare(v, probe) <= 0).Select(Convert.ToHexString).Order(), lte.Select(e => e.Label).Order());
        }
    }

    // ── Unique check (not affected, guarded here) ─────────────────────────────

    [Fact]
    public async Task Unique_Index_Accepts_A_Prefix_Of_An_Existing_Value_And_Rejects_Duplicates()
    {
        await _db.PeopleV2.CreateIndexAsync(p => p.Name, IndexName, unique: true);
        await _db.PeopleV2.InsertAsync(new PersonV2 { Name = "p70" });
        await _db.PeopleV2.InsertAsync(new PersonV2 { Name = "p7" });

        await Assert.ThrowsAsync<BLiteValidationException>(() => _db.PeopleV2.InsertAsync(new PersonV2 { Name = "p7" }).AsTask());
        await Assert.ThrowsAsync<BLiteValidationException>(() => _db.PeopleV2.InsertAsync(new PersonV2 { Name = "p70" }).AsTask());

        Assert.Single(_db.PeopleV2.AsQueryable().Where(p => p.Name == "p7").ToList());
    }

    // ── Dynamic collection ────────────────────────────────────────────────────
    // Dynamic indexes store the plain user key (no document id suffix), so they were not affected;
    // these tests guard the same queries there. Values are distinct: duplicate keys in a dynamic
    // non-unique index are a separate problem (a forward scan can miss duplicates of a key that
    // straddle a leaf split).

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Dynamic_Collection_Equality_And_Range_Are_Exact(bool unique)
    {
        var names = Names(unique: true);
        using var engine = new BLiteEngine(_dynamicDbPath);
        var col = engine.GetOrCreateCollection("people");
        await col.CreateIndexAsync("name", IndexName, unique);
        foreach (var n in names)
            await col.InsertAsync(col.CreateDocument(["name"], b => b.AddString("name", n)));

        foreach (var probe in Probes)
        {
            var eq = await col.QueryIndexAsync(IndexName, probe, probe).ToListAsync();
            Assert.True(Expected(names, n => n == probe).SequenceEqual(eq.Select(d => Name(d))), $"QueryIndexAsync(\"{probe}\", \"{probe}\")");

            var blql = col.Query(BlqlFilter.Eq("name", probe)).ToList();
            Assert.True(Expected(names, n => n == probe).SequenceEqual(Sorted(blql.Select(d => Name(d)))), $"BLQL name == \"{probe}\"");
        }

        foreach (var ascending in new[] { true, false })
        foreach (var min in Bounds)
        foreach (var max in Bounds)
        {
            var expected = Expected(names, n => InRange(n, min, max), ascending);
            var actual = await col.QueryIndexAsync(IndexName, min, max, ascending).ToListAsync();
            Assert.True(expected.SequenceEqual(actual.Select(d => Name(d))),
                $"QueryIndexAsync(\"{min}\", \"{max}\", ascending: {ascending}): expected {expected.Count}, got {actual.Count}");
        }

        foreach (var prefix in new[] { "p1", "p5", "p7", "q" })
        {
            var actual = col.Query(BlqlFilter.StartsWith("name", prefix)).ToList();
            Assert.Equal(Expected(names, n => n.StartsWith(prefix, StringComparison.Ordinal)), Sorted(actual.Select(d => Name(d))));
        }
    }
}
