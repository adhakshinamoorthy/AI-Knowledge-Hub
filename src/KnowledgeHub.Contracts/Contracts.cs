namespace KnowledgeHub.Contracts;

public sealed record DocumentDto(Guid Id, string Name, string ContentType, long Size, string Status, DateTimeOffset CreatedAt, string? FailureReason);
public sealed record SearchRequest(string Query, int TopK = 5, double ScoreThreshold = 0.35, IReadOnlyDictionary<string, string>? Filters = null, int Page = 1, int PageSize = 20);
public sealed record SearchHit(Guid ChunkId, Guid DocumentId, string DocumentName, string Text, int? Page, string? Section, double Score);
public sealed record Citation(Guid ChunkId, Guid DocumentId, string DocumentName, int? Page, string? Section, double SimilarityScore, string Snippet, string Url);
public sealed record ChatRequest(string Question, Guid? ConversationId = null, int TopK = 5);
public sealed record ChatResponse(Guid ConversationId, string Answer, IReadOnlyList<Citation> Citations);
public sealed record ChatHistoryItem(Guid Id, string Role, string Content, DateTimeOffset CreatedAt, IReadOnlyList<Citation> Citations);
public sealed record JobMessage(Guid JobId, Guid DocumentId, string TenantId, string Type);
public sealed record ApiError(string Code, string Message, string CorrelationId);
