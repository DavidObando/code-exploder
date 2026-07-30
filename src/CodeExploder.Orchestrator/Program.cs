using CodeExploder.Orchestrator;
using CodeExploder.Storage;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddCodeExploderStorage(builder.Configuration);
builder.Services.AddHostedService<LeaseReaperService>();
builder.Services.AddHostedService<RetentionService>();

var host = builder.Build();

using (var scope = host.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<MigrationRunner>().ApplyPendingAsync();
}

host.Run();
