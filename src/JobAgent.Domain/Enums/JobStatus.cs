namespace JobAgent.Domain.Enums;

public static class JobStatus
{
    public const string Novo = "novo";
    public const string Aplicado = "aplicado";
    public const string Entrevista = "entrevista";
    public const string Descartada = "descartada";
    public const string Salvo = "salvo";

    public static readonly IReadOnlyList<string> All = [Novo, Aplicado, Entrevista, Descartada, Salvo];

    public static bool IsValid(string status) =>
        !string.IsNullOrWhiteSpace(status) && All.Contains(status.Trim().ToLowerInvariant());
}
