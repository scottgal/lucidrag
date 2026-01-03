using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Mostlylucid.Shared.Entities;


public class CategoryEntity
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    

    public int Id { get; set; }
    

    public string Name { get; set; }
    public ICollection<BlogPostEntity> BlogPosts { get; set; }
    
    public ICollection<EmailSubscriptionEntity> EmailSubscriptions { get; set; }
}