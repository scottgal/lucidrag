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

    // Cross-modal retrieval entities
    public DbSet<RetrievalEntityRecord> RetrievalEntities => Set<RetrievalEntityRecord>();
    public DbSet<EntityEmbedding> EntityEmbeddings => Set<EntityEmbedding>();

    // Evidence repository
    public DbSet<EvidenceArtifact> EvidenceArtifacts => Set<EvidenceArtifact>();

    // Scanned page grouping
    public DbSet<ScannedPageGroup> ScannedPageGroups => Set<ScannedPageGroup>();
    public DbSet<ScannedPageMembership> ScannedPageMemberships => Set<ScannedPageMembership>();

    // Ingestion sources and jobs
    public DbSet<IngestionSourceEntity> IngestionSources => Set<IngestionSourceEntity>();
    public DbSet<IngestionJobEntity> IngestionJobs => Set<IngestionJobEntity>();

    // Community detection
    public DbSet<CommunityEntity> Communities => Set<CommunityEntity>();
    public DbSet<CommunityMembership> CommunityMemberships => Set<CommunityMembership>();

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

        // RetrievalEntityRecord - Cross-modal entities (document, image, audio, video, data)
        modelBuilder.Entity<RetrievalEntityRecord>(entity =>
        {
            entity.ToTable("retrieval_entities");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ContentType).HasMaxLength(32).IsRequired();
            entity.Property(e => e.Source).HasMaxLength(2048).IsRequired();
            entity.Property(e => e.ContentHash).HasMaxLength(64);
            entity.Property(e => e.Title).HasMaxLength(512);
            entity.Property(e => e.Summary).HasMaxLength(4000);
            entity.Property(e => e.EmbeddingModel).HasMaxLength(128);
            entity.Property(e => e.ReviewReason).HasMaxLength(1000);

            // JSON columns (PostgreSQL: jsonb, SQLite: text)
            if (!isSqlite)
            {
                entity.Property(e => e.Tags).HasColumnType("jsonb");
                entity.Property(e => e.Metadata).HasColumnType("jsonb");
                entity.Property(e => e.CustomMetadata).HasColumnType("jsonb");
                entity.Property(e => e.Signals).HasColumnType("jsonb");
                entity.Property(e => e.ExtractedEntities).HasColumnType("jsonb");
                entity.Property(e => e.Relationships).HasColumnType("jsonb");
                entity.Property(e => e.SourceModalities).HasColumnType("jsonb");
                entity.Property(e => e.ProcessingState).HasColumnType("jsonb");
            }

            // Indexes for common queries
            entity.HasIndex(e => e.ContentType);
            entity.HasIndex(e => e.CollectionId);
            entity.HasIndex(e => e.ContentHash);
            entity.HasIndex(e => e.NeedsReview);
            entity.HasIndex(e => e.CreatedAt);
            entity.HasIndex(e => new { e.CollectionId, e.ContentType });

            entity.HasOne(e => e.Collection)
                .WithMany()
                .HasForeignKey(e => e.CollectionId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // EntityEmbedding - Multi-vector storage for cross-modal search
        modelBuilder.Entity<EntityEmbedding>(entity =>
        {
            entity.ToTable("entity_embeddings");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Model).HasMaxLength(128);
            if (!isSqlite) entity.Property(e => e.Vector).HasColumnType("jsonb");

            // Indexes
            entity.HasIndex(e => e.EntityId);
            entity.HasIndex(e => new { e.EntityId, e.Name }).IsUnique();

            entity.HasOne(e => e.Entity)
                .WithMany(e => e.Embeddings)
                .HasForeignKey(e => e.EntityId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // EvidenceArtifact - Evidence storage for entities
        modelBuilder.Entity<EvidenceArtifact>(entity =>
        {
            entity.ToTable("evidence_artifacts");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ArtifactType).HasMaxLength(64).IsRequired();
            entity.Property(e => e.MimeType).HasMaxLength(128).IsRequired();
            entity.Property(e => e.StorageBackend).HasMaxLength(32).IsRequired();
            entity.Property(e => e.StoragePath).HasMaxLength(2048).IsRequired();
            entity.Property(e => e.ContentHash).HasMaxLength(64);
            entity.Property(e => e.ProducerSource).HasMaxLength(128);
            entity.Property(e => e.ProducerVersion).HasMaxLength(32);
            if (!isSqlite) entity.Property(e => e.Metadata).HasColumnType("jsonb");

            // Indexes
            entity.HasIndex(e => e.EntityId);
            entity.HasIndex(e => e.ArtifactType);
            entity.HasIndex(e => e.ContentHash);
            entity.HasIndex(e => new { e.EntityId, e.ArtifactType });

            entity.HasOne(e => e.Entity)
                .WithMany(e => e.EvidenceArtifacts)
                .HasForeignKey(e => e.EntityId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ScannedPageGroup - Groups scanned pages into documents
        modelBuilder.Entity<ScannedPageGroup>(entity =>
        {
            entity.ToTable("scanned_page_groups");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.GroupName).HasMaxLength(512).IsRequired();
            entity.Property(e => e.GroupingStrategy).HasMaxLength(32).IsRequired();
            entity.Property(e => e.FilenamePattern).HasMaxLength(256);
            entity.Property(e => e.DirectoryPath).HasMaxLength(1024);
            if (!isSqlite) entity.Property(e => e.Metadata).HasColumnType("jsonb");

            // Indexes
            entity.HasIndex(e => e.CollectionId);
            entity.HasIndex(e => e.GroupingStrategy);

            entity.HasOne(e => e.Collection)
                .WithMany()
                .HasForeignKey(e => e.CollectionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ScannedPageMembership - Junction table for page groupings
        modelBuilder.Entity<ScannedPageMembership>(entity =>
        {
            entity.ToTable("scanned_page_memberships");
            entity.HasKey(e => new { e.GroupId, e.EntityId });
            entity.Property(e => e.OriginalFilename).HasMaxLength(512);

            // Indexes
            entity.HasIndex(e => e.EntityId);

            entity.HasOne(e => e.Group)
                .WithMany(g => g.Pages)
                .HasForeignKey(e => e.GroupId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Entity)
                .WithMany(e => e.PageMemberships)
                .HasForeignKey(e => e.EntityId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // IngestionSourceEntity - Registered ingestion sources
        modelBuilder.Entity<IngestionSourceEntity>(entity =>
        {
            entity.ToTable("ingestion_sources");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(255).IsRequired();
            entity.Property(e => e.SourceType).HasMaxLength(32).IsRequired();
            entity.Property(e => e.Location).HasMaxLength(2048).IsRequired();
            entity.Property(e => e.FilePattern).HasMaxLength(256);
            entity.Property(e => e.Credentials).HasMaxLength(4096);
            if (!isSqlite)
            {
                entity.Property(e => e.Options).HasColumnType("jsonb");
            }

            // Indexes
            entity.HasIndex(e => e.SourceType);
            entity.HasIndex(e => e.IsEnabled);
            entity.HasIndex(e => e.Name);

            entity.HasOne(e => e.Collection)
                .WithMany()
                .HasForeignKey(e => e.CollectionId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // IngestionJobEntity - Ingestion job records
        modelBuilder.Entity<IngestionJobEntity>(entity =>
        {
            entity.ToTable("ingestion_jobs");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Status).HasMaxLength(32).IsRequired();
            if (!isSqlite)
            {
                entity.Property(e => e.Errors).HasColumnType("jsonb");
            }

            // Indexes
            entity.HasIndex(e => e.SourceId);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.CreatedAt);

            entity.HasOne(e => e.Source)
                .WithMany(s => s.Jobs)
                .HasForeignKey(e => e.SourceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // CommunityEntity - Detected communities in the knowledge graph
        modelBuilder.Entity<CommunityEntity>(entity =>
        {
            entity.ToTable("communities");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(512).IsRequired();
            entity.Property(e => e.Algorithm).HasMaxLength(64);
            if (!isSqlite)
            {
                entity.Property(e => e.Features).HasColumnType("jsonb");
                entity.Property(e => e.Embedding).HasColumnType("jsonb");
            }

            // Indexes
            entity.HasIndex(e => e.Level);
            entity.HasIndex(e => e.ParentCommunityId);
            entity.HasIndex(e => e.EntityCount);

            // Self-referencing hierarchy
            entity.HasOne(e => e.ParentCommunity)
                .WithMany(e => e.ChildCommunities)
                .HasForeignKey(e => e.ParentCommunityId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // CommunityMembership - Entity membership in communities
        modelBuilder.Entity<CommunityMembership>(entity =>
        {
            entity.ToTable("community_memberships");
            entity.HasKey(e => new { e.CommunityId, e.EntityId });

            // Indexes
            entity.HasIndex(e => e.EntityId);
            entity.HasIndex(e => e.Centrality);

            entity.HasOne(e => e.Community)
                .WithMany(c => c.Members)
                .HasForeignKey(e => e.CommunityId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Entity)
                .WithMany()
                .HasForeignKey(e => e.EntityId)
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
