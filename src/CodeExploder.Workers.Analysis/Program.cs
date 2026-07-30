using CodeExploder.Storage;
using CodeExploder.Workers.Analysis;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddCodeExploderStorage(builder.Configuration);
builder.Services.AddSessionEventPublishing();
builder.Services.AddHostedService<NoopPipelineWorker>();

var host = builder.Build();

using (var scope = host.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<MigrationRunner>().ApplyPendingAsync();
}

host.Run();
