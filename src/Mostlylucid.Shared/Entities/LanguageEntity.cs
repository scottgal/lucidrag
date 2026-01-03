using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Mostlylucid.Shared.Entities;

public class LanguageEntity
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }
 
    public string Name { get; set; }
    public ICollection<BlogPostEntity> BlogPosts { get; set; }
}