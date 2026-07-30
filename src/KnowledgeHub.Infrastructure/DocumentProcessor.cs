using KnowledgeHub.Application;
using KnowledgeHub.Contracts;
using KnowledgeHub.Domain;

namespace KnowledgeHub.Infrastructure;

public sealed class DocumentProcessor(IKnowledgeHubRepository repository, IDocumentParser parser, IEmbeddingProvider embeddings, IVectorStore vectors, IFileStorage storage, DocumentChunker chunker)
{
    public async Task ProcessAsync(Guid documentId, string tenantId, CancellationToken ct)
    {
        var document = await repository.GetDocumentAsync(documentId, tenantId, true, ct) ?? throw new InvalidOperationException("Document not found.");
        try
        {
            document.Status = DocumentStatus.Parsing; document.UpdatedAt = DateTimeOffset.UtcNow; await repository.SaveChangesAsync(ct);
            var parsed = await parser.ParseAsync(document.StoragePath, ct);
            document.Title = parsed.Title;
            document.ExtractedTextPath = await storage.SaveTextAsync(document.Id, "extracted.txt", string.Join(Environment.NewLine + Environment.NewLine, parsed.Pages.Select(x => x.Text)), ct);
            document.Status = DocumentStatus.Parsed;
            document.Chunks.AddRange(chunker.Chunk(document.Id, tenantId, parsed));
            await repository.SaveChangesAsync(ct);
            document.Status = DocumentStatus.Embedding; await repository.SaveChangesAsync(ct);
            var embedded = await embeddings.EmbedBatchAsync(document.Chunks.Select(x => x.Text).ToArray(), ct);
            await vectors.UpsertAsync(document.Chunks.Zip(embedded).Select(x => new VectorRecord(x.First.Id, document.Id, tenantId, document.Name, x.First.Text, x.First.Page, x.First.Section, x.Second)).ToArray(), ct);
            document.Status = DocumentStatus.Indexed; document.UpdatedAt = DateTimeOffset.UtcNow;
            await repository.AddAuditAsync(new AuditLog { TenantId = tenantId, Actor = "worker", Action = "document.indexed", Resource = $"document/{document.Id}", DataJson = $"{{\"chunks\":{document.Chunks.Count}}}" }, ct);
            await repository.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            document.Status = DocumentStatus.Failed; document.FailureReason = ex.Message[..Math.Min(1000, ex.Message.Length)]; document.UpdatedAt = DateTimeOffset.UtcNow; await repository.SaveChangesAsync(ct); throw;
        }
    }
}
