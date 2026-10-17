using Microsoft.Playwright;
using Xunit.Abstractions;

namespace BLite.Wasm.BrowserTests;

/// <summary>
/// BLite.Wasm in a real browser. Every test runs in a fresh browser context, so
/// each starts with empty OPFS and IndexedDB storage. A failing or hanging
/// scenario fails its test; every outcome is also appended to the fixture's report.
/// </summary>
[Collection(BrowserCollection.Name)]
public sealed class BrowserScenarioTests : IAsyncLifetime
{
    private readonly BrowserFixture _fixture;
    private readonly ITestOutputHelper _output;
    private IBrowserContext _context = null!;
    private readonly List<HarnessPage> _pages = new();

    public BrowserScenarioTests(BrowserFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    public static TheoryData<string> AllBackends => new() { "IndexedDb", "Opfs", "InMemory" };
    public static TheoryData<string> PersistentBackends => new() { "IndexedDb", "Opfs" };

    public async Task InitializeAsync() => _context = await _fixture.Browser.NewContextAsync();

    public async Task DisposeAsync()
    {
        foreach (var page in _pages)
            await page.DisposeAsync();
        await Task.WhenAny(_context.CloseAsync(), Task.Delay(10_000));
    }

    // ── Environment ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Probe_reports_runtime_and_backend_detection()
    {
        var page = await OpenAsync();
        var probe = await CallAsync(page, "Probe", "-");
        AssertOk(probe);
    }

    /// <summary>
    /// The public static IsAvailable() probes call [JSImport] functions whose module is
    /// only imported by BLiteWasm.CreateAsync / DetectBestBackendAsync.
    /// </summary>
    [Theory]
    [InlineData("IndexedDb")]
    [InlineData("Opfs")]
    public async Task IsAvailable_before_any_other_call(string backend)
    {
        var page = await OpenAsync();
        AssertOk(await CallAsync(page, "IsAvailableFirst", backend, backend));
    }

    // ── Async API, every backend ─────────────────────────────────────────────

    [Theory, MemberData(nameof(AllBackends))]
    public async Task Crud_through_the_async_api(string backend)
    {
        var page = await OpenAsync();
        AssertOk(await CallAsync(page, "Crud", backend, backend, "crud", 50));
    }

    [Theory, MemberData(nameof(AllBackends))]
    public async Task Concurrent_async_inserts(string backend)
    {
        var page = await OpenAsync();
        AssertOk(await CallAsync(page, "ConcurrentInserts", backend, backend, "concurrent", 50));
    }

    [Theory, MemberData(nameof(AllBackends))]
    public async Task Blql_async_operators(string backend)
    {
        var page = await OpenAsync();
        AssertOk(await CallAsync(page, "BlqlAsync", backend, backend, "blql-async"));
    }

    [Theory]
    [InlineData("IndexedDb", false)]
    [InlineData("Opfs", false)]
    [InlineData("InMemory", false)]
    [InlineData("InMemory", true)]
    public async Task Typed_context_with_async_linq(string backend, bool indexed)
    {
        var page = await OpenAsync();
        AssertOk(await CallAsync(page, "TypedAsyncLinq", $"{backend}/indexed={indexed}", backend, "typed-async", indexed));
    }

    [Theory]
    [InlineData("IndexedDb", "async-commit")]
    [InlineData("IndexedDb", "sync-begin")]
    [InlineData("IndexedDb", "dispose-uncommitted")]
    [InlineData("IndexedDb", "sync-rollback")]
    [InlineData("InMemory", "dispose-uncommitted")]
    public async Task Explicit_transactions(string backend, string mode)
    {
        var page = await OpenAsync();
        AssertOk(await CallAsync(page, "Transactions", backend, backend, "txn-" + mode, mode));
    }

    // ── Synchronous entry points (sync-over-async in Core) ────────────────────

    [Theory]
    [InlineData("tolist")]
    [InlineData("count")]
    [InlineData("first")]
    [InlineData("any")]
    [InlineData("foreach")]
    public async Task Typed_sync_linq_operator(string op)
    {
        var page = await OpenAsync();
        AssertOk(await CallAsync(page, "TypedSyncLinq", "InMemory/" + op, "InMemory", "typed-sync", op, false));
    }

    [Theory]
    [InlineData("tolist")]
    [InlineData("count")]
    [InlineData("first")]
    [InlineData("any")]
    public async Task Blql_sync_operator(string op)
    {
        var page = await OpenAsync();
        AssertOk(await CallAsync(page, "BlqlSync", "InMemory/" + op, "InMemory", "blql-sync", op));
    }

    // ── Durability across a page reload ───────────────────────────────────────

    [Theory]
    [InlineData("IndexedDb", "checkpoint")]
    [InlineData("IndexedDb", "dispose")]
    [InlineData("IndexedDb", "commit-only")]
    [InlineData("Opfs", "checkpoint")]
    [InlineData("Opfs", "dispose")]
    [InlineData("Opfs", "commit-only")]
    public async Task Committed_write_survives_reload(string backend, string mode)
    {
        var page = await OpenAsync();
        var walDb = backend == "Opfs" ? "durable-wal" : "durable";
        AssertOk(await CallAsync(page, "WriteMarker", $"{backend}/{mode}", backend, "durable", "m1", mode));
        _output.WriteLine($"IndexedDB before reload: {await page.IndexedDbCountsAsync(walDb)}");
        await page.ReloadAsync();
        _output.WriteLine($"IndexedDB after reload: {await page.IndexedDbCountsAsync(walDb)}");
        var read = await CallAsync(page, "ReadMarkers", $"{backend}/{mode}", backend, "durable");
        _output.WriteLine($"IndexedDB after reopen: {await page.IndexedDbCountsAsync(walDb)}");
        if (read.Ok && read.Data["markers"] != "m1")
        {
            // Diagnostic only: does a second open, after recovery wrote the pages, see the marker?
            await page.ReloadAsync();
            await CallAsync(page, "ReadMarkers", $"{backend}/{mode}/second-open", backend, "durable");
        }
        AssertOk(read);
        Assert.True(read.Data["markers"] == "m1", $"Expected marker m1 after reload, found '{read.Data["markers"]}'.");
    }

    [Theory]
    [InlineData("IndexedDb", true, false)]
    [InlineData("IndexedDb", false, false)]
    [InlineData("Opfs", true, false)]
    [InlineData("Opfs", false, false)]
    [InlineData("IndexedDb", true, true)]
    public async Task Typed_write_survives_reload(string backend, bool checkpoint, bool indexed)
    {
        var page = await OpenAsync();
        var label = $"{backend}/checkpoint={checkpoint}/indexed={indexed}";
        AssertOk(await CallAsync(page, "TypedWrite", label, backend, "typed-durable", "t1", checkpoint, indexed));
        await page.ReloadAsync();
        var read = await CallAsync(page, "TypedRead", label, backend, "typed-durable", indexed);
        AssertOk(read);
        Assert.True(read.Data["names"] == "t1", $"Expected t1 after reload, found '{read.Data["names"]}'.");
        Assert.True(read.Data["indexQuery"] == "1", $"Index query after reload returned {read.Data["indexQuery"]}.");
    }

    // ── Two tabs on one database ──────────────────────────────────────────────

    [Theory, MemberData(nameof(PersistentBackends))]
    public async Task Two_tabs_writing_the_same_database_keep_both_writes(string backend)
    {
        var a = await OpenAsync();
        var b = await OpenAsync();
        AssertOk(await CallAsync(a, "Hold", backend + "/tab-a", backend, "shared"));
        AssertOk(await CallAsync(b, "Hold", backend + "/tab-b", backend, "shared"));
        AssertOk(await CallAsync(a, "HeldInsert", backend + "/tab-a", "a", true));
        AssertOk(await CallAsync(b, "HeldInsert", backend + "/tab-b", "b", true));
        var seenByB = await CallAsync(b, "HeldRead", backend + "/tab-b");
        _output.WriteLine($"tab b sees: {seenByB.Data.GetValueOrDefault("markers")}");

        var c = await OpenAsync();
        var read = await CallAsync(c, "ReadMarkers", backend + "/tab-c", backend, "shared");
        AssertOk(read);
        Assert.True(read.Data["markers"] == "a,b", $"A fresh tab should see both writes, found '{read.Data["markers"]}'.");
    }

    // ── Size ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("IndexedDb", 5)]
    [InlineData("IndexedDb", 20)]
    [InlineData("IndexedDb", 50)]
    [InlineData("Opfs", 5)]
    [InlineData("Opfs", 20)]
    [InlineData("Opfs", 50)]
    public async Task Large_database_open_time_and_memory(string backend, int megabytes)
    {
        var page = await OpenAsync();
        var walDb = backend == "Opfs" ? "large-wal" : "large";
        var fill = await CallAsync(page, "Fill", $"{backend}/{megabytes}MB", TimeSpan.FromMinutes(15), backend, "large", megabytes, 1000);
        AssertOk(fill);
        var counts = await page.IndexedDbCountsAsync(walDb);
        _output.WriteLine($"IndexedDB after fill: {counts}");
        _fixture.Record(new { scenario = "IndexedDbAfterFill", label = $"{backend}/{megabytes}MB", counts });
        await page.ReloadAsync();
        var baseline = await page.WasmMemoryBytesAsync();
        var open = await CallAsync(page, "OpenTiming", $"{backend}/{megabytes}MB", TimeSpan.FromMinutes(10), backend, "large");
        var after = await page.WasmMemoryBytesAsync();
        _output.WriteLine($"wasm memory: {baseline / 1048576:F0} MB before open, {after / 1048576:F0} MB after");
        _fixture.Record(new { scenario = "WasmMemory", label = $"{backend}/{megabytes}MB", beforeMB = baseline / 1048576, afterMB = after / 1048576 });
        AssertOk(open);
        Assert.True(open.Data["documents"] == fill.Data["documents"],
            $"Filled {fill.Data["documents"]} documents and checkpointed, but the reopened database holds {open.Data["documents"]}.");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<HarnessPage> OpenAsync()
    {
        var page = await HarnessPage.OpenAsync(_context, _fixture.BaseUrl);
        _pages.Add(page);
        return page;
    }

    private Task<ScenarioOutcome> CallAsync(HarnessPage page, string method, string label, params object[] args) =>
        CallAsync(page, method, label, null, args);

    private async Task<ScenarioOutcome> CallAsync(HarnessPage page, string method, string label, TimeSpan? timeout, params object[] args)
    {
        var outcome = await page.CallAsync(method, args, timeout);
        var unobserved = outcome.Status is "hang" or "crash" ? "" : await page.Page.EvaluateAsync<string>("() => window.harness.run('DrainUnobserved')");
        _output.WriteLine($"[{label}] {outcome.Describe()}");
        if (!string.IsNullOrEmpty(unobserved))
            _output.WriteLine($"[{label}] unobserved task exceptions:\n{unobserved}");
        _fixture.Record(new
        {
            browser = _fixture.BrowserName,
            variant = _fixture.Variant,
            scenario = outcome.Scenario,
            label,
            status = outcome.Status,
            elapsedMs = Math.Round(outcome.ElapsedMs, 1),
            errorType = outcome.ErrorType,
            error = outcome.Error,
            stack = outcome.Stack,
            data = outcome.Data,
            unobserved
        });
        return outcome;
    }

    private static void AssertOk(ScenarioOutcome outcome) => Assert.True(outcome.Ok, outcome.Describe());
}
