using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LinkForge;

public static class EngineServiceCollectionExtensions
{
    /// <summary>Registers the coordinator with the real OpenAI provider wrapped in retry/timeout/fallback handling.</summary>
    public static IServiceCollection AddEngine(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<OpenAiOptions>(configuration.GetSection(OpenAiOptions.SectionName));
        services.Configure<WorkspaceOptions>(configuration.GetSection(WorkspaceOptions.SectionName));
        services.Configure<WorkflowOptions>(configuration.GetSection(WorkflowOptions.SectionName));
        services.AddSingleton<OpenAiEngineerProvider>();
        services.AddSingleton<IEngineerProvider>(provider => new ResilientEngineerProvider(
            provider.GetRequiredService<OpenAiEngineerProvider>(),
            provider.GetRequiredService<IOptions<OpenAiOptions>>(),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ResilientEngineerProvider>>()));
        services.AddSingleton<ICommandRunner, AllowlistedProcessRunner>();
        services.AddSingleton<IChangeVerifier, SandboxChangeVerifier>();
        services.AddSingleton<ICodebaseContext, SourceCodebaseContext>();
        services.AddSingleton<PipelineCoordinator>();
        return services;
    }
}
