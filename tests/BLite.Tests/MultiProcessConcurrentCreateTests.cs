using System.Collections.Concurrent;
using BLite.Bson;
using BLite.Core.Storage;
using BLite.Shared;

namespace BLite.Tests;

/// <summary>
/// Several processes opening the same, not yet existing database at the same moment with
/// <see cref="PageFileConfig.AllowMultiProcessAccess"/> must all succeed (#216). Each opener
/// creates the file, writes the header, initialises the dictionary and creates the collections
/// of <see cref="TestDbContext"/>; before the fix, one of them typically failed with an
/// <see cref="UnauthorizedAccessException"/> (a page write past its stale memory map), a
/// "Page size mismatch" (a header read before the creator wrote it) or an
/// <see cref="AccessViolationException"/> (a page read past its stale memory map).
/// <para>
/// Like <see cref="MultiProcessWalSharedMemoryTests"/>, the openers are separate engine
/// instances on the same files within this process: each owns its own file handles, memory
/// map and cross-process locks, so the same races apply as between two OS processes.
/// </para>
/// </summary>
public sealed class MultiProcessConcurrentCreateTests : IDisposable
{
    private const int Rounds = 10;

    private static readonly PageFileConfig MultiProcess = PageFileConfig.Default with { AllowMultiProcessAccess = true };

    private readonly string _tempDir;

    public MultiProcessConcurrentCreateTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"blite_mpcreate_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public Task TwoProcessesCreatingANewDatabase_BothSucceed() => CreateConcurrently(openers: 2);

    [Fact]
    public Task FourProcessesCreatingANewDatabase_AllSucceed() => CreateConcurrently(openers: 4);

    private async Task CreateConcurrently(int openers)
    {
        for (int round = 0; round < Rounds; round++)
        {
            var dbPath = Path.Combine(_tempDir, $"{openers}_{round}.db");
            var contexts = new TestDbContext?[openers];
            var failures = new ConcurrentQueue<Exception>();

            // Release all openers at once so their opens overlap as much as possible.
            using (var start = new Barrier(openers))
            {
                var threads = Enumerable.Range(0, openers).Select(i => new Thread(() =>
                {
                    start.SignalAndWait();
                    try { contexts[i] = new TestDbContext(dbPath, MultiProcess); }
                    catch (Exception ex) { failures.Enqueue(ex); }
                })).ToArray();
                foreach (var t in threads) t.Start();
                foreach (var t in threads)
                    Assert.True(t.Join(TimeSpan.FromMinutes(1)), $"round {round}: an opener hung");
            }
            foreach (var ctx in contexts) ctx?.Dispose();

            Assert.True(failures.IsEmpty,
                $"round {round}: {failures.Count} of {openers} openers failed:\n{string.Join("\n\n", failures)}");

            // The database they created together must be a normal, writable database.
            await AssertUsable(dbPath, $"round {round}");
        }
    }

    [Fact]
    public async Task HalfInitialisedFile_LeftByACrashDuringCreate_IsInitialisedOnOpen()
    {
        // A creator that died between sizing the new file and writing its header leaves a
        // zero-filled file of the initial size behind. Opening it must finish the creation
        // instead of failing with "Page size mismatch: file was created with 32 byte pages".
        var dbPath = Path.Combine(_tempDir, "half.db");
        long block = MultiProcess.GrowthBlockSize;
        using (var fs = new FileStream(dbPath, FileMode.CreateNew))
            fs.SetLength((2L * MultiProcess.PageSize + block - 1) / block * block);

        await AssertUsable(dbPath, "after a crash during create");
    }

    [Fact]
    public void PagesAnotherEngineAddedPastThisEnginesMap_CanBeWrittenAndRead()
    {
        var dbPath = Path.Combine(_tempDir, "grow.db");
        using var a = new StorageEngine(dbPath, MultiProcess);
        using var b = new StorageEngine(dbPath, MultiProcess);

        // A grows the file well past the end of B's memory map.
        uint last = 0;
        for (int i = 0; i < 100; i++) last = a.AllocatePage();
        a.WritePageImmediate(last - 1, Filled(a.PageSize, 0xA1));

        // B must map the grown file instead of writing / reading past its map, which threw
        // UnauthorizedAccessException (write) or killed the process with an
        // AccessViolationException (read).
        b.WritePageImmediate(last, Filled(b.PageSize, 0xB2));
        var buffer = new byte[b.PageSize];
        b.ReadPage(last - 1, null, buffer);
        Assert.Equal(Filled(b.PageSize, 0xA1), buffer);
        a.ReadPage(last, null, buffer);
        Assert.Equal(Filled(a.PageSize, 0xB2), buffer);
    }

    [Fact]
    public void PagesAnotherEngineAllocatedUnderTheOpenLock_AreNotHandedOutAgain()
    {
        var dbPath = Path.Combine(_tempDir, "alloc.db");
        using var a = new StorageEngine(dbPath, MultiProcess);
        using var b = new StorageEngine(dbPath, MultiProcess);

        // A grows the file by a block and keeps allocating from it outside the open lock.
        var fromA = new HashSet<uint> { a.AllocatePage() };

        // B, under the open lock (creating a collection, adding a dictionary key, ...), skips
        // past A's block and grows the file by another one.
        var fromB = new List<uint>();
        using (b.EnterOpenLock())
            for (int i = 0; i < 4; i++) fromB.Add(b.AllocatePage());

        // A runs past the end of its block into the one B added: it must skip B's block too.
        for (int i = 0; i < 192; i++) fromA.Add(a.AllocatePage());

        Assert.Empty(fromB.Where(fromA.Contains));
    }

    [Fact]
    public void DictionaryKeysAddedByAnotherEngine_KeepTheirIds()
    {
        var dbPath = Path.Combine(_tempDir, "dict.db");
        using var a = new StorageEngine(dbPath, MultiProcess);
        using var b = new StorageEngine(dbPath, MultiProcess);

        ushort alpha = a.GetOrAddDictionaryEntry("alpha");

        // B loaded the dictionary before A added "alpha". It must not give "alpha"'s id to
        // another key, and must resolve "alpha" to the id A gave it.
        ushort beta = b.GetOrAddDictionaryEntry("beta");
        Assert.NotEqual(alpha, beta);
        Assert.Equal(alpha, b.GetOrAddDictionaryEntry("alpha"));
        Assert.Equal(beta, a.GetOrAddDictionaryEntry("beta"));
    }

    private static byte[] Filled(int size, byte value)
    {
        var page = new byte[size];
        Array.Fill(page, value);
        return page;
    }

    private static async Task AssertUsable(string dbPath, string name)
    {
        using (var ctx = new TestDbContext(dbPath, MultiProcess))
        {
            using var txn = ctx.BeginTransaction();
            await ctx.Users.InsertAsync(new User { Id = ObjectId.NewObjectId(), Name = name }, txn);
            await txn.CommitAsync();
        }
        using (var ctx = new TestDbContext(dbPath, MultiProcess))
        {
            var users = new List<User>();
            await foreach (var u in ctx.Users.FindAllAsync()) users.Add(u);
            Assert.Equal(name, Assert.Single(users).Name);
        }
    }
}
