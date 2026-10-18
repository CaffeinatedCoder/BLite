using BLite.Bson;
using BLite.Shared;

namespace BLite.Tests;

/// <summary>
/// Regression coverage for a <c>DocumentDbContext</c> whose class name does not end in
/// "Context" (see <c>UnsuffixedNameFixtures.cs</c> in BLite.Shared). The generator's syntax
/// filter used to require that suffix, so such a context got no generated code and no
/// diagnostic; its collection properties stayed null (EntglDb/BLite#300).
/// </summary>
public class UnsuffixedNameGeneratorTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"blite_unsuffixed_{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        TryDelete(_dbPath);
        TryDelete(Path.ChangeExtension(_dbPath, ".wal"));
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task Context_NamedWithoutSuffix_HasGeneratedCollections_AndRoundTrips()
    {
        using var db = new Shop(_dbPath);

        Assert.NotNull(db.Customers);

        var id = ObjectId.NewObjectId();
        await db.Customers.InsertAsync(new ShopCustomer { Id = id, Name = "Ada" });

        var stored = await db.Customers.FindByIdAsync(id);
        Assert.NotNull(stored);
        Assert.Equal("Ada", stored.Name);
    }

    [Fact]
    public async Task DerivedContext_NamedWithoutSuffix_HasBothCollections()
    {
        using var db = new OnlineShop(_dbPath);

        Assert.NotNull(db.Customers);
        Assert.NotNull(db.Orders);

        var id = ObjectId.NewObjectId();
        await db.Orders.InsertAsync(new ShopOrder { Id = id, Total = 12.5m });
        var stored = await db.Orders.FindByIdAsync(id);
        Assert.NotNull(stored);
        Assert.Equal(12.5m, stored.Total);
    }

    [Fact]
    public void Generated_Mappers_Exist_ForTheUnsuffixedContext()
    {
        var mapperTypes = typeof(Shop).Assembly
            .GetTypes()
            // "{namespace}.{class}_{file}_Mappers", as for every context (see GlobalNamespaceGeneratorTests).
            .Where(t => t.Namespace == $"{typeof(Shop).Namespace}.{nameof(Shop)}_UnsuffixedNameFixtures_Mappers")
            .ToArray();

        Assert.NotEmpty(mapperTypes);
        Assert.Contains(mapperTypes, t => t.Name.Contains(nameof(ShopCustomer)));
    }
}
