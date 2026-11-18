using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Google.Apis.Auth;
using JobAgent.Domain.Interfaces;
using JobAgent.Domain.Models;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace JobAgent.Service;

public sealed class AuthService(
    IUserRepository users,
    ICandidateProfileRepository profiles,
    IJobRepository jobs,
    IOptions<AuthOptions> options) : IAuthService
{
    public Task<string> CreateNonceAsync(CancellationToken ct = default) => users.CreateNonceAsync(ct);

    public async Task<AuthResponseDto> SignInAsync(GoogleSignInRequest request, CancellationToken ct = default)
    {
        var auth = options.Value;
        if (string.IsNullOrWhiteSpace(auth.GoogleClientId)
            || Encoding.UTF8.GetByteCount(auth.JwtSigningKey) < 32)
        {
            throw new InvalidOperationException("A autenticação não está configurada corretamente no servidor.");
        }

        if (string.IsNullOrWhiteSpace(request.Credential)
            || string.IsNullOrWhiteSpace(request.Nonce)
            || !await users.ConsumeNonceAsync(request.Nonce, ct))
        {
            throw new UnauthorizedAccessException("A solicitação de autenticação expirou. Tente novamente.");
        }

        GoogleJsonWebSignature.Payload payload;
        try
        {
            payload = await GoogleJsonWebSignature.ValidateAsync(
                request.Credential,
                new GoogleJsonWebSignature.ValidationSettings
                {
                    Audience = [auth.GoogleClientId]
                });
        }
        catch (InvalidJwtException)
        {
            throw new UnauthorizedAccessException("A credencial do Google é inválida ou expirou.");
        }

        if (!string.Equals(payload.Nonce, request.Nonce, StringComparison.Ordinal)
            || !payload.EmailVerified
            || string.IsNullOrWhiteSpace(payload.Subject)
            || string.IsNullOrWhiteSpace(payload.Email))
        {
            throw new UnauthorizedAccessException("Não foi possível validar a identidade da conta Google.");
        }

        var user = await users.SignInOrInviteAsync(new GoogleIdentity
        {
            Subject = payload.Subject,
            Email = payload.Email,
            DisplayName = payload.Name ?? payload.Email,
            PictureUrl = payload.Picture
        }, request.InviteCode, auth.AdminEmail, ct);

        if (user.Role == "admin")
        {
            await profiles.MigrateLegacyProfilesAsync(user.Id, ct);
        }

        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(auth.TokenLifetimeMinutes, 5, 120));
        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(auth.JwtSigningKey));
        var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id),
            new Claim(ClaimTypes.NameIdentifier, user.Id),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(ClaimTypes.Name, user.DisplayName),
            new Claim(ClaimTypes.Role, user.Role)
        };
        var token = new JwtSecurityToken(
            auth.Issuer,
            auth.Audience,
            claims,
            notBefore: DateTime.UtcNow,
            expires: expiresAt.UtcDateTime,
            signingCredentials: credentials);
        var savedJobsCount = await jobs.GetCountAsync(user.Id, ct);

        return new AuthResponseDto
        {
            AccessToken = new JwtSecurityTokenHandler().WriteToken(token),
            ExpiresAt = expiresAt,
            User = new AuthUserDto
            {
                Id = user.Id,
                Email = user.Email,
                DisplayName = user.DisplayName,
                PictureUrl = user.PictureUrl,
                Role = user.Role,
                SearchCreditsRemaining = user.SearchCreditsRemaining,
                SavedJobsCount = savedJobsCount
            }
        };
    }
}
