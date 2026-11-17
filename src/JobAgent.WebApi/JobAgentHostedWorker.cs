using JobAgent.Domain.Interfaces;
using JobAgent.Domain.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobAgent.WebApi;

public sealed class JobAgentHostedWorker(
    IJobOrchestrator orchestrator,
    IOptionsMonitor<AgentOptions> options,
    ILogger<JobAgentHostedWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Wait 5 seconds after startup before first check
        await Task.Delay(TimeSpan.FromSeconds(5), ct);

        while (!ct.IsCancellationRequested)
        {
            var o = options.CurrentValue;
            if (!o.EnableBackgroundWorker)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                continue;
            }

            var interval = Math.Max(1, o.RunEveryMinutes);
            log.LogInformation("JobAgent Hosted Worker: executando ciclo agendado...");

            try
            {
                await orchestrator.RunOnceAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Erro no ciclo agendado do JobAgent Hosted Worker");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(interval), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
