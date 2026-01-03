using Mostlylucid.Shared.Entities;
using Mostlylucid.Shared.Models.EmailSubscription;

namespace Mostlylucid.Shared.Models.EmailSubscription;

public static class Mapper
{
     public static EmailSubscriptionModel FromEntity(this EmailSubscriptionEntity entity)
    {
        return new EmailSubscriptionModel
        {
            Id = entity.Id,
            Token = entity.Token,
            SubscriptionType = entity.SubscriptionType,
            Language = entity.Language,
            Email = entity.Email,
            CreatedDate = entity.CreatedDate,
            Day = entity.Day,
            DayOfMonth = entity.DayOfMonth,
            LastSent = entity.LastSent,
            Categories = entity.Categories?.Select(c => c.Name).ToList(),
            EmailConfirmed = entity.EmailConfirmed
        };
    }
    
    public static EmailSubscriptionEntity ToEntity(this EmailSubscriptionModel model)
    {
        return new EmailSubscriptionEntity
        {
            Id = model.Id,
            Token = model.Token,
            SubscriptionType = model.SubscriptionType,
            Language = model.Language,
            Email = model.Email,
            CreatedDate = model.CreatedDate,
            LastSent = model.LastSent,
            Categories = model.Categories?.Select(c => new CategoryEntity { Name = c }).ToList(),
            EmailConfirmed = model.EmailConfirmed,
            Day = model.Day
        };
    }
}