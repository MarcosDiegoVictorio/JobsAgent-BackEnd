using JobAgent.Domain.Interfaces;
using JobAgent.Domain.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobAgent.Worker;

public sealed class Worker(
    IJobOrchestrator orchestrator,
    IOptionsMonitor<AgentOptions> options,
    ILogger<Worker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        log.LogInformation("JobAgent Worker iniciado.");

        // Initial run on startup
        try
        {
            log.LogInformation("Executando ciclo inicial de busca de vagas...");
            await orchestrator.RunOnceAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Erro no ciclo inicial de busca de vagas");
        }

        while (!ct.IsCancellationRequested)
        {
            var interval = Math.Max(1, options.CurrentValue.RunEveryMinutes);
            log.LogInformation("Aguardando próximo ciclo em {Minutes} minutos...", interval);

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(interval), ct);
                await orchestrator.RunOnceAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Erro no ciclo de execução do JobAgent Worker");
            }
        }

        log.LogInformation("JobAgent Worker finalizado.");
    }
}
