using System.IO.MemoryMappedFiles;
using BLite.Bson;
using BLite.Core;
using BLite.Core.GDPR;
using BLite.Core.Indexing;
using BLite.Core.Storage;
using BLite.Shared;

namespace BLite.Tests;

/// <summary>
/// Per-collection required features (#187): a collection that records a feature this version does
/// not know is refused instead of being opened, modified or dropped. The newer version that
/// recorded it is simulated by adding the feature to the stored metadata.
/// </summary>
public class RequiredFeaturesTests : IDisposable
{
    private readonly string _dbPath;

    private static readonly RequiredFeature FutureIndex =
        new("future-index", FeatureRequirement.Open, "future indexes (BLite 99.0 or later)");

    private static readonly RequiredFeature FutureWriteOnly =
        new("future-stats", FeatureRequirement.Write, "future statistics (BLite 99.0 or later)");

    public RequiredFeaturesTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"blite_required_features_{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        foreach (var path in new[] { _dbPath, Path.ChangeExtension(_dbPath, ".wal"), _dbPath + "-shm", Path.ChangeExtension(_dbPath, ".wal-shm") })
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    /// <summary>Records <paramref name="features"/> on the collection, as a newer version would.</summary>
    private void RequireFeatures(string collection, params RequiredFeature[] features)
    {
        using var storage = new StorageEngine(_dbPath, PageFileConfig.Default);
        var meta = storage.GetCollectionMetadata(collection) ?? new CollectionMetadata { Name = collection };
        meta.RequiredFeatures.AddRange(features);
        storage.SaveCollectionMetadata(meta);
    }

    private CollectionMetadata ReadMetadata(string collection)
    {
        using var storage = new StorageEngine(_dbPath, PageFileConfig.Default);
        return storage.GetCollectionMetadata(collection)!;
    }

    private async Task SeedUsersAsync()
    {
        using var db = new TestDbContext(_dbPath);
        await db.Users.InsertAsync(new User { Name = "Ada", Age = 36 });
        await db.Users.InsertAsync(new User { Name = "Grace", Age = 45 });
    }

    private async Task SeedDynamicAsync(params string[] collections)
    {
        using var engine = new BLiteEngine(_dbPath);
        foreach (var name in collections)
            await engine.InsertAsync(name, engine.CreateDocument(["name"], b => b.AddString("name", name + "-1")));
    }

    // ── Persistence ──────────────────────────────────────────────────────────

    [Fact]
    public async Task RequiredFeatures_RoundTrip_IncludingRequirementsThisVersionDoesNotKnow()
    {
        await SeedDynamicAsync("items");
        var later = new RequiredFeature("later-kind", (FeatureRequirement)7, "");
        RequireFeatures("items", FutureIndex, FutureWriteOnly, later);

        var meta = ReadMetadata("items");

        Assert.Equal(new[] { FutureIndex, FutureWriteOnly, later }, meta.RequiredFeatures);
    }

    [Fact]
    public async Task RecordWithoutTheSection_AsOlderReleasesWriteIt_LoadsWithNoFeatures_AndTheSectionIsAppended()
    {
        await SeedDynamicAsync("items");
        RequireFeatures("items", FutureIndex);
        var withFeature = ReadMetadata("items");

        // Strip the section from the stored record: what remains is exactly the record a release
        // without required features writes, and exactly the bytes such a release reads.
        int sectionLength;
        using (var ms = new MemoryStream())
        using (var writer = new BinaryWriter(ms))
        {
            writer.Write(1);
            writer.Write(FutureIndex.Id);
            writer.Write((byte)FutureIndex.Requirement);
            writer.Write(FutureIndex.Description);
            writer.Flush();
            sectionLength = (int)ms.Length;
        }
        ShortenRecord("items", sectionLength);

        var older = ReadMetadata("items");
        Assert.Empty(older.RequiredFeatures);
        Assert.Equal(withFeature.PrimaryRootPageId, older.PrimaryRootPageId);
        Assert.Equal(withFeature.Indexes.Count, older.Indexes.Count);
        Assert.Equal(withFeature.SequenceValue, older.SequenceValue);

        // The collection opens and stays usable, and the next save writes an empty section.
        using (var engine = new BLiteEngine(_dbPath))
        {
            var items = engine.GetOrCreateCollection("items");
            await items.InsertAsync(engine.CreateDocument(["name"], b => b.AddString("name", "items-2")));
            await items.CreateIndexAsync("name");
            Assert.Equal(2, await items.FindAllAsync().CountAsync());
        }
        Assert.Empty(ReadMetadata("items").RequiredFeatures);
    }

    /// <summary>Removes the last <paramref name="bytes"/> bytes of the collection's stored record.</summary>
    private void ShortenRecord(string collection, int bytes)
    {
        using var storage = new StorageEngine(_dbPath, PageFileConfig.Default);
        var page = new byte[storage.PageSize];
        uint pageId = 1;
        while (pageId != 0)
        {
            storage.ReadPage(pageId, null, page);
            var header = SlottedPageHeader.ReadFrom(page);
            for (ushort i = 0; i < header.SlotCount; i++)
            {
                var offset = SlottedPageHeader.Size + i * SlotEntry.Size;
                var slot = SlotEntry.ReadFrom(page.AsSpan(offset));
                if ((slot.Flags & SlotFlags.Deleted) != 0) continue;
                using var reader = new BinaryReader(new MemoryStream(page, slot.Offset, slot.Length, false));
                if (reader.ReadString() != collection) continue;

                slot.Length -= (ushort)bytes;
                slot.WriteTo(page.AsSpan(offset));
                storage.WritePageImmediate(pageId, page);
                return;
            }
            pageId = header.NextOverflowPage;
        }
        throw new InvalidOperationException($"No record for '{collection}'.");
    }

    // ── Typed collections ────────────────────────────────────────────────────

    [Fact]
    public async Task TypedContext_RefusesToOpen_ACollectionWithAnUnknownFeature()
    {
        await SeedUsersAsync();
        RequireFeatures("users", FutureIndex);

        var ex = Assert.Throws<NotSupportedException>(() => new TestDbContext(_dbPath));
        Assert.Contains("'users'", ex.Message);
        Assert.Contains("future indexes (BLite 99.0 or later)", ex.Message);

        // The failed open released the files and changed nothing.
        var meta = ReadMetadata("users");
        Assert.Equal(new[] { FutureIndex }, meta.RequiredFeatures);
    }

    [Fact]
    public async Task TypedContext_LeavesAnUnmappedCollectionWithAnUnknownFeature_InsteadOfDroppingIt()
    {
        await SeedUsersAsync();
        await SeedDynamicAsync("written_by_newer", "plain_orphan");
        RequireFeatures("written_by_newer", FutureIndex);

        using (var db = new TestDbContext(_dbPath))
            Assert.Equal(2, await db.Users.AsQueryable().CountAsync());

        using var storage = new StorageEngine(_dbPath, PageFileConfig.Default);
        Assert.NotNull(storage.GetCollectionMetadata("written_by_newer"));
        Assert.Null(storage.GetCollectionMetadata("plain_orphan")); // unchanged behaviour for the rest
    }

    // ── Dynamic collections and the engine ───────────────────────────────────

    [Fact]
    public async Task Engine_RefusesTheCollection_AndKeepsTheOthersUsable()
    {
        await SeedDynamicAsync("future", "items");
        RequireFeatures("future", FutureIndex);

        using var engine = new BLiteEngine(_dbPath);

        var ex = Assert.Throws<NotSupportedException>(() => engine.GetOrCreateCollection("future"));
        Assert.Contains("'future'", ex.Message);
        Assert.Contains("future indexes (BLite 99.0 or later)", ex.Message);
        await Assert.ThrowsAsync<NotSupportedException>(() => engine.TruncateCollectionAsync("future"));
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            engine.VacuumAsync(new VacuumOptions { CollectionName = "future" }));

        // Listing names it without opening it. Vacuuming everything refuses before touching any
        // collection, since it could not erase this one's deleted documents.
        var names = engine.ListCollections();
        Assert.Contains("future", names, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("items", names, StringComparer.OrdinalIgnoreCase);
        await Assert.ThrowsAsync<NotSupportedException>(() => engine.VacuumAsync());

        var items = engine.GetOrCreateCollection("items");
        await items.InsertAsync(engine.CreateDocument(["name"], b => b.AddString("name", "items-2")));
        Assert.Equal(2, await items.FindAllAsync().CountAsync());

        engine.Dispose();
        var meta = ReadMetadata("future");
        Assert.Equal(new[] { FutureIndex }, meta.RequiredFeatures);
        Assert.NotEqual(0u, meta.PrimaryRootPageId);
    }

    [Fact]
    public async Task SubjectExportAndMigration_Refuse_InsteadOfSkippingTheCollection()
    {
        await SeedDynamicAsync("future", "items");
        RequireFeatures("future", FutureIndex);

        using (var engine = new BLiteEngine(_dbPath))
        {
            var query = new SubjectQuery { FieldName = "name", FieldValue = BsonValue.FromString("future-1") };
            await Assert.ThrowsAsync<NotSupportedException>(() => engine.ExportSubjectDataAsync(query));
        }

        // Migrate a copy, so the multi-file layout lands in a directory of its own.
        var dir = Path.Combine(Path.GetTempPath(), $"blite_required_features_mig_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var source = Path.Combine(dir, "source.db");
            File.Copy(_dbPath, source);
            if (File.Exists(Path.ChangeExtension(_dbPath, ".wal")))
                File.Copy(Path.ChangeExtension(_dbPath, ".wal"), Path.ChangeExtension(source, ".wal"));

            var target = PageFileConfig.Server(source);
            Directory.CreateDirectory(Path.GetDirectoryName(target.WalPath!)!);
            Directory.CreateDirectory(target.CollectionDataDirectory!);
            await Assert.ThrowsAsync<NotSupportedException>(() => BLiteMigration.ToMultiFileAsync(source, target));

            using var storage = new StorageEngine(source, PageFileConfig.Default);
            Assert.Equal(new[] { FutureIndex }, storage.GetCollectionMetadata("future")!.RequiredFeatures);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Storage_RefusesToRewriteDeleteOrFree_ACollectionWithAnUnknownFeature()
    {
        await SeedDynamicAsync("future");
        RequireFeatures("future", FutureIndex);

        using var storage = new StorageEngine(_dbPath, PageFileConfig.Default);
        var meta = storage.GetCollectionMetadata("future")!;
        meta.SequenceValue = 42;

        Assert.Throws<NotSupportedException>(() => storage.SaveCollectionMetadata(meta));
        Assert.Throws<NotSupportedException>(() => storage.FreeCollectionPages("future"));
        Assert.Throws<NotSupportedException>(() => storage.DeleteCollectionMetadata("future"));

        var stored = storage.GetCollectionMetadata("future")!;
        Assert.Equal(0, stored.SequenceValue);
        Assert.Equal(new[] { FutureIndex }, stored.RequiredFeatures);
    }

    [Fact]
    public async Task IndexOfATypeThisVersionDoesNotKnow_IsRefused_EvenWithoutARecordedFeature()
    {
        // What a newer index type looks like after a release without required features rewrote
        // the record: the index is still listed, its feature is gone. 200 stands for any type
        // added after this version.
        await SeedUsersAsync();
        using (var storage = new StorageEngine(_dbPath, PageFileConfig.Default))
        {
            var meta = storage.GetCollectionMetadata("users")!;
            meta.Indexes.Add(new IndexMetadata { Name = "idx_future_Name", Type = (IndexType)200, PropertyPaths = ["Name"], RootPageId = 0 });
            storage.SaveCollectionMetadata(meta);
        }

        var ex = Assert.Throws<NotSupportedException>(() => new TestDbContext(_dbPath));
        Assert.Contains("index 'idx_future_Name' of type 200", ex.Message);

        using var engine = new BLiteEngine(_dbPath);
        Assert.Throws<NotSupportedException>(() => engine.GetOrCreateCollection("users"));
        Assert.Contains("users", engine.ListCollections(), StringComparer.OrdinalIgnoreCase);
    }

    // ── Requirement levels ───────────────────────────────────────────────────

    [Fact]
    public async Task WriteOnlyFeature_IsRefusedForWriting_ButOpensReadOnly()
    {
        await SeedDynamicAsync("stats");
        RequireFeatures("stats", FutureWriteOnly);

        using (var engine = new BLiteEngine(_dbPath))
        {
            var ex = Assert.Throws<NotSupportedException>(() => engine.GetOrCreateCollection("stats"));
            Assert.Contains("future statistics (BLite 99.0 or later)", ex.Message);
            Assert.Contains("read-only", ex.Message);
        }

        var readOnly = PageFileConfig.Default with { Access = MemoryMappedFileAccess.Read, AllowMultiProcessAccess = true };
        using (var engine = new BLiteEngine(_dbPath, readOnly))
        {
            var docs = await engine.GetOrCreateCollection("stats").FindAllAsync().ToListAsync();
            Assert.Single(docs);
        }
    }

    [Fact]
    public async Task UnknownRequirementLevel_IsTreatedAsNeededToOpen_EvenReadOnly()
    {
        await SeedDynamicAsync("later");
        RequireFeatures("later", new RequiredFeature("later-kind", (FeatureRequirement)7, ""));

        var readOnly = PageFileConfig.Default with { Access = MemoryMappedFileAccess.Read, AllowMultiProcessAccess = true };
        using var engine = new BLiteEngine(_dbPath, readOnly);

        var ex = Assert.Throws<NotSupportedException>(() => engine.GetOrCreateCollection("later"));
        Assert.Contains("'later-kind'", ex.Message); // no description: named by id
    }

    [Fact]
    public async Task Message_ListsEveryUnsupportedFeature()
    {
        await SeedDynamicAsync("future");
        RequireFeatures("future", FutureIndex, FutureWriteOnly);

        using var engine = new BLiteEngine(_dbPath);
        var ex = Assert.Throws<NotSupportedException>(() => engine.GetOrCreateCollection("future"));

        Assert.Contains("future indexes (BLite 99.0 or later)", ex.Message);
        Assert.Contains("future statistics (BLite 99.0 or later)", ex.Message);
        Assert.DoesNotContain("read-only", ex.Message); // one of them is needed even to read
    }
}
