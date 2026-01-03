using Mostlylucid.Shared.Entities;

namespace Mostlylucid.Shared.Models.EmailSubscription;

public  class EmailSubscriptionModel
{
   
    
    public int Id { get; set; }
    
    public required string Token { get; set; }
    
    public SubscriptionType SubscriptionType { get; set; }

    public string Language { get; set; } = Constants.EnglishLanguage;
    
    public required string Email { get; set; } 
    
    public DateTimeOffset CreatedDate { get; set; } = DateTimeOffset.Now;
    
    public DateTimeOffset? LastSent { get; set; }
    
    public List<string>? Categories { get; set; }
    
    public int? DayOfMonth { get; set; }
    
    public string?  Day { get; set; } 
    
    public bool EmailConfirmed { get; set; } = false;

    public override string ToString()
    {
        return $"Id: {Id}, Token: {Token}, SubscriptionType: {SubscriptionType}, Language: {Language}," +
               $" Email: {Email}, CreatedDate: {CreatedDate}, LastSent: {LastSent}, Categories: {Categories}," +
               $" DayOfMonth: {DayOfMonth}, Day: {Day}, EmailConfirmed: {EmailConfirmed}";
    }
}