namespace Mostlylucid.DocSummarizer.Images.Models;

/// <summary>
/// Detected image type based on deterministic analysis
/// </summary>
public enum ImageType
{
    /// <summary>
    /// Unable to determine type
    /// </summary>
    Unknown,

    /// <summary>
    /// Natural photograph (camera/phone photo)
    /// </summary>
    Photo,

    /// <summary>
    /// Screenshot from computer/phone UI
    /// </summary>
    Screenshot,

    /// <summary>
    /// Technical diagram, flowchart, architecture drawing
    /// </summary>
    Diagram,

    /// <summary>
    /// Scanned document (paper, book page)
    /// </summary>
    ScannedDocument,

    /// <summary>
    /// Small icon or logo
    /// </summary>
    Icon,

    /// <summary>
    /// Data visualization (chart, graph, plot)
    /// </summary>
    Chart,

    /// <summary>
    /// Digital artwork, illustration, render
    /// </summary>
    Artwork,

    /// <summary>
    /// Meme or image with overlaid text
    /// </summary>
    Meme
}
