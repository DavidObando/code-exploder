using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeExploder.Llm;

public static class LlmServices
{
    /// <summary>
    /// Binds <see cref="LlmOptions"/> from the "Llm" config section and registers the
    /// shared HttpClient, <see cref="ILlmClient"/>, and <see cref="LlmReadinessGate"/>.
    /// The HttpClient timeout is infinite; each call enforces LlmOptions.TimeoutSeconds.
    /// </summary>
    public static IServiceCollection AddCodeExploderLlm(this IServiceCollection services, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var options = config.GetSection("Llm").Get<LlmOptions>() ?? new LlmOptions();
        services.AddSingleton(options);
        services.AddSingleton(static _ => new HttpClient { Timeout = Timeout.InfiniteTimeSpan });
        services.AddSingleton<ILlmClient>(static sp => new LlmClient(
            sp.GetRequiredService<HttpClient>(),
            sp.GetRequiredService<LlmOptions>(),
            sp.GetService<ILogger<LlmClient>>() ?? NullLogger<LlmClient>.Instance));
        services.AddSingleton(static sp => new LlmReadinessGate(
            sp.GetRequiredService<HttpClient>(),
            sp.GetRequiredService<LlmOptions>(),
            sp.GetService<ILogger<LlmReadinessGate>>() ?? NullLogger<LlmReadinessGate>.Instance));
        return services;
    }
}
