using System.Security.Claims;
using System.Text;
using System.Text.Json;
using KnowledgeHub.Application;
using KnowledgeHub.Contracts;
using KnowledgeHub.Domain;
using KnowledgeHub.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog((context, services, logger) => logger.ReadFrom.Configuration(context.Configuration).ReadFrom.Services(services).Enrich.FromLogContext().WriteTo.Console());
builder.Services.AddApplication().AddInfrastructure(builder.Configuration);
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks().AddDbContextCheck<KnowledgeHubDbContext>();
builder.Services.AddRateLimiter(x => x.AddFixedWindowLimiter("api", o => { o.PermitLimit = 120; o.Window = TimeSpan.FromMinutes(1); o.QueueLimit = 0; }));
var jwtKey = builder.Configuration["Security:JwtKey"] ?? throw new InvalidOperationException("Security:JwtKey is required.");
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(x => x.TokenValidationParameters = new TokenValidationParameters { ValidateIssuer = true, ValidIssuer = builder.Configuration["Security:Issuer"], ValidateAudience = true, ValidAudience = builder.Configuration["Security:Audience"], ValidateLifetime = true, ValidateIssuerSigningKey = true, IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)) });
builder.Services.AddAuthorization();
builder.Services.AddOpenTelemetry().ConfigureResource(x => x.AddService("KnowledgeHub.Api")).WithTracing(x => x.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddOtlpExporter()).WithMetrics(x => x.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddRuntimeInstrumentation().AddOtlpExporter());

var app = builder.Build();
app.UseExceptionHandler();
app.UseSerilogRequestLogging();
app.Use(async (context, next) => { context.Response.Headers["X-Correlation-ID"] = context.TraceIdentifier; await next(); });
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
if (app.Environment.IsDevelopment()) app.MapOpenApi();
app.MapHealthChecks("/health").AllowAnonymous();

var secured = app.MapGroup("").RequireAuthorization().RequireRateLimiting("api");
secured.MapPost("/documents/upload", Upload).DisableAntiforgery();
secured.MapPost("/documents/upload/bulk", async (IFormFileCollection files, ClaimsPrincipal user, IFileStorage storage, IKnowledgeHubRepository repo, IJobQueue queue, CancellationToken ct) =>
{
    var results = new List<DocumentDto>(); foreach (var file in files) results.Add(await Upload(file, user, storage, repo, queue, ct)); return Results.Accepted(value: results);
}).DisableAntiforgery();
secured.MapGet("/documents", async (ClaimsPrincipal user, IKnowledgeHubRepository repo, CancellationToken ct) => Results.Ok((await repo.ListDocumentsAsync(Tenant(user), ct)).Select(ToDto)));
secured.MapGet("/documents/{id:guid}", async (Guid id, ClaimsPrincipal user, IKnowledgeHubRepository repo, CancellationToken ct) => await repo.GetDocumentAsync(id, Tenant(user), true, ct) is { } d ? Results.Ok(new { Document = ToDto(d), Chunks = d.Chunks.Select(x => new { x.Id, x.Index, x.Page, x.Section, x.Text }) }) : Results.NotFound());
secured.MapGet("/documents/status/{id:guid}", async (Guid id, ClaimsPrincipal user, IKnowledgeHubRepository repo, CancellationToken ct) => await repo.GetDocumentAsync(id, Tenant(user), false, ct) is { } d ? Results.Ok(new { d.Id, Status = d.Status.ToString(), d.FailureReason, d.UpdatedAt }) : Results.NotFound());
secured.MapDelete("/documents/{id:guid}", async (Guid id, ClaimsPrincipal user, IKnowledgeHubRepository repo, IVectorStore vectors, IFileStorage storage, CancellationToken ct) => { var tenant = Tenant(user); var d = await repo.GetDocumentAsync(id, tenant, false, ct); if (d is null) return Results.NotFound(); await vectors.DeleteDocumentAsync(id, tenant, ct); await storage.DeleteDocumentAsync(id, ct); d.Status = DocumentStatus.Deleted; await repo.SaveChangesAsync(ct); return Results.NoContent(); });
secured.MapPost("/documents/reindex", async (Guid[] documentIds, ClaimsPrincipal user, IKnowledgeHubRepository repo, IJobQueue queue, CancellationToken ct) => { foreach (var id in documentIds) { var job = new ProcessingJob { DocumentId = id, TenantId = Tenant(user), Type = JobType.Reindex }; await repo.AddJobAsync(job, ct); await queue.PublishAsync(new JobMessage(job.Id, id, job.TenantId, job.Type.ToString()), ct); } await repo.SaveChangesAsync(ct); return Results.Accepted(); });
secured.MapPost("/search", async (SearchRequest request, ClaimsPrincipal user, RagService rag, CancellationToken ct) => Results.Ok(await rag.SearchAsync(request, Tenant(user), ct)));
secured.MapGet("/search", async (string q, int? topK, double? threshold, ClaimsPrincipal user, RagService rag, CancellationToken ct) => Results.Ok(await rag.SearchAsync(new SearchRequest(q, topK ?? 5, threshold ?? .35), Tenant(user), ct)));
secured.MapPost("/chat", async (ChatRequest request, ClaimsPrincipal user, RagService rag, CancellationToken ct) => Results.Ok(await rag.ChatAsync(request, Tenant(user), User(user), ct)));
secured.MapPost("/chat/stream", async (ChatRequest request, ClaimsPrincipal user, RagService rag, HttpResponse response, CancellationToken ct) => { response.ContentType = "text/event-stream"; await foreach (var token in rag.StreamAsync(request, Tenant(user), User(user), ct)) { await response.WriteAsync($"data: {JsonSerializer.Serialize(token)}\n\n", ct); await response.Body.FlushAsync(ct); } });
secured.MapGet("/chat/history", async (Guid conversationId, ClaimsPrincipal user, IKnowledgeHubRepository repo, CancellationToken ct) => await repo.GetConversationAsync(conversationId, Tenant(user), true, ct) is { } c ? Results.Ok(c.Messages.OrderBy(x => x.CreatedAt).Select(x => new ChatHistoryItem(x.Id, x.Role.ToString(), x.Content, x.CreatedAt, JsonSerializer.Deserialize<Citation[]>(x.CitationsJson) ?? []))) : Results.NotFound());
secured.MapDelete("/chat/history", async (Guid conversationId, ClaimsPrincipal user, IKnowledgeHubRepository repo, CancellationToken ct) => { await repo.DeleteConversationAsync(conversationId, Tenant(user), ct); return Results.NoContent(); });

await using (var scope = app.Services.CreateAsyncScope()) { await scope.ServiceProvider.GetRequiredService<KnowledgeHubDbContext>().Database.EnsureCreatedAsync(); }
await app.RunAsync();

static async Task<DocumentDto> Upload(IFormFile file, ClaimsPrincipal user, IFileStorage storage, IKnowledgeHubRepository repo, IJobQueue queue, CancellationToken ct)
{
    if (file.Length is <= 0 or > 50 * 1024 * 1024) throw new BadHttpRequestException("File must be between 1 byte and 50 MB.");
    var extension = Path.GetExtension(file.FileName).ToLowerInvariant(); if (extension is not (".pdf" or ".docx" or ".xlsx" or ".md" or ".txt")) throw new BadHttpRequestException("Unsupported document type.");
    var tenant = Tenant(user); var id = Guid.NewGuid(); await using var stream = file.OpenReadStream(); var path = await storage.SaveAsync(id, file.FileName, stream, ct);
    var document = new Document { Id = id, TenantId = tenant, Name = Path.GetFileName(file.FileName), ContentType = file.ContentType, Size = file.Length, StoragePath = path };
    var job = new ProcessingJob { DocumentId = id, TenantId = tenant, Type = JobType.Parse };
    await repo.AddDocumentAsync(document, ct); await repo.AddJobAsync(job, ct); await repo.AddAuditAsync(new AuditLog { TenantId = tenant, Actor = User(user), Action = "document.uploaded", Resource = $"document/{id}" }, ct); await repo.SaveChangesAsync(ct);
    await queue.PublishAsync(new JobMessage(job.Id, id, tenant, job.Type.ToString()), ct); return ToDto(document);
}
static string Tenant(ClaimsPrincipal user) => user.FindFirstValue("tenant_id") ?? throw new UnauthorizedAccessException("tenant_id claim is required.");
static string User(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";
static DocumentDto ToDto(Document d) => new(d.Id, d.Name, d.ContentType, d.Size, d.Status.ToString(), d.CreatedAt, d.FailureReason);

public partial class Program;
