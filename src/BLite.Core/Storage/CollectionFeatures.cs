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
/// version knows is refused the same way, so a new index type stays protected even after such a
/// release dropped the record.
/// </remarks>
internal static class CollectionFeatures
{
    // Features this version implements. Empty until a feature that needs protecting ships.
    private static readonly HashSet<string> s_known = new(StringComparer.Ordinal);

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
    {
        var result = new List<RequiredFeature>();
        foreach (var feature in metadata.RequiredFeatures)
        {
            if (IsKnown(feature.Id)) continue;
            if (readOnly && feature.Requirement == FeatureRequirement.Write) continue;
            result.Add(feature);
        }

        // A version that does not know an index type cannot query or maintain the index, so it is
        // treated as a feature needed to open the collection.
        foreach (var index in metadata.Indexes)
        {
            var type = (byte)index.Type;
            if (type > s_maxIndexType)
                result.Add(new RequiredFeature($"index-type-{type}", FeatureRequirement.Open,
                    $"index '{index.Name}' of type {type}, which this version does not know"));
        }
        return result;
    }

    private static string Describe(IEnumerable<RequiredFeature> features)
        => string.Join(", ", features.Select(f =>
            string.IsNullOrWhiteSpace(f.Description) ? $"'{f.Id}'" : f.Description));
}
