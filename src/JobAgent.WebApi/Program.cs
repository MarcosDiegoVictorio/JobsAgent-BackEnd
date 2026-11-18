using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Google.Apis.Auth;
using JobAgent.Domain.Entities;
using JobAgent.Domain.Enums;
using JobAgent.Domain.Interfaces;
using JobAgent.Domain.Models;
using JobAgent.Service;
using JobAgent.Service.Extensions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddJobAgentCore(builder.Configuration);

var authOptions = builder.Configuration.GetSection("Auth").Get<AuthOptions>() ?? new AuthOptions();
if (Encoding.UTF8.GetByteCount(authOptions.JwtSigningKey) < 32)
{
    throw new InvalidOperationException(
        "Configure Auth:JwtSigningKey com ao menos 32 caracteres aleatórios antes de iniciar a API.");
}

var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(authOptions.JwtSigningKey));
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = authOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = authOptions.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = signingKey,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = ClaimTypes.Name,
            RoleClaimType = ClaimTypes.Role
        };
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                var userId = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier)
                    ?? context.Principal?.FindFirstValue(JwtRegisteredClaimNames.Sub);
                var repository = context.HttpContext.RequestServices.GetRequiredService<IUserRepository>();
                var user = string.IsNullOrWhiteSpace(userId)
                    ? null
                    : await repository.GetByIdAsync(userId, context.HttpContext.RequestAborted);
                if (user is null || user.IsDisabled)
                {
                    context.Fail("A conta não está disponível.");
                    return;
                }

                if (context.Principal?.Identity is ClaimsIdentity identity)
                {
                    foreach (var roleClaim in identity.FindAll(identity.RoleClaimType).ToList())
                    {
                        identity.RemoveClaim(roleClaim);
                    }

                    identity.AddClaim(new Claim(identity.RoleClaimType, user.Role));
                }
            }
        };
    });
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
    options.AddPolicy("AdminOnly", policy => policy.RequireRole("admin"));
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("auth", context =>
    {
        var clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(clientIp, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        });
    });
});

builder.Services.AddOpenApi();
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
    {
        policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod();
    });
});

var app = builder.Build();
using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<IUserRepository>().InitAsync();
}

app.UseCors("Frontend");
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

var authApi = app.MapGroup("/api/auth")
    .WithTags("Authentication")
    .AllowAnonymous()
    .RequireRateLimiting("auth");
authApi.MapGet("/config", (IOptions<AuthOptions> options) =>
{
    if (string.IsNullOrWhiteSpace(options.Value.GoogleClientId))
    {
        return Results.Problem("O login do Google não está configurado.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    return Results.Ok(new { googleClientId = options.Value.GoogleClientId, inviteOnly = true });
});
authApi.MapPost("/nonce", async (IAuthService auth, CancellationToken ct) =>
    Results.Ok(new { nonce = await auth.CreateNonceAsync(ct) }));
authApi.MapPost("/google", async (
    [FromBody] GoogleSignInRequest request,
    IAuthService auth,
    CancellationToken ct) =>
{
    try
    {
        return Results.Ok(await auth.SignInAsync(request, ct));
    }
    catch (UnauthorizedAccessException ex)
    {
        return Results.Json(new { message = ex.Message }, statusCode: StatusCodes.Status401Unauthorized);
    }
    catch (InvalidJwtException)
    {
        return Results.Unauthorized();
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

var accountApi = app.MapGroup("/api/account").WithTags("Account").RequireAuthorization();
accountApi.MapGet("/", async (
    ClaimsPrincipal principal,
    IUserRepository users,
    IJobRepository jobs,
    CancellationToken ct) =>
{
    var userId = GetUserId(principal);
    var user = await users.GetByIdAsync(userId, ct);
    if (user is null)
    {
        return Results.Unauthorized();
    }

    var count = await jobs.GetCountAsync(userId, ct);
    var latest = await users.GetLatestSearchAsync(userId, ct);
    return Results.Ok(new SearchUserStatusDto
    {
        User = ToAuthUserDto(user, count),
        LastRun = latest is null ? new RunStatus() : ToRunStatus(latest)
    });
});

var adminApi = app.MapGroup("/api/admin").WithTags("Administration").RequireAuthorization("AdminOnly");
adminApi.MapGet("/users", async (IUserRepository users, CancellationToken ct) =>
    Results.Ok(await users.GetUsersAsync(ct)));
adminApi.MapPost("/invitations", async (
    [FromBody] InvitationCreateRequest request,
    ClaimsPrincipal principal,
    IUserRepository users,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Email)
        || request.Email.Length > 254
        || !System.Net.Mail.MailAddress.TryCreate(request.Email.Trim(), out var address)
        || !address.Address.Equals(request.Email.Trim(), StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { message = "Informe um e-mail válido." });
    }

    if (request.ExpiresInDays is < 1 or > 30)
    {
        return Results.BadRequest(new { message = "A validade do convite deve ser de 1 a 30 dias." });
    }

    var code = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
    var invitation = await users.CreateInvitationAsync(
        address.Address,
        code,
        hash,
        GetUserId(principal),
        DateTimeOffset.UtcNow.AddDays(request.ExpiresInDays),
        ct);
    return Results.Created("/api/admin/invitations", invitation);
});
adminApi.MapPost("/users/{userId}/search-credit", async (
    string userId,
    ClaimsPrincipal principal,
    IUserRepository users,
    CancellationToken ct) =>
{
    var credits = await users.GrantSearchCreditAsync(userId, GetUserId(principal), ct);
    return credits is null
        ? Results.NotFound(new { message = "Usuário não encontrado ou desativado." })
        : Results.Ok(new SearchCreditGrantDto { SearchCreditsRemaining = credits.Value });
});

var jobsApi = app.MapGroup("/api/jobs").WithTags("Jobs").RequireAuthorization();
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
    ClaimsPrincipal principal,
    IJobRepository repo,
    IProfileService profileSvc,
    CancellationToken ct) =>
{
    var userId = GetUserId(principal);
    var profile = await profileSvc.GetActiveProfileAsync(userId, ct);
    var filter = new JobFilter
    {
        UserId = userId,
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
    return Results.Ok(await repo.GetPagedAsync(filter, ct));
});

jobsApi.MapGet("/stats", async (
    ClaimsPrincipal principal,
    IJobRepository repo,
    CancellationToken ct) =>
    Results.Ok(await repo.GetStatisticsAsync(GetUserId(principal), ct)));

jobsApi.MapGet("/{id}", async (
    string id,
    ClaimsPrincipal principal,
    IJobRepository repo,
    IProfileService profileSvc,
    CancellationToken ct) =>
{
    var userId = GetUserId(principal);
    var profile = await profileSvc.GetActiveProfileAsync(userId, ct);
    var job = await repo.GetByIdAsync(id, userId, profile.Id, ct);
    return job is not null ? Results.Ok(job) : Results.NotFound(new { message = "Vaga não encontrada" });
});

jobsApi.MapPatch("/{id}/status", async (
    string id,
    [FromBody] UpdateJobStatusRequest request,
    ClaimsPrincipal principal,
    IJobRepository repo,
    IProfileService profileSvc,
    CancellationToken ct) =>
{
    if (!JobStatus.IsValid(request.Status))
    {
        return Results.BadRequest(new { message = $"Status inválido. Permitidos: {string.Join(", ", JobStatus.All)}" });
    }

    var userId = GetUserId(principal);
    var profile = await profileSvc.GetActiveProfileAsync(userId, ct);
    var updated = await repo.UpdateStatusAsync(id, userId, profile.Id, request.Status, request.Notes, ct);
    return updated ? Results.Ok(new { success = true, id, status = request.Status }) : Results.NotFound();
});

jobsApi.MapDelete("/{id}", async (
    string id,
    ClaimsPrincipal principal,
    IJobRepository repo,
    IProfileService profileSvc,
    CancellationToken ct) =>
{
    var userId = GetUserId(principal);
    var profile = await profileSvc.GetActiveProfileAsync(userId, ct);
    var deleted = await repo.DeleteAsync(id, userId, profile.Id, ct);
    return deleted ? Results.Ok(new { success = true, id }) : Results.NotFound();
});

var orchestratorApi = app.MapGroup("/api/orchestrator").WithTags("Orchestrator").RequireAuthorization();
orchestratorApi.MapGet("/status", async (
    ClaimsPrincipal principal,
    IJobOrchestrator orchestrator,
    IUserRepository users,
    CancellationToken ct) =>
{
    var userId = GetUserId(principal);
    var active = orchestrator.GetCurrentStatus(userId);
    if (active.IsRunning)
    {
        return Results.Ok(active);
    }

    var latest = await users.GetLatestSearchAsync(userId, ct);
    return Results.Ok(latest is null ? active : ToRunStatus(latest));
});
orchestratorApi.MapPost("/run", async (
    ClaimsPrincipal principal,
    IJobOrchestrator orchestrator,
    IProfileService profiles,
    IUserRepository users,
    ILoggerFactory loggerFactory,
    CancellationToken ct) =>
{
    var userId = GetUserId(principal);
    var profile = await profiles.GetActiveProfileAsync(userId, ct);
    if (string.IsNullOrWhiteSpace(profile.ProfileMarkdown))
    {
        return Results.BadRequest(new { message = "Preencha e salve seu perfil antes de iniciar a busca." });
    }

    var reservation = await users.ReserveSearchAsync(userId, ct);
    if (!reservation.Accepted || reservation.RunId is null)
    {
        return Results.Conflict(new { message = reservation.Error });
    }

    var logger = loggerFactory.CreateLogger("JobSearch");
    var runId = reservation.RunId;
    _ = Task.Run(async () =>
    {
        try
        {
            await orchestrator.RunOnceAsync(userId, runId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Busca {RunId} do usuário {UserId} falhou.", runId, userId);
        }
    }, CancellationToken.None);

    return Results.Accepted(value: new
    {
        message = "Busca iniciada.",
        remainingSearchCredits = reservation.RemainingCredits,
        savedJobsCount = reservation.SavedJobsCount
    });
});

var configApi = app.MapGroup("/api/config").WithTags("Configuration").RequireAuthorization("AdminOnly");
configApi.MapGet("/", async (
    ClaimsPrincipal principal,
    IConfigurationService configSvc,
    IProfileService profileSvc,
    CancellationToken ct) =>
{
    var config = await configSvc.GetConfigurationAsync(ct);
    var profile = await profileSvc.GetActiveProfileAsync(GetUserId(principal), ct);
    config.Agent.IncludeTitleKeywords = profile.IncludeTitleKeywords;
    config.Agent.ExcludeTitleKeywords = profile.ExcludeTitleKeywords;
    return Results.Ok(config);
});
configApi.MapPut("/", async (
    [FromBody] AppConfigDto config,
    ClaimsPrincipal principal,
    IConfigurationService configSvc,
    IProfileService profileSvc,
    CancellationToken ct) =>
{
    await configSvc.SaveConfigurationAsync(config, ct);
    await profileSvc.UpdateActiveKeywordsAsync(
        GetUserId(principal),
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

var profileApi = app.MapGroup("/api/profile").WithTags("Profile").RequireAuthorization();
profileApi.MapGet("/", async (ClaimsPrincipal principal, IProfileService profiles, CancellationToken ct) =>
    Results.Ok(new { content = await profiles.GetProfileAsync(GetUserId(principal), ct) }));
profileApi.MapPut("/", async (
    [FromBody] ProfileUpdateRequest request,
    ClaimsPrincipal principal,
    IProfileService profiles,
    CancellationToken ct) =>
{
    await profiles.SaveProfileAsync(GetUserId(principal), request.Content, ct);
    return Results.Ok(new { success = true, message = "Perfil salvo com sucesso!" });
});
profileApi.MapGet("/active", async (ClaimsPrincipal principal, IProfileService profiles, CancellationToken ct) =>
    Results.Ok(ToCandidateProfileDto(await profiles.GetActiveProfileAsync(GetUserId(principal), ct))));
profileApi.MapGet("/profiles", async (ClaimsPrincipal principal, IProfileService profiles, CancellationToken ct) =>
    Results.Ok((await profiles.GetProfilesAsync(GetUserId(principal), ct)).Select(ToCandidateProfileDto)));
profileApi.MapPut("/active", async (
    [FromBody] CandidateProfileUpdateRequest? request,
    ClaimsPrincipal principal,
    IProfileService profiles,
    CancellationToken ct) =>
{
    if (request is null)
    {
        return Results.BadRequest(new { message = "Os dados do perfil estão vazios." });
    }

    var validationMessage = ValidateProfileContent(request.Content, request.IncludeTitleKeywords, request.ExcludeTitleKeywords);
    if (validationMessage is not null)
    {
        return Results.BadRequest(new { message = validationMessage });
    }

    var profile = await profiles.SaveActiveProfileAsync(
        GetUserId(principal), request.Content, request.IncludeTitleKeywords, request.ExcludeTitleKeywords, ct);
    return Results.Ok(ToCandidateProfileDto(profile));
});
profileApi.MapPost("/profiles", async (
    [FromBody] CandidateProfileCreateRequest? request,
    ClaimsPrincipal principal,
    IProfileService profiles,
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

    var validationMessage = ValidateProfileContent(request.Content, request.IncludeTitleKeywords, request.ExcludeTitleKeywords);
    if (validationMessage is not null)
    {
        return Results.BadRequest(new { message = validationMessage });
    }

    try
    {
        var profile = await profiles.CreateProfileAsync(
            GetUserId(principal), request.Name, request.Content,
            request.IncludeTitleKeywords, request.ExcludeTitleKeywords, ct);
        return Results.Created($"/api/profile/profiles/{profile.Id}", ToCandidateProfileDto(profile));
    }
    catch (InvalidOperationException ex)
    {
        return Results.Conflict(new { message = ex.Message });
    }
});
profileApi.MapPut("/profiles/{id}/activate", async (
    string id,
    ClaimsPrincipal principal,
    IProfileService profiles,
    CancellationToken ct) =>
{
    var profile = await profiles.ActivateProfileAsync(GetUserId(principal), id, ct);
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
        return Results.Ok(await analyzer.AnalyzePdfAsync(stream, form["currentProfile"].ToString(), ct));
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
    ClaimsPrincipal principal,
    IProfileService profiles,
    CancellationToken ct) =>
{
    if (analysis is null)
    {
        return Results.BadRequest(new { message = "A análise do perfil está vazia." });
    }

    var validationMessage = ValidateProfileContent(
        analysis.ProfileMarkdown, analysis.IncludeTitleKeywords, analysis.ExcludeTitleKeywords);
    if (validationMessage is not null)
    {
        return Results.BadRequest(new { message = validationMessage });
    }

    var includeKeywords = analysis.IncludeTitleKeywords.Select(keyword => keyword.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    var excludeKeywords = analysis.ExcludeTitleKeywords.Select(keyword => keyword.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    var profile = await profiles.SaveActiveProfileAsync(
        GetUserId(principal), analysis.ProfileMarkdown, includeKeywords, excludeKeywords, ct);
    return Results.Ok(new { success = true, message = "Perfil e filtros atualizados com as sugestões revisadas.", profile = ToCandidateProfileDto(profile) });
});

app.Run();

static string GetUserId(ClaimsPrincipal principal) =>
    principal.FindFirstValue(ClaimTypes.NameIdentifier)
    ?? throw new UnauthorizedAccessException("A identidade do usuário não foi encontrada.");

static AuthUserDto ToAuthUserDto(JobAgent.Domain.Entities.AppUser user, int savedJobsCount) => new()
{
    Id = user.Id,
    Email = user.Email,
    DisplayName = user.DisplayName,
    PictureUrl = user.PictureUrl,
    Role = user.Role,
    SearchCreditsRemaining = user.SearchCreditsRemaining,
    SavedJobsCount = savedJobsCount
};

static RunStatus ToRunStatus(SearchRunRecord run) => new()
{
    IsRunning = run.Status == "running",
    LastRunTime = run.StartedAt,
    LastRunDuration = run.CompletedAt is null ? null : run.CompletedAt - run.StartedAt,
    LastScoredCount = run.EvaluatedCount,
    LastCollectedCount = run.CollectedCount,
    LastError = run.Error,
    TotalRuns = 1,
    CurrentPhase = run.Status == "running" ? "Em andamento" : "Concluído"
};

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
