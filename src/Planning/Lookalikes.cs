namespace Shoko.SeasonPosters.Planning;

/// <summary>Which posters count as lookalikes that the planner keeps apart within a show.</summary>
public enum LookalikePolicy
{
    /// <summary>
    /// Avoid the same artwork, even with different text. A textless or other-language version is used only when
    /// nothing else is left.
    /// </summary>
    AvoidSameArtwork,

    /// <summary>Avoid only images that look the same. Textless and other-language versions count as different.</summary>
    AvoidSameImageOnly,

    /// <summary>Only the image itself counts as a duplicate, as before lookalikes were detected.</summary>
    Off,
}

/// <summary>Tells the planner how much two posters look alike.</summary>
public interface ILookalikes
{
    Likeness Compare(PosterCandidate first, PosterCandidate second);

    /// <summary>Whether the poster can be compared yet. Asking about one that cannot queues it to be fingerprinted.</summary>
    bool IsKnown(PosterCandidate poster);
}

/// <summary>Treats every pair of different images as different.</summary>
public sealed class NoLookalikes : ILookalikes
{
    public static readonly NoLookalikes Instance = new();

    private NoLookalikes()
    {
    }

    public Likeness Compare(PosterCandidate first, PosterCandidate second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        return first.UniquenessKey == second.UniquenessKey ? Likeness.SameImage : Likeness.Different;
    }

    public bool IsKnown(PosterCandidate poster) => true;
}

/// <summary>
/// Compares posters by their fingerprints. A poster without a fingerprint yet counts as different, and is listed in
/// <see cref="Missing"/> so the caller can fingerprint it and plan again.
/// </summary>
public sealed class FingerprintLookalikes(Func<Guid, PosterFingerprint?> find, LookalikePolicy policy) : ILookalikes
{
    private readonly Dictionary<(Guid, Guid), Likeness> _compared = [];
    private readonly Dictionary<Guid, Guid> _missing = [];

    /// <summary>Posters that were compared without a fingerprint, as uniqueness key to image ID.</summary>
    public IReadOnlyDictionary<Guid, Guid> Missing => _missing;

    public Likeness Compare(PosterCandidate first, PosterCandidate second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        if (first.UniquenessKey == second.UniquenessKey)
            return Likeness.SameImage;
        if (policy is LookalikePolicy.Off)
            return Likeness.Different;

        var pair = first.UniquenessKey.CompareTo(second.UniquenessKey) < 0
            ? (first.UniquenessKey, second.UniquenessKey)
            : (second.UniquenessKey, first.UniquenessKey);
        if (!_compared.TryGetValue(pair, out var likeness))
        {
            var a = Find(first);
            var b = Find(second);
            if (a is null || b is null)
                return Likeness.Different;

            likeness = _compared[pair] = PosterFingerprint.Compare(a, b);
        }

        return policy is LookalikePolicy.AvoidSameImageOnly && likeness is Likeness.SameArtwork ? Likeness.Different : likeness;
    }

    public bool IsKnown(PosterCandidate poster)
    {
        ArgumentNullException.ThrowIfNull(poster);
        return policy is LookalikePolicy.Off || Find(poster) is not null;
    }

    private PosterFingerprint? Find(PosterCandidate poster)
    {
        var fingerprint = find(poster.UniquenessKey);
        if (fingerprint is null)
            _missing.TryAdd(poster.UniquenessKey, poster.ImageId);
        return fingerprint;
    }
}
