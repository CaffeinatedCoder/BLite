using System;
using System.Collections.Generic;
using System.Linq;
using BLite.Core.Indexing;

namespace BLite.Core.Storage;

/// <summary>What a BLite version must support a <see cref="RequiredFeature"/> for.</summary>
internal enum FeatureRequirement : byte
{
    /// <summary>
    /// Needed to open the collection at all, because a version without the feature would read it
    /// wrongly (e.g. serve queries from an index it misinterprets). Any value other than
    /// <see cref="Write"/>, including ones added later, is treated this way.
    /// </summary>
    Open = 1,

    /// <summary>
    /// Needed only to modify the collection: reading is correct without the feature, but writes
    /// would leave the data it depends on out of date. A version without it may still open the
    /// collection when the database is opened read-only.
    /// </summary>
    Write = 2,
}

/// <summary>
/// A capability a collection depends on, recorded in its <see cref="CollectionMetadata"/> by the
/// version that started using it.
/// </summary>
/// <param name="Id">Stable identifier that versions implementing the feature recognise.</param>
/// <param name="Requirement">Whether the feature is needed to open the collection or only to write to it.</param>
/// <param name="Description">
/// Human-readable name for error messages, including the first version that supports it, e.g.
/// <c>"full-text indexes (BLite 5.3 or later)"</c>. Stored with the feature so that versions which
/// do not know it can still tell the user what is missing.
/// </param>
internal sealed record RequiredFeature(string Id, FeatureRequirement Requirement, string Description);

/// <summary>
/// Per-collection required features (#187). A collection that uses something older versions cannot
/// maintain (a new index type, a new document encoding, ...) records it in
/// <see cref="CollectionMetadata.RequiredFeatures"/>; a version that finds a feature it does not
/// know refuses to open or modify the collection instead of silently damaging it.
/// </summary>
/// <remarks>
/// Only versions that contain this check can refuse; releases before it ignore the record and may
/// also drop it when they rewrite the collection's metadata. An index of a type newer than any this
/// version knows is refused the same way, which keeps a new index type protected after such a
/// release dropped the record, but only while that release kept the index entry: a 5.2.x dynamic
/// collection rewrites the index list from the indexes it loaded, so its index changes drop both.
/// New per-index data must therefore go only in trailing sections of the record, never inline in
/// the index list (as the vector dimensions and metric are): a version that misreads the index list
/// cannot load the record at all, and so never gets to refuse it.
/// </remarks>
internal static class CollectionFeatures
{
    /// <summary>
    /// Full-text indexes (#70), recorded while the collection has at least one. Needed to open: a
    /// version without them reads a full-text index as a B-Tree index, so its query planner would
    /// answer equality, range and IN lookups on the property from the postings, and its writes
    /// would leave keys and stale postings behind.
    /// </summary>
    internal static readonly RequiredFeature FullTextIndexes =
        new("fulltext-index", FeatureRequirement.Open, "full-text indexes (BLite 5.3 or later)");

    // Features this version implements.
    private static readonly HashSet<string> s_known = new(StringComparer.Ordinal) { FullTextIndexes.Id };

    // Highest IndexType this version knows; larger values come from newer versions. 0 is accepted:
    // older releases could persist an index whose type was never set.
    private static readonly byte s_maxIndexType =
#if NET5_0_OR_GREATER
        Enum.GetValues<IndexType>().Max(t => (byte)t);
#else
        Enum.GetValues(typeof(IndexType)).Cast<IndexType>().Max(t => (byte)t);
#endif

    private static readonly string s_version =
        typeof(CollectionFeatures).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>Whether this version implements the feature with the given id.</summary>
    internal static bool IsKnown(string id) => s_known.Contains(id);

    /// <summary>
    /// Records the features the indexes in <paramref name="metadata"/> require and removes those
    /// none of them requires any more (today: <see cref="FullTextIndexes"/> while there is a
    /// full-text index). Call after changing the index list, before saving the record. Other
    /// features, including ones this version does not know, are left as they are.
    /// </summary>
    /// <returns>Whether <paramref name="metadata"/> changed.</returns>
    internal static bool SyncIndexFeatures(CollectionMetadata metadata)
    {
        bool needed = metadata.Indexes.Any(i => i.Type == IndexType.FullText);
        bool recorded = metadata.RequiredFeatures.Any(f => f.Id == FullTextIndexes.Id);
        if (needed == recorded) return false;

        if (needed) metadata.RequiredFeatures.Add(FullTextIndexes);
        else metadata.RequiredFeatures.RemoveAll(f => f.Id == FullTextIndexes.Id);
        return true;
    }

    /// <summary>
    /// Whether this version may open the collection: every feature it requires is known, except
    /// <see cref="FeatureRequirement.Write"/> features when the database is open read-only.
    /// </summary>
    internal static bool CanOpen(CollectionMetadata metadata, bool readOnly)
        => Unsupported(metadata, readOnly).Count == 0;

    /// <summary>Whether this version may modify the collection: every feature it requires is known.</summary>
    internal static bool CanWrite(CollectionMetadata metadata)
        => Unsupported(metadata, readOnly: false).Count == 0;

    /// <summary>Throws unless <see cref="CanOpen"/> allows opening the collection.</summary>
    /// <exception cref="NotSupportedException">The collection requires features this version does not support.</exception>
    internal static void EnsureCanOpen(CollectionMetadata metadata, bool readOnly)
    {
        var unsupported = Unsupported(metadata, readOnly);
        if (unsupported.Count == 0) return;

        throw unsupported.All(f => f.Requirement == FeatureRequirement.Write)
            ? new NotSupportedException(
                $"Collection '{metadata.Name}' requires features that BLite {s_version} cannot maintain: " +
                $"{Describe(unsupported)}. Open the database read-only to read it, or use a BLite version " +
                "that supports these features.")
            : new NotSupportedException(
                $"Collection '{metadata.Name}' requires features that BLite {s_version} does not support: " +
                $"{Describe(unsupported)}. Use a BLite version that supports these features.");
    }

    /// <summary>Throws unless <see cref="CanWrite"/> allows modifying the collection.</summary>
    /// <exception cref="NotSupportedException">The collection requires features this version does not support.</exception>
    internal static void EnsureCanWrite(CollectionMetadata metadata)
    {
        var unsupported = Unsupported(metadata, readOnly: false);
        if (unsupported.Count == 0) return;

        throw new NotSupportedException(
            $"Collection '{metadata.Name}' requires features that BLite {s_version} does not support: " +
            $"{Describe(unsupported)}. It cannot be modified by this version; use a BLite version that " +
            "supports these features.");
    }

    private static List<RequiredFeature> Unsupported(CollectionMetadata metadata, bool readOnly)
        => Unsupported(metadata, readOnly, s_known, s_maxIndexType);

    /// <summary>
    /// The features that keep a version knowing only <paramref name="known"/> features and index
    /// types up to <paramref name="maxIndexType"/> from opening (or, with <paramref name="readOnly"/>
    /// false, modifying) the collection. Takes the version's knowledge as parameters so tests can
    /// ask what an older version would refuse.
    /// </summary>
    internal static List<RequiredFeature> Unsupported(CollectionMetadata metadata, bool readOnly, ICollection<string> known, byte maxIndexType)
    {
        var result = new List<RequiredFeature>();
        foreach (var feature in metadata.RequiredFeatures)
        {
            if (known.Contains(feature.Id)) continue;
            if (readOnly && feature.Requirement == FeatureRequirement.Write) continue;
            result.Add(feature);
        }

        // A version that does not know an index type cannot query or maintain the index, so it is
        // treated as a feature needed to open the collection.
        foreach (var index in metadata.Indexes)
        {
            var type = (byte)index.Type;
            if (type > maxIndexType)
                result.Add(new RequiredFeature($"index-type-{type}", FeatureRequirement.Open,
                    $"index '{index.Name}' of type {type}, which this version does not know"));
        }
        return result;
    }

    private static string Describe(IEnumerable<RequiredFeature> features)
        => string.Join(", ", features.Select(f =>
            string.IsNullOrWhiteSpace(f.Description) ? $"'{f.Id}'" : f.Description));
}
