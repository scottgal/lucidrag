using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Mostlylucid.Shared.Entities;

public class EmailSubscriptionEntity
{
    [Key]

    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [StringLength(100)] public required string Token { get; set; }

    [StringLength(100)]
    public required string Email { get; set; }
    
    public SubscriptionType SubscriptionType { get; set; }

    [StringLength(2)]
    public string Language { get; set; } = Constants.EnglishLanguage;
    
    
    [StringLength(100)]
    
    public DateTimeOffset CreatedDate { get; set; } = DateTimeOffset.Now;
    
    public DateTimeOffset? LastSent { get; set; }
    
    public int? DayOfMonth { get; set; }
    
    [StringLength(10)]
    public string? Day { get; set; } 
    
    public List<CategoryEntity>? Categories { get; set; }
    
    public bool EmailConfirmed { get; set; } = false;
}