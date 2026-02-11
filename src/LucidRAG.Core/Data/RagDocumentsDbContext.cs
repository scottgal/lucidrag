using LucidRAG.Entities;
using LucidRAG.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace LucidRAG.Data;

public class RagDocumentsDbContext(DbContextOptions<RagDocumentsDbContext> options)
    : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<CollectionEntity> Collections => Set<CollectionEntity>();
    public DbSet<FolderEntity> Folders => Set<FolderEntity>();
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

    // Salient terms for autocomplete
    public DbSet<CollectionSalientTerm> SalientTerms => Set<CollectionSalientTerm>();

    // Feature embeddings for semantic similarity (pgvector)
    public DbSet<FeatureEmbedding> FeatureEmbeddings => Set<FeatureEmbedding>();

    // Intra-document segment graphs
    public DbSet<SegmentLink> SegmentLinks => Set<SegmentLink>();

    // SaaS API keys
    public DbSet<ApiKeyEntity> ApiKeys => Set<ApiKeyEntity>();
    public DbSet<ApiKeyIndexingSource> ApiKeyIndexingSources => Set<ApiKeyIndexingSource>();
    public DbSet<ApiKeyReadDomain> ApiKeyReadDomains => Set<ApiKeyReadDomain>();

    // SaaS analytics
    public DbSet<SaasQueryLogEntity> SaasQueryLogs => Set<SaasQueryLogEntity>();
    public DbSet<SaasUsageRollupEntity> SaasUsageRollups => Set<SaasUsageRollupEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Apply DateTimeOffset converters for SQLite compatibility
        if (Database.IsSqlite())
            ApplySqliteDateTimeOffsetConverters(modelBuilder);
        else
            // Enable pgvector extension for PostgreSQL
            modelBuilder.HasPostgresExtension("vector");

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

        // Folder - Virtual folders for organizing documents within collections
        modelBuilder.Entity<FolderEntity>(entity =>
        {
            entity.ToTable("folders");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(255).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(1000);

            // Indexes for efficient queries
            entity.HasIndex(e => e.CollectionId);
            entity.HasIndex(e => e.ParentFolderId);
            entity.HasIndex(e => new { e.CollectionId, e.ParentFolderId, e.Name }).IsUnique();

            // Relationships
            entity.HasOne(e => e.Collection)
                .WithMany()
                .HasForeignKey(e => e.CollectionId)
                .OnDelete(DeleteBehavior.Cascade);

            // Self-referencing hierarchy for nested folders
            entity.HasOne(e => e.ParentFolder)
                .WithMany(f => f.ChildFolders)
                .HasForeignKey(e => e.ParentFolderId)
                .OnDelete(DeleteBehavior.Restrict); // Prevent cascading deletes through hierarchy
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
            entity.HasIndex(e => e.FolderId);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.ContentHash);
            entity.HasIndex(e => e.SourceUrl);

            entity.HasOne(e => e.Collection)
                .WithMany(c => c.Documents)
                .HasForeignKey(e => e.CollectionId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Folder)
                .WithMany(f => f.Documents)
                .HasForeignKey(e => e.FolderId)
                .OnDelete(DeleteBehavior.SetNull); // Documents move to root when folder deleted
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
            entity.Property(e => e.SegmentHash).HasMaxLength(32);
            entity.Property(e => e.ProducerSource).HasMaxLength(128);
            entity.Property(e => e.ProducerVersion).HasMaxLength(32);
            if (!isSqlite) entity.Property(e => e.Metadata).HasColumnType("jsonb");

            // Indexes
            entity.HasIndex(e => e.EntityId);
            entity.HasIndex(e => e.ArtifactType);
            entity.HasIndex(e => e.ContentHash);
            entity.HasIndex(e => e.SegmentHash); // Fast lookup for RAG text hydration
            entity.HasIndex(e => new { e.EntityId, e.ArtifactType });

            // GIN index on JSONB metadata for efficient signal filtering
            // Enables fast queries on decomposed prompts like:
            // - "all yellow images" -> WHERE metadata @> '{"dominantColor": "yellow"}'
            // - "high salience segments" -> WHERE (metadata->>'salienceScore')::float > 0.8
            // - "introduction sections" -> WHERE metadata->>'sectionTitle' ILIKE '%introduction%'
            if (!isSqlite)
                entity.HasIndex(e => e.Metadata)
                    .HasMethod("gin");

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
            if (!isSqlite) entity.Property(e => e.Options).HasColumnType("jsonb");

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
            if (!isSqlite) entity.Property(e => e.Errors).HasColumnType("jsonb");

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
            entity.HasIndex(e => e.CollectionId);
            entity.HasIndex(e => new { e.CollectionId, e.Name }).IsUnique(); // Unique names per collection/tenant
            entity.HasIndex(e => e.Level);
            entity.HasIndex(e => e.ParentCommunityId);
            entity.HasIndex(e => e.EntityCount);

            // Relationships - CollectionId is nullable for global communities
            entity.HasOne(e => e.Collection)
                .WithMany()
                .HasForeignKey(e => e.CollectionId)
                .OnDelete(DeleteBehavior.SetNull);

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

        // CollectionSalientTerm - Pre-computed autocomplete terms
        modelBuilder.Entity<CollectionSalientTerm>(entity =>
        {
            entity.ToTable("collection_salient_terms");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Term).HasMaxLength(200).IsRequired();
            entity.Property(e => e.NormalizedTerm).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Source).HasMaxLength(32).IsRequired();

            // Indexes for fast autocomplete lookups
            entity.HasIndex(e => new { e.CollectionId, e.NormalizedTerm });
            entity.HasIndex(e => new { e.CollectionId, e.Score });
            entity.HasIndex(e => e.UpdatedAt);

            entity.HasOne(e => e.Collection)
                .WithMany()
                .HasForeignKey(e => e.CollectionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // FeatureEmbedding - pgvector semantic similarity
        modelBuilder.Entity<FeatureEmbedding>(entity =>
        {
            entity.ToTable("feature_embeddings");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.FeatureText).HasMaxLength(512).IsRequired();
            entity.Property(e => e.NormalizedText).HasMaxLength(512).IsRequired();
            entity.Property(e => e.FeatureType).HasMaxLength(64).IsRequired();
            entity.Property(e => e.EmbeddingModel).HasMaxLength(128);

            // pgvector column - only for PostgreSQL
            if (isSqlite)
                // SQLite doesn't support pgvector - ignore the Vector property entirely
                entity.Ignore(e => e.Embedding);
            else
                entity.Property(e => e.Embedding).HasColumnType("vector(384)");

            if (!isSqlite) entity.Property(e => e.Metadata).HasColumnType("jsonb");

            // Indexes for efficient queries
            entity.HasIndex(e => new { e.CollectionId, e.NormalizedText, e.FeatureType }).IsUnique();
            entity.HasIndex(e => new { e.CollectionId, e.FeatureType });
            entity.HasIndex(e => e.UpdatedAt);

            // HNSW index for pgvector similarity search - created via migration SQL
            // CREATE INDEX ON feature_embeddings USING hnsw (embedding vector_cosine_ops);

            entity.HasOne(e => e.Collection)
                .WithMany()
                .HasForeignKey(e => e.CollectionId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // SegmentLink - Intra-document segment graph
        modelBuilder.Entity<SegmentLink>(entity =>
        {
            entity.ToTable("segment_links");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.SourceSegmentHash).HasMaxLength(64).IsRequired();
            entity.Property(e => e.TargetSegmentHash).HasMaxLength(64).IsRequired();
            entity.Property(e => e.LinkType).HasMaxLength(32).IsRequired();

            if (!isSqlite) entity.Property(e => e.Metadata).HasColumnType("jsonb");

            // Indexes for efficient graph traversal
            entity.HasIndex(e => e.DocumentId);
            entity.HasIndex(e => e.SourceSegmentHash);
            entity.HasIndex(e => e.TargetSegmentHash);
            entity.HasIndex(e => new { e.SourceSegmentHash, e.TargetSegmentHash }).IsUnique();

            entity.HasOne(e => e.Document)
                .WithMany()
                .HasForeignKey(e => e.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ApiKeyEntity - SaaS API keys
        modelBuilder.Entity<ApiKeyEntity>(entity =>
        {
            entity.ToTable("api_keys");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.KeyPrefix).HasMaxLength(16).IsRequired();
            entity.Property(e => e.KeyHash).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.Property(e => e.UserId).HasMaxLength(450);
            entity.Property(e => e.NormalizedOwnerEmail).HasMaxLength(320);
            entity.Property(e => e.Plan).HasMaxLength(20).IsRequired();

            entity.HasIndex(e => e.KeyHash).IsUnique();
            entity.HasIndex(e => e.KeyPrefix).IsUnique();
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.NormalizedOwnerEmail);

            entity.HasOne(e => e.Collection)
                .WithMany()
                .HasForeignKey(e => e.CollectionId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // ApiKeyIndexingSource - 1 per key
        modelBuilder.Entity<ApiKeyIndexingSource>(entity =>
        {
            entity.ToTable("api_key_indexing_sources");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.SourceType).HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.Property(e => e.SourceValue).HasMaxLength(500).IsRequired();
            entity.Property(e => e.CrawlStatus).HasMaxLength(20);

            entity.HasIndex(e => e.ApiKeyId).IsUnique();

            entity.HasOne(e => e.ApiKey)
                .WithOne(k => k.IndexingSource)
                .HasForeignKey<ApiKeyIndexingSource>(e => e.ApiKeyId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ApiKeyReadDomain - up to 5 per key
        modelBuilder.Entity<ApiKeyReadDomain>(entity =>
        {
            entity.ToTable("api_key_read_domains");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Domain).HasMaxLength(253).IsRequired();

            entity.HasIndex(e => new { e.ApiKeyId, e.Domain }).IsUnique();

            entity.HasOne(e => e.ApiKey)
                .WithMany(k => k.ReadDomains)
                .HasForeignKey(e => e.ApiKeyId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // SaasQueryLogEntity - per-query audit log
        modelBuilder.Entity<SaasQueryLogEntity>(entity =>
        {
            entity.ToTable("saas_query_logs");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.QueryText).HasMaxLength(2000).IsRequired();
            entity.Property(e => e.QueryType).HasMaxLength(20).IsRequired();
            entity.Property(e => e.SearchMode).HasMaxLength(20);
            entity.Property(e => e.ErrorCode).HasMaxLength(100);
            entity.Property(e => e.RequestDomain).HasMaxLength(253);
            entity.Property(e => e.CountryCode).HasMaxLength(2);
            entity.Property(e => e.UserAgent).HasMaxLength(500);

            entity.HasIndex(e => new { e.ApiKeyId, e.CreatedAt });
            entity.HasIndex(e => e.CountryCode);
            entity.HasIndex(e => e.QueryType);

            entity.HasOne(e => e.ApiKey)
                .WithMany()
                .HasForeignKey(e => e.ApiKeyId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // SaasUsageRollupEntity - pre-aggregated daily stats
        modelBuilder.Entity<SaasUsageRollupEntity>(entity =>
        {
            entity.ToTable("saas_usage_rollups");
            entity.HasKey(e => e.Id);

            entity.HasIndex(e => new { e.ApiKeyId, e.Date }).IsUnique();

            entity.HasOne(e => e.ApiKey)
                .WithMany()
                .HasForeignKey(e => e.ApiKeyId)
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
        foreach (var property in entityType.GetProperties())
            if (property.ClrType == typeof(DateTimeOffset))
                property.SetValueConverter(dateTimeOffsetConverter);
            else if (property.ClrType == typeof(DateTimeOffset?))
                property.SetValueConverter(nullableDateTimeOffsetConverter);
    }
}