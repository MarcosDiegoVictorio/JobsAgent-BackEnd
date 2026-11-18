using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JobAgent.Worker;

public sealed class Worker(ILogger<Worker> log) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken ct)
    {
        log.LogWarning("A busca agendada está desativada. As buscas exigem uma conta autenticada e crédito disponível.");
        return Task.CompletedTask;
    }
}
