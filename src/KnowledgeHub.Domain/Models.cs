namespace KnowledgeHub.Domain;

public enum DocumentStatus { Uploaded, Parsing, Parsed, Embedding, Indexed, Failed, Deleted }
public enum JobType { Parse, Embed, Index, Reindex, Cleanup }
public enum JobStatus { Queued, Running, Completed, Failed, DeadLettered }
public enum MessageRole { User, Assistant, System }

public sealed class Document
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string TenantId { get; init; }
    public required string Name { get; init; }
    public required string ContentType { get; init; }
    public required string StoragePath { get; init; }
    public long Size { get; init; }
    public string? Title { get; set; }
    public string? ExtractedTextPath { get; set; }
    public DocumentStatus Status { get; set; } = DocumentStatus.Uploaded;
    public string? FailureReason { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<DocumentChunk> Chunks { get; } = [];
}

public sealed class DocumentChunk
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid DocumentId { get; init; }
    public required string TenantId { get; init; }
    public required string Text { get; init; }
    public int Index { get; init; }
    public int? Page { get; init; }
    public string? Section { get; init; }
    public string MetadataJson { get; init; } = "{}";
    public Document? Document { get; init; }
}

public sealed class Conversation
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string TenantId { get; init; }
    public required string UserId { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public List<ChatMessage> Messages { get; } = [];
}

public sealed class ChatMessage
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid ConversationId { get; init; }
    public MessageRole Role { get; init; }
    public required string Content { get; init; }
    public string CitationsJson { get; init; } = "[]";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class ProcessingJob
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid DocumentId { get; init; }
    public required string TenantId { get; init; }
    public JobType Type { get; init; }
    public JobStatus Status { get; set; } = JobStatus.Queued;
    public int Attempt { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset AvailableAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class AuditLog
{
    public long Id { get; init; }
    public required string TenantId { get; init; }
    public required string Actor { get; init; }
    public required string Action { get; init; }
    public required string Resource { get; init; }
    public string DataJson { get; init; } = "{}";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
