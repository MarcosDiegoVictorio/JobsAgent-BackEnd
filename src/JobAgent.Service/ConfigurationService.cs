using System.Text.Json;
using System.Text.Json.Nodes;
using JobAgent.Domain.Interfaces;
using JobAgent.Domain.Models;
using MailKit.Net.Imap;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobAgent.Service;

public sealed class ConfigurationService(
    IOptionsMonitor<AgentOptions> agentOptions,
    IOptionsMonitor<GeminiOptions> geminiOptions,
    IOptionsMonitor<AnthropicOptions> anthropicOptions,
    IOptionsMonitor<TelegramOptions> telegramOptions,
    IOptionsMonitor<RssOptions> rssOptions,
    IOptionsMonitor<EmailOptions> emailOptions,
    ILogger<ConfigurationService> log) : IConfigurationService
{
    private static string AppSettingsPath
    {
        get
        {
            var cur = Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json");
            if (File.Exists(cur)) return cur;
            return Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        }
    }

    public Task<AppConfigDto> GetConfigurationAsync(CancellationToken ct = default)
    {
        var config = new AppConfigDto
        {
            Agent = agentOptions.CurrentValue,
            Gemini = geminiOptions.CurrentValue,
            Anthropic = anthropicOptions.CurrentValue,
            Telegram = telegramOptions.CurrentValue,
            Rss = rssOptions.CurrentValue,
            Email = emailOptions.CurrentValue
        };

        return Task.FromResult(config);
    }

    public async Task SaveConfigurationAsync(AppConfigDto config, CancellationToken ct = default)
    {
        var filePath = AppSettingsPath;
        log.LogInformation("Salvando nova configuração em {Path}", filePath);

        JsonObject root;
        if (File.Exists(filePath))
        {
            var existingText = await File.ReadAllTextAsync(filePath, ct);
            root = JsonNode.Parse(existingText) as JsonObject ?? [];
        }
        else
        {
            root = [];
        }

        // Update or insert sections
        root["Agent"] = JsonSerializer.SerializeToNode(config.Agent);
        root["Gemini"] = JsonSerializer.SerializeToNode(config.Gemini);
        root["Anthropic"] = JsonSerializer.SerializeToNode(config.Anthropic);
        root["Telegram"] = JsonSerializer.SerializeToNode(config.Telegram);
        root["Rss"] = JsonSerializer.SerializeToNode(config.Rss);
        root["Email"] = JsonSerializer.SerializeToNode(config.Email);

        var options = new JsonSerializerOptions { WriteIndented = true };
        var jsonString = root.ToJsonString(options);

        await File.WriteAllTextAsync(filePath, jsonString, ct);

        // Also check if appsettings.json exists in root project dir
        var parentDir = Directory.GetParent(Directory.GetCurrentDirectory())?.FullName;
        if (parentDir != null)
        {
            var rootSettings = Path.Combine(parentDir, "appsettings.json");
            if (File.Exists(rootSettings) && rootSettings != filePath)
            {
                await File.WriteAllTextAsync(rootSettings, jsonString, ct);
            }
        }
    }

    public async Task<(bool Success, string Message)> TestEmailConnectionAsync(CancellationToken ct = default)
    {
        var o = emailOptions.CurrentValue;
        if (string.IsNullOrWhiteSpace(o.User) || string.IsNullOrWhiteSpace(o.AppPassword))
        {
            return (false, "E-mail ou Senha de Aplicativo não configurados.");
        }

        try
        {
            using var imap = new ImapClient();
            imap.Timeout = 15000;
            await imap.ConnectAsync(o.Host, o.Port, SecureSocketOptions.SslOnConnect, ct);
            await imap.AuthenticateAsync(o.User, o.AppPassword, ct);

            var inbox = imap.Inbox;
            await inbox.OpenAsync(MailKit.FolderAccess.ReadOnly, ct);
            var count = inbox.Count;

            await imap.DisconnectAsync(true, ct);
            return (true, $"Conexão IMAP bem-sucedida! Pasta {o.Folder} acessada com {count} mensagens.");
        }
        catch (Exception ex)
        {
            return (false, $"Falha ao conectar via IMAP ({o.Host}:{o.Port}): {ex.Message}");
        }
    }
}
