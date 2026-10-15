using BLite.Bson;
using BLite.Core;
using BLite.Core.Collections;
using BLite.Core.Indexing;
using BLite.Core.Metadata;

namespace BLite.Shared;

// Contexts for adding model-declared indexes to collections that already hold documents.

/// <summary>"App v1": the collections without any secondary index.</summary>
public partial class IndexEvoV1DbContext : DocumentDbContext
{
    public DocumentCollection<ObjectId, PersonV2> People { get; set; } = null!;
    public DocumentCollection<ObjectId, VectorEntity> Vectors { get; set; } = null!;
    public DocumentCollection<ObjectId, GeoEntity> Places { get; set; } = null!;

    public IndexEvoV1DbContext(string path) : base(path) => InitializeCollections();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PersonV2>().ToCollection("evo_people");
        modelBuilder.Entity<VectorEntity>().ToCollection("evo_vectors");
        modelBuilder.Entity<GeoEntity>().ToCollection("evo_places");
    }
}

/// <summary>"App v2": the same collections with a B-Tree, a vector and a spatial index.</summary>
public partial class IndexEvoV2DbContext : DocumentDbContext
{
    public DocumentCollection<ObjectId, PersonV2> People { get; set; } = null!;
    public DocumentCollection<ObjectId, VectorEntity> Vectors { get; set; } = null!;
    public DocumentCollection<ObjectId, GeoEntity> Places { get; set; } = null!;

    public IndexEvoV2DbContext(string path) : base(path) => InitializeCollections();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PersonV2>().ToCollection("evo_people").HasIndex(p => p.Age);
        modelBuilder.Entity<VectorEntity>().ToCollection("evo_vectors")
            .HasVectorIndex(x => x.Embedding, dimensions: 3, metric: VectorMetric.L2, name: "idx_vector");
        modelBuilder.Entity<GeoEntity>().ToCollection("evo_places").HasSpatialIndex(x => x.Location, name: "idx_spatial");
    }
}

/// <summary>Declares a vector index whose dimension does not match the stored embeddings.</summary>
public partial class IndexEvoBadVectorDbContext : DocumentDbContext
{
    public DocumentCollection<ObjectId, VectorEntity> Vectors { get; set; } = null!;

    public IndexEvoBadVectorDbContext(string path) : base(path) => InitializeCollections();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<VectorEntity>().ToCollection("evo_vectors")
            .HasVectorIndex(x => x.Embedding, dimensions: 4, metric: VectorMetric.L2, name: "idx_vector");
    }
}
