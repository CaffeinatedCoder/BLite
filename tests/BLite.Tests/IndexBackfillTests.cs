using System.Linq.Expressions;
using System.Reflection;
using BLite.Bson;
using BLite.Core;
using BLite.Core.Collections;
using BLite.Core.Indexing;
using BLite.Core.Metadata;
using BLite.Core.Storage;
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
        Assert.Contains("removed from the model", ex.Message);
        Assert.IsType<ArgumentException>(ex.InnerException); // the dimension mismatch
        Assert.Contains($"{ex.InnerException!.Message.TrimEnd('.')}. Nothing was saved", ex.Message);

        // Nothing was registered, so the data stays readable and the next open tries again.
        using (var v1 = new IndexEvoV1DbContext(_dbPath))
        {
            Assert.DoesNotContain(v1.Vectors.GetIndexes(), i => i.Name == "idx_vector");
            Assert.Equal(2, v1.Vectors.AsQueryable().ToList().Count);
        }
        Assert.Throws<InvalidOperationException>(() => new IndexEvoBadVectorDbContext(_dbPath));
    }

    /// <summary>
    /// A fill that fails after the index has split must free every page it added, not only the
    /// root (#171 review): the error repeats on every open until the data is fixed, so a leak would
    /// grow the file each time.
    /// </summary>
    [Fact]
    public async Task IndexThatFailsLate_FreesEveryPageItAdded_SoRepeatedOpensDoNotGrowTheFile()
    {
        using (var v1 = new IndexEvoV1DbContext(_dbPath))
        {
            await v1.Vectors.InsertBulkAsync(Enumerable.Range(0, 3000)
                .Select(i => new VectorEntity { Title = $"v{i}", Embedding = [i, i % 7, i % 13] }).ToList());

            // Give the last document in primary-key order a wrong-dimension embedding, so the fill
            // fails only after thousands of inserts.
            var primary = (BTreeIndex)typeof(DocumentCollection<ObjectId, VectorEntity>)
                .GetField("_primaryIndex", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(v1.Vectors)!;
            var lastId = primary.Range(IndexKey.MinKey, IndexKey.MaxKey, IndexDirection.Backward).First().Key.As<ObjectId>();
            var last = (await v1.Vectors.FindByIdAsync(lastId))!;
            last.Embedding = [1f, 2f];
            await v1.Vectors.UpdateAsync(last);
        }

        var sizes = new List<long>();
        for (int open = 0; open < 4; open++)
        {
            var ex = Assert.Throws<InvalidOperationException>(() => new IndexEvoV2DbContext(_dbPath));
            Assert.Contains("idx_vector", ex.Message);
            sizes.Add(new FileInfo(_dbPath).Length);
        }

        // The first failed open may extend the file; the following ones reuse the freed pages.
        Assert.All(sizes.Skip(1), size => Assert.Equal(sizes[0], size));
        using var reopened = new IndexEvoV1DbContext(_dbPath);
        Assert.DoesNotContain(reopened.Vectors.GetIndexes(), i => i.Name == "idx_vector");
    }

    /// <summary>
    /// If the filled index cannot be persisted, it is not left registered in memory either, and
    /// its pages are freed; the next open builds it again.
    /// </summary>
    [Fact]
    public async Task AddedIndex_WhoseMetadataCannotBeSaved_IsNotRegistered_AndItsPagesAreFreed()
    {
        using (var v1 = new IndexEvoV1DbContext(_dbPath))
        {
            await v1.People.InsertBulkAsync(Enumerable.Range(0, 3000)
                .Select(i => new PersonV2 { Name = $"p{i}", Age = i % 90 }).ToList());
            await v1.CheckpointAsync();

            // Hold the metadata lock so saving the new index times out.
            var metadataLock = (SemaphoreSlim)typeof(StorageEngine)
                .GetField("_metadataLock", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(v1.Storage)!;
            Expression<Func<PersonV2, int>> age = p => p.Age;
            metadataLock.Wait();
            try
            {
                Assert.Throws<TimeoutException>(() => v1.People.ApplyIndexBuilder(new IndexBuilder<PersonV2>(age, "idx_Age", unique: false)));
            }
            finally
            {
                metadataLock.Release();
            }

            Assert.Null(v1.People.IndexManager.GetIndex("idx_Age"));
            Assert.DoesNotContain(v1.People.GetIndexes(), i => i.Name == "idx_Age");
            Assert.DoesNotContain(v1.Storage.GetCollectionMetadata("evo_people")!.Indexes, i => i.Name == "idx_Age");
            await AssertIndexPagesWereFreedAsync(v1);
        }

        using var v2 = new IndexEvoV2DbContext(_dbPath);
        Assert.Equal(34, (await v2.People.QueryIndexAsync("idx_Age", 3, 3).ToListAsync()).Count);
    }

    /// <summary>
    /// A unique index added by the model is enforced in the session that creates it, not only after
    /// the next open.
    /// </summary>
    [Fact]
    public async Task AddedUniqueIndex_IsEnforcedRightAway()
    {
        await SeedAsync();
        using var v1 = new IndexEvoV1DbContext(_dbPath);
        Expression<Func<PersonV2, string>> name = p => p.Name;

        v1.People.ApplyIndexBuilder(new IndexBuilder<PersonV2>(name, "idx_Name", unique: true));

        await Assert.ThrowsAnyAsync<Exception>(async () => await v1.People.InsertAsync(new PersonV2 { Name = "p99", Age = 1 }));
        await v1.People.InsertAsync(new PersonV2 { Name = "new", Age = 1 });
        Assert.Single(v1.People.AsQueryable().ToList(), p => p.Name == "p99");
    }

    /// <summary>
    /// A 3000-entry B-Tree spans dozens of pages. Each must be back on the free list (handed out
    /// next, below the high-water mark) or queued for the next WAL truncation.
    /// </summary>
    private static async Task AssertIndexPagesWereFreedAsync(IndexEvoV1DbContext db)
    {
        var storage = db.Storage;
        await db.CheckpointAsync();
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        int queued = ((HashSet<uint>)typeof(StorageEngine).GetField("_pendingFreePages", flags)!.GetValue(storage)!).Count;
        var highWater = ((IPageStorage)typeof(StorageEngine).GetField("_pageFile", flags)!.GetValue(storage)!).NextPageId;
        int reused = 0;
        while (storage.AllocatePage() < highWater) reused++;
        Assert.True(reused + queued > 20, $"only {reused} pages were returned to the free list and {queued} queued");
    }
}
