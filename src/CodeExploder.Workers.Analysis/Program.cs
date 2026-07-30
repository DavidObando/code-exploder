using CodeExploder.GitHub;
using CodeExploder.Storage;
using CodeExploder.Workers.Analysis;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddCodeExploderStorage(builder.Configuration);
builder.Services.AddSessionEventPublishing();
builder.Services.AddSingleton<GitCli>();
builder.Services.AddSingleton(sp => new GitHubApiClient(
    GitHubApiClient.CreateHttpClient(),
    sp.GetRequiredService<ILogger<GitHubApiClient>>()));
builder.Services.AddHostedService<AnalysisPipelineWorker>();
builder.Services.AddHostedService<WorkspaceJanitorService>();

var host = builder.Build();

using (var scope = host.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<MigrationRunner>().ApplyPendingAsync();
}

host.Run();
