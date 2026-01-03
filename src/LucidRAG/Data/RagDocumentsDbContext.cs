using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using LucidRAG.Entities;

namespace LucidRAG.Data;

public class RagDocumentsDbContext(DbContextOptions<RagDocumentsDbContext> options) : DbContext(options)
{
    public DbSet<CollectionEntity> Collections => Set<CollectionEntity>();
    public DbSet<DocumentEntity> Documents => Set<DocumentEntity>();
    public DbSet<ExtractedEntity> Entities => Set<ExtractedEntity>();
    public DbSet<DocumentEntityLink> DocumentEntityLinks => Set<DocumentEntityLink>();
    public DbSet<EntityRelationship> EntityRelationships => Set<EntityRelationship>();
    public DbSet<ConversationEntity> Conversations => Set<ConversationEntity>();
    public DbSet<ConversationMessage> ConversationMessages => Set<ConversationMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Apply DateTimeOffset converters for SQLite compatibility
        if (Database.IsSqlite())
        {
            ApplySqliteDateTimeOffsetConverters(modelBuilder);
        }

        var isSqlite = Database.IsSqlite();

        // Collection
        modelBuilder.Entity<CollectionEntity>(entity =>
        {
            entity.ToTable("collections");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(255).IsRequired();
            if (!isSqlite) entity.Property(e => e.Settings).HasColumnType("jsonb");
            entity.HasIndex(e => e.Name);
        });

        // Document
        modelBuilder.Entity<DocumentEntity>(entity =>
        {
            entity.ToTable("documents");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(500).IsRequired();
            entity.Property(e => e.OriginalFilename).HasMaxLength(500);
            entity.Property(e => e.ContentHash).HasMaxLength(64).IsRequired();
            entity.Property(e => e.FilePath).HasMaxLength(1000);
            entity.Property(e => e.MimeType).HasMaxLength(100);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(50);
            if (!isSqlite) entity.Property(e => e.Metadata).HasColumnType("jsonb");

            entity.Property(e => e.SourceUrl).HasMaxLength(2000);

            entity.HasIndex(e => e.CollectionId);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.ContentHash);
            entity.HasIndex(e => e.SourceUrl);

            entity.HasOne(e => e.Collection)
                .WithMany(c => c.Documents)
                .HasForeignKey(e => e.CollectionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ExtractedEntity
        modelBuilder.Entity<ExtractedEntity>(entity =>
        {
            entity.ToTable("entities");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.CanonicalName).HasMaxLength(500).IsRequired();
            entity.Property(e => e.EntityType).HasMaxLength(50).IsRequired();
            if (!isSqlite) entity.Property(e => e.Aliases).HasColumnType("text[]");

            entity.HasIndex(e => e.EntityType);
            entity.HasIndex(e => new { e.CanonicalName, e.EntityType }).IsUnique();
        });

        // DocumentEntityLink (junction table)
        modelBuilder.Entity<DocumentEntityLink>(entity =>
        {
            entity.ToTable("document_entities");
            entity.HasKey(e => new { e.DocumentId, e.EntityId });
            if (!isSqlite) entity.Property(e => e.SegmentIds).HasColumnType("text[]");

            entity.HasOne(e => e.Document)
                .WithMany(d => d.EntityLinks)
                .HasForeignKey(e => e.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Entity)
                .WithMany(e => e.DocumentLinks)
                .HasForeignKey(e => e.EntityId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // EntityRelationship
        modelBuilder.Entity<EntityRelationship>(entity =>
        {
            entity.ToTable("entity_relationships");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.RelationshipType).HasMaxLength(100).IsRequired();
            if (!isSqlite) entity.Property(e => e.SourceDocuments).HasColumnType("uuid[]");

            entity.HasIndex(e => e.SourceEntityId);
            entity.HasIndex(e => e.TargetEntityId);
            entity.HasIndex(e => e.RelationshipType);

            entity.HasOne(e => e.SourceEntity)
                .WithMany(e => e.OutgoingRelationships)
                .HasForeignKey(e => e.SourceEntityId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.TargetEntity)
                .WithMany(e => e.IncomingRelationships)
                .HasForeignKey(e => e.TargetEntityId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Conversation
        modelBuilder.Entity<ConversationEntity>(entity =>
        {
            entity.ToTable("conversations");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Title).HasMaxLength(255);

            entity.HasOne(e => e.Collection)
                .WithMany(c => c.Conversations)
                .HasForeignKey(e => e.CollectionId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // ConversationMessage
        modelBuilder.Entity<ConversationMessage>(entity =>
        {
            entity.ToTable("conversation_messages");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Role).HasMaxLength(20).IsRequired();
            entity.Property(e => e.Content).IsRequired();
            if (!isSqlite) entity.Property(e => e.Metadata).HasColumnType("jsonb");

            entity.HasOne(e => e.Conversation)
                .WithMany(c => c.Messages)
                .HasForeignKey(e => e.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ApplySqliteDateTimeOffsetConverters(ModelBuilder modelBuilder)
    {
        // SQLite doesn't support DateTimeOffset in ORDER BY clauses
        // Convert DateTimeOffset to/from ticks (long) for sorting compatibility
        var dateTimeOffsetConverter = new ValueConverter<DateTimeOffset, long>(
            v => v.ToUnixTimeMilliseconds(),
            v => DateTimeOffset.FromUnixTimeMilliseconds(v));

        var nullableDateTimeOffsetConverter = new ValueConverter<DateTimeOffset?, long?>(
            v => v.HasValue ? v.Value.ToUnixTimeMilliseconds() : null,
            v => v.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(v.Value) : null);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTimeOffset))
                {
                    property.SetValueConverter(dateTimeOffsetConverter);
                }
                else if (property.ClrType == typeof(DateTimeOffset?))
                {
                    property.SetValueConverter(nullableDateTimeOffsetConverter);
                }
            }
        }
    }
}
