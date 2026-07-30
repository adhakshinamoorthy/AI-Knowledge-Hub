using KnowledgeHub.Application;
using KnowledgeHub.Infrastructure;
using KnowledgeHub.Worker;
using Serilog;
using Serilog.Events;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSerilog(x => x.MinimumLevel.Is(LogEventLevel.Information).WriteTo.Console());
builder.Services.AddApplication().AddInfrastructure(builder.Configuration);
builder.Services.AddHostedService<Worker>();
await builder.Build().RunAsync();
