using BLite.Core;
using BLite.Core.Retention;
using BLite.Core.Storage;
using BLite.Shared;

namespace BLite.Tests;

/// <summary>
/// The collection metadata chain (page 1 and its overflow pages) must not grow with every save,
/// and a chain grown by older releases shrinks again (#195).
/// </summary>
public class MetadataChainTests : IDisposable
{
    private readonly string _dbPath;

    public MetadataChainTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"blite_metadata_chain_{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        foreach (var path in new[] { _dbPath, Path.ChangeExtension(_dbPath, ".wal") })
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    private sealed record ChainInfo(List<uint> Pages, int Live, int Deleted, List<int> LiveLengths)
    {
        /// <summary>Pages the live records need when packed in order.</summary>
        public int MinimalPages(int pageSize)
        {
            int pages = 1, room = pageSize - SlottedPageHeader.Size;
            foreach (var length in LiveLengths)
            {
                if (length + SlotEntry.Size > room) { pages++; room = pageSize - SlottedPageHeader.Size; }
                room -= length + SlotEntry.Size;
            }
            return pages;
        }
    }

    private static ChainInfo Inspect(StorageEngine storage)
    {
        var pages = new List<uint>();
        var lengths = new List<int>();
        int live = 0, deleted = 0;
        var buffer = new byte[storage.PageSize];
        uint pageId = 1;
        while (pageId != 0)
        {
            storage.ReadPage(pageId, null, buffer);
            var header = SlottedPageHeader.ReadFrom(buffer);
            if (header.PageType != PageType.Collection) break;
            pages.Add(pageId);
            for (int i = 0; i < header.SlotCount; i++)
            {
                var slot = SlotEntry.ReadFrom(buffer.AsSpan(SlottedPageHeader.Size + i * SlotEntry.Size));
                if ((slot.Flags & SlotFlags.Deleted) != 0) deleted++;
                else { live++; lengths.Add(slot.Length); }
            }
            pageId = header.NextOverflowPage;
        }
        return new ChainInfo(pages, live, deleted, lengths);
    }

    private async Task SeedAsync(params string[] collections)
    {
        using var engine = new BLiteEngine(_dbPath);
        foreach (var name in collections)
            await engine.InsertAsync(name, engine.CreateDocument(["name"], b => b.AddString("name", name + "-1")));
    }

    private static string[] Names(int count) => Enumerable.Range(0, count).Select(i => $"c{i}").ToArray();

    // ── No growth ────────────────────────────────────────────────────────────

    [Fact]
    public async Task SequenceSaves_RewriteTheRecordInPlace()
    {
        await SeedAsync(Names(8));

        using (var storage = new StorageEngine(_dbPath, PageFileConfig.Default))
        {
            var before = Inspect(storage);
            for (int i = 1; i <= 5000; i++)
                Assert.Equal(i, storage.GetNextSequence("c3"));

            var after = Inspect(storage);
            Assert.Equal(before.Pages, after.Pages);
            Assert.Equal(8, after.Live);
            Assert.Equal(0, after.Deleted);
        }

        using (var storage = new StorageEngine(_dbPath, PageFileConfig.Default))
            Assert.Equal(5001, storage.GetNextSequence("c3"));
    }

    [Fact]
    public async Task TypedInsertsWithGeneratedIntIds_KeepTheChainAtOnePage()
    {
        using (var db = new TestDbContext(_dbPath))
        {
            for (int i = 0; i < 2000; i++)
                await db.Products.InsertAsync(new Product { Title = $"p{i}", Price = i });
        }

        using (var db = new TestDbContext(_dbPath))
        {
            var ids = db.Products.AsQueryable().Select(p => p.Id).ToList().OrderBy(id => id).ToList();
            Assert.Equal(Enumerable.Range(1, 2000), ids);
        }

        using var storage = new StorageEngine(_dbPath, PageFileConfig.Default);
        var chain = Inspect(storage);
        Assert.Single(chain.Pages);
        Assert.True(chain.Deleted <= 1); // at most the record the last growing save replaced
    }

    [Fact]
    public async Task RecordsThatShrinkAndGrow_ReuseTheSpaceTheyLeave()
    {
        await SeedAsync(Names(8));

        using var storage = new StorageEngine(_dbPath, PageFileConfig.Default);
        var pagesBefore = Inspect(storage).Pages;
        for (int i = 0; i < 500; i++)
        {
            var meta = storage.GetCollectionMetadata("c5")!;
            meta.GeneralRetentionPolicy = new RetentionPolicy
            {
                MaxAgeMs = i,
                TimestampField = new string('t', i % 2 == 0 ? 400 : 10),   // alternately grows and shrinks
            };
            storage.SaveCollectionMetadata(meta);
        }

        var chain = Inspect(storage);
        Assert.Equal(pagesBefore, chain.Pages);
        Assert.Equal(8, chain.Live);
        var last = storage.GetCollectionMetadata("c5")!;
        Assert.Equal(499, last.GeneralRetentionPolicy!.MaxAgeMs);
        Assert.Equal(new string('t', 10), last.GeneralRetentionPolicy.TimestampField);
    }

    // ── Chains that span pages ───────────────────────────────────────────────

    [Fact]
    public async Task ChainOfSeveralPages_GrowsOnlyWithItsLiveRecords_AndShrinksWhenTheyGo()
    {
        // Records of ~1.2 KB: 40 collections need several 16 KB pages.
        var names = Names(40);
        await SeedAsync(names);
        using (var storage = new StorageEngine(_dbPath, PageFileConfig.Default))
        {
            foreach (var name in names)
            {
                var meta = storage.GetCollectionMetadata(name)!;
                meta.GeneralRetentionPolicy = new RetentionPolicy { TimestampField = new string('x', 1100) };
                storage.SaveCollectionMetadata(meta);
            }
            var grown = Inspect(storage);
            Assert.True(grown.Pages.Count >= 3);
            Assert.Equal(grown.MinimalPages(storage.PageSize), grown.Pages.Count);

            // Rewrite every record many times: the chain keeps its length.
            for (int round = 0; round < 20; round++)
                foreach (var name in names)
                    storage.GetNextSequence(name);
            var full = Inspect(storage);
            Assert.Equal(grown.Pages, full.Pages);
            Assert.Equal(40, full.Live);

            // Drop most collections: the next save packs the rest into page 1 and frees the others.
            foreach (var name in names.Skip(10))
                storage.DeleteCollectionMetadata(name);
            storage.GetNextSequence("c0");

            var shrunk = Inspect(storage);
            Assert.Equal(new uint[] { 1 }, shrunk.Pages);
            Assert.Equal(10, shrunk.Live);
            Assert.Equal(0, shrunk.Deleted);

            // The freed pages are reused before the file grows.
            var freed = full.Pages.Skip(1).ToHashSet();
            Assert.Contains(storage.AllocatePage(), freed);
        }

        using (var storage = new StorageEngine(_dbPath, PageFileConfig.Default))
        {
            var all = storage.GetAllCollectionsMetadata();
            Assert.Equal(names.Take(10), all.Select(m => m.Name).OrderBy(n => int.Parse(n[1..])));
            Assert.All(all, m => Assert.Equal(new string('x', 1100), m.GeneralRetentionPolicy!.TimestampField));
            Assert.Equal(20, all.Single(m => m.Name == "c1").SequenceValue);
        }

        using (var engine = new BLiteEngine(_dbPath))
            Assert.Single(await engine.GetOrCreateCollection("c3").FindAllAsync().ToListAsync());
    }

    // ── Chains grown by older releases ───────────────────────────────────────

    /// <summary>
    /// Saves the record of <paramref name="collection"/> <paramref name="saves"/> times the way
    /// releases before #195 did: the old slot is marked deleted and a copy is appended to the first
    /// page with room, or to a new page linked at the end.
    /// </summary>
    private static void GrowLikeOlderReleases(StorageEngine storage, string collection, int saves)
    {
        for (int n = 0; n < saves; n++)
        {
            var chain = new List<(uint Id, byte[] Buffer)>();
            byte[]? record = null;
            uint pageId = 1;
            while (pageId != 0)
            {
                var buffer = new byte[storage.PageSize];
                storage.ReadPage(pageId, null, buffer);
                var header = SlottedPageHeader.ReadFrom(buffer);
                chain.Add((pageId, buffer));
                for (int i = 0; i < header.SlotCount && record == null; i++)
                {
                    var offset = SlottedPageHeader.Size + i * SlotEntry.Size;
                    var slot = SlotEntry.ReadFrom(buffer.AsSpan(offset));
                    if ((slot.Flags & SlotFlags.Deleted) != 0) continue;
                    using var reader = new BinaryReader(new MemoryStream(buffer, slot.Offset, slot.Length, false));
                    if (reader.ReadString() != collection) continue;
                    record = buffer.AsSpan(slot.Offset, slot.Length).ToArray();
                    slot.Flags |= SlotFlags.Deleted;
                    slot.WriteTo(buffer.AsSpan(offset));
                    storage.WritePageImmediate(pageId, buffer);
                }
                pageId = header.NextOverflowPage;
            }

            var target = chain.FirstOrDefault(p =>
                SlottedPageHeader.ReadFrom(p.Buffer).AvailableFreeSpace >= record!.Length + SlotEntry.Size);
            if (target.Buffer == null)
            {
                var id = storage.AllocatePage();
                var page = new byte[storage.PageSize];
                new SlottedPageHeader
                {
                    PageId = id, PageType = PageType.Collection, FreeSpaceStart = SlottedPageHeader.Size,
                    FreeSpaceEnd = (ushort)storage.PageSize
                }.WriteTo(page);
                var last = chain[^1];
                var lastHeader = SlottedPageHeader.ReadFrom(last.Buffer);
                lastHeader.NextOverflowPage = id;
                lastHeader.WriteTo(last.Buffer);
                storage.WritePageImmediate(last.Id, last.Buffer);
                target = (id, page);
            }

            var h = SlottedPageHeader.ReadFrom(target.Buffer);
            var dataOffset = h.FreeSpaceEnd - record!.Length;
            record.CopyTo(target.Buffer.AsSpan(dataOffset));
            new SlotEntry { Offset = (ushort)dataOffset, Length = (ushort)record.Length }
                .WriteTo(target.Buffer.AsSpan(SlottedPageHeader.Size + h.SlotCount * SlotEntry.Size));
            h.SlotCount++;
            h.FreeSpaceEnd = (ushort)dataOffset;
            h.FreeSpaceStart = (ushort)(SlottedPageHeader.Size + h.SlotCount * SlotEntry.Size);
            h.WriteTo(target.Buffer);
            storage.WritePageImmediate(target.Id, target.Buffer);
        }
    }

    [Fact]
    public async Task ChainGrownByOlderReleases_ShrinksBackOnTheNextSave()
    {
        await SeedAsync(Names(8));
        uint[] grownPages;
        using (var storage = new StorageEngine(_dbPath, PageFileConfig.Default))
        {
            GrowLikeOlderReleases(storage, "c3", 3000);
            var grown = Inspect(storage);
            Assert.True(grown.Pages.Count >= 7, $"expected a grown chain, got {grown.Pages.Count} pages");
            Assert.Equal(3000, grown.Deleted);
            grownPages = grown.Pages.ToArray();
        }

        using (var engine = new BLiteEngine(_dbPath))
        {
            // The collection reads as before; the next metadata save (an index here) repairs the chain.
            Assert.Single(await engine.GetOrCreateCollection("c3").FindAllAsync().ToListAsync());
            await engine.GetOrCreateCollection("c4").CreateIndexAsync("name");
        }

        using (var storage = new StorageEngine(_dbPath, PageFileConfig.Default))
        {
            var healed = Inspect(storage);
            Assert.Equal(new uint[] { 1 }, healed.Pages);
            Assert.Equal(8, healed.Live);
            Assert.True(healed.Deleted <= 1); // the record c4 replaced, until the next compaction
            Assert.Equal(Names(8), storage.GetAllCollectionsMetadata().Select(m => m.Name).OrderBy(n => n));
            Assert.Single(storage.GetCollectionMetadata("c4")!.Indexes);
            Assert.Contains(storage.AllocatePage(), grownPages.Skip(1));
        }
    }

    // ── Duplicates a crash during compaction can leave ───────────────────────

    [Fact]
    public async Task DuplicateRecord_IsListedOnce_DeletedWithTheOriginal_AndDroppedByTheNextCompaction()
    {
        await SeedAsync(Names(3));
        using var storage = new StorageEngine(_dbPath, PageFileConfig.Default);

        // A second, identical copy of c1's record on a page further on, as a crash part-way
        // through a compaction leaves it.
        var page1 = new byte[storage.PageSize];
        storage.ReadPage(1, null, page1);
        byte[]? record = null;
        var header1 = SlottedPageHeader.ReadFrom(page1);
        for (int i = 0; i < header1.SlotCount; i++)
        {
            var slot = SlotEntry.ReadFrom(page1.AsSpan(SlottedPageHeader.Size + i * SlotEntry.Size));
            using var reader = new BinaryReader(new MemoryStream(page1, slot.Offset, slot.Length, false));
            if (reader.ReadString() == "c1") record = page1.AsSpan(slot.Offset, slot.Length).ToArray();
        }
        var extraId = storage.AllocatePage();
        var extra = new byte[storage.PageSize];
        var extraHeader = new SlottedPageHeader
        {
            PageId = extraId, PageType = PageType.Collection, SlotCount = 1,
            FreeSpaceStart = SlottedPageHeader.Size + SlotEntry.Size,
            FreeSpaceEnd = (ushort)(storage.PageSize - record!.Length)
        };
        extraHeader.WriteTo(extra);
        record.CopyTo(extra.AsSpan(extraHeader.FreeSpaceEnd));
        new SlotEntry { Offset = extraHeader.FreeSpaceEnd, Length = (ushort)record.Length }
            .WriteTo(extra.AsSpan(SlottedPageHeader.Size));
        storage.WritePageImmediate(extraId, extra);
        header1.NextOverflowPage = extraId;
        header1.WriteTo(page1);
        storage.WritePageImmediate(1, page1);

        Assert.Equal(3, storage.GetAllCollectionsMetadata().Count);

        // The next save drops the copy and the page that held it.
        storage.GetNextSequence("c2");
        var compacted = Inspect(storage);
        Assert.Equal(new uint[] { 1 }, compacted.Pages);
        Assert.Equal(3, compacted.Live);

        // With a copy back, a delete removes both.
        var againId = storage.AllocatePage();
        extraHeader.PageId = againId;
        extraHeader.WriteTo(extra);
        storage.WritePageImmediate(againId, extra);
        storage.ReadPage(1, null, page1);
        header1 = SlottedPageHeader.ReadFrom(page1);
        header1.NextOverflowPage = againId;
        header1.WriteTo(page1);
        storage.WritePageImmediate(1, page1);
        Assert.Equal(3, storage.GetAllCollectionsMetadata().Count);

        storage.DeleteCollectionMetadata("c1");
        Assert.Null(storage.GetCollectionMetadata("c1"));
        Assert.Equal(new[] { "c0", "c2" }, storage.GetAllCollectionsMetadata().Select(m => m.Name).OrderBy(n => n));
    }
}
