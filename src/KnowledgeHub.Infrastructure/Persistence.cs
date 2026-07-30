using KnowledgeHub.Application;
using KnowledgeHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeHub.Infrastructure;

public sealed class KnowledgeHubDbContext(DbContextOptions<KnowledgeHubDbContext> options) : DbContext(options)
{
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<DocumentChunk> Chunks => Set<DocumentChunk>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ChatMessage> Messages => Set<ChatMessage>();
    public DbSet<ProcessingJob> Jobs => Set<ProcessingJob>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Document>().HasIndex(x => new { x.TenantId, x.CreatedAt });
        b.Entity<Document>().Property(x => x.Status).HasConversion<string>();
        b.Entity<Document>().HasMany(x => x.Chunks).WithOne(x => x.Document).HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<DocumentChunk>().HasIndex(x => new { x.TenantId, x.DocumentId });
        b.Entity<Conversation>().HasMany(x => x.Messages).WithOne().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Conversation>().HasIndex(x => new { x.TenantId, x.UserId });
        b.Entity<ProcessingJob>().Property(x => x.Type).HasConversion<string>();
        b.Entity<ProcessingJob>().Property(x => x.Status).HasConversion<string>();
        b.Entity<ProcessingJob>().HasIndex(x => new { x.Status, x.AvailableAt });
        b.Entity<AuditLog>().HasIndex(x => new { x.TenantId, x.CreatedAt });
    }
}

public sealed class KnowledgeHubRepository(KnowledgeHubDbContext db) : IKnowledgeHubRepository
{
    public async Task AddDocumentAsync(Document document, CancellationToken ct) => await db.Documents.AddAsync(document, ct);
    public async Task<Document?> GetDocumentAsync(Guid id, string tenantId, bool includeChunks, CancellationToken ct)
    { var query = db.Documents.Where(x => x.Id == id && x.TenantId == tenantId); return await (includeChunks ? query.Include(x => x.Chunks) : query).SingleOrDefaultAsync(ct); }
    public async Task<IReadOnlyList<Document>> ListDocumentsAsync(string tenantId, CancellationToken ct) => await db.Documents.Where(x => x.TenantId == tenantId && x.Status != DocumentStatus.Deleted).OrderByDescending(x => x.CreatedAt).AsNoTracking().ToListAsync(ct);
    public async Task SaveChangesAsync(CancellationToken ct) => await db.SaveChangesAsync(ct);
    public async Task AddConversationAsync(Conversation conversation, CancellationToken ct) => await db.Conversations.AddAsync(conversation, ct);
    public async Task<Conversation?> GetConversationAsync(Guid id, string tenantId, bool includeMessages, CancellationToken ct)
    { var query = db.Conversations.Where(x => x.Id == id && x.TenantId == tenantId); return await (includeMessages ? query.Include(x => x.Messages) : query).SingleOrDefaultAsync(ct); }
    public async Task AddMessageAsync(ChatMessage message, CancellationToken ct) => await db.Messages.AddAsync(message, ct);
    public async Task DeleteConversationAsync(Guid id, string tenantId, CancellationToken ct) => await db.Conversations.Where(x => x.Id == id && x.TenantId == tenantId).ExecuteDeleteAsync(ct);
    public async Task AddJobAsync(ProcessingJob job, CancellationToken ct) => await db.Jobs.AddAsync(job, ct);
    public async Task AddAuditAsync(AuditLog log, CancellationToken ct) => await db.AuditLogs.AddAsync(log, ct);
}
