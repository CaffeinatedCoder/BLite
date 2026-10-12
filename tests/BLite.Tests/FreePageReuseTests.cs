using BLite.Core;
using BLite.Core.Storage;
using BLite.Shared;

namespace BLite.Tests;

/// <summary>
/// A freed page must not be handed out again while the WAL still holds a committed image of
/// it: reads prefer the WAL, so the reused page would read back its old contents (#168).
/// </summary>
public class FreePageReuseTests : IDisposable
{
    private readonly string _dbPath;

    public FreePageReuseTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"freepage_{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        TryDelete(_dbPath);
        TryDelete(Path.ChangeExtension(_dbPath, ".wal"));
        var collDir = _dbPath + "_coll";
        if (Directory.Exists(collDir))
            try { Directory.Delete(collDir, recursive: true); } catch { /* best-effort */ }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task DropAndRecreateIndex_RangeScan_ReturnsEachDocumentOnce()
    {
        using var db = new TestDbContext(_dbPath);
        for (int i = 0; i < 200; i++)
            await db.PeopleV2.InsertAsync(new PersonV2 { Name = $"p{i}", Age = i % 50 });

        for (int pass = 0; pass < 4; pass++)
        {
            await db.PeopleV2.DropIndexAsync("idx_Age");
            await db.PeopleV2.CreateIndexAsync(p => p.Age, "idx_Age");

            Assert.Equal(200, (await db.PeopleV2.QueryIndexAsync("idx_Age", null, null).ToListAsync()).Count);
        }

        Assert.Equal(200, db.PeopleV2.AsQueryable().Where(p => p.Age >= 0).ToList().Count);
        Assert.Equal(4, db.PeopleV2.AsQueryable().Where(p => p.Age == 49).ToList().Count);
    }

    [Fact]
    public async Task FreedPage_IsReusedOnlyAfterWalTruncation_AndReadsItsNewContents()
    {
        using var storage = new StorageEngine(_dbPath, PageFileConfig.Default);
        var page = await AllocateAndCommitAsync(storage, fill: 0xAA);

        storage.FreePage(page);
        Assert.NotEqual(page, storage.AllocatePage());

        await storage.CheckpointAsync();
        Assert.Equal(page, storage.AllocatePage());

        var fresh = Filled(storage.PageSize, 0xBB);
        storage.WritePageImmediate(page, fresh);
        var read = new byte[storage.PageSize];
        storage.ReadPage(page, null, read);
        Assert.Equal(fresh, read);
    }

    [Fact]
    public async Task FreedPage_WithEmptyWalIndex_IsReleasedByCheckpoint()
    {
        using var storage = new StorageEngine(_dbPath, PageFileConfig.Default);
        var page = await AllocateAndCommitAsync(storage, fill: 0xAA);
        await storage.CheckpointAsync();

        storage.FreePage(page);
        await storage.CheckpointAsync();

        Assert.Equal(page, storage.AllocatePage());
    }

    [Fact]
    public async Task FreedPage_StillQueuedAtDispose_IsNotLeaked()
    {
        uint page;
        using (var storage = new StorageEngine(_dbPath, PageFileConfig.Default))
        {
            page = await AllocateAndCommitAsync(storage, fill: 0xAA);
            storage.FreePage(page);
        }

        using (var storage = new StorageEngine(_dbPath, PageFileConfig.Default))
            Assert.Equal(page, storage.AllocatePage());
    }

    [Fact]
    public async Task DroppedCollectionFile_DiscardsItsQueuedFrees()
    {
        var config = PageFileConfig.Default with { CollectionDataDirectory = _dbPath + "_coll" };
        using var storage = new StorageEngine(_dbPath, config);

        var oldPage = storage.AllocateCollectionPage("items");
        await WriteAndCommitAsync(storage, oldPage, fill: 0xAA);
        storage.FreePage(oldPage);
        storage.DropCollectionFile("items");

        // The recreated file reuses the slot, so its first page gets the same encoded ID.
        var livePage = storage.AllocateCollectionPage("items");
        await WriteAndCommitAsync(storage, livePage, fill: 0xCC);
        await storage.CheckpointAsync();

        // Releasing the stale free would have put the live page on the new file's free list.
        Assert.NotEqual(livePage, storage.AllocateCollectionPage("items"));
    }

    private static async Task<uint> AllocateAndCommitAsync(StorageEngine storage, byte fill)
    {
        var page = storage.AllocatePage();
        await WriteAndCommitAsync(storage, page, fill);
        return page;
    }

    private static async Task WriteAndCommitAsync(StorageEngine storage, uint page, byte fill)
    {
        using var txn = storage.BeginTransaction();
        storage.WritePage(page, txn.TransactionId, Filled(storage.PageSize, fill));
        await txn.CommitAsync();
    }

    private static byte[] Filled(int size, byte value)
    {
        var buffer = new byte[size];
        buffer.AsSpan(32).Fill(value); // keep a zeroed page header
        return buffer;
    }
}
