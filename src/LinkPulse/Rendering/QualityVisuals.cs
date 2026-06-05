using LinkPulse.Abstractions;

namespace LinkPulse.Rendering;

/// <summary>
/// Maps a <see cref="QualityRating"/> to its human-facing label and the CSS state modifier the
/// component renders. Kept separate from the markup so the mapping is pure and unit-testable, and
/// so colour is never the sole signal: the label travels with every visual (accessibility, &#167;8).
/// </summary>
internal static class QualityVisuals
{
    /// <summary>The one-word label shown on the badge and as the popover heading.</summary>
    public static string Label(QualityRating rating) => rating switch
    {
        QualityRating.Excellent => "Excellent",
        QualityRating.Good => "Good",
        QualityRating.Fair => "Fair",
        QualityRating.Poor => "Poor",
        _ => "Disconnected",
    };

    /// <summary>
    /// The CSS state modifier appended to the root element's class (e.g. <c>lp--good</c>), which
    /// selects the themeable dot/accent colour custom property for the rating.
    /// </summary>
    public static string Modifier(QualityRating rating) => rating switch
    {
        QualityRating.Excellent => "excellent",
        QualityRating.Good => "good",
        QualityRating.Fair => "fair",
        QualityRating.Poor => "poor",
        _ => "disconnected",
    };
}
