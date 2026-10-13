using BLite.Core;
using BLite.Core.Indexing;
using BLite.Shared;

namespace BLite.Tests;

/// <summary>
/// <c>Near</c> / <c>Within</c> must give the same answer whether or not an index serves them,
/// and <c>VectorSearch</c> must not silently match every document outside an index plan (#165).
/// </summary>
public class NearWithinCorrectnessTests : IDisposable
{
    private static readonly (double, double) Milan = (45.4642, 9.1899);
    private static readonly (double, double) NewYork = (40.7128, -74.0060);

    private readonly string _dbPath;
    private readonly TestDbContext _db;

    public NearWithinCorrectnessTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"near_within_{Guid.NewGuid():N}.db");
        _db = new TestDbContext(_dbPath);
    }

    public void Dispose()
    {
        _db.Dispose();
        foreach (var f in new[] { _dbPath, Path.ChangeExtension(_dbPath, ".wal") })
            try { if (File.Exists(f)) File.Delete(f); } catch { /* best-effort */ }
    }

    // ── In-memory evaluation ────────────────────────────────────────────────

    [Fact]
    public void Near_InMemory_UsesGreatCircleDistance()
    {
        Assert.False(NewYork.Near(Milan, 10.0));
        Assert.True(NewYork.Near(Milan, 6500.0));
    }

    [Fact]
    public void Within_InMemory_IncludesEdges()
    {
        Assert.True((45.0, 9.0).Within((44.0, 8.0), (46.0, 10.0)));
        Assert.True((44.0, 10.0).Within((44.0, 8.0), (46.0, 10.0)));
        Assert.False(NewYork.Within((44.0, 8.0), (46.0, 10.0)));
    }

    [Fact]
    public void VectorSearch_InMemory_Throws()
    {
        Assert.Throws<NotSupportedException>(() => new[] { 1f }.VectorSearch([1f], 1));
    }

    // ── LINQ ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Near_InsideOr_OnlyMatchesNearbyDocuments()
    {
        await _db.GeoItems.InsertAsync(new GeoEntity { Name = "New York", Location = NewYork });
        await _db.GeoItems.InsertAsync(new GeoEntity { Name = "Milan", Location = (45.47, 9.19) });

        var results = _db.GeoItems.AsQueryable()
            .Where(p => p.Location.Near(Milan, 10.0) || p.Name == "nobody")
            .ToList();

        Assert.Equal("Milan", Assert.Single(results).Name);
    }

    [Fact]
    public async Task Within_InsideOr_OnlyMatchesDocumentsInTheBox()
    {
        await _db.GeoItems.InsertAsync(new GeoEntity { Name = "New York", Location = NewYork });
        (double, double) min = (44.0, 8.0), max = (46.0, 10.0);

        var results = _db.GeoItems.AsQueryable()
            .Where(p => p.Location.Within(min, max) || p.Name == "nobody")
            .ToList();

        Assert.Empty(results);
    }

    [Fact]
    public async Task NegatedNear_FullScan_ReturnsOnlyFarDocuments()
    {
        await _db.GeoItems.InsertAsync(new GeoEntity { Name = "New York", Location = NewYork });
        await _db.GeoItems.InsertAsync(new GeoEntity { Name = "Milan", Location = (45.47, 9.19) });

        var results = _db.GeoItems.AsQueryable().Where(p => !p.Location.Near(Milan, 10.0)).ToList();

        Assert.Equal("New York", Assert.Single(results).Name);
    }

    [Fact]
    public async Task IndexedNear_ExcludesBoxCorner()
    {
        // (0.85, 0.85) is 133.7 km from (0, 0): inside the 100 km bounding box, outside the circle.
        await _db.GeoItems.InsertAsync(new GeoEntity { Name = "corner", Location = (0.85, 0.85) });
        await _db.GeoItems.InsertAsync(new GeoEntity { Name = "inside", Location = (0.5, 0.5) });
        var center = (0.0, 0.0);

        var viaLinq = _db.GeoItems.AsQueryable().Where(p => p.Location.Near(center, 100.0)).ToList();
        var viaAsync = await _db.GeoItems.NearAsync("idx_spatial", center, 100.0).ToListAsync();
        var count = _db.GeoItems.AsQueryable().Count(p => p.Location.Near(center, 100.0));

        Assert.Equal("inside", Assert.Single(viaLinq).Name);
        Assert.Equal("inside", Assert.Single(viaAsync).Name);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task NearAndWithin_OnTheSameIndex_ReturnTheirIntersection()
    {
        await _db.GeoItems.InsertAsync(new GeoEntity { Name = "both", Location = (0.1, 0.1) });
        await _db.GeoItems.InsertAsync(new GeoEntity { Name = "near only", Location = (-0.1, -0.1) });
        await _db.GeoItems.InsertAsync(new GeoEntity { Name = "box only", Location = (5.0, 5.0) });
        var center = (0.0, 0.0);
        (double, double) min = (0.0, 0.0), max = (10.0, 10.0);

        var results = _db.GeoItems.AsQueryable()
            .Where(p => p.Location.Near(center, 50.0) && p.Location.Within(min, max))
            .ToList();

        Assert.Equal("both", Assert.Single(results).Name);
    }

    [Fact]
    public async Task VectorSearch_WithAnotherCondition_FiltersTheNearestNeighbours()
    {
        await _db.VectorItems.InsertAsync(new VectorEntity { Title = "A", Embedding = [0f, 0f, 0f] });
        await _db.VectorItems.InsertAsync(new VectorEntity { Title = "B", Embedding = [1f, 1f, 1f] });
        await _db.VectorItems.InsertAsync(new VectorEntity { Title = "B", Embedding = [50f, 50f, 50f] });
        float[] query = [0f, 0f, 0f];

        var results = _db.VectorItems.AsQueryable()
            .Where(x => x.Title == "B" && x.Embedding.VectorSearch(query, 2))
            .ToList();

        Assert.Equal(1f, Assert.Single(results).Embedding[0]);
    }

    [Fact]
    public async Task VectorSearch_InsideOr_Throws()
    {
        await _db.VectorItems.InsertAsync(new VectorEntity { Title = "A", Embedding = [0f, 0f, 0f] });
        float[] query = [0f, 0f, 0f];

        Assert.Throws<NotSupportedException>(() => _db.VectorItems.AsQueryable()
            .Where(x => x.Embedding.VectorSearch(query, 1) || x.Title == "A")
            .ToList());
    }

    [Fact]
    public async Task VectorSearch_FollowedByGroupByOrAggregate_UsesTheIndexResult()
    {
        await _db.VectorItems.InsertAsync(new VectorEntity { Title = "A", Embedding = [0f, 0f, 0f] });
        await _db.VectorItems.InsertAsync(new VectorEntity { Title = "A", Embedding = [1f, 1f, 1f] });
        await _db.VectorItems.InsertAsync(new VectorEntity { Title = "B", Embedding = [50f, 50f, 50f] });
        float[] query = [0f, 0f, 0f];

        var groups = _db.VectorItems.AsQueryable()
            .Where(x => x.Embedding.VectorSearch(query, 2))
            .GroupBy(x => x.Title)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToList();
        var sum = _db.VectorItems.AsQueryable()
            .Where(x => x.Embedding.VectorSearch(query, 2))
            .Sum(x => (int)x.Embedding[0]);

        Assert.Equal("A", Assert.Single(groups).Key);
        Assert.Equal(2, groups[0].Count);
        Assert.Equal(1, sum);
    }

    [Fact]
    public async Task NearOrNear_OnTheSameIndex_ReturnsDocumentsNearEither()
    {
        await _db.GeoItems.InsertAsync(new GeoEntity { Name = "Milan", Location = (45.47, 9.19) });
        await _db.GeoItems.InsertAsync(new GeoEntity { Name = "New York", Location = (40.72, -74.0) });
        await _db.GeoItems.InsertAsync(new GeoEntity { Name = "Tokyo", Location = (35.68, 139.69) });

        var results = _db.GeoItems.AsQueryable()
            .Where(p => p.Location.Near(Milan, 10.0) || p.Location.Near(NewYork, 10.0))
            .ToList()
            .Select(p => p.Name).OrderBy(n => n).ToList();

        Assert.Equal(new[] { "Milan", "New York" }, results);
    }

    [Fact]
    public async Task TwoVectorSearches_ExplainThatOnlyOneIsSupported()
    {
        await _db.VectorItems.InsertAsync(new VectorEntity { Title = "A", Embedding = [0f, 0f, 0f] });
        float[] q1 = [0f, 0f, 0f], q2 = [1f, 1f, 1f];

        var ex = Assert.Throws<NotSupportedException>(() => _db.VectorItems.AsQueryable()
            .Where(x => x.Embedding.VectorSearch(q1, 1) && x.Embedding.VectorSearch(q2, 1))
            .ToList());
        Assert.Contains("only one VectorSearch", ex.Message);
    }

    // Only the VectorSearch in a Where that FetchAsync applied (or in a terminal predicate folded
    // into it) is served by the index. One in a projection, a group or an aggregate selector after
    // the fetch must still throw, not match everything.

    private static readonly float[] Q = [0f, 0f, 0f];

    private async Task InsertVectorProbeDataAsync()
    {
        await _db.VectorItems.InsertAsync(new VectorEntity { Title = "A", Embedding = [0f, 0f, 0f] });
        await _db.VectorItems.InsertAsync(new VectorEntity { Title = "A", Embedding = [1f, 1f, 1f] });
        await _db.VectorItems.InsertAsync(new VectorEntity { Title = "B", Embedding = [50f, 50f, 50f] });
    }

    [Fact]
    public async Task VectorSearch_InWhereThenProjectionOrderedAfterSelect_UsesTheIndexResult()
    {
        await InsertVectorProbeDataAsync();

        var titles = _db.VectorItems.AsQueryable()
            .Where(x => x.Embedding.VectorSearch(Q, 2))
            .Select(x => new { x.Title })
            .OrderBy(a => a.Title)
            .ToList();

        Assert.Equal(new[] { "A", "A" }, titles.Select(a => a.Title));
    }

    [Fact]
    public async Task VectorSearch_InProjection_AfterIndexedVectorWhere_Throws()
    {
        await InsertVectorProbeDataAsync();
        float[] q2 = [1f, 1f, 1f];

        Assert.Throws<NotSupportedException>(() => _db.VectorItems.AsQueryable()
            .Where(x => x.Embedding.VectorSearch(Q, 2))
            .Select(x => new { x.Title, ok = x.Embedding.VectorSearch(q2, 1) })
            .OrderBy(a => a.Title)
            .ToList());
    }

    [Fact]
    public async Task VectorSearch_InProjection_AfterOtherWhere_Throws()
    {
        await InsertVectorProbeDataAsync();

        Assert.Throws<NotSupportedException>(() => _db.VectorItems.AsQueryable()
            .Where(x => x.Title != null)
            .Select(x => new { x.Title, ok = x.Embedding.VectorSearch(Q, 1) })
            .OrderBy(a => a.Title)
            .ToList());
    }

    [Fact]
    public async Task VectorSearch_InGroupCount_AfterOtherWhere_Throws()
    {
        await InsertVectorProbeDataAsync();

        Assert.Throws<NotSupportedException>(() => _db.VectorItems.AsQueryable()
            .Where(x => x.Title != null)
            .GroupBy(x => x.Title)
            .Select(g => new { g.Key, Count = g.Count(x => x.Embedding.VectorSearch(Q, 1)) })
            .ToList());
    }

    [Fact]
    public async Task VectorSearch_InSumSelector_AfterOtherWhere_Throws()
    {
        await InsertVectorProbeDataAsync();

        Assert.Throws<NotSupportedException>(() => _db.VectorItems.AsQueryable()
            .Where(x => x.Title != null)
            .Sum(x => x.Embedding.VectorSearch(Q, 1) ? 1 : 0));
    }

    // Count(p) after a plain Where is folded into the WHERE (#180), so its VectorSearch is served
    // by the index like any other: the vector term drives and Title != null is the post-filter.
    [Fact]
    public async Task VectorSearch_InCountPredicate_AfterOtherWhere_IsFoldedIntoTheWhere()
    {
        await InsertVectorProbeDataAsync();

        var folded = _db.VectorItems.AsQueryable()
            .Where(x => x.Title != null)
            .Count(x => x.Embedding.VectorSearch(Q, 1));
        var combined = _db.VectorItems.AsQueryable()
            .Where(x => x.Title != null && x.Embedding.VectorSearch(Q, 1))
            .Count();

        Assert.Equal(combined, folded);
        Assert.Equal(1, folded);
    }

    // ── DynamicCollection ───────────────────────────────────────────────────

    [Fact]
    public async Task Dynamic_Near_ExcludesBoxCorner()
    {
        var path = Path.ChangeExtension(_dbPath, ".dyn.db");
        try
        {
            using var engine = new BLiteEngine(path);
            var col = engine.GetOrCreateCollection("places");
            await col.CreateSpatialIndexAsync("pos");
            await col.InsertAsync(col.CreateDocument(["name", "pos"], b => b
                .AddString("name", "corner").AddCoordinates("pos", (0.85, 0.85))));
            await col.InsertAsync(col.CreateDocument(["name", "pos"], b => b
                .AddString("name", "inside").AddCoordinates("pos", (0.5, 0.5))));

            var results = await col.NearAsync("idx_spatial_pos", (0.0, 0.0), 100.0).ToListAsync();

            Assert.True(Assert.Single(results).TryGetString("name", out var name));
            Assert.Equal("inside", name);
        }
        finally
        {
            foreach (var f in new[] { path, Path.ChangeExtension(path, ".wal") })
                try { if (File.Exists(f)) File.Delete(f); } catch { /* best-effort */ }
        }
    }
}
