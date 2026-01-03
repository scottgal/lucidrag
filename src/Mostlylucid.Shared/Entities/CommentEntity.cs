using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Mostlylucid.Shared.Entities;


public class CommentEntity
{
    [Key]
 
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }
    
  
    public CommentStatus Status { get; set; } = CommentStatus.Pending;
    

    public string Author { get; set; }
    

    public string? HtmlContent { get; set; }
    

    public string Content { get; set; }
    
  
    public DateTime CreatedAt { get; set; }

    [NotMapped]
    public int CurrentDepth { get; set; } = 0;
    
    // Foreign key for the post this comment belongs to
    public int PostId { get; set; }
    
    public BlogPostEntity Post { get; set; }

    [Column("parent_comment_id")]
    public int? ParentCommentId { get; set; }
    public CommentEntity ParentComment { get; set; } 
    
    // Navigation property for the closure table relationships
    public ICollection<CommentClosure> Ancestors { get; set; } = new List<CommentClosure>();
    public ICollection<CommentClosure> Descendants { get; set; }= new List<CommentClosure>();
}



[Table("comment_closures")]
public class CommentClosure
{
    [Column("ancestor_id")]
    public int AncestorId { get; set; }
    [Column("descendant_id")]
    public CommentEntity Ancestor { get; set; }

    [Column("descendant_id")]
    public int DescendantId { get; set; }
    public CommentEntity Descendant { get; set; }

    [Column("depth")]
    public int Depth { get; set; } // 0 means direct parent-child, 1 means grandparent-grandchild, etc.
}