using System.ComponentModel.DataAnnotations.Schema;
using BLite.Bson;
using BLite.Core;
using BLite.Core.Collections;
using BLite.Core.Metadata;

namespace BLite.Shared;

/// <summary>One property per numeric type LINQ aggregates, plain and nullable.</summary>
public class Reading
{
    public ObjectId Id { get; set; }
    public string Tag { get; set; } = string.Empty;
    public int Count { get; set; }
    public long Total { get; set; }
    public int Units { get; set; }
    public double Ratio { get; set; }
    public float Weight { get; set; }
    public decimal Price { get; set; }
    public int? MaybeCount { get; set; }
    public long? MaybeTotal { get; set; }
    public double? MaybeRatio { get; set; }
    public float? MaybeWeight { get; set; }
    public decimal? MaybePrice { get; set; }

    /// <summary>
    /// Stored as <c>"score"</c>, while <see cref="Score"/> is stored as <c>"level"</c>: each field
    /// carries the conventional name of the other property.
    /// </summary>
    [Column("score")]
    public int Level { get; set; }

    [Column("level")]
    public int Score { get; set; }
}

/// <summary>
/// <see cref="Reading"/> with B-Tree indexes on <c>Count</c> (int), <c>Total</c> (long) and
/// <c>MaybeCount</c> (int?), and none on the other properties.
/// </summary>
public partial class AggregateDbContext : DocumentDbContext
{
    public DocumentCollection<ObjectId, Reading> Readings { get; set; } = null!;

    public AggregateDbContext(string path) : base(path) => InitializeCollections();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Reading>().ToCollection("readings")
            .HasIndex(r => r.Count)
            .HasIndex(r => r.Total)
            .HasIndex(r => r.MaybeCount);
    }
}

/// <summary>A later version of <see cref="Reading"/> with properties (initialised) that older documents lack.</summary>
public class ReadingV2
{
    public ObjectId Id { get; set; }
    public string Tag { get; set; } = string.Empty;
    public int Count { get; set; }
    public long Total { get; set; }
    public int? MaybeCount { get; set; }
    public int Bonus { get; set; } = 7;
    public double? Factor { get; set; } = 1.5;
}

/// <summary>Reads the <c>readings</c> collection as <see cref="ReadingV2"/>.</summary>
public partial class AggregateV2DbContext : DocumentDbContext
{
    public DocumentCollection<ObjectId, ReadingV2> Readings { get; set; } = null!;

    public AggregateV2DbContext(string path) : base(path) => InitializeCollections();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.Entity<ReadingV2>().ToCollection("readings");
}

/// <summary>Stores <c>hidden</c>, <c>unmapped</c> and <c>gone</c>, which <see cref="ShapeV2"/> no longer reads.</summary>
public class ShapeV1
{
    public ObjectId Id { get; set; }
    public int Count { get; set; }
    public int Hidden { get; set; }
    public int Unmapped { get; set; }
    public int Gone { get; set; }
}

public partial class ShapeV1DbContext : DocumentDbContext
{
    public DocumentCollection<ObjectId, ShapeV1> Shapes { get; set; } = null!;

    public ShapeV1DbContext(string path) : base(path) => InitializeCollections();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.Entity<ShapeV1>().ToCollection("shapes");
}

/// <summary>
/// A later version of <see cref="ShapeV1"/>: the fields older documents still hold are no longer
/// the values of these properties.
/// </summary>
public class ShapeV2
{
    public ObjectId Id { get; set; }
    public int Count { get; set; }

    [BsonIgnore]
    public int Hidden { get; set; }

    [NotMapped]
    public int Unmapped { get; set; }

    public int Gone => Count * 10;
}

public partial class ShapeV2DbContext : DocumentDbContext
{
    public DocumentCollection<ObjectId, ShapeV2> Shapes { get; set; } = null!;

    public ShapeV2DbContext(string path) : base(path) => InitializeCollections();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.Entity<ShapeV2>().ToCollection("shapes");
}

/// <summary>Accessors with logic: the stored value is not what the materialised getter returns.</summary>
public class Doubled
{
    private int _g;
    private long _h;

    public ObjectId Id { get; set; }

    /// <summary>Stores <c>2 * _g</c>; reading it back sets <c>_g</c> to that, so the getter doubles it again.</summary>
    public int G { get => _g * 2; set => _g = value; }

    /// <summary>The same, with the logic in the setter of a property with a B-Tree index.</summary>
    public long H { get => _h; set => _h = value + 1; }

    public int Plain { get; set; }
}

/// <summary>A key named <c>Code</c> next to an ordinary property named <c>Id</c>.</summary>
public class Coded
{
    [System.ComponentModel.DataAnnotations.Key]
    public int Code { get; set; }

    public int Id { get; set; }
}

/// <summary>Its constructor transforms the value it is passed when the entity is read back.</summary>
public class Scaled
{
    public Scaled(int amount) => Amount = amount * 3;

    public ObjectId Id { get; set; }
    public int Amount { get; set; }
}

public partial class VerbatimDbContext : DocumentDbContext
{
    public DocumentCollection<ObjectId, Doubled> Doubled { get; set; } = null!;
    public DocumentCollection<int, Coded> Coded { get; set; } = null!;
    public DocumentCollection<ObjectId, Scaled> Scaled { get; set; } = null!;

    public VerbatimDbContext(string path) : base(path) => InitializeCollections();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Doubled>().ToCollection("doubled")
            .HasIndex(d => d.G)
            .HasIndex(d => d.H);
        modelBuilder.Entity<Coded>().ToCollection("coded");
        modelBuilder.Entity<Scaled>().ToCollection("scaled");
    }
}
