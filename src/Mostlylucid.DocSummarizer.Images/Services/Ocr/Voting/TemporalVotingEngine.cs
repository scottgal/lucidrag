using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Images.Services.Analysis.Waves;

namespace Mostlylucid.DocSummarizer.Images.Services.Ocr.Voting;

/// <summary>
/// Performs character-level voting across multiple OCR results from different frames.
/// Dramatically improves accuracy by combining OCR from multiple frames.
///
/// Algorithm:
/// 1. Align text regions from different frames by bounding box IoU (Intersection over Union)
/// 2. For each aligned region, vote on character-by-character basis
/// 3. Most common character wins (with confidence weighting)
/// 4. Output: consensus text with aggregate confidence
///
/// Benefits:
/// - Corrects transient OCR errors (noise, compression artifacts)
/// - Increases confidence in final result
/// - Works well with partial text reveals (progressive typing)
/// </summary>
public class TemporalVotingEngine
{
    private readonly ILogger<TemporalVotingEngine>? _logger;
    private readonly bool _verbose;
    private readonly double _iouThreshold; // Minimum IoU to consider regions aligned
    private readonly bool _confidenceWeighting; // Weight votes by OCR confidence

    public TemporalVotingEngine(
        double iouThreshold = 0.5,
        bool confidenceWeighting = true,
        bool verbose = false,
        ILogger<TemporalVotingEngine>? logger = null)
    {
        _iouThreshold = iouThreshold;
        _confidenceWeighting = confidenceWeighting;
        _verbose = verbose;
        _logger = logger;
    }

    /// <summary>
    /// Perform temporal voting on OCR results from multiple frames.
    /// Returns consensus text with confidence scores.
    /// </summary>
    public VotingResult PerformVoting(List<FrameOcrResult> frameResults)
    {
        if (frameResults.Count == 0)
        {
            return new VotingResult
            {
                ConsensusText = string.Empty,
                Confidence = 0.0,
                AgreementScore = 0.0,
                TextRegions = new List<OcrTextRegion>()
            };
        }

        if (frameResults.Count == 1)
        {
            // Single frame - no voting needed
            var singleFrame = frameResults[0];
            return new VotingResult
            {
                ConsensusText = string.Join(" ", singleFrame.TextRegions.Select(r => r.Text)),
                Confidence = singleFrame.TextRegions.Any()
                    ? singleFrame.TextRegions.Average(r => r.Confidence)
                    : 0.0,
                AgreementScore = 1.0,
                TextRegions = singleFrame.TextRegions
            };
        }

        _logger?.LogInformation("Performing temporal voting on {Count} frames", frameResults.Count);

        // Step 1: Group text regions from different frames by spatial proximity (IoU)
        var regionGroups = GroupRegionsByLocation(frameResults);

        if (_verbose)
        {
            _logger?.LogDebug("Grouped {Total} regions into {Groups} spatial clusters",
                frameResults.Sum(f => f.TextRegions.Count), regionGroups.Count);
        }

        // Step 2: Perform character-level voting for each group
        var consensusRegions = new List<OcrTextRegion>();
        var allAgreementScores = new List<double>();

        foreach (var group in regionGroups)
        {
            var (consensusText, confidence, agreementScore) = VoteOnTextRegion(group);

            if (!string.IsNullOrWhiteSpace(consensusText))
            {
                // Use bounding box from highest-confidence region in group
                var bestRegion = group.OrderByDescending(r => r.Confidence).First();

                consensusRegions.Add(new OcrTextRegion
                {
                    Text = consensusText,
                    Confidence = confidence,
                    BoundingBox = bestRegion.BoundingBox
                });

                allAgreementScores.Add(agreementScore);
            }
        }

        // Step 3: Combine consensus regions into final text
        var consensusTextFull = string.Join(" ", consensusRegions.Select(r => r.Text));
        var avgConfidence = consensusRegions.Any() ? consensusRegions.Average(r => r.Confidence) : 0.0;
        var avgAgreement = allAgreementScores.Any() ? allAgreementScores.Average() : 0.0;

        _logger?.LogInformation(
            "Voting complete: {Regions} consensus regions, confidence={Confidence:F3}, agreement={Agreement:F3}",
            consensusRegions.Count, avgConfidence, avgAgreement);

        return new VotingResult
        {
            ConsensusText = consensusTextFull,
            Confidence = avgConfidence,
            AgreementScore = avgAgreement,
            TextRegions = consensusRegions
        };
    }

    /// <summary>
    /// Group text regions from different frames by spatial proximity using IoU.
    /// </summary>
    private List<List<OcrTextRegion>> GroupRegionsByLocation(List<FrameOcrResult> frameResults)
    {
        var allRegions = frameResults.SelectMany(f => f.TextRegions).ToList();
        var groups = new List<List<OcrTextRegion>>();
        var assigned = new HashSet<OcrTextRegion>();

        foreach (var region in allRegions)
        {
            if (assigned.Contains(region)) continue;

            // Start new group with this region
            var group = new List<OcrTextRegion> { region };
            assigned.Add(region);

            // Find all other regions that overlap with this one
            foreach (var other in allRegions)
            {
                if (assigned.Contains(other)) continue;

                var iou = ComputeIoU(region.BoundingBox, other.BoundingBox);
                if (iou >= _iouThreshold)
                {
                    group.Add(other);
                    assigned.Add(other);
                }
            }

            groups.Add(group);
        }

        return groups;
    }

    /// <summary>
    /// Perform character-level voting on a group of text regions.
    /// Returns (consensus text, confidence, agreement score).
    /// </summary>
    private (string ConsensusText, double Confidence, double AgreementScore) VoteOnTextRegion(
        List<OcrTextRegion> regions)
    {
        if (regions.Count == 0)
        {
            return (string.Empty, 0.0, 0.0);
        }

        if (regions.Count == 1)
        {
            return (regions[0].Text, regions[0].Confidence, 1.0);
        }

        // Find the longest text (assume it's most complete)
        var maxLength = regions.Max(r => r.Text.Length);

        var consensusChars = new char[maxLength];
        var confidences = new double[maxLength];
        var agreementScores = new List<double>();

        // Vote on each character position
        for (int pos = 0; pos < maxLength; pos++)
        {
            var votes = new Dictionary<char, double>(); // char -> weighted vote count

            foreach (var region in regions)
            {
                if (pos < region.Text.Length)
                {
                    var ch = region.Text[pos];
                    var weight = _confidenceWeighting ? region.Confidence : 1.0;

                    if (votes.ContainsKey(ch))
                    {
                        votes[ch] += weight;
                    }
                    else
                    {
                        votes[ch] = weight;
                    }
                }
            }

            if (votes.Count == 0)
            {
                // No votes for this position - use space
                consensusChars[pos] = ' ';
                confidences[pos] = 0.0;
                agreementScores.Add(0.0);
            }
            else
            {
                // Winner: character with most votes
                var winner = votes.OrderByDescending(kv => kv.Value).First();
                consensusChars[pos] = winner.Key;

                // Confidence: proportion of total votes for winner
                var totalVotes = votes.Values.Sum();
                var winnerConfidence = totalVotes > 0 ? winner.Value / totalVotes : 0.0;
                confidences[pos] = winnerConfidence;

                // Agreement: how many regions contributed to this position
                var contributingRegions = regions.Count(r => pos < r.Text.Length);
                var agreement = contributingRegions / (double)regions.Count;
                agreementScores.Add(agreement);
            }
        }

        var consensusText = new string(consensusChars).Trim();
        var avgConfidence = confidences.Length > 0 ? confidences.Average() : 0.0;
        var avgAgreement = agreementScores.Any() ? agreementScores.Average() : 0.0;

        if (_verbose)
        {
            _logger?.LogDebug(
                "Voted on region: '{Text}' (confidence={Conf:F3}, agreement={Agree:F3}, {Count} candidates)",
                consensusText, avgConfidence, avgAgreement, regions.Count);
        }

        return (consensusText, avgConfidence, avgAgreement);
    }

    /// <summary>
    /// Compute Intersection over Union (IoU) between two bounding boxes.
    /// Returns value from 0.0 (no overlap) to 1.0 (perfect overlap).
    /// </summary>
    private double ComputeIoU(BoundingBox box1, BoundingBox box2)
    {
        // Compute intersection rectangle
        var x1 = Math.Max(box1.X1, box2.X1);
        var y1 = Math.Max(box1.Y1, box2.Y1);
        var x2 = Math.Min(box1.X2, box2.X2);
        var y2 = Math.Min(box1.Y2, box2.Y2);

        if (x2 < x1 || y2 < y1)
        {
            // No intersection
            return 0.0;
        }

        var intersectionArea = (x2 - x1) * (y2 - y1);
        var box1Area = box1.Width * box1.Height;
        var box2Area = box2.Width * box2.Height;
        var unionArea = box1Area + box2Area - intersectionArea;

        return unionArea > 0 ? intersectionArea / (double)unionArea : 0.0;
    }
}

/// <summary>
/// OCR result from a single frame.
/// </summary>
public record FrameOcrResult
{
    /// <summary>
    /// Frame index in sequence.
    /// </summary>
    public required int FrameIndex { get; init; }

    /// <summary>
    /// Text regions detected in this frame.
    /// </summary>
    public required List<OcrTextRegion> TextRegions { get; init; }
}

/// <summary>
/// Result of temporal voting operation.
/// </summary>
public record VotingResult
{
    /// <summary>
    /// Consensus text from voting across all frames.
    /// </summary>
    public required string ConsensusText { get; init; }

    /// <summary>
    /// Average confidence score (0-1) for consensus text.
    /// </summary>
    public required double Confidence { get; init; }

    /// <summary>
    /// Agreement score (0-1) indicating how well frames agreed.
    /// Higher = more frames contributed to each character.
    /// </summary>
    public required double AgreementScore { get; init; }

    /// <summary>
    /// Consensus text regions with voted text.
    /// </summary>
    public required List<OcrTextRegion> TextRegions { get; init; }
}
