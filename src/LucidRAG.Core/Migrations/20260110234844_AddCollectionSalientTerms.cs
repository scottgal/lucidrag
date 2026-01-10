using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LucidRAG.Migrations
{
    /// <inheritdoc />
    public partial class AddCollectionSalientTerms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AspNetRoles",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUsers",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    DisplayName = table.Column<string>(type: "text", nullable: true),
                    AvatarUrl = table.Column<string>(type: "text", nullable: true),
                    TenantId = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastLoginAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    EmailConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: true),
                    SecurityStamp = table.Column<string>(type: "text", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "text", nullable: true),
                    PhoneNumber = table.Column<string>(type: "text", nullable: true),
                    PhoneNumberConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    TwoFactorEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    LockoutEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LockoutEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    AccessFailedCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "collections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Settings = table.Column<string>(type: "jsonb", nullable: true),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_collections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "communities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Summary = table.Column<string>(type: "text", nullable: true),
                    Features = table.Column<string>(type: "jsonb", nullable: true),
                    Algorithm = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Level = table.Column<int>(type: "integer", nullable: false),
                    ParentCommunityId = table.Column<Guid>(type: "uuid", nullable: true),
                    EntityCount = table.Column<int>(type: "integer", nullable: false),
                    Cohesion = table.Column<float>(type: "real", nullable: false),
                    Embedding = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_communities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_communities_communities_ParentCommunityId",
                        column: x => x.ParentCommunityId,
                        principalTable: "communities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "entities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CanonicalName = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    EntityType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Aliases = table.Column<string[]>(type: "text[]", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_entities", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetRoleClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RoleId = table.Column<string>(type: "text", nullable: false),
                    ClaimType = table.Column<string>(type: "text", nullable: true),
                    ClaimValue = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoleClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetRoleClaims_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    ClaimType = table.Column<string>(type: "text", nullable: true),
                    ClaimValue = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetUserClaims_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserLogins",
                columns: table => new
                {
                    LoginProvider = table.Column<string>(type: "text", nullable: false),
                    ProviderKey = table.Column<string>(type: "text", nullable: false),
                    ProviderDisplayName = table.Column<string>(type: "text", nullable: true),
                    UserId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserLogins", x => new { x.LoginProvider, x.ProviderKey });
                    table.ForeignKey(
                        name: "FK_AspNetUserLogins_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserRoles",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "text", nullable: false),
                    RoleId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserRoles", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserTokens",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "text", nullable: false),
                    LoginProvider = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserTokens", x => new { x.UserId, x.LoginProvider, x.Name });
                    table.ForeignKey(
                        name: "FK_AspNetUserTokens_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "collection_salient_terms",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CollectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Term = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    NormalizedTerm = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Score = table.Column<double>(type: "double precision", nullable: false),
                    Source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DocumentFrequency = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_collection_salient_terms", x => x.Id);
                    table.ForeignKey(
                        name: "FK_collection_salient_terms_collections_CollectionId",
                        column: x => x.CollectionId,
                        principalTable: "collections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "conversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CollectionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Title = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conversations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_conversations_collections_CollectionId",
                        column: x => x.CollectionId,
                        principalTable: "collections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "documents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CollectionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    OriginalFilename = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FilePath = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    FileSizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    MimeType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    StatusMessage = table.Column<string>(type: "text", nullable: true),
                    ProcessingProgress = table.Column<float>(type: "real", nullable: false),
                    SegmentCount = table.Column<int>(type: "integer", nullable: false),
                    EntityCount = table.Column<int>(type: "integer", nullable: false),
                    TableCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Metadata = table.Column<string>(type: "jsonb", nullable: true),
                    SourceUrl = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    SourcePath = table.Column<string>(type: "text", nullable: true),
                    SourceCreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SourceModifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    VectorStoreDocId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_documents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_documents_collections_CollectionId",
                        column: x => x.CollectionId,
                        principalTable: "collections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ingestion_sources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    SourceType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Location = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    FilePattern = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Recursive = table.Column<bool>(type: "boolean", nullable: false),
                    CollectionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Options = table.Column<string>(type: "jsonb", nullable: true),
                    Credentials = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSyncAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TotalItemsIngested = table.Column<int>(type: "integer", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ingestion_sources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ingestion_sources_collections_CollectionId",
                        column: x => x.CollectionId,
                        principalTable: "collections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "retrieval_entities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ContentType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Source = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CollectionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Title = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Summary = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    TextContent = table.Column<string>(type: "text", nullable: true),
                    EmbeddingModel = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    QualityScore = table.Column<double>(type: "double precision", nullable: false),
                    ContentConfidence = table.Column<double>(type: "double precision", nullable: false),
                    NeedsReview = table.Column<bool>(type: "boolean", nullable: false),
                    ReviewReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Tags = table.Column<string>(type: "jsonb", nullable: true),
                    Metadata = table.Column<string>(type: "jsonb", nullable: true),
                    CustomMetadata = table.Column<string>(type: "jsonb", nullable: true),
                    Signals = table.Column<string>(type: "jsonb", nullable: true),
                    ExtractedEntities = table.Column<string>(type: "jsonb", nullable: true),
                    Relationships = table.Column<string>(type: "jsonb", nullable: true),
                    SourceModalities = table.Column<string>(type: "jsonb", nullable: true),
                    ProcessingState = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_retrieval_entities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_retrieval_entities_collections_CollectionId",
                        column: x => x.CollectionId,
                        principalTable: "collections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "scanned_page_groups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CollectionId = table.Column<Guid>(type: "uuid", nullable: true),
                    GroupName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    GroupingStrategy = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    FilenamePattern = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    DirectoryPath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    Metadata = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scanned_page_groups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_scanned_page_groups_collections_CollectionId",
                        column: x => x.CollectionId,
                        principalTable: "collections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "community_memberships",
                columns: table => new
                {
                    CommunityId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    Centrality = table.Column<float>(type: "real", nullable: false),
                    IsRepresentative = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_community_memberships", x => new { x.CommunityId, x.EntityId });
                    table.ForeignKey(
                        name: "FK_community_memberships_communities_CommunityId",
                        column: x => x.CommunityId,
                        principalTable: "communities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_community_memberships_entities_EntityId",
                        column: x => x.EntityId,
                        principalTable: "entities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "entity_relationships",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceEntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetEntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    RelationshipType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Strength = table.Column<float>(type: "real", nullable: false),
                    SourceDocuments = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_entity_relationships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_entity_relationships_entities_SourceEntityId",
                        column: x => x.SourceEntityId,
                        principalTable: "entities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_entity_relationships_entities_TargetEntityId",
                        column: x => x.TargetEntityId,
                        principalTable: "entities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "conversation_messages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    Metadata = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conversation_messages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_conversation_messages_conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "document_entities",
                columns: table => new
                {
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    MentionCount = table.Column<int>(type: "integer", nullable: false),
                    SegmentIds = table.Column<string[]>(type: "text[]", nullable: false),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_entities", x => new { x.DocumentId, x.EntityId });
                    table.ForeignKey(
                        name: "FK_document_entities_documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_document_entities_entities_EntityId",
                        column: x => x.EntityId,
                        principalTable: "entities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ingestion_jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ItemsDiscovered = table.Column<int>(type: "integer", nullable: false),
                    ItemsProcessed = table.Column<int>(type: "integer", nullable: false),
                    ItemsFailed = table.Column<int>(type: "integer", nullable: false),
                    ItemsSkipped = table.Column<int>(type: "integer", nullable: false),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    Errors = table.Column<string>(type: "jsonb", nullable: true),
                    IncrementalSync = table.Column<bool>(type: "boolean", nullable: false),
                    MaxItems = table.Column<int>(type: "integer", nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ingestion_jobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ingestion_jobs_ingestion_sources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "ingestion_sources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "entity_embeddings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Dimension = table.Column<int>(type: "integer", nullable: false),
                    Vector = table.Column<string>(type: "jsonb", nullable: true),
                    VectorBinary = table.Column<byte[]>(type: "bytea", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_entity_embeddings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_entity_embeddings_retrieval_entities_EntityId",
                        column: x => x.EntityId,
                        principalTable: "retrieval_entities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "evidence_artifacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArtifactType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MimeType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    StorageBackend = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    StoragePath = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    FileSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    SegmentHash = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Content = table.Column<string>(type: "text", nullable: true),
                    ProducerSource = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ProducerVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Confidence = table.Column<double>(type: "double precision", nullable: true),
                    Metadata = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_evidence_artifacts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_evidence_artifacts_retrieval_entities_EntityId",
                        column: x => x.EntityId,
                        principalTable: "retrieval_entities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "scanned_page_memberships",
                columns: table => new
                {
                    GroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    PageNumber = table.Column<int>(type: "integer", nullable: false),
                    OriginalFilename = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scanned_page_memberships", x => new { x.GroupId, x.EntityId });
                    table.ForeignKey(
                        name: "FK_scanned_page_memberships_retrieval_entities_EntityId",
                        column: x => x.EntityId,
                        principalTable: "retrieval_entities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_scanned_page_memberships_scanned_page_groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "scanned_page_groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AspNetRoleClaims_RoleId",
                table: "AspNetRoleClaims",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "RoleNameIndex",
                table: "AspNetRoles",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserClaims_UserId",
                table: "AspNetUserClaims",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserLogins_UserId",
                table: "AspNetUserLogins",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserRoles_RoleId",
                table: "AspNetUserRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "AspNetUsers",
                column: "NormalizedEmail");

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                table: "AspNetUsers",
                column: "NormalizedUserName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_collection_salient_terms_CollectionId_NormalizedTerm",
                table: "collection_salient_terms",
                columns: new[] { "CollectionId", "NormalizedTerm" });

            migrationBuilder.CreateIndex(
                name: "IX_collection_salient_terms_CollectionId_Score",
                table: "collection_salient_terms",
                columns: new[] { "CollectionId", "Score" });

            migrationBuilder.CreateIndex(
                name: "IX_collection_salient_terms_UpdatedAt",
                table: "collection_salient_terms",
                column: "UpdatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_collections_Name",
                table: "collections",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_communities_EntityCount",
                table: "communities",
                column: "EntityCount");

            migrationBuilder.CreateIndex(
                name: "IX_communities_Level",
                table: "communities",
                column: "Level");

            migrationBuilder.CreateIndex(
                name: "IX_communities_ParentCommunityId",
                table: "communities",
                column: "ParentCommunityId");

            migrationBuilder.CreateIndex(
                name: "IX_community_memberships_Centrality",
                table: "community_memberships",
                column: "Centrality");

            migrationBuilder.CreateIndex(
                name: "IX_community_memberships_EntityId",
                table: "community_memberships",
                column: "EntityId");

            migrationBuilder.CreateIndex(
                name: "IX_conversation_messages_ConversationId",
                table: "conversation_messages",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_conversations_CollectionId",
                table: "conversations",
                column: "CollectionId");

            migrationBuilder.CreateIndex(
                name: "IX_document_entities_EntityId",
                table: "document_entities",
                column: "EntityId");

            migrationBuilder.CreateIndex(
                name: "IX_documents_CollectionId",
                table: "documents",
                column: "CollectionId");

            migrationBuilder.CreateIndex(
                name: "IX_documents_ContentHash",
                table: "documents",
                column: "ContentHash");

            migrationBuilder.CreateIndex(
                name: "IX_documents_SourceUrl",
                table: "documents",
                column: "SourceUrl");

            migrationBuilder.CreateIndex(
                name: "IX_documents_Status",
                table: "documents",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_entities_CanonicalName_EntityType",
                table: "entities",
                columns: new[] { "CanonicalName", "EntityType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_entities_EntityType",
                table: "entities",
                column: "EntityType");

            migrationBuilder.CreateIndex(
                name: "IX_entity_embeddings_EntityId",
                table: "entity_embeddings",
                column: "EntityId");

            migrationBuilder.CreateIndex(
                name: "IX_entity_embeddings_EntityId_Name",
                table: "entity_embeddings",
                columns: new[] { "EntityId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_entity_relationships_RelationshipType",
                table: "entity_relationships",
                column: "RelationshipType");

            migrationBuilder.CreateIndex(
                name: "IX_entity_relationships_SourceEntityId",
                table: "entity_relationships",
                column: "SourceEntityId");

            migrationBuilder.CreateIndex(
                name: "IX_entity_relationships_TargetEntityId",
                table: "entity_relationships",
                column: "TargetEntityId");

            migrationBuilder.CreateIndex(
                name: "IX_evidence_artifacts_ArtifactType",
                table: "evidence_artifacts",
                column: "ArtifactType");

            migrationBuilder.CreateIndex(
                name: "IX_evidence_artifacts_ContentHash",
                table: "evidence_artifacts",
                column: "ContentHash");

            migrationBuilder.CreateIndex(
                name: "IX_evidence_artifacts_EntityId",
                table: "evidence_artifacts",
                column: "EntityId");

            migrationBuilder.CreateIndex(
                name: "IX_evidence_artifacts_EntityId_ArtifactType",
                table: "evidence_artifacts",
                columns: new[] { "EntityId", "ArtifactType" });

            migrationBuilder.CreateIndex(
                name: "IX_evidence_artifacts_Metadata",
                table: "evidence_artifacts",
                column: "Metadata")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "IX_evidence_artifacts_SegmentHash",
                table: "evidence_artifacts",
                column: "SegmentHash");

            migrationBuilder.CreateIndex(
                name: "IX_ingestion_jobs_CreatedAt",
                table: "ingestion_jobs",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ingestion_jobs_SourceId",
                table: "ingestion_jobs",
                column: "SourceId");

            migrationBuilder.CreateIndex(
                name: "IX_ingestion_jobs_Status",
                table: "ingestion_jobs",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_ingestion_sources_CollectionId",
                table: "ingestion_sources",
                column: "CollectionId");

            migrationBuilder.CreateIndex(
                name: "IX_ingestion_sources_IsEnabled",
                table: "ingestion_sources",
                column: "IsEnabled");

            migrationBuilder.CreateIndex(
                name: "IX_ingestion_sources_Name",
                table: "ingestion_sources",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_ingestion_sources_SourceType",
                table: "ingestion_sources",
                column: "SourceType");

            migrationBuilder.CreateIndex(
                name: "IX_retrieval_entities_CollectionId",
                table: "retrieval_entities",
                column: "CollectionId");

            migrationBuilder.CreateIndex(
                name: "IX_retrieval_entities_CollectionId_ContentType",
                table: "retrieval_entities",
                columns: new[] { "CollectionId", "ContentType" });

            migrationBuilder.CreateIndex(
                name: "IX_retrieval_entities_ContentHash",
                table: "retrieval_entities",
                column: "ContentHash");

            migrationBuilder.CreateIndex(
                name: "IX_retrieval_entities_ContentType",
                table: "retrieval_entities",
                column: "ContentType");

            migrationBuilder.CreateIndex(
                name: "IX_retrieval_entities_CreatedAt",
                table: "retrieval_entities",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_retrieval_entities_NeedsReview",
                table: "retrieval_entities",
                column: "NeedsReview");

            migrationBuilder.CreateIndex(
                name: "IX_scanned_page_groups_CollectionId",
                table: "scanned_page_groups",
                column: "CollectionId");

            migrationBuilder.CreateIndex(
                name: "IX_scanned_page_groups_GroupingStrategy",
                table: "scanned_page_groups",
                column: "GroupingStrategy");

            migrationBuilder.CreateIndex(
                name: "IX_scanned_page_memberships_EntityId",
                table: "scanned_page_memberships",
                column: "EntityId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AspNetRoleClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserLogins");

            migrationBuilder.DropTable(
                name: "AspNetUserRoles");

            migrationBuilder.DropTable(
                name: "AspNetUserTokens");

            migrationBuilder.DropTable(
                name: "collection_salient_terms");

            migrationBuilder.DropTable(
                name: "community_memberships");

            migrationBuilder.DropTable(
                name: "conversation_messages");

            migrationBuilder.DropTable(
                name: "document_entities");

            migrationBuilder.DropTable(
                name: "entity_embeddings");

            migrationBuilder.DropTable(
                name: "entity_relationships");

            migrationBuilder.DropTable(
                name: "evidence_artifacts");

            migrationBuilder.DropTable(
                name: "ingestion_jobs");

            migrationBuilder.DropTable(
                name: "scanned_page_memberships");

            migrationBuilder.DropTable(
                name: "AspNetRoles");

            migrationBuilder.DropTable(
                name: "AspNetUsers");

            migrationBuilder.DropTable(
                name: "communities");

            migrationBuilder.DropTable(
                name: "conversations");

            migrationBuilder.DropTable(
                name: "documents");

            migrationBuilder.DropTable(
                name: "entities");

            migrationBuilder.DropTable(
                name: "ingestion_sources");

            migrationBuilder.DropTable(
                name: "retrieval_entities");

            migrationBuilder.DropTable(
                name: "scanned_page_groups");

            migrationBuilder.DropTable(
                name: "collections");
        }
    }
}
