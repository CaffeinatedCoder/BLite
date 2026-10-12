using BLite.Core;
using BLite.Shared;

namespace BLite.Tests;

/// <summary>
/// An index declared in the model of a collection that already holds documents must be filled
/// from them when it is first created (#171).
/// </summary>
public class IndexBackfillTests : IDisposable
{
    private readonly string _dbPath;

    public IndexBackfillTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"idx_backfill_{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        foreach (var f in new[] { _dbPath, Path.ChangeExtension(_dbPath, ".wal") })
            try { if (File.Exists(f)) File.Delete(f); } catch { /* best-effort */ }
    }

    private async Task SeedAsync()
    {
        using var v1 = new IndexEvoV1DbContext(_dbPath);
        for (int i = 0; i < 100; i++)
            await v1.People.InsertAsync(new PersonV2 { Name = $"p{i}", Age = i % 10 });
        await v1.Vectors.InsertAsync(new VectorEntity { Title = "A", Embedding = [0f, 0f, 0f] });
        await v1.Vectors.InsertAsync(new VectorEntity { Title = "B", Embedding = [5f, 5f, 5f] });
        await v1.Places.InsertAsync(new GeoEntity { Name = "Milan", Location = (45.46, 9.19) });
        await v1.Places.InsertAsync(new GeoEntity { Name = "New York", Location = (40.71, -74.01) });
    }

    [Fact]
    public async Task AddedBTreeIndex_CoversExistingDocuments()
    {
        await SeedAsync();

        using var v2 = new IndexEvoV2DbContext(_dbPath);

        Assert.Equal(10, v2.People.AsQueryable().Where(p => p.Age == 3).ToList().Count);
        Assert.Equal(10, (await v2.People.QueryIndexAsync("idx_Age", 3, 3).ToListAsync()).Count);
    }

    [Fact]
    public async Task AddedVectorIndex_CoversExistingDocuments()
    {
        await SeedAsync();

        using var v2 = new IndexEvoV2DbContext(_dbPath);

        var nearest = await v2.Vectors.VectorSearchAsync("idx_vector", [4f, 4f, 4f], 1).ToListAsync();
        Assert.Equal("B", Assert.Single(nearest).Title);
    }

    [Fact]
    public async Task AddedSpatialIndex_CoversExistingDocuments()
    {
        await SeedAsync();

        using var v2 = new IndexEvoV2DbContext(_dbPath);

        var inBox = await v2.Places.WithinAsync("idx_spatial", (44.0, 8.0), (46.0, 10.0)).ToListAsync();
        Assert.Equal("Milan", Assert.Single(inBox).Name);
    }

    [Fact]
    public async Task AddedIndex_IsFilledOnce_AndMaintainedAfterwards()
    {
        await SeedAsync();
        using (var v2 = new IndexEvoV2DbContext(_dbPath))
            await v2.People.InsertAsync(new PersonV2 { Name = "late", Age = 3 });

        using var reopened = new IndexEvoV2DbContext(_dbPath);

        Assert.Equal(11, (await reopened.People.QueryIndexAsync("idx_Age", 3, 3).ToListAsync()).Count);
    }

    [Fact]
    public async Task IndexThatCannotBeFilled_FailsTheOpen_AndIsNotPersisted()
    {
        await SeedAsync();

        // The stored embeddings have 3 dimensions; the declared index expects 4.
        var ex = Assert.Throws<InvalidOperationException>(() => new IndexEvoBadVectorDbContext(_dbPath));
        Assert.Contains("idx_vector", ex.Message);

        // Nothing was registered, so the data stays readable and the next open tries again.
        using (var v1 = new IndexEvoV1DbContext(_dbPath))
        {
            Assert.DoesNotContain(v1.Vectors.GetIndexes(), i => i.Name == "idx_vector");
            Assert.Equal(2, v1.Vectors.AsQueryable().ToList().Count);
        }
        Assert.Throws<InvalidOperationException>(() => new IndexEvoBadVectorDbContext(_dbPath));
    }
}
