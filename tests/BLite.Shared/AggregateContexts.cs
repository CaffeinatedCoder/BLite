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
