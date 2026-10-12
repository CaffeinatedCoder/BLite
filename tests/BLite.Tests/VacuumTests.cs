using BLite.Bson;
using BLite.Core;
using BLite.Core.Collections;
using BLite.Core.Indexing;
using BLite.Core.Storage;
using BLite.Core.Transactions;
using BLite.Shared;

namespace BLite.Tests;

/// <summary>
/// Verifies that <see cref="BLiteEngine.VacuumAsync"/>,
/// <see cref="DynamicCollection.VacuumAsync"/>, and
/// <see cref="DocumentCollection{TId,T}.VacuumAsync"/> do not regress:
/// <list type="bullet">
///   <item>Free Space Index (FSI) accuracy — pages remain findable after VACUUM.</item>
///   <item>Primary index integrity — all surviving documents are still locatable by ID.</item>
///   <item>Secondary index integrity — range and equality queries return correct results.</item>
///   <item>Slot-level secure erase — freed byte ranges are zero after delete and after VACUUM.</item>
///   <item>File truncation — <see cref="VacuumOptions.TruncateFile"/> shrinks the file.</item>
/// </list>
/// </summary>
public class VacuumTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _walPath;

    public VacuumTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"vacuum_{Guid.NewGuid():N}.db");
        _walPath = Path.ChangeExtension(_dbPath, ".wal");
    }

    public void Dispose()
    {
        TryDelete(_dbPath);
        TryDelete(_walPath);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
    }

    // ======================================================================
    //  VACUUM on BLiteEngine (DynamicCollection path)
    // ======================================================================

    /// <summary>
    /// After inserting documents, deleting some, and running VACUUM, every surviving
    /// document must still be retrievable via FindByIdAsync (primary index integrity).
    /// </summary>
    [Fact]
    public async Task Vacuum_Engine_PrimaryIndex_SurvivingDocuments_StillFoundById()
    {
        using var engine = new BLiteEngine(_dbPath);
        var col = engine.GetOrCreateCollection("docs");

        var ids = new List<BsonId>();
        for (int i = 0; i < 50; i++)
            ids.Add(await col.InsertAsync(MakeDoc(col, i)));

        // Delete every other document (25 deletions -> 25 survivors)
        var deletedSet = new HashSet<int>();
        for (int i = 0; i < ids.Count; i += 2)
        {
            await col.DeleteAsync(ids[i]);
            deletedSet.Add(i);
        }

        await engine.VacuumAsync(new VacuumOptions { TruncateFile = false });

        for (int i = 0; i < ids.Count; i++)
        {
            var found = await col.FindByIdAsync(ids[i]);
            if (deletedSet.Contains(i))
                Assert.Null(found);
            else
                Assert.NotNull(found);
        }
    }

    /// <summary>
    /// After VACUUM, a secondary BTree index must still return the correct results
    /// for a range query (secondary index integrity).
    /// </summary>
    [Fact]
    public async Task Vacuum_Engine_SecondaryIndex_RangeQuery_StillCorrect()
    {
        using var engine = new BLiteEngine(_dbPath);
        var col = engine.GetOrCreateCollection("scored");
        await col.CreateIndexAsync("score", "idx_score");

        for (int i = 0; i < 30; i++)
            await col.InsertAsync(MakeDocWithScore(col, i));

        // Delete documents with score < 10 and score >= 20 to create fragmentation
        var allDocs = await col.FindAllAsync().ToListAsync();
        foreach (var doc in allDocs)
        {
            if (doc.TryGetInt32("score", out var s) && (s < 10 || s >= 20))
            {
                if (doc.TryGetId(out var bid))
                    await col.DeleteAsync(bid);
            }
        }

        await engine.VacuumAsync(new VacuumOptions { TruncateFile = false });

        // Index query [10, 19] must return exactly 10 documents
        var results = await col.QueryIndexAsync("idx_score", 10, 19).ToListAsync();
        Assert.Equal(10, results.Count);

        foreach (var doc in results)
        {
            Assert.True(doc.TryGetInt32("score", out var s));
            Assert.InRange(s, 10, 19);
        }
    }

    /// <summary>
    /// After VACUUM, newly inserted documents must be accepted without error —
    /// confirming that the FSI is still functional after the vacuum pass.
    /// </summary>
    [Fact]
    public async Task Vacuum_Engine_FSI_NewInserts_SucceedAfterVacuum()
    {
        using var engine = new BLiteEngine(_dbPath);
        var col = engine.GetOrCreateCollection("fsi_test");

        var ids = new List<BsonId>();
        for (int i = 0; i < 40; i++)
            ids.Add(await col.InsertAsync(MakeDoc(col, i)));

        // Delete 30 of 40 documents -> plenty of free space on pages
        foreach (var id in ids.Take(30))
            await col.DeleteAsync(id);

        await engine.VacuumAsync(new VacuumOptions { TruncateFile = false });

        // FSI must still locate free space for new inserts
        for (int i = 0; i < 10; i++)
            await col.InsertAsync(MakeDoc(col, 1000 + i));

        // 10 survivors + 10 new = 20
        Assert.Equal(20, await col.CountAsync());
    }

    // ======================================================================
    //  VACUUM on DocumentCollection (typed path)
    // ======================================================================

    /// <summary>
    /// Typed DocumentCollection: every surviving document is still findable by its
    /// primary-index ID after VacuumAsync runs.
    /// </summary>
    [Fact]
    public async Task Vacuum_DocumentCollection_PrimaryIndex_SurvivingDocuments_StillFoundById()
    {
        using var db = new TestDbContext(_dbPath);

        var inserted = new List<User>();
        for (int i = 0; i < 40; i++)
        {
            var u = new User { Id = ObjectId.NewObjectId(), Name = $"User {i}", Age = i };
            inserted.Add(u);
            await db.Users.InsertAsync(u);
        }
        await db.SaveChangesAsync();

        // Delete every third user
        var deletedIds = new HashSet<ObjectId>();
        foreach (var u in inserted.Where((_, idx) => idx % 3 == 0))
        {
            await db.Users.DeleteAsync(u.Id);
            deletedIds.Add(u.Id);
        }
        await db.SaveChangesAsync();

        await db.Users.VacuumAsync();

        foreach (var u in inserted)
        {
            var found = await db.Users.FindByIdAsync(u.Id);
            if (deletedIds.Contains(u.Id))
                Assert.Null(found);
            else
            {
                Assert.NotNull(found);
                Assert.Equal(u.Name, found!.Name);
            }
        }
    }

    /// <summary>
    /// Typed DocumentCollection with a secondary B-Tree index on Age: the index still
    /// returns correct results after VacuumAsync.
    /// </summary>
    [Fact]
    public async Task Vacuum_DocumentCollection_SecondaryIndex_Age_StillCorrect()
    {
        using var db = new TestDbContext(_dbPath);

        // `People` (people_collection) has a secondary index on Age (declared in OnModelCreating).
        // Use IDs starting from 1 to avoid the int default-value (0) being treated as
        // "not assigned" by the auto-increment mapper.
        for (int i = 0; i < 30; i++)
            await db.People.InsertAsync(new Person { Id = i + 1, Name = $"P{i}", Age = i });
        await db.SaveChangesAsync();

        // Delete people with Age < 10 to create fragmented pages
        var toDelete = await db.People.FindAllAsync().Where(p => p.Age < 10).ToListAsync();
        foreach (var p in toDelete)
            await db.People.DeleteAsync(p.Id);
        await db.SaveChangesAsync();

        await db.People.VacuumAsync();

        // Secondary index range query Age 10..20 must return exactly 11 documents
        // (Age 10, 11, ..., 20 inclusive — all inserted with Age = i for i in 10..20)
        var results = await db.People.QueryIndexAsync("idx_age", 10, 20).ToListAsync();
        Assert.Equal(11, results.Count);
        foreach (var p in results)
            Assert.InRange(p.Age, 10, 20);
    }

    /// <summary>
    /// After VacuumAsync, the total document count must match the expected survivors.
    /// </summary>
    [Fact]
    public async Task Vacuum_DocumentCollection_CountRemainsCorrect()
    {
        using var db = new TestDbContext(_dbPath);

        for (int i = 0; i < 50; i++)
            await db.Users.InsertAsync(new User { Id = ObjectId.NewObjectId(), Name = $"U{i}", Age = i });
        await db.SaveChangesAsync();

        var toDelete = await db.Users.FindAllAsync().Take(20).ToListAsync();
        foreach (var u in toDelete)
            await db.Users.DeleteAsync(u.Id);
        await db.SaveChangesAsync();

        await db.Users.VacuumAsync();

        Assert.Equal(30, await db.Users.CountAsync());
    }

    /// <summary>
    /// After VacuumAsync, newly inserted documents must be accepted without error,
    /// confirming that the FSI is still consistent after the vacuum pass.
    /// </summary>
    [Fact]
    public async Task Vacuum_DocumentCollection_FSI_NewInserts_SucceedAfterVacuum()
    {
        using var db = new TestDbContext(_dbPath);

        for (int i = 0; i < 40; i++)
            await db.Users.InsertAsync(new User { Id = ObjectId.NewObjectId(), Name = $"U{i}", Age = i });
        await db.SaveChangesAsync();

        var toDelete = await db.Users.FindAllAsync().Take(30).ToListAsync();
        foreach (var u in toDelete)
            await db.Users.DeleteAsync(u.Id);
        await db.SaveChangesAsync();

        await db.Users.VacuumAsync();

        // Must be able to insert 10 more documents without error
        for (int i = 0; i < 10; i++)
            await db.Users.InsertAsync(new User { Id = ObjectId.NewObjectId(), Name = $"New{i}", Age = 100 + i });
        await db.SaveChangesAsync();

        // 10 survivors + 10 new = 20
        Assert.Equal(20, await db.Users.CountAsync());
    }

    /// <summary>
    /// Products collection has a secondary index on Price. After VACUUM, range queries
    /// on that index must still return the correct results.
    /// </summary>
    [Fact]
    public async Task Vacuum_DocumentCollection_SecondaryIndex_Price_StillCorrect()
    {
        using var db = new TestDbContext(_dbPath);

        for (int i = 1; i <= 30; i++)
            await db.Products.InsertAsync(new Product { Id = i, Title = $"Product{i}", Price = i * 1.5m });
        await db.SaveChangesAsync();

        // Delete products with Id <= 10 to fragment the page
        for (int i = 1; i <= 10; i++)
            await db.Products.DeleteAsync(i);
        await db.SaveChangesAsync();

        await db.Products.VacuumAsync();

        // Products with Price in [16.5, 30.0] are Id 11..20 (Price = 16.5 .. 30.0, 10 products).
        var results = await db.Products.QueryIndexAsync("idx_price", 16.5m, 30m).ToListAsync();
        Assert.Equal(10, results.Count);
        var sortedResults = results.OrderBy(p => p.Id).ToList();
        for (int i = 0; i < sortedResults.Count; i++)
        {
            var expectedId = i + 11; // Ids 11..20
            Assert.Equal(expectedId, sortedResults[i].Id);
            Assert.Equal(expectedId * 1.5m, sortedResults[i].Price);
            Assert.InRange(sortedResults[i].Price, 16.5m, 30m);
        }
    }

    // ======================================================================
    //  Secure erase: immediate (slot-level, on delete)
    // ======================================================================

    /// <summary>
    /// After deleting a document from a DynamicCollection, the free space area on the
    /// page must be entirely zero-filled (slot-level secure erase).
    /// </summary>
    [Fact]
    public async Task SecureErase_Immediate_DynamicCollection_FreeSpaceIsZero_AfterDelete()
    {
        using var engine = new BLiteEngine(_dbPath);
        var col = engine.GetOrCreateCollection("secure");

        var id = await col.InsertAsync(MakeDoc(col, 42));
        await col.DeleteAsync(id);

        // Checkpoint so the page is written to the file
        await engine.Storage.CheckpointAsync();

        AssertAllDataPageFreeSpaceIsZero(engine.Storage, "secure");
    }

    /// <summary>
    /// After deleting a document from a typed DocumentCollection, the free space area
    /// on the page must be entirely zero-filled (slot-level secure erase).
    /// </summary>
    [Fact]
    public async Task SecureErase_Immediate_DocumentCollection_FreeSpaceIsZero_AfterDelete()
    {
        using var db = new TestDbContext(_dbPath);

        var user = new User { Id = ObjectId.NewObjectId(), Name = "Sensitive PII", Age = 42 };
        await db.Users.InsertAsync(user);
        await db.SaveChangesAsync();

        await db.Users.DeleteAsync(user.Id);
        await db.SaveChangesAsync();

        await db.CheckpointAsync();

        AssertAllDataPageFreeSpaceIsZero(db.Storage, "users");
    }

    // ======================================================================
    //  Secure erase: VACUUM pass
    // ======================================================================

    /// <summary>
    /// After VacuumAsync on a DynamicCollection, every data page's free space region
    /// must be all-zeros — erasing any residual data from previous deletions.
    /// </summary>
    [Fact]
    public async Task Vacuum_DynamicCollection_FreeBytesAreZero_AfterVacuum()
    {
        using var engine = new BLiteEngine(_dbPath);
        var col = engine.GetOrCreateCollection("erase");

        for (int i = 0; i < 20; i++)
            await col.InsertAsync(MakeDoc(col, i));

        var allDocs = await col.FindAllAsync().ToListAsync();
        foreach (var doc in allDocs.Take(10))
        {
            if (doc.TryGetId(out var bid))
                await col.DeleteAsync(bid);
        }

        await engine.VacuumAsync(new VacuumOptions { SecureErase = true, TruncateFile = false });
        await engine.Storage.CheckpointAsync();

        AssertAllDataPageFreeSpaceIsZero(engine.Storage, "erase");
    }

    /// <summary>
    /// After VacuumAsync on a typed DocumentCollection, every data page's free space
    /// region must be all-zeros.
    /// </summary>
    [Fact]
    public async Task Vacuum_DocumentCollection_FreeBytesAreZero_AfterVacuum()
    {
        using var db = new TestDbContext(_dbPath);

        for (int i = 0; i < 20; i++)
            await db.Users.InsertAsync(new User { Id = ObjectId.NewObjectId(), Name = $"U{i}", Age = i });
        await db.SaveChangesAsync();

        var toDelete = await db.Users.FindAllAsync().Take(10).ToListAsync();
        foreach (var u in toDelete)
            await db.Users.DeleteAsync(u.Id);
        await db.SaveChangesAsync();

        await db.Users.VacuumAsync();
        await db.CheckpointAsync();

        AssertAllDataPageFreeSpaceIsZero(db.Storage, "users");
    }

    // ======================================================================
    //  File truncation
    // ======================================================================

    /// <summary>
    /// After deleting many documents and running VACUUM with TruncateFile=true,
    /// the file must not be larger than it was before the delete+vacuum.
    /// </summary>
    [Fact]
    public async Task Vacuum_TruncateFile_ShrinksFile()
    {
        using var engine = new BLiteEngine(_dbPath);
        var col = engine.GetOrCreateCollection("trunc");

        // Insert enough documents to span multiple pages
        for (int i = 0; i < 200; i++)
            await col.InsertAsync(MakeDoc(col, i));

        await engine.Storage.CheckpointAsync();
        long sizeBeforeDelete = new FileInfo(_dbPath).Length;

        // Delete all documents to maximise free pages
        var allDocs = await col.FindAllAsync().ToListAsync();
        foreach (var doc in allDocs)
        {
            if (doc.TryGetId(out var bid))
                await col.DeleteAsync(bid);
        }

        await engine.VacuumAsync(new VacuumOptions { SecureErase = true, TruncateFile = true });

        long sizeAfterVacuum = new FileInfo(_dbPath).Length;

        Assert.True(
            sizeAfterVacuum <= sizeBeforeDelete,
            $"Expected file to shrink: before={sizeBeforeDelete} bytes, after={sizeAfterVacuum} bytes");
    }

    // ======================================================================
    //  VacuumOptions: CollectionName scoping
    // ======================================================================

    /// <summary>
    /// VacuumAsync with CollectionName set must only compact the named collection;
    /// documents in other collections must remain intact.
    /// </summary>
    [Fact]
    public async Task Vacuum_CollectionName_OnlyVacuumsTargetCollection()
    {
        using var engine = new BLiteEngine(_dbPath);
        var col1 = engine.GetOrCreateCollection("c1");
        var col2 = engine.GetOrCreateCollection("c2");

        for (int i = 0; i < 10; i++)
        {
            await col1.InsertAsync(MakeDoc(col1, i));
            await col2.InsertAsync(MakeDoc(col2, i));
        }

        // Delete 5 of 10 documents from c2 only
        var c2Docs = await col2.FindAllAsync().ToListAsync();
        foreach (var doc in c2Docs.Take(5))
        {
            if (doc.TryGetId(out var bid))
                await col2.DeleteAsync(bid);
        }

        // Vacuum only c1 — c2's data pages are not touched
        await engine.VacuumAsync(new VacuumOptions { CollectionName = "c1", TruncateFile = false });

        // c1: no deletions, count still 10
        Assert.Equal(10, await col1.CountAsync());

        // c2: 5 surviving documents
        Assert.Equal(5, await col2.CountAsync());
    }

    /// <summary>
    /// VacuumAsync with a CollectionName that doesn't exist must silently return.
    /// </summary>
    [Fact]
    public async Task Vacuum_NonExistentCollectionName_DoesNotThrow()
    {
        using var engine = new BLiteEngine(_dbPath);
        var ex = await Record.ExceptionAsync(() =>
            engine.VacuumAsync(new VacuumOptions { CollectionName = "nonexistent" }));
        Assert.Null(ex);
    }

    // ======================================================================
    //  In-memory backend
    // ======================================================================

    /// <summary>
    /// VacuumAsync on an in-memory engine must not throw (MemoryPageStorage is a
    /// no-op for TruncateToMinimumAsync).
    /// </summary>
    [Fact]
    public async Task Vacuum_InMemory_Engine_DoesNotThrow()
    {
        using var engine = BLiteEngine.CreateInMemory();
        var col = engine.GetOrCreateCollection("mem");

        for (int i = 0; i < 10; i++)
            await col.InsertAsync(MakeDoc(col, i));

        var allDocs = await col.FindAllAsync().ToListAsync();
        foreach (var doc in allDocs.Take(5))
        {
            if (doc.TryGetId(out var bid))
                await col.DeleteAsync(bid);
        }

        var ex = await Record.ExceptionAsync(() =>
            engine.VacuumAsync(new VacuumOptions { TruncateFile = false }));
        Assert.Null(ex);

        Assert.Equal(5, await col.CountAsync());
    }

    /// <summary>
    /// In-memory VacuumAsync must preserve primary index integrity.
    /// </summary>
    [Fact]
    public async Task Vacuum_InMemory_PrimaryIndex_Intact()
    {
        using var engine = BLiteEngine.CreateInMemory();
        var col = engine.GetOrCreateCollection("mem_idx");

        var ids = new List<BsonId>();
        for (int i = 0; i < 20; i++)
            ids.Add(await col.InsertAsync(MakeDoc(col, i)));

        // Delete odd-indexed entries
        var deletedIndices = new HashSet<int>();
        for (int i = 1; i < ids.Count; i += 2)
        {
            await col.DeleteAsync(ids[i]);
            deletedIndices.Add(i);
        }

        await engine.VacuumAsync(new VacuumOptions { TruncateFile = false });

        for (int i = 0; i < ids.Count; i++)
        {
            var found = await col.FindByIdAsync(ids[i]);
            if (deletedIndices.Contains(i))
                Assert.Null(found);
            else
                Assert.NotNull(found);
        }
    }

    // ======================================================================
    //  Idempotency
    // ======================================================================

    /// <summary>
    /// Running VacuumAsync twice must produce the same observable state as once.
    /// </summary>
    [Fact]
    public async Task Vacuum_Idempotent_DocumentsStillCorrectAfterTwoPasses()
    {
        using var engine = new BLiteEngine(_dbPath);
        var col = engine.GetOrCreateCollection("idem");

        var ids = new List<BsonId>();
        for (int i = 0; i < 20; i++)
            ids.Add(await col.InsertAsync(MakeDoc(col, i)));

        for (int i = 0; i < 10; i++)
            await col.DeleteAsync(ids[i]);

        await engine.VacuumAsync(new VacuumOptions { TruncateFile = false });
        await engine.VacuumAsync(new VacuumOptions { TruncateFile = false });

        for (int i = 10; i < ids.Count; i++)
            Assert.NotNull(await col.FindByIdAsync(ids[i]));

        Assert.Equal(10, await col.CountAsync());
    }

    // ======================================================================
    //  RebuildIndexes on DocumentCollection (#167)
    // ======================================================================

    /// <summary>
    /// A rebuild must replace the B-Tree contents, not re-insert every document into the
    /// existing tree: each document is found exactly once by equality and range queries.
    /// </summary>
    [Fact]
    public async Task Vacuum_RebuildIndexes_DocumentCollection_BTree_NoDuplicates()
    {
        using var db = new TestDbContext(_dbPath);
        for (int i = 1; i <= 3; i++)
            await db.PeopleV2.InsertAsync(new PersonV2 { Name = $"p{i}", Age = 30 + i });

        await db.PeopleV2.VacuumAsync(new VacuumOptions { RebuildIndexes = true });

        Assert.Single(db.PeopleV2.AsQueryable().Where(p => p.Age == 32).ToList());
        Assert.Equal(3, db.PeopleV2.AsQueryable().Where(p => p.Age >= 31).ToList().Count);
    }

    /// <summary>
    /// Back-to-back rebuilds stay idempotent even though each one frees the previous tree while
    /// the WAL still holds its pages (#168). Once the WAL is checkpointed, those pages are reused,
    /// so further rebuilds do not grow the file.
    /// </summary>
    [Fact]
    public async Task Vacuum_RebuildIndexes_DocumentCollection_Repeated_NoDuplicatesNoGrowth()
    {
        using var db = new TestDbContext(_dbPath);
        for (int i = 0; i < 200; i++)
            await db.PeopleV2.InsertAsync(new PersonV2 { Name = $"p{i}", Age = i % 50 });

        var options = new VacuumOptions { RebuildIndexes = true, TruncateFile = false };
        for (int pass = 0; pass < 5; pass++)
            await db.PeopleV2.VacuumAsync(options);

        Assert.Equal(4, db.PeopleV2.AsQueryable().Where(p => p.Age == 7).ToList().Count);
        Assert.Equal(200, db.PeopleV2.AsQueryable().Where(p => p.Age >= 0).ToList().Count);

        await db.CheckpointAsync();
        long sizeBefore = new FileInfo(_dbPath).Length;
        for (int pass = 0; pass < 5; pass++)
        {
            await db.PeopleV2.VacuumAsync(options);
            await db.CheckpointAsync();
        }

        Assert.Equal(200, db.PeopleV2.AsQueryable().Where(p => p.Age >= 0).ToList().Count);
        Assert.Equal(sizeBefore, new FileInfo(_dbPath).Length);
    }

    /// <summary>
    /// The rebuilt tree's root page is persisted, so the index is still correct after reopening.
    /// </summary>
    [Fact]
    public async Task Vacuum_RebuildIndexes_DocumentCollection_SurvivesReopen()
    {
        using (var db = new TestDbContext(_dbPath))
        {
            for (int i = 1; i <= 10; i++)
                await db.PeopleV2.InsertAsync(new PersonV2 { Name = $"p{i}", Age = i });
            await db.PeopleV2.VacuumAsync(new VacuumOptions { RebuildIndexes = true });
        }

        using (var db = new TestDbContext(_dbPath))
        {
            Assert.Single(db.PeopleV2.AsQueryable().Where(p => p.Age == 5).ToList());
            Assert.Equal(10, db.PeopleV2.AsQueryable().Where(p => p.Age >= 1).ToList().Count);

            await db.PeopleV2.InsertAsync(new PersonV2 { Name = "late", Age = 5 });
            Assert.Equal(2, db.PeopleV2.AsQueryable().Where(p => p.Age == 5).ToList().Count);
        }
    }

    /// <summary>
    /// Spatial indexes are not rebuilt (their pages cannot be enumerated and freed yet); the
    /// existing R-Tree is kept and keeps answering queries.
    /// </summary>
    [Fact]
    public async Task Vacuum_RebuildIndexes_DocumentCollection_Spatial_IsKept()
    {
        using var db = new TestDbContext(_dbPath);
        await db.GeoItems.InsertAsync(new GeoEntity { Name = "milan", Location = (45.46, 9.19) });
        uint SpatialRoot() => db.Storage.GetAllCollectionsMetadata()
            .SelectMany(m => m.Indexes).Single(i => i.Name == "idx_spatial").RootPageId;
        var rootBefore = SpatialRoot();

        await db.GeoItems.VacuumAsync(new VacuumOptions { RebuildIndexes = true });

        Assert.Equal(rootBefore, SpatialRoot());
        (double, double) min = (45.0, 9.0), max = (46.0, 10.0);
        Assert.Equal("milan", Assert.Single(db.GeoItems.AsQueryable().Where(p => p.Location.Within(min, max)).ToList()).Name);
    }

    /// <summary>
    /// A rebuild that fails part-way keeps the old index and returns every page of the half-built
    /// copy, including the pages its splits added, to the free list.
    /// </summary>
    [Fact]
    public async Task Vacuum_RebuildIndexes_DocumentCollection_FailedRebuild_KeepsIndexAndFreesCopy()
    {
        using var db = new TestDbContext(_dbPath);
        var people = await InsertPeopleAsync(db);
        var queryAge = await CorruptLastDocumentAsync(db);

        await Assert.ThrowsAnyAsync<Exception>(() => db.PeopleV2.VacuumAsync(new VacuumOptions { RebuildIndexes = true, TruncateFile = false }));

        // The old index still answers queries.
        Assert.Equal(people.Count(p => p.Age == queryAge), (await db.PeopleV2.QueryIndexAsync("idx_Age", queryAge, queryAge).ToListAsync()).Count);
        await AssertCopyWasFreedAsync(db);
    }

    /// <summary>
    /// If the abort record of a failed rebuild cannot be written either, the rebuild's own failure
    /// is what surfaces, and the half-built copy is still freed.
    /// </summary>
    [Fact]
    public async Task Vacuum_RebuildIndexes_DocumentCollection_FailedRollback_KeepsOriginalErrorAndFreesCopy()
    {
        using var db = new TestDbContext(_dbPath);
        var people = await InsertPeopleAsync(db);
        var queryAge = await CorruptLastDocumentAsync(db);

        var walField = typeof(StorageEngine).GetField("_wal", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var wal = (IWriteAheadLog)walField.GetValue(db.Storage)!;
        walField.SetValue(db.Storage, new AbortRecordFailingWal(wal));
        Exception ex;
        try
        {
            ex = await Assert.ThrowsAnyAsync<Exception>(() => db.PeopleV2.VacuumAsync(new VacuumOptions { RebuildIndexes = true, TruncateFile = false }));
        }
        finally
        {
            walField.SetValue(db.Storage, wal);
        }

        Assert.IsNotType<AbortRecordFailedException>(ex);
        Assert.Equal(people.Count(p => p.Age == queryAge), (await db.PeopleV2.QueryIndexAsync("idx_Age", queryAge, queryAge).ToListAsync()).Count);
        await AssertCopyWasFreedAsync(db);
    }

    /// <summary>
    /// If the rebuilt copy committed but cannot be swapped in (here: its metadata cannot be saved),
    /// the old index stays in place, in memory and on disk, and the copy's pages are freed.
    /// </summary>
    [Fact]
    public async Task Vacuum_RebuildIndexes_DocumentCollection_FailedSwap_KeepsIndexAndFreesCopy()
    {
        int expected;
        using (var db = new TestDbContext(_dbPath))
        {
            var people = await InsertPeopleAsync(db);
            expected = people.Count(p => p.Age == 7);
            uint StoredRoot() => db.Storage.GetCollectionMetadata("peoplev2_collection")!
                .Indexes.Single(i => i.Name == "idx_Age").RootPageId;
            var rootBefore = StoredRoot();

            // Hold the metadata lock so saving the new root times out.
            var metadataLock = (SemaphoreSlim)typeof(StorageEngine)
                .GetField("_metadataLock", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(db.Storage)!;
            metadataLock.Wait();
            try
            {
                await Assert.ThrowsAsync<TimeoutException>(() => db.PeopleV2.VacuumAsync(new VacuumOptions { RebuildIndexes = true, TruncateFile = false }));
            }
            finally
            {
                metadataLock.Release();
            }

            Assert.Equal(rootBefore, StoredRoot());
            Assert.Equal(rootBefore, db.PeopleV2.IndexManager.GetIndex("idx_Age")!.RootPageId);
            Assert.Equal(expected, (await db.PeopleV2.QueryIndexAsync("idx_Age", 7, 7).ToListAsync()).Count);
            await AssertCopyWasFreedAsync(db);
        }

        using (var db = new TestDbContext(_dbPath))
            Assert.Equal(expected, (await db.PeopleV2.QueryIndexAsync("idx_Age", 7, 7).ToListAsync()).Count);
    }

    private static async Task<List<PersonV2>> InsertPeopleAsync(TestDbContext db)
    {
        var people = Enumerable.Range(0, 3000).Select(i => new PersonV2 { Name = $"p{i}", Age = i % 90 }).ToList();
        await db.PeopleV2.InsertBulkAsync(people);
        await db.CheckpointAsync();
        return people;
    }

    /// <summary>
    /// Corrupts the last document in primary-key order, so a rebuild fails after most inserts.
    /// Returns an age that the corrupted document does not have, to query afterwards.
    /// </summary>
    private static async Task<int> CorruptLastDocumentAsync(TestDbContext db)
    {
        var storage = db.Storage;
        var primary = (BTreeIndex)typeof(DocumentCollection<ObjectId, PersonV2>)
            .GetField("_primaryIndex", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(db.PeopleV2)!;
        var lastEntry = primary.Range(IndexKey.MinKey, IndexKey.MaxKey, IndexDirection.Backward).First();
        var location = lastEntry.Location;
        var corruptedAge = (await db.PeopleV2.FindByIdAsync(lastEntry.Key.As<ObjectId>()))!.Age;
        var page = new byte[storage.PageSize];
        storage.ReadPage(location.PageId, null, page);
        var slot = SlotEntry.ReadFrom(page.AsSpan(SlottedPageHeader.Size + location.SlotIndex * SlotEntry.Size));
        page.AsSpan(slot.Offset + 4, 12).Fill(0xFF); // invalid element types
        storage.WritePageImmediate(location.PageId, page);
        return (corruptedAge + 1) % 90;
    }

    /// <summary>
    /// The copy of a 3000-entry index spans dozens of pages, not just its root. Each is either
    /// back on the free list (handed out next, below the high-water mark) or still queued for the
    /// next WAL truncation (a recycled page with a WAL image; a background checkpoint can make the
    /// explicit one below return without truncating).
    /// </summary>
    private static async Task AssertCopyWasFreedAsync(TestDbContext db)
    {
        var storage = db.Storage;
        await db.CheckpointAsync();
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        int queued = ((HashSet<uint>)typeof(StorageEngine).GetField("_pendingFreePages", flags)!.GetValue(storage)!).Count;
        var highWater = ((IPageStorage)typeof(StorageEngine).GetField("_pageFile", flags)!.GetValue(storage)!).NextPageId;
        int reused = 0;
        while (storage.AllocatePage() < highWater) reused++;
        Assert.True(reused + queued > 20, $"only {reused} pages were returned to the free list and {queued} queued");
    }

    private sealed class AbortRecordFailedException() : IOException("abort record could not be written");

    /// <summary>Delegates to the real WAL but fails to write abort records.</summary>
    private sealed class AbortRecordFailingWal(IWriteAheadLog inner) : IWriteAheadLog
    {
        public ValueTask WriteBeginRecordAsync(ulong transactionId, CancellationToken ct = default) => inner.WriteBeginRecordAsync(transactionId, ct);
        public ValueTask WriteCommitRecordAsync(ulong transactionId, CancellationToken ct = default) => inner.WriteCommitRecordAsync(transactionId, ct);
        public ValueTask WriteAbortRecordAsync(ulong transactionId, CancellationToken ct = default) => throw new AbortRecordFailedException();
        public ValueTask WriteDataRecordAsync(ulong transactionId, uint pageId, ReadOnlyMemory<byte> afterImage, CancellationToken ct = default) => inner.WriteDataRecordAsync(transactionId, pageId, afterImage, ct);
        public Task FlushAsync(CancellationToken ct = default) => inner.FlushAsync(ct);
        public long GetCurrentSize() => inner.GetCurrentSize();
        public Task TruncateAsync(CancellationToken ct = default) => inner.TruncateAsync(ct);
        public List<WalRecord> ReadAll() => inner.ReadAll();
        public void Dispose() { }
    }

    /// <summary>
    /// A rebuilt vector index holds only current embeddings, so nearest-neighbour search sees
    /// the updated vector.
    /// </summary>
    [Fact]
    public async Task Vacuum_RebuildIndexes_DocumentCollection_Vector_UsesCurrentEmbeddings()
    {
        using var db = new TestDbContext(_dbPath);
        var a = new VectorEntity { Title = "A", Embedding = [0f, 0f, 0f] };
        await db.VectorItems.InsertAsync(a);
        await db.VectorItems.InsertAsync(new VectorEntity { Title = "B", Embedding = [5f, 5f, 5f] });
        a.Embedding = [100f, 100f, 100f];
        await db.VectorItems.UpdateAsync(a);

        await db.VectorItems.VacuumAsync(new VacuumOptions { RebuildIndexes = true });

        var top = await db.VectorItems.VectorSearchAsync("idx_vector", [0f, 0f, 0f], 1).ToListAsync();
        Assert.Equal("B", Assert.Single(top).Title);
    }

    // ======================================================================
    //  Helpers
    // ======================================================================

    /// <summary>
    /// Reads every data page of <paramref name="collectionName"/> and asserts that
    /// the free-space byte range [FreeSpaceStart, FreeSpaceEnd) is entirely zero.
    /// </summary>
    private static void AssertAllDataPageFreeSpaceIsZero(StorageEngine storage, string collectionName)
    {
        var pageSize = storage.PageSize;
        var buf = new byte[pageSize];

        foreach (var pageId in storage.GetCollectionPageIds(collectionName))
        {
            storage.ReadPage(pageId, null, buf);
            var hdr = SlottedPageHeader.ReadFrom(buf);
            if (hdr.PageType != PageType.Data) continue;

            int freeStart = hdr.FreeSpaceStart;
            int freeEnd   = hdr.FreeSpaceEnd;
            if (freeEnd <= freeStart) continue;

            for (int b = freeStart; b < freeEnd; b++)
                Assert.True(
                    buf[b] == 0,
                    $"Non-zero byte at offset {b} in page {pageId} " +
                    $"(free-space range [{freeStart},{freeEnd}))");
        }
    }

    private static BsonDocument MakeDoc(DynamicCollection col, int value)
        => col.CreateDocument(
            ["value", "payload"],
            b => b.AddInt32("value", value)
                  .AddString("payload", $"data-{value:D6}"));

    private static BsonDocument MakeDocWithScore(DynamicCollection col, int score)
        => col.CreateDocument(
            ["score"],
            b => b.AddInt32("score", score));
}
