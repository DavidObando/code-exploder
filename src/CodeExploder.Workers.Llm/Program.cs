using CodeExploder.Llm;
using CodeExploder.Storage;
using CodeExploder.Workers.Llm;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddCodeExploderStorage(builder.Configuration);
builder.Services.AddSessionEventPublishing();
builder.Services.AddCodeExploderLlm(builder.Configuration);
builder.Services.AddSingleton<CodeExploder.Qa.Retriever>();
builder.Services.AddSingleton<CodeExploder.Qa.AnswerLoop>();
builder.Services.AddHostedService<LlmPipelineWorker>();
builder.Services.AddHostedService<EmbedLaneWorker>();

var host = builder.Build();

using (var scope = host.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<MigrationRunner>().ApplyPendingAsync();
}

host.Run();
