using KnowledgeHub.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace KnowledgeHub.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<KnowledgeHubDbContext>(x => x.UseNpgsql(configuration.GetConnectionString("Postgres")));
        services.AddScoped<IKnowledgeHubRepository, KnowledgeHubRepository>();
        services.Configure<StorageOptions>(configuration.GetSection(StorageOptions.Section));
        services.Configure<OllamaOptions>(configuration.GetSection(OllamaOptions.Section));
        services.Configure<QdrantOptions>(configuration.GetSection(QdrantOptions.Section));
        services.Configure<RabbitOptions>(configuration.GetSection(RabbitOptions.Section));
        services.AddSingleton<IFileStorage, LocalFileStorage>();
        services.AddSingleton<IDocumentParser, DocumentParser>();
        services.AddHttpClient<OllamaClient>((sp, c) => c.BaseAddress = new Uri(sp.GetRequiredService<IOptions<OllamaOptions>>().Value.BaseUrl.TrimEnd('/') + "/"));
        services.AddScoped<IEmbeddingProvider>(sp => sp.GetRequiredService<OllamaClient>());
        services.AddScoped<ILanguageModel>(sp => sp.GetRequiredService<OllamaClient>());
        services.AddHttpClient<QdrantVectorStore>((sp, c) => c.BaseAddress = new Uri(sp.GetRequiredService<IOptions<QdrantOptions>>().Value.BaseUrl.TrimEnd('/') + "/"));
        services.AddScoped<IVectorStore>(sp => sp.GetRequiredService<QdrantVectorStore>());
        services.AddSingleton<IJobQueue, RabbitJobQueue>();
        services.AddSingleton<IKnowledgeCache, RedisKnowledgeCache>();
        services.AddScoped<DocumentProcessor>();
        return services;
    }
}
