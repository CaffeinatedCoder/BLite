using BLite.Bson;
using BLite.Core;
using BLite.Core.Collections;
using BLite.Core.Metadata;

namespace BLite.Shared;

public class Article
{
    public ObjectId Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Body { get; set; }
    public int Year { get; set; }
}

/// <summary>
/// Articles with full-text indexes on <c>Body</c> and <c>Title</c>, next to a B-Tree index on
/// <c>Title</c> for equality and range lookups.
/// </summary>
public partial class FullTextDbContext : DocumentDbContext
{
    public DocumentCollection<ObjectId, Article> Articles { get; set; } = null!;

    public FullTextDbContext(string path) : base(path) => InitializeCollections();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Article>().ToCollection("articles")
            .HasIndex(a => a.Title)
            .HasFullTextIndex(a => a.Title)
            .HasFullTextIndex(a => a.Body);
    }
}

/// <summary>The same collection without any secondary index.</summary>
public partial class FullTextNoIndexDbContext : DocumentDbContext
{
    public DocumentCollection<ObjectId, Article> Articles { get; set; } = null!;

    public FullTextNoIndexDbContext(string path) : base(path) => InitializeCollections();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.Entity<Article>().ToCollection("articles");
}
