using JobAgent.Domain.Interfaces;
using JobAgent.Domain.Models;
using JobAgent.Repository;
using JobAgent.Service.Sources;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace JobAgent.Service.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddJobAgentCore(this IServiceCollection services, IConfiguration configuration)
    {
        // Bind Options
        services.Configure<AgentOptions>(configuration.GetSection("Agent"));
        services.Configure<GeminiOptions>(configuration.GetSection("Gemini"));
        services.Configure<AnthropicOptions>(configuration.GetSection("Anthropic"));
        services.Configure<TelegramOptions>(configuration.GetSection("Telegram"));
        services.Configure<RssOptions>(configuration.GetSection("Rss"));
        services.Configure<EmailOptions>(configuration.GetSection("Email"));
        services.Configure<AuthOptions>(configuration.GetSection("Auth"));
        services.Configure<DatabaseOptions>(configuration.GetSection("Database"));

        // Http Clients
        services.AddHttpClient("Gemini", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(60);
        });
        services.AddHttpClient("Anthropic", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(90);
        });
        services.AddHttpClient("Telegram", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(20);
        });
        services.AddHttpClient("Rss", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddHttpClient("Gupy", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(20);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("JobAgent/1.0");
        });

        // Repository
        services.AddSingleton<DatabaseConnectionFactory>();
        services.AddSingleton<DatabaseInitializer>();
        services.AddSingleton<IJobRepository, JobRepository>();
        services.AddSingleton<ICandidateProfileRepository, CandidateProfileRepository>();
        services.AddSingleton<IUserRepository, UserRepository>();

        // Core Scorer Services
        services.AddSingleton<GeminiScorer>();
        services.AddSingleton<CandidateProfileAnalyzer>();
        services.AddSingleton<ClaudeScorer>();
        services.AddSingleton<LlmJobScorer>();
        services.AddSingleton<IJobScorer>(sp => sp.GetRequiredService<LlmJobScorer>());

        services.AddSingleton<ITelegramNotifier, TelegramNotifier>();
        services.AddSingleton<IAuthService, AuthService>();
        services.AddSingleton<IProfileService, ProfileService>();
        services.AddSingleton<IConfigurationService, ConfigurationService>();
        services.AddSingleton<IJobOrchestrator, JobOrchestrator>();

        // Sources
        services.AddSingleton<IJobSource, RssJobSource>();
        services.AddSingleton<IJobSource, GupyJobSource>();

        return services;
    }
}
