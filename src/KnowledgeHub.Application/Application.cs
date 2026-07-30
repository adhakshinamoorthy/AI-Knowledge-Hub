using System.Runtime.CompilerServices;
using System.Text;
using FluentValidation;
using KnowledgeHub.Contracts;
using KnowledgeHub.Domain;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace KnowledgeHub.Application;

public sealed class ChunkingOptions { public const string Section = "Chunking"; public int Size { get; set; } = 800; public int Overlap { get; set; } = 120; }
public sealed class SearchOptions { public const string Section = "Search"; public int TopK { get; set; } = 5; public double ScoreThreshold { get; set; } = 0.35; }
public sealed record ParsedPage(int? Page, string? Section, string Text);
public sealed record ParsedDocument(string Title, IReadOnlyList<ParsedPage> Pages, IReadOnlyDictionary<string, string> Metadata);
public sealed record VectorRecord(Guid ChunkId, Guid DocumentId, string TenantId, string DocumentName, string Text, int? Page, string? Section, float[] Vector);

public interface IKnowledgeHubRepository
{
    Task AddDocumentAsync(Document document, CancellationToken ct);
    Task<Document?> GetDocumentAsync(Guid id, string tenantId, bool includeChunks, CancellationToken ct);
    Task<IReadOnlyList<Document>> ListDocumentsAsync(string tenantId, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
    Task AddConversationAsync(Conversation conversation, CancellationToken ct);
    Task<Conversation?> GetConversationAsync(Guid id, string tenantId, bool includeMessages, CancellationToken ct);
    Task AddMessageAsync(ChatMessage message, CancellationToken ct);
    Task DeleteConversationAsync(Guid id, string tenantId, CancellationToken ct);
    Task AddJobAsync(ProcessingJob job, CancellationToken ct);
    Task AddAuditAsync(AuditLog log, CancellationToken ct);
}
public interface IFileStorage { Task<string> SaveAsync(Guid id, string name, Stream content, CancellationToken ct); Task<Stream> OpenReadAsync(string path, CancellationToken ct); Task<string> SaveTextAsync(Guid id, string name, string content, CancellationToken ct); Task DeleteDocumentAsync(Guid id, CancellationToken ct); }
public interface IDocumentParser { bool CanParse(string extension); Task<ParsedDocument> ParseAsync(string path, CancellationToken ct); }
public interface IEmbeddingProvider { Task<float[]> EmbedAsync(string text, CancellationToken ct); Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct); }
public interface IVectorStore { Task UpsertAsync(IReadOnlyList<VectorRecord> vectors, CancellationToken ct); Task<IReadOnlyList<SearchHit>> SearchAsync(string tenantId, float[] vector, SearchRequest request, CancellationToken ct); Task DeleteDocumentAsync(Guid documentId, string tenantId, CancellationToken ct); }
public interface ILanguageModel { IAsyncEnumerable<string> StreamAsync(string prompt, CancellationToken ct); }
public interface IJobQueue { Task PublishAsync(JobMessage message, CancellationToken ct); }
public interface IKnowledgeCache { Task<T?> GetAsync<T>(string key, CancellationToken ct); Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken ct); Task RemoveByPrefixAsync(string prefix, CancellationToken ct); }

public sealed class DocumentChunker(IOptions<ChunkingOptions> options)
{
    public IReadOnlyList<DocumentChunk> Chunk(Guid documentId, string tenantId, ParsedDocument parsed)
    {
        var size = Math.Max(100, options.Value.Size);
        var overlap = Math.Clamp(options.Value.Overlap, 0, size - 1);
        var result = new List<DocumentChunk>();
        foreach (var page in parsed.Pages)
        {
            var text = Normalize(page.Text);
            for (var start = 0; start < text.Length; start += size - overlap)
            {
                var length = Math.Min(size, text.Length - start);
                var end = start + length;
                if (end < text.Length)
                {
                    var boundary = text.LastIndexOfAny(['.', '!', '?', '\n'], end - 1, length);
                    if (boundary > start + size / 2) length = boundary - start + 1;
                }
                var chunkText = text.Substring(start, length).Trim();
                if (chunkText.Length > 0) result.Add(new DocumentChunk { DocumentId = documentId, TenantId = tenantId, Text = chunkText, Index = result.Count, Page = page.Page, Section = page.Section });
                if (start + length >= text.Length) break;
            }
        }
        return result;
    }
    private static string Normalize(string value) => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

public sealed class RagService(IEmbeddingProvider embeddings, IVectorStore vectors, ILanguageModel model, IKnowledgeHubRepository repository, IKnowledgeCache cache)
{
    public async Task<ChatResponse> ChatAsync(ChatRequest request, string tenantId, string userId, CancellationToken ct)
    {
        var conversation = request.ConversationId is { } id ? await repository.GetConversationAsync(id, tenantId, true, ct) : null;
        conversation ??= new Conversation { TenantId = tenantId, UserId = userId };
        if (request.ConversationId is null) await repository.AddConversationAsync(conversation, ct);
        var hits = await SearchAsync(new SearchRequest(request.Question, request.TopK), tenantId, ct);
        var citations = hits.Select(ToCitation).ToArray();
        var prompt = BuildPrompt(request.Question, hits, conversation.Messages.TakeLast(8));
        var answer = new StringBuilder();
        await foreach (var token in model.StreamAsync(prompt, ct)) answer.Append(token);
        await repository.AddMessageAsync(new ChatMessage { ConversationId = conversation.Id, Role = MessageRole.User, Content = request.Question }, ct);
        await repository.AddMessageAsync(new ChatMessage { ConversationId = conversation.Id, Role = MessageRole.Assistant, Content = answer.ToString(), CitationsJson = System.Text.Json.JsonSerializer.Serialize(citations) }, ct);
        await repository.SaveChangesAsync(ct);
        return new ChatResponse(conversation.Id, answer.ToString(), citations);
    }

    public async IAsyncEnumerable<string> StreamAsync(ChatRequest request, string tenantId, string userId, [EnumeratorCancellation] CancellationToken ct)
    {
        var vector = await embeddings.EmbedAsync(request.Question, ct);
        var hits = await vectors.SearchAsync(tenantId, vector, new SearchRequest(request.Question, request.TopK), ct);
        await foreach (var token in model.StreamAsync(BuildPrompt(request.Question, hits, []), ct)) yield return token;
    }

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchRequest request, string tenantId, CancellationToken ct)
    {
        var key = $"search:{tenantId}:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(request))))}";
        var cached = await cache.GetAsync<SearchHit[]>(key, ct); if (cached is not null) return cached;
        var hits = (await vectors.SearchAsync(tenantId, await embeddings.EmbedAsync(request.Query, ct), request, ct)).ToArray();
        await cache.SetAsync(key, hits, TimeSpan.FromMinutes(5), ct); return hits;
    }

    private static Citation ToCitation(SearchHit hit) => new(hit.ChunkId, hit.DocumentId, hit.DocumentName, hit.Page, hit.Section, hit.Score, hit.Text[..Math.Min(220, hit.Text.Length)], $"/documents/{hit.DocumentId}#chunk-{hit.ChunkId}");
    private static string BuildPrompt(string question, IReadOnlyList<SearchHit> hits, IEnumerable<ChatMessage> history) => $"""
        You are a grounded knowledge assistant. Answer only from CONTEXT. If context is insufficient, say you do not know. Cite sources as [1], [2].
        HISTORY:
        {string.Join('\n', history.Select(x => $"{x.Role}: {x.Content}"))}
        CONTEXT:
        {string.Join("\n\n", hits.Select((x, i) => $"[{i + 1}] {x.DocumentName}, page {x.Page?.ToString() ?? "n/a"}, section {x.Section ?? "n/a"}\n{x.Text}"))}
        QUESTION: {question}
        ANSWER:
        """;
}

public sealed class ChatRequestValidator : AbstractValidator<ChatRequest>
{
    public ChatRequestValidator() { RuleFor(x => x.Question).NotEmpty().MaximumLength(8_000); RuleFor(x => x.TopK).InclusiveBetween(1, 50); }
}
public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddOptions<ChunkingOptions>().BindConfiguration(ChunkingOptions.Section).Validate(x => x.Size > x.Overlap && x.Size >= 100).ValidateOnStart();
        services.AddOptions<SearchOptions>().BindConfiguration(SearchOptions.Section).ValidateOnStart();
        services.AddMediatR(x => x.RegisterServicesFromAssembly(typeof(DependencyInjection).Assembly));
        services.AddValidatorsFromAssemblyContaining<ChatRequestValidator>();
        services.AddSingleton<DocumentChunker>();
        services.AddScoped<RagService>();
        return services;
    }
}
