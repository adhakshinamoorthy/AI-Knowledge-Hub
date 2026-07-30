using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using KnowledgeHub.Application;
using KnowledgeHub.Contracts;
using KnowledgeHub.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using StackExchange.Redis;
using UglyToad.PdfPig;

namespace KnowledgeHub.Infrastructure;

public sealed class StorageOptions { public const string Section = "Storage"; public string Root { get; set; } = "data"; }
public sealed class OllamaOptions { public const string Section = "Ollama"; public string BaseUrl { get; set; } = "http://localhost:11434"; public string EmbeddingModel { get; set; } = "nomic-embed-text"; public string ChatModel { get; set; } = "qwen3:4b"; }
public sealed class QdrantOptions { public const string Section = "Qdrant"; public string BaseUrl { get; set; } = "http://localhost:6333"; public string Collection { get; set; } = "knowledge"; public int VectorSize { get; set; } = 768; }
public sealed class RabbitOptions { public const string Section = "RabbitMQ"; public string Host { get; set; } = "localhost"; public string User { get; set; } = "guest"; public string Password { get; set; } = "guest"; public string Queue { get; set; } = "knowledge.jobs"; }
public sealed class RedisOptions { public const string Section = "Redis"; public string ConnectionString { get; set; } = "localhost:6379"; }

public sealed class RedisKnowledgeCache : IKnowledgeCache, IDisposable
{
    private readonly ConnectionMultiplexer redis;
    public RedisKnowledgeCache(IConfiguration configuration) => redis = ConnectionMultiplexer.Connect(configuration.GetSection(RedisOptions.Section).GetValue<string>("ConnectionString") ?? "localhost:6379,abortConnect=false");
    public async Task<T?> GetAsync<T>(string key, CancellationToken ct) { var value = await redis.GetDatabase().StringGetAsync(key); return value.HasValue ? JsonSerializer.Deserialize<T>((string)value!) : default; }
    public async Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken ct) => await redis.GetDatabase().StringSetAsync(key, JsonSerializer.Serialize(value), ttl);
    public async Task RemoveByPrefixAsync(string prefix, CancellationToken ct) { var endpoints = redis.GetEndPoints(); foreach (var endpoint in endpoints) { var server = redis.GetServer(endpoint); await foreach (var key in server.KeysAsync(pattern: prefix + "*").WithCancellation(ct)) await redis.GetDatabase().KeyDeleteAsync(key); } }
    public void Dispose() => redis.Dispose();
}

public sealed class LocalFileStorage(IOptions<StorageOptions> options) : IFileStorage
{
    public async Task<string> SaveAsync(Guid id, string name, Stream content, CancellationToken ct)
    {
        var directory = Path.GetFullPath(Path.Combine(options.Value.Root, id.ToString("N")));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "original" + Path.GetExtension(Path.GetFileName(name)).ToLowerInvariant());
        await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        await content.CopyToAsync(target, ct);
        return path;
    }
    public Task<Stream> OpenReadAsync(string path, CancellationToken ct) => Task.FromResult<Stream>(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true));
    public async Task<string> SaveTextAsync(Guid id, string name, string content, CancellationToken ct) { var directory = Path.GetFullPath(Path.Combine(options.Value.Root, id.ToString("N"))); Directory.CreateDirectory(directory); var path = Path.Combine(directory, Path.GetFileName(name)); await File.WriteAllTextAsync(path, content, ct); return path; }
    public Task DeleteDocumentAsync(Guid id, CancellationToken ct) { var path = Path.GetFullPath(Path.Combine(options.Value.Root, id.ToString("N"))); if (Directory.Exists(path)) Directory.Delete(path, true); return Task.CompletedTask; }
}

public sealed class DocumentParser : IDocumentParser
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".txt", ".md", ".pdf", ".docx", ".xlsx" };
    public bool CanParse(string extension) => Extensions.Contains(extension);
    public async Task<ParsedDocument> ParseAsync(string path, CancellationToken ct)
    {
        var extension = Path.GetExtension(path);
        return extension.ToLowerInvariant() switch
        {
            ".txt" or ".md" => new(Path.GetFileNameWithoutExtension(path), [new(null, null, await File.ReadAllTextAsync(path, ct))], new Dictionary<string, string>()),
            ".pdf" => await ParsePdfAsync(path, ct),
            ".docx" => await Task.Run(() => ParseDocx(path), ct),
            ".xlsx" => await Task.Run(() => ParseXlsx(path), ct),
            _ => throw new NotSupportedException($"Unsupported document type: {extension}")
        };
    }
    private static Task<ParsedDocument> ParsePdfAsync(string path, CancellationToken ct) => Task.Run(() =>
    {
        using var pdf = PdfDocument.Open(path);
        var pages = pdf.GetPages().Select(x => new ParsedPage(x.Number, null, x.Text)).ToArray();
        return new ParsedDocument(Path.GetFileNameWithoutExtension(path), pages, new Dictionary<string, string> { ["pages"] = pages.Length.ToString() });
    }, ct);
    private static ParsedDocument ParseDocx(string path)
    {
        using var doc = WordprocessingDocument.Open(path, false);
        var title = doc.PackageProperties.Title ?? Path.GetFileNameWithoutExtension(path);
        var paragraphs = doc.MainDocumentPart?.Document.Body?.Descendants<DocumentFormat.OpenXml.Wordprocessing.Paragraph>().Select(x => x.InnerText).Where(x => !string.IsNullOrWhiteSpace(x)) ?? [];
        return new(title, [new(null, null, string.Join(Environment.NewLine, paragraphs))], new Dictionary<string, string> { ["author"] = doc.PackageProperties.Creator ?? "" });
    }
    private static ParsedDocument ParseXlsx(string path)
    {
        using var doc = SpreadsheetDocument.Open(path, false);
        var shared = doc.WorkbookPart?.SharedStringTablePart?.SharedStringTable;
        var pages = new List<ParsedPage>();
        foreach (var sheet in doc.WorkbookPart?.Workbook.Sheets?.Elements<Sheet>() ?? [])
        {
            var ws = (WorksheetPart)doc.WorkbookPart!.GetPartById(sheet.Id!);
            var rows = ws.Worksheet.Descendants<Row>().Select(r => string.Join("\t", r.Elements<Cell>().Select(c => c.DataType?.Value == CellValues.SharedString && int.TryParse(c.CellValue?.Text, out var i) ? shared?.ElementAt(i).InnerText ?? "" : c.CellValue?.Text ?? "")));
            pages.Add(new(null, sheet.Name?.Value, string.Join(Environment.NewLine, rows)));
        }
        return new(Path.GetFileNameWithoutExtension(path), pages, new Dictionary<string, string> { ["sheets"] = pages.Count.ToString() });
    }
}

public sealed class OllamaClient(HttpClient http, IOptions<OllamaOptions> options) : IEmbeddingProvider, ILanguageModel
{
    public async Task<float[]> EmbedAsync(string text, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("api/embed", new { model = options.Value.EmbeddingModel, input = text }, ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        return body.GetProperty("embeddings")[0].EnumerateArray().Select(x => x.GetSingle()).ToArray();
    }
    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("api/embed", new { model = options.Value.EmbeddingModel, input = texts }, ct); response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        return body.GetProperty("embeddings").EnumerateArray().Select(v => v.EnumerateArray().Select(x => x.GetSingle()).ToArray()).ToArray();
    }
    public async IAsyncEnumerable<string> StreamAsync(string prompt, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/generate") { Content = JsonContent.Create(new { model = options.Value.ChatModel, prompt, stream = true, think = false }) };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct); response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct); using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(ct) is { } line) { var json = JsonSerializer.Deserialize<JsonElement>(line); if (json.TryGetProperty("response", out var token)) yield return token.GetString() ?? ""; }
    }
}

public sealed class QdrantVectorStore(HttpClient http, IOptions<QdrantOptions> options) : IVectorStore
{
    public async Task UpsertAsync(IReadOnlyList<VectorRecord> vectors, CancellationToken ct)
    {
        if (vectors.Count == 0) return;
        using (var check = await http.GetAsync($"collections/{options.Value.Collection}", ct))
        {
            if (check.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                using var create = await http.PutAsJsonAsync($"collections/{options.Value.Collection}", new { vectors = new { size = options.Value.VectorSize, distance = "Cosine" } }, ct);
                create.EnsureSuccessStatusCode();
            }
            else check.EnsureSuccessStatusCode();
        }
        var points = vectors.Select(x => new { id = x.ChunkId, vector = x.Vector, payload = new { x.DocumentId, x.TenantId, x.DocumentName, x.Text, x.Page, x.Section } });
        using var response = await http.PutAsJsonAsync($"collections/{options.Value.Collection}/points?wait=true", new { points }, ct); response.EnsureSuccessStatusCode();
    }
    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string tenantId, float[] vector, SearchRequest request, CancellationToken ct)
    {
        var filter = new { must = new[] { new { key = "tenantId", match = new { value = tenantId } } } };
        using var response = await http.PostAsJsonAsync($"collections/{options.Value.Collection}/points/search", new { vector, limit = request.TopK, score_threshold = request.ScoreThreshold, with_payload = true, filter }, ct); response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        return json.GetProperty("result").EnumerateArray().Select(x => { var p = x.GetProperty("payload"); return new SearchHit(x.GetProperty("id").GetGuid(), p.GetProperty("documentId").GetGuid(), p.GetProperty("documentName").GetString()!, p.GetProperty("text").GetString()!, p.TryGetProperty("page", out var pg) && pg.ValueKind == JsonValueKind.Number ? pg.GetInt32() : null, p.TryGetProperty("section", out var s) ? s.GetString() : null, x.GetProperty("score").GetDouble()); }).ToArray();
    }
    public async Task DeleteDocumentAsync(Guid documentId, string tenantId, CancellationToken ct) { using var response = await http.PostAsJsonAsync($"collections/{options.Value.Collection}/points/delete?wait=true", new { filter = new { must = new object[] { new { key = "documentId", match = new { value = documentId } }, new { key = "tenantId", match = new { value = tenantId } } } } }, ct); response.EnsureSuccessStatusCode(); }
}

public sealed class RabbitJobQueue(IOptions<RabbitOptions> options) : IJobQueue
{
    public async Task PublishAsync(JobMessage message, CancellationToken ct)
    {
        var factory = new ConnectionFactory { HostName = options.Value.Host, UserName = options.Value.User, Password = options.Value.Password };
        await using var connection = await factory.CreateConnectionAsync(ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);
        await channel.QueueDeclareAsync(options.Value.Queue + ".dead", durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
        await channel.QueueDeclareAsync(options.Value.Queue, durable: true, exclusive: false, autoDelete: false, arguments: new Dictionary<string, object?> { ["x-dead-letter-exchange"] = "", ["x-dead-letter-routing-key"] = options.Value.Queue + ".dead" }, cancellationToken: ct);
        var props = new BasicProperties { Persistent = true, MessageId = message.JobId.ToString() };
        await channel.BasicPublishAsync("", options.Value.Queue, mandatory: true, basicProperties: props, body: Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message)), cancellationToken: ct);
    }
}
