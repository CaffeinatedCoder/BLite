using BLite.Core.Collections;
using BLite.Core.Indexing;
using BLite.Core.Indexing.Internal;
using BLite.Core.Storage;
using BLite.Core.Transactions;

namespace BLite.Tests;

/// <summary>
/// <see cref="RTreeIndex.CollectAllPages()"/> enumerates every page of an R-tree, so spatial
/// indexes can be freed when they are dropped or rebuilt (#189).
/// </summary>
public class RTreeIndexPagesTests : IDisposable
{
    private readonly string _dbPath;
    private readonly StorageEngine _storage;

    public RTreeIndexPagesTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"blite_rtree_pages_{Guid.NewGuid():N}.db");
        _storage = new StorageEngine(_dbPath, PageFileConfig.Default);
    }

    public void Dispose()
    {
        _storage.Dispose();
        foreach (var p in new[] { _dbPath, Path.ChangeExtension(_dbPath, ".wal") })
            if (File.Exists(p)) File.Delete(p);
    }

    private static GeoBox Point(int i) => GeoBox.FromPoint(new GeoPoint(40.0 + (i % 100) * 0.01, 9.0 + (i / 100) * 0.01));

    private static int CountAll(RTreeIndex index, ITransaction? tx = null)
        => index.Search(new GeoBox(-90, -180, 90, 180), tx).Count();

    [Fact]
    public void CollectAllPages_EmptyTree_IsTheRootOnly()
    {
        var index = new RTreeIndex(_storage, IndexOptions.CreateSpatial("loc"), 0);

        Assert.Equal([index.RootPageId], index.CollectAllPages());
    }

    [Fact]
    public void CollectAllPages_SplitTree_ReturnsEveryNodeOnce()
    {
        var index = new RTreeIndex(_storage, IndexOptions.CreateSpatial("loc"), 0);
        const int n = 2000;
        for (int i = 0; i < n; i++)
            index.Insert(Point(i), new DocumentLocation((uint)(i + 1), 0));
        Assert.Equal(n, CountAll(index));

        var pages = index.CollectAllPages();

        int minLeaves = (n + SpatialPage.GetMaxEntries(_storage.PageSize) - 1) / SpatialPage.GetMaxEntries(_storage.PageSize);
        Assert.True(pages.Count > minLeaves, $"{pages.Count} pages for a tree that needs at least {minLeaves} leaves plus a root");
        Assert.Equal(pages.Count, pages.Distinct().Count());
        Assert.Contains(index.RootPageId, pages);

        // Every leaf entry is reachable through the collected pages: re-walk them and count entries.
        var buffer = new byte[_storage.PageSize];
        int entries = 0;
        foreach (var pageId in pages)
        {
            _storage.ReadPage(pageId, null, buffer);
            if (SpatialPage.GetIsLeaf(buffer)) entries += SpatialPage.GetEntryCount(buffer);
        }
        Assert.Equal(n, entries);
    }

    /// <summary>
    /// SplitNode allocates pages outside the transaction and links them only from nodes written
    /// in it. The transaction overload sees them; the committed view does not until the commit,
    /// and never after a rollback.
    /// </summary>
    [Fact]
    public async Task CollectAllPages_WithTransaction_SeesUncommittedSplitPages()
    {
        var index = new RTreeIndex(_storage, IndexOptions.CreateSpatial("loc"), 0);
        var tx = _storage.BeginTransaction(IsolationLevel.ReadCommitted);
        for (int i = 0; i < 2000; i++)
            index.Insert(Point(i), new DocumentLocation((uint)(i + 1), 0), tx);

        var uncommitted = index.CollectAllPages(tx.TransactionId);
        Assert.True(uncommitted.Count > 1, "the inserts must have split the root");
        Assert.Equal([index.RootPageId], index.CollectAllPages(0));

        await tx.RollbackAsync();

        Assert.Equal([index.RootPageId], index.CollectAllPages(0));
        Assert.Equal(0, CountAll(index));
    }

    [Fact]
    public async Task CollectAllPages_AfterCommit_CommittedViewMatchesTransactionView()
    {
        var index = new RTreeIndex(_storage, IndexOptions.CreateSpatial("loc"), 0);
        var tx = _storage.BeginTransaction(IsolationLevel.ReadCommitted);
        for (int i = 0; i < 2000; i++)
            index.Insert(Point(i), new DocumentLocation((uint)(i + 1), 0), tx);
        var uncommitted = index.CollectAllPages(tx.TransactionId);

        await tx.CommitAsync();

        Assert.Equal(uncommitted.OrderBy(p => p), index.CollectAllPages(0).OrderBy(p => p));
        Assert.Equal(2000, CountAll(index));
    }

    /// <summary>Dropping a collection returns its spatial index pages to the free list.</summary>
    [Fact]
    public async Task FreeCollectionPages_Frees_SpatialIndexPages()
    {
        _storage.Dispose();
        using var db = new TestDbContext(_dbPath);
        await db.GeoItems.InsertBulkAsync(Enumerable.Range(0, 3000)
            .Select(i => new GeoEntity { Name = $"g{i}", Location = (40.0 + (i % 100) * 0.01, 9.0 + (i / 100) * 0.01) }));
        await db.CheckpointAsync();
        var spatialPages = db.GeoItems.IndexManager.GetIndex("idx_spatial")!.CollectAllPages(0);
        Assert.True(spatialPages.Count > 1, "the index must span more than its root");

        var freed = db.Storage.FreeCollectionPagesCore("geo_items");

        Assert.All(spatialPages, p => Assert.Contains(p, freed));
    }
}
