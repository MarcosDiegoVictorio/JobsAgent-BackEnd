using System.Net.Http.Json;
using JobAgent.Domain.Common;
using JobAgent.Domain.Entities;
using JobAgent.Domain.Interfaces;
using JobAgent.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobAgent.Service;

public sealed class TelegramNotifier(
    IHttpClientFactory httpFactory,
    IOptionsMonitor<TelegramOptions> options,
    ILogger<TelegramNotifier> log) : ITelegramNotifier
{
    public async Task SendAsync(JobPosting job, JobScore score, CancellationToken ct = default)
    {
        var text = $"""
            ⭐ {score.Score}/10 — {job.Title}
            🏢 {job.Company}
            🔗 {job.Url}

            📋 {score.Summary}

            ✅ Pontos Fortes: {string.Join("; ", score.Strengths)}
            ⚠️ Gaps: {string.Join("; ", score.Gaps)}

            📝 Dicas para o Currículo:
            {score.CvTips}

            ✍️ Mensagem sugerida ao recrutador:
            {score.RecruiterMessage}
            """;
        text = TextHelper.Truncate(text, 3900);

        var o = options.CurrentValue;
        if (string.IsNullOrWhiteSpace(o.BotToken) || string.IsNullOrWhiteSpace(o.ChatId))
        {
            log.LogInformation("Vaga relevante encontrada (Telegram não configurado):\n{Text}", text);
            return;
        }

        try
        {
            var client = httpFactory.CreateClient("Telegram");
            using var res = await client.PostAsJsonAsync(
                $"https://api.telegram.org/bot{o.BotToken}/sendMessage",
                new { chat_id = o.ChatId, text, disable_web_page_preview = true },
                ct);
            res.EnsureSuccessStatusCode();
            log.LogInformation("Notificação enviada ao Telegram com sucesso para a vaga: {Title}", job.Title);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Falha ao enviar notificação para o Telegram");
        }
    }

    public async Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken ct = default)
    {
        var o = options.CurrentValue;
        if (string.IsNullOrWhiteSpace(o.BotToken))
        {
            return (false, "BotToken do Telegram não configurado.");
        }
        if (string.IsNullOrWhiteSpace(o.ChatId))
        {
            return (false, "ChatId do Telegram não configurado.");
        }

        try
        {
            var client = httpFactory.CreateClient("Telegram");
            client.Timeout = TimeSpan.FromSeconds(15);

            var text = "🚀 Teste de conexão do JobAgent: Seu bot do Telegram está configurado e funcionando perfeitamente!";
            using var res = await client.PostAsJsonAsync(
                $"https://api.telegram.org/bot{o.BotToken}/sendMessage",
                new { chat_id = o.ChatId, text, disable_web_page_preview = true },
                ct);

            if (res.IsSuccessStatusCode)
            {
                return (true, "Mensagem de teste enviada com sucesso para o seu Telegram!");
            }

            var err = await res.Content.ReadAsStringAsync(ct);
            return (false, $"Erro na API do Telegram ({(int)res.StatusCode}): {TextHelper.Truncate(err, 200)}");
        }
        catch (Exception ex)
        {
            return (false, $"Falha ao conectar ao Telegram: {ex.Message}");
        }
    }
}
