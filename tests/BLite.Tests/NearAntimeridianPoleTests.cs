using BLite.Core;
using BLite.Core.Indexing;
using BLite.Shared;

namespace BLite.Tests;

/// <summary>
/// Indexed <c>Near</c> must find every point within the radius, including points across the
/// ±180° meridian, on the far side of a pole, and at the circle's widest longitude (#166).
/// </summary>
public class NearAntimeridianPoleTests : IDisposable
{
    private readonly string _dbPath;
    private readonly TestDbContext _db;

    public NearAntimeridianPoleTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"near_edges_{Guid.NewGuid():N}.db");
        _db = new TestDbContext(_dbPath);
    }

    public void Dispose()
    {
        _db.Dispose();
        foreach (var f in new[] { _dbPath, Path.ChangeExtension(_dbPath, ".wal") })
            try { if (File.Exists(f)) File.Delete(f); } catch { /* best-effort */ }
    }

    private async Task<List<string>> NearAsync((double, double) center, double radiusKm)
    {
        var viaIndex = (await _db.GeoItems.NearAsync("idx_spatial", center, radiusKm).ToListAsync()).Select(e => e.Name).OrderBy(n => n).ToList();
        var viaLinq = _db.GeoItems.AsQueryable().Where(p => p.Location.Near(center, radiusKm)).ToList().Select(e => e.Name).OrderBy(n => n).ToList();
        Assert.Equal(viaIndex, viaLinq);
        return viaIndex;
    }

    [Fact]
    public async Task Near_AcrossTheAntimeridian_FindsPointsOnBothSides()
    {
        // 22.2 km apart across ±180°.
        await _db.GeoItems.InsertAsync(new GeoEntity { Name = "east", Location = (0.0, 179.9) });
        await _db.GeoItems.InsertAsync(new GeoEntity { Name = "west", Location = (0.0, -179.9) });

        Assert.Equal(new[] { "east", "west" }, await NearAsync((0.0, -179.9), 50.0));
        Assert.Equal(new[] { "east", "west" }, await NearAsync((0.0, 179.9), 50.0));
    }

    [Fact]
    public async Task Near_OverTheNorthPole_FindsPointsOnTheFarSide()
    {
        // 161.8 km apart; the shortest path crosses the pole.
        await _db.GeoItems.InsertAsync(new GeoEntity { Name = "over the pole", Location = (89.5, 150.0) });
        await _db.GeoItems.InsertAsync(new GeoEntity { Name = "too far", Location = (85.0, 180.0) });

        Assert.Equal(new[] { "over the pole" }, await NearAsync((89.0, 0.0), 200.0));
    }

    [Fact]
    public async Task Near_AroundTheSouthPole_FindsPointsAtEveryLongitude()
    {
        foreach (var lon in new[] { -180.0, -90.0, 0.0, 90.0, 179.0 })
            await _db.GeoItems.InsertAsync(new GeoEntity { Name = $"lon {lon}", Location = (-89.9, lon) });

        Assert.Equal(5, (await NearAsync((-89.9, 45.0), 30.0)).Count);
    }

    [Fact]
    public async Task Near_AtHighLatitude_FindsPointsAtTheWidestLongitude()
    {
        // The circle around (80, 0) with a 556 km radius is widest near 81.3°N, where it reaches
        // ±30.1° of longitude, more than radius / cos(80°) = ±28.8° at the center latitude.
        (double, double) center = (80.0, 0.0), widest = (81.3, 29.5);
        Assert.True(SpatialMath.DistanceKm(80.0, 0.0, widest.Item1, widest.Item2) < 556.0);
        await _db.GeoItems.InsertAsync(new GeoEntity { Name = "widest", Location = widest });

        Assert.Equal(new[] { "widest" }, await NearAsync(center, 556.0));
    }

    [Fact]
    public async Task Dynamic_Near_AcrossTheAntimeridian_FindsPointsOnBothSides()
    {
        var path = Path.ChangeExtension(_dbPath, ".dyn.db");
        try
        {
            using var engine = new BLiteEngine(path);
            var col = engine.GetOrCreateCollection("places");
            await col.CreateSpatialIndexAsync("pos");
            await col.InsertAsync(col.CreateDocument(["name", "pos"], b => b
                .AddString("name", "east").AddCoordinates("pos", (0.0, 179.9))));

            var results = await col.NearAsync("idx_spatial_pos", (0.0, -179.9), 50.0).ToListAsync();

            Assert.True(Assert.Single(results).TryGetString("name", out var name));
            Assert.Equal("east", name);
        }
        finally
        {
            foreach (var f in new[] { path, Path.ChangeExtension(path, ".wal") })
                try { if (File.Exists(f)) File.Delete(f); } catch { /* best-effort */ }
        }
    }
}
