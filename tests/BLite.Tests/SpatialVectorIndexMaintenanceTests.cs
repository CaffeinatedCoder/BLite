using BLite.Bson;
using BLite.Core;
using BLite.Core.Indexing;
using BLite.Shared;

namespace BLite.Tests;

/// <summary>
/// Spatial (R-Tree) and vector (HNSW) indexes must follow updates and deletes, so a search never
/// returns a document at a position or embedding it no longer has (#164).
/// </summary>
public class SpatialVectorIndexMaintenanceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly TestDbContext _db;

    public SpatialVectorIndexMaintenanceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"idx_maint_{Guid.NewGuid():N}.db");
        _db = new TestDbContext(_dbPath);
    }

    public void Dispose()
    {
        _db.Dispose();
        foreach (var f in new[] { _dbPath, Path.ChangeExtension(_dbPath, ".wal") })
            try { if (File.Exists(f)) File.Delete(f); } catch { /* best-effort */ }
    }

    private List<GeoEntity> Within((double, double) min, (double, double) max)
        => _db.GeoItems.AsQueryable().Where(p => p.Location.Within(min, max)).ToList();

    private Task<List<VectorEntity>> NearestAsync(float[] query, int k)
        => _db.VectorItems.VectorSearchAsync("idx_vector", query, k).ToListAsync();

    // ── Spatial ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Spatial_Update_MovesDocument()
    {
        var e = new GeoEntity { Name = "mover", Location = (45.0, 9.0) };
        await _db.GeoItems.InsertAsync(e);
        e.Location = (10.0, 10.0);
        await _db.GeoItems.UpdateAsync(e);

        Assert.Empty(Within((44.0, 8.0), (46.0, 10.0)));
        Assert.Equal("mover", Assert.Single(Within((9.0, 9.0), (11.0, 11.0))).Name);
    }

    [Fact]
    public async Task Spatial_DeleteThenInsertElsewhere_DoesNotReturnTheNewDocument()
    {
        var e = new GeoEntity { Name = "deleted", Location = (45.0, 9.0) };
        await _db.GeoItems.InsertAsync(e);
        await _db.GeoItems.DeleteAsync(e.Id);
        await _db.GeoItems.InsertAsync(new GeoEntity { Name = "far", Location = (-30.0, 120.0) });

        Assert.Empty(Within((44.0, 8.0), (46.0, 10.0)));
    }

    [Fact]
    public async Task Spatial_DeleteHalf_OfASplitTree_ReturnsExactlyTheSurvivors()
    {
        var all = new List<GeoEntity>();
        for (int i = 0; i < 1000; i++)
        {
            var e = new GeoEntity { Name = $"p{i}", Location = (i % 40 * 0.5, i / 40 * 0.5) };
            await _db.GeoItems.InsertAsync(e);
            all.Add(e);
        }
        foreach (var e in all.Where((_, i) => i % 2 == 0))
            await _db.GeoItems.DeleteAsync(e.Id);
        // Reuse the freed slots far outside the queried area.
        for (int i = 0; i < 500; i++)
            await _db.GeoItems.InsertAsync(new GeoEntity { Name = $"far{i}", Location = (-60.0, -120.0) });

        var found = Within((-1.0, -1.0), (100.0, 100.0)).Select(e => e.Name).OrderBy(n => n).ToList();
        var expected = all.Where((_, i) => i % 2 == 1).Select(e => e.Name).OrderBy(n => n).ToList();
        Assert.Equal(expected, found);
    }

    [Fact]
    public async Task Spatial_RelocatingUpdate_MovesDocument()
    {
        var e = new GeoEntity { Name = "small", Location = (45.0, 9.0) };
        await _db.GeoItems.InsertAsync(e);
        e.Name = new string('x', 2000); // no longer fits its slot
        e.Location = (10.0, 10.0);
        await _db.GeoItems.UpdateAsync(e);

        Assert.Empty(Within((44.0, 8.0), (46.0, 10.0)));
        Assert.Equal(e.Name, Assert.Single(Within((9.0, 9.0), (11.0, 11.0))).Name);
    }

    [Fact]
    public async Task Spatial_DeleteRolledBack_KeepsDocument()
    {
        var e = new GeoEntity { Name = "kept", Location = (45.0, 9.0) };
        await _db.GeoItems.InsertAsync(e);

        using (var txn = await _db.BeginTransactionAsync())
        {
            await _db.GeoItems.DeleteAsync(e.Id, txn);
            await txn.RollbackAsync();
        }

        Assert.Equal("kept", Assert.Single(Within((44.0, 8.0), (46.0, 10.0))).Name);
    }

    // ── Vector ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Vector_Update_UsesNewEmbedding()
    {
        var a = new VectorEntity { Title = "A", Embedding = [0f, 0f, 0f] };
        await _db.VectorItems.InsertAsync(a);
        await _db.VectorItems.InsertAsync(new VectorEntity { Title = "B", Embedding = [5f, 5f, 5f] });
        a.Embedding = [100f, 100f, 100f];
        await _db.VectorItems.UpdateAsync(a);

        Assert.Equal("B", Assert.Single(await NearestAsync([0f, 0f, 0f], 1)).Title);
        Assert.Equal("A", Assert.Single(await NearestAsync([100f, 100f, 100f], 1)).Title);
    }

    [Fact]
    public async Task Vector_DeleteThenInsertElsewhere_DoesNotReturnTheNewDocument()
    {
        var a = new VectorEntity { Title = "A", Embedding = [0f, 0f, 0f] };
        await _db.VectorItems.InsertAsync(a);
        await _db.VectorItems.DeleteAsync(a.Id);
        await _db.VectorItems.InsertAsync(new VectorEntity { Title = "C", Embedding = [50f, 50f, 50f] });
        await _db.VectorItems.InsertAsync(new VectorEntity { Title = "B", Embedding = [5f, 5f, 5f] });

        Assert.Equal("B", Assert.Single(await NearestAsync([0f, 0f, 0f], 1)).Title);
    }

    [Fact]
    public async Task Vector_DeleteMoreThanEfSearch_SearchStillReturnsKLiveResults()
    {
        // 200 deleted nodes sit closer to the query than any survivor, more than efSearch (100).
        var all = new List<VectorEntity>();
        for (int i = 0; i < 300; i++)
        {
            var e = new VectorEntity { Title = $"v{i}", Embedding = [i, i, i] };
            await _db.VectorItems.InsertAsync(e);
            all.Add(e);
        }
        foreach (var e in all.Take(200))
            await _db.VectorItems.DeleteAsync(e.Id);

        var titles = (await NearestAsync([0f, 0f, 0f], 10)).Select(e => e.Title).ToList();
        Assert.Equal(Enumerable.Range(200, 10).Select(i => $"v{i}"), titles);
    }

    [Fact]
    public async Task Vector_RelocatingUpdatesWithAnUnchangedVector_AddNoNodes()
    {
        var a = new VectorEntity { Title = "a", Embedding = [1f, 2f, 3f] };
        await _db.VectorItems.InsertAsync(a);
        await _db.VectorItems.InsertAsync(new VectorEntity { Title = "b", Embedding = [4f, 5f, 6f] });
        int before = VectorNodeCount();

        for (int i = 1; i <= 3; i++)
        {
            a.Title = new string('x', 1000 * i); // outgrows its slot every time
            await _db.VectorItems.UpdateAsync(a);
        }

        Assert.Equal(before, VectorNodeCount());
        Assert.Equal(a.Title, (await NearestAsync([1f, 2f, 3f], 1)).Single().Title);
    }

    /// <summary>All HNSW nodes of idx_vector, deleted ones included.</summary>
    private int VectorNodeCount()
    {
        var manager = typeof(BLite.Core.Collections.DocumentCollection<ObjectId, VectorEntity>)
            .GetField("_indexManager", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(_db.VectorItems)!;
        var index = (BLite.Core.Indexing.CollectionSecondaryIndex<ObjectId, VectorEntity>)manager.GetType()
            .GetMethod("GetIndex")!.Invoke(manager, ["idx_vector"])!;
        var vectorIndex = (VectorSearchIndex)index.GetType()
            .GetField("_vectorIndex", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(index)!;
        var buffer = new byte[_db.Storage.PageSize];
        int nodes = 0;
        foreach (var page in vectorIndex.CollectAllPages())
        {
            _db.Storage.ReadPage(page, null, buffer);
            nodes += BLite.Core.Storage.VectorPage.GetNodeCount(buffer);
        }
        return nodes;
    }

    [Fact]
    public async Task Vector_UpdateOfOtherField_KeepsOneResultPerDocument()
    {
        var a = new VectorEntity { Title = "A0", Embedding = [1f, 1f, 1f] };
        await _db.VectorItems.InsertAsync(a);
        await _db.VectorItems.InsertAsync(new VectorEntity { Title = "B", Embedding = [9f, 9f, 9f] });
        for (int i = 1; i <= 3; i++)
        {
            a.Title = $"A{i}";
            await _db.VectorItems.UpdateAsync(a);
        }
        a.Title = new string('a', 2000); // relocating update with an unchanged vector
        await _db.VectorItems.UpdateAsync(a);

        var results = await NearestAsync([1f, 1f, 1f], 5);
        Assert.Equal(2, results.Count);
        Assert.Equal(a.Title, results[0].Title);
    }

    [Fact]
    public async Task Vector_DeleteRolledBack_KeepsDocument()
    {
        var a = new VectorEntity { Title = "A", Embedding = [0f, 0f, 0f] };
        await _db.VectorItems.InsertAsync(a);
        await _db.VectorItems.InsertAsync(new VectorEntity { Title = "B", Embedding = [5f, 5f, 5f] });

        using (var txn = await _db.BeginTransactionAsync())
        {
            await _db.VectorItems.DeleteAsync(a.Id, txn);
            await txn.RollbackAsync();
        }

        Assert.Equal("A", Assert.Single(await NearestAsync([0f, 0f, 0f], 1)).Title);
    }

    // ── DynamicCollection ───────────────────────────────────────────────────

    [Fact]
    public async Task Dynamic_Spatial_UpdateAndDelete_AreReflected()
    {
        using var engine = new BLiteEngine(Path.ChangeExtension(_dbPath, ".dyn.db"));
        try
        {
            var col = engine.GetOrCreateCollection("places");
            await col.CreateSpatialIndexAsync("pos");

            var moverId = await col.InsertAsync(col.CreateDocument(["name", "pos"], b => b
                .AddString("name", "mover").AddCoordinates("pos", (45.0, 9.0))));
            var goneId = await col.InsertAsync(col.CreateDocument(["name", "pos"], b => b
                .AddString("name", "gone").AddCoordinates("pos", (45.1, 9.1))));

            await col.UpdateAsync(moverId, col.CreateDocument(["name", "pos"], b => b
                .AddString("name", "mover").AddCoordinates("pos", (10.0, 10.0))));
            await col.DeleteAsync(goneId);

            Assert.Empty(await col.WithinAsync("idx_spatial_pos", (44.0, 8.0), (46.0, 10.0)).ToListAsync());
            Assert.Single(await col.WithinAsync("idx_spatial_pos", (9.0, 9.0), (11.0, 11.0)).ToListAsync());
        }
        finally
        {
            engine.Dispose();
            foreach (var f in new[] { Path.ChangeExtension(_dbPath, ".dyn.db"), Path.ChangeExtension(_dbPath, ".dyn.wal") })
                try { if (File.Exists(f)) File.Delete(f); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task Dynamic_Vector_UpdateAndDelete_AreReflected()
    {
        using var engine = new BLiteEngine(Path.ChangeExtension(_dbPath, ".dyn.db"));
        try
        {
            var col = engine.GetOrCreateCollection("vecs");
            await col.CreateVectorIndexAsync("embedding", dimensions: 3, metric: VectorMetric.L2);

            BsonDocument Doc(string title, float[] v) => col.CreateDocument(["title", "embedding"], b => b
                .AddString("title", title).AddFloatArray("embedding", v));

            var movedId = await col.InsertAsync(Doc("moved", [0f, 0f, 0f]));
            var goneId = await col.InsertAsync(Doc("gone", [1f, 1f, 1f]));
            var renamedId = await col.InsertAsync(Doc("renamed", [5f, 5f, 5f]));

            await col.UpdateAsync(movedId, Doc("moved", [100f, 100f, 100f]));
            await col.DeleteAsync(goneId);
            await col.UpdateAsync(renamedId, Doc("renamed twice", [5f, 5f, 5f]));

            var top = await col.VectorSearchAsync("idx_vector_embedding", [0f, 0f, 0f], k: 3).ToListAsync();
            var titles = top.Select(d => d.TryGetString("title", out var t) ? t : null).ToList();
            Assert.Equal(new[] { "renamed twice", "moved" }, titles);
        }
        finally
        {
            engine.Dispose();
            foreach (var f in new[] { Path.ChangeExtension(_dbPath, ".dyn.db"), Path.ChangeExtension(_dbPath, ".dyn.wal") })
                try { if (File.Exists(f)) File.Delete(f); } catch { /* best-effort */ }
        }
    }

    // ── Statistics ──────────────────────────────────────────────────────────

    /// <summary>
    /// An update that gives a document a key, or takes it away, changes the index's document
    /// count like an insert or delete would, whether the document stays in its slot or moves.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Update_BetweenNullAndNonNullKey_AdjustsEstimatedDocumentCount(bool relocating)
    {
        var people = _db.PeopleWithEmbeddedAddress;
        // Read the index directly: GetIndexes() caches its result until the set of indexes changes.
        long Count() => people.IndexManager.GetAllIndexes().Select(i => i.GetInfo())
            .Single(i => i.PropertyPaths[0].StartsWith("MainAddress", StringComparison.OrdinalIgnoreCase))
            .EstimatedDocumentCount;
        // In place: each update is smaller than the stored document. Relocating: each one is larger.
        string Name(int step) => new string('x', relocating ? 1000 * step : 1000 / step);

        var p = new PersonWithEmbeddedAddress { Id = 1, Name = Name(1) };
        await people.InsertAsync(p);
        Assert.Equal(0, Count());

        p.MainAddress = new Address { City = new City { Name = "Milan" } };
        p.Name = Name(2);
        await people.UpdateAsync(p);
        Assert.Equal(1, Count());

        p.MainAddress = null;
        p.Name = Name(3);
        await people.UpdateAsync(p);
        Assert.Equal(0, Count());
    }
}
