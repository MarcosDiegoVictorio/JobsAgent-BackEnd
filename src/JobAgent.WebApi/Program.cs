using JobAgent.Domain.Entities;
using JobAgent.Domain.Enums;
using JobAgent.Domain.Interfaces;
using JobAgent.Domain.Models;
using JobAgent.Service;
using JobAgent.Service.Extensions;
using JobAgent.WebApi;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Register Core Domain, Repository, and Services
builder.Services.AddJobAgentCore(builder.Configuration);

// Hosted background worker
builder.Services.AddHostedService<JobAgentHostedWorker>();

// OpenAPI
builder.Services.AddOpenApi();

// Allow only configured frontend origins when the UI is hosted separately.
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
    {
        policy.WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

var app = builder.Build();

// Ensure DB is initialized on startup
using (var scope = app.Services.CreateScope())
{
    var repo = scope.ServiceProvider.GetRequiredService<IJobRepository>();
    await repo.InitAsync();
}

app.UseCors("Frontend");

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

// ==========================================
// API Endpoints: Jobs
// ==========================================
var jobsApi = app.MapGroup("/api/jobs").WithTags("Jobs");

jobsApi.MapGet("/", async (
    [FromQuery] int? page,
    [FromQuery] int? pageSize,
    [FromQuery] string? status,
    [FromQuery] int? minScore,
    [FromQuery] int? maxScore,
    [FromQuery] string? source,
    [FromQuery] string? search,
    [FromQuery] string? orderBy,
    [FromQuery] bool? descending,
    IJobRepository repo,
    IProfileService profileSvc,
    CancellationToken ct) =>
{
    var profile = await profileSvc.GetActiveProfileAsync(ct);
    var filter = new JobFilter
    {
        ProfileId = profile.Id,
        Page = page ?? 1,
        PageSize = pageSize ?? 20,
        Status = status,
        MinScore = minScore,
        MaxScore = maxScore,
        Source = source,
        Search = search,
        OrderBy = orderBy ?? "score",
        Descending = descending ?? true
    };

    var result = await repo.GetPagedAsync(filter, ct);
    return Results.Ok(result);
});

jobsApi.MapGet("/stats", async (IJobRepository repo, IProfileService profileSvc, CancellationToken ct) =>
{
    var profile = await profileSvc.GetActiveProfileAsync(ct);
    var stats = await repo.GetStatisticsAsync(profile.Id, ct);
    return Results.Ok(stats);
});

jobsApi.MapGet("/{id}", async (string id, IJobRepository repo, IProfileService profileSvc, CancellationToken ct) =>
{
    var profile = await profileSvc.GetActiveProfileAsync(ct);
    var job = await repo.GetByIdAsync(id, profile.Id, ct);
    return job is not null ? Results.Ok(job) : Results.NotFound(new { message = "Vaga não encontrada" });
});

jobsApi.MapPatch("/{id}/status", async (
    string id,
    [FromBody] UpdateJobStatusRequest req,
    IJobRepository repo,
    IProfileService profileSvc,
    CancellationToken ct) =>
{
    if (!JobStatus.IsValid(req.Status))
    {
        return Results.BadRequest(new { message = $"Status inválido. Permitidos: {string.Join(", ", JobStatus.All)}" });
    }

    var profile = await profileSvc.GetActiveProfileAsync(ct);
    var updated = await repo.UpdateStatusAsync(id, profile.Id, req.Status, req.Notes, ct);
    return updated ? Results.Ok(new { success = true, id, status = req.Status }) : Results.NotFound();
});

jobsApi.MapDelete("/{id}", async (string id, IJobRepository repo, IProfileService profileSvc, CancellationToken ct) =>
{
    var profile = await profileSvc.GetActiveProfileAsync(ct);
    var deleted = await repo.DeleteAsync(id, profile.Id, ct);
    return deleted ? Results.Ok(new { success = true, id }) : Results.NotFound();
});

// ==========================================
// API Endpoints: Orchestrator / Run
// ==========================================
var orchApi = app.MapGroup("/api/orchestrator").WithTags("Orchestrator");

orchApi.MapGet("/status", (IJobOrchestrator orchestrator) =>
{
    return Results.Ok(orchestrator.GetCurrentStatus());
});

orchApi.MapPost("/run", (IJobOrchestrator orchestrator, [FromQuery] bool? wait, CancellationToken ct) =>
{
    if (wait == true)
    {
        // Synchronous execution
        var task = Task.Run(async () => await orchestrator.RunOnceAsync(ct), ct);
        return Results.Ok(task.Result);
    }

    // Fire and forget in background
    _ = Task.Run(async () =>
    {
        try
        {
            await orchestrator.RunOnceAsync(CancellationToken.None);
        }
        catch { }
    });

    return Results.Accepted(value: new { message = "Ciclo de busca iniciado em segundo plano." });
});

// ==========================================
// API Endpoints: Configuration
// ==========================================
var configApi = app.MapGroup("/api/config").WithTags("Configuration");

configApi.MapGet("/", async (IConfigurationService configSvc, IProfileService profileSvc, CancellationToken ct) =>
{
    var config = await configSvc.GetConfigurationAsync(ct);
    var profile = await profileSvc.GetActiveProfileAsync(ct);
    config.Agent.IncludeTitleKeywords = profile.IncludeTitleKeywords;
    config.Agent.ExcludeTitleKeywords = profile.ExcludeTitleKeywords;
    return Results.Ok(config);
});

configApi.MapPut("/", async (
    [FromBody] AppConfigDto config,
    IConfigurationService configSvc,
    IProfileService profileSvc,
    CancellationToken ct) =>
{
    await configSvc.SaveConfigurationAsync(config, ct);
    await profileSvc.UpdateActiveKeywordsAsync(
        config.Agent.IncludeTitleKeywords,
        config.Agent.ExcludeTitleKeywords,
        ct);
    return Results.Ok(new { success = true, message = "Configurações salvas com sucesso!" });
});

configApi.MapPost("/test/gemini", async (LlmJobScorer scorer, CancellationToken ct) =>
{
    var (success, message) = await scorer.TestGeminiAsync(ct);
    return Results.Ok(new TestResultDto { Success = success, Message = message });
});

configApi.MapPost("/test/anthropic", async (LlmJobScorer scorer, CancellationToken ct) =>
{
    var (success, message) = await scorer.TestAnthropicAsync(ct);
    return Results.Ok(new TestResultDto { Success = success, Message = message });
});

configApi.MapPost("/test/telegram", async (ITelegramNotifier notifier, CancellationToken ct) =>
{
    var (success, message) = await notifier.TestConnectionAsync(ct);
    return Results.Ok(new TestResultDto { Success = success, Message = message });
});

configApi.MapPost("/test/email", async (IConfigurationService configSvc, CancellationToken ct) =>
{
    var (success, message) = await configSvc.TestEmailConnectionAsync(ct);
    return Results.Ok(new TestResultDto { Success = success, Message = message });
});

// ==========================================
// API Endpoints: Candidate Profile
// ==========================================
var profileApi = app.MapGroup("/api/profile").WithTags("Profile");

profileApi.MapGet("/", async (IProfileService profileSvc, CancellationToken ct) =>
{
    var markdown = await profileSvc.GetProfileAsync(ct);
    return Results.Ok(new { content = markdown });
});

profileApi.MapPut("/", async ([FromBody] ProfileUpdateRequest req, IProfileService profileSvc, CancellationToken ct) =>
{
    await profileSvc.SaveProfileAsync(req.Content, ct);
    return Results.Ok(new { success = true, message = "Perfil salvo com sucesso!" });
});

profileApi.MapGet("/active", async (IProfileService profileSvc, CancellationToken ct) =>
{
    var profile = await profileSvc.GetActiveProfileAsync(ct);
    return Results.Ok(ToCandidateProfileDto(profile));
});

profileApi.MapGet("/profiles", async (IProfileService profileSvc, CancellationToken ct) =>
{
    var profiles = await profileSvc.GetProfilesAsync(ct);
    return Results.Ok(profiles.Select(ToCandidateProfileDto));
});

profileApi.MapPut("/active", async (
    [FromBody] CandidateProfileUpdateRequest? request,
    IProfileService profileSvc,
    CancellationToken ct) =>
{
    if (request is null)
    {
        return Results.BadRequest(new { message = "Os dados do perfil estão vazios." });
    }

    var validationMessage = ValidateProfileContent(
        request.Content,
        request.IncludeTitleKeywords,
        request.ExcludeTitleKeywords);
    if (validationMessage is not null)
    {
        return Results.BadRequest(new { message = validationMessage });
    }

    var profile = await profileSvc.SaveActiveProfileAsync(
        request.Content,
        request.IncludeTitleKeywords,
        request.ExcludeTitleKeywords,
        ct);
    return Results.Ok(ToCandidateProfileDto(profile));
});

profileApi.MapPost("/profiles", async (
    [FromBody] CandidateProfileCreateRequest? request,
    IProfileService profileSvc,
    CancellationToken ct) =>
{
    if (request is null)
    {
        return Results.BadRequest(new { message = "Os dados do perfil estão vazios." });
    }

    if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 80)
    {
        return Results.BadRequest(new { message = "Informe um nome de perfil com até 80 caracteres." });
    }

    var validationMessage = ValidateProfileContent(
        request.Content,
        request.IncludeTitleKeywords,
        request.ExcludeTitleKeywords);
    if (validationMessage is not null)
    {
        return Results.BadRequest(new { message = validationMessage });
    }

    try
    {
        var profile = await profileSvc.CreateProfileAsync(
            request.Name,
            request.Content,
            request.IncludeTitleKeywords,
            request.ExcludeTitleKeywords,
            ct);
        return Results.Created($"/api/profile/profiles/{profile.Id}", ToCandidateProfileDto(profile));
    }
    catch (InvalidOperationException ex)
    {
        return Results.Conflict(new { message = ex.Message });
    }
});

profileApi.MapPut("/profiles/{id}/activate", async (
    string id,
    IProfileService profileSvc,
    CancellationToken ct) =>
{
    var profile = await profileSvc.ActivateProfileAsync(id, ct);
    return profile is null
        ? Results.NotFound(new { message = "Perfil não encontrado." })
        : Results.Ok(ToCandidateProfileDto(profile));
});

profileApi.MapPost("/analyze-pdf", async (
    HttpRequest request,
    CandidateProfileAnalyzer analyzer,
    CancellationToken ct) =>
{
    var maxRequestBytes = CandidateProfileAnalyzer.MaximumPdfBytes + 64 * 1024;
    var bodySize = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
    if (bodySize is { IsReadOnly: false })
    {
        bodySize.MaxRequestBodySize = maxRequestBytes;
    }

    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new { message = "Envie o perfil como um arquivo PDF." });
    }

    if (request.ContentLength is long length && length > maxRequestBytes)
    {
        return Results.Problem("O arquivo excede o limite de 10 MB.", statusCode: StatusCodes.Status413PayloadTooLarge);
    }

    IFormCollection form;
    try
    {
        form = await request.ReadFormAsync(ct);
    }
    catch (InvalidDataException)
    {
        return Results.BadRequest(new { message = "O formulário de upload está inválido." });
    }

    var file = form.Files.GetFile("file");
    if (file is null || file.Length == 0)
    {
        return Results.BadRequest(new { message = "Selecione um arquivo PDF para analisar." });
    }

    if (file.Length > CandidateProfileAnalyzer.MaximumPdfBytes)
    {
        return Results.Problem("O arquivo excede o limite de 10 MB.", statusCode: StatusCodes.Status413PayloadTooLarge);
    }

    if (!Path.GetExtension(file.FileName).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { message = "O arquivo selecionado deve ter extensão .pdf." });
    }

    try
    {
        await using var stream = file.OpenReadStream();
        var analysis = await analyzer.AnalyzePdfAsync(
            stream,
            form["currentProfile"].ToString(),
            ct);
        return Results.Ok(analysis);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (HttpRequestException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway);
    }
    catch (InvalidDataException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway);
    }
});

profileApi.MapPut("/apply-analysis", async (
    [FromBody] ProfileAnalysisDto? analysis,
    IProfileService profileSvc,
    IConfigurationService configSvc,
    CancellationToken ct) =>
{
    if (analysis is null)
    {
        return Results.BadRequest(new { message = "A análise do perfil está vazia." });
    }

    if (string.IsNullOrWhiteSpace(analysis.ProfileMarkdown) || analysis.ProfileMarkdown.Length > 30_000)
    {
        return Results.BadRequest(new { message = "O perfil precisa conter texto e não pode exceder 30.000 caracteres." });
    }

    if (analysis.IncludeTitleKeywords is null
        || analysis.ExcludeTitleKeywords is null
        || analysis.IncludeTitleKeywords.Count > 25
        || analysis.ExcludeTitleKeywords.Count > 25
        || analysis.IncludeTitleKeywords.Concat(analysis.ExcludeTitleKeywords)
            .Any(keyword => string.IsNullOrWhiteSpace(keyword) || keyword.Length > 100))
    {
        return Results.BadRequest(new { message = "Revise as palavras-chave: cada lista aceita até 25 termos de no máximo 100 caracteres." });
    }

    var config = await configSvc.GetConfigurationAsync(ct);
    config.Agent.IncludeTitleKeywords = analysis.IncludeTitleKeywords
        .Select(keyword => keyword.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
    config.Agent.ExcludeTitleKeywords = analysis.ExcludeTitleKeywords
        .Select(keyword => keyword.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    await profileSvc.SaveActiveProfileAsync(
        analysis.ProfileMarkdown,
        config.Agent.IncludeTitleKeywords,
        config.Agent.ExcludeTitleKeywords,
        ct);
    await configSvc.SaveConfigurationAsync(config, ct);

    return Results.Ok(new { success = true, message = "Perfil e filtros atualizados com as sugestões revisadas." });
});

app.Run();

static CandidateProfileDto ToCandidateProfileDto(CandidateProfile profile) => new()
{
    Id = profile.Id,
    Name = profile.Name,
    ProfileMarkdown = profile.ProfileMarkdown,
    IncludeTitleKeywords = profile.IncludeTitleKeywords,
    ExcludeTitleKeywords = profile.ExcludeTitleKeywords,
    IsActive = profile.IsActive,
    CreatedAt = profile.CreatedAt,
    UpdatedAt = profile.UpdatedAt
};

static string? ValidateProfileContent(
    string? content,
    IReadOnlyCollection<string>? includeKeywords,
    IReadOnlyCollection<string>? excludeKeywords)
{
    if (string.IsNullOrWhiteSpace(content) || content.Length > 30_000)
    {
        return "O perfil precisa conter texto e não pode exceder 30.000 caracteres.";
    }

    if (includeKeywords is null
        || excludeKeywords is null
        || includeKeywords.Count > 25
        || excludeKeywords.Count > 25
        || includeKeywords.Concat(excludeKeywords)
            .Any(keyword => string.IsNullOrWhiteSpace(keyword) || keyword.Length > 100))
    {
        return "Revise as palavras-chave: cada lista aceita até 25 termos de no máximo 100 caracteres.";
    }

    return null;
}

public sealed record ProfileUpdateRequest(string Content);
