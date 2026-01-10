using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LucidRAG.Entities;

/// <summary>
/// Groups scanned pages into logical documents.
/// Supports filename pattern, directory, manual, and standalone grouping.
/// </summary>
public class ScannedPageGroup
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// Optional collection this group belongs to.
    /// </summary>
    public Guid? CollectionId { get; set; }

    /// <summary>
    /// Human-readable name for the group (e.g., "Invoice 2024-001").
    /// </summary>
    [Required]
    [MaxLength(512)]
    public required string GroupName { get; set; }

    /// <summary>
    /// How pages are grouped (see GroupingStrategies constants).
    /// </summary>
    [Required]
    [MaxLength(32)]
    public required string GroupingStrategy { get; set; }

    /// <summary>
    /// Regex pattern for filename-based grouping.
    /// Example: "invoice_(\d+)_page_\d+\.jpg" matches "invoice_001_page_1.jpg"
    /// </summary>
    [MaxLength(256)]
    public string? FilenamePattern { get; set; }

    /// <summary>
    /// Directory path for directory-based grouping.
    /// </summary>
    [MaxLength(1024)]
    public string? DirectoryPath { get; set; }

    /// <summary>
    /// Additional configuration (JSON).
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string? Metadata { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Navigation
    public CollectionEntity? Collection { get; set; }
    public ICollection<ScannedPageMembership> Pages { get; set; } = [];
}

/// <summary>
/// Junction table linking scanned page entities to groups.
/// </summary>
public class ScannedPageMembership
{
    /// <summary>
    /// The group this page belongs to.
    /// </summary>
    public Guid GroupId { get; set; }

    /// <summary>
    /// The entity representing this scanned page.
    /// </summary>
    public Guid EntityId { get; set; }

    /// <summary>
    /// Page order within the group (1-indexed for display).
    /// </summary>
    public int PageNumber { get; set; }

    /// <summary>
    /// Original filename of the scanned page.
    /// </summary>
    [MaxLength(512)]
    public string? OriginalFilename { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Navigation
    public ScannedPageGroup? Group { get; set; }
    public RetrievalEntityRecord? Entity { get; set; }
}

/// <summary>
/// Page grouping strategies.
/// </summary>
public static class GroupingStrategies
{
    /// <summary>
    /// Group by common filename prefix/pattern.
    /// Example: "invoice_001_page_*.jpg" → one document.
    /// </summary>
    public const string FilenamePattern = "filename_pattern";

    /// <summary>
    /// All files in the same directory form one document.
    /// </summary>
    public const string Directory = "directory";

    /// <summary>
    /// User manually specifies which pages go together.
    /// </summary>
    public const string Manual = "manual";

    /// <summary>
    /// Each page is its own standalone document.
    /// </summary>
    public const string Standalone = "standalone";
}
