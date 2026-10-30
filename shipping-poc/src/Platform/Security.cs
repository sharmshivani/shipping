using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Shipping.Platform;

public static class Security
{
    /// <summary>
    /// Validates OIDC-issued JWT access tokens (signature via the auth-service JWKS, issuer, audience, expiry).
    /// Config: Auth:Authority = the PUBLIC issuer URL (must equal the "iss" claim).
    ///         Auth:InternalBaseUrl (optional) = in-cluster URL of auth-service. When set, discovery/JWKS
    ///         requests for the public authority are transparently sent to the internal URL instead,
    ///         so pods never hairpin out through the load balancer.
    /// </summary>
    public static AuthenticationBuilder AddJwtResourceServer(this IServiceCollection services, IConfiguration cfg, string audience)
    {
        var authority = cfg["Auth:Authority"] ?? throw new InvalidOperationException("Auth:Authority is required");
        var internalBase = cfg["Auth:InternalBaseUrl"];

        return services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
        {
            o.Authority = authority;
            o.Audience = audience;
            o.RequireHttpsMetadata = authority.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            o.MapInboundClaims = false;                       // keep "sub", "scope" as issued
            o.TokenValidationParameters.NameClaimType = "name";
            o.TokenValidationParameters.ClockSkew = TimeSpan.FromSeconds(30);

            if (!string.IsNullOrWhiteSpace(internalBase))
            {
                o.BackchannelHttpHandler = new AuthorityRewriteHandler(
                    new Uri(authority.TrimEnd('/') + "/"), new Uri(internalBase));
            }
        });
    }

    public static AuthorizationBuilder AddScopePolicy(this AuthorizationBuilder builder, string scope) =>
        builder.AddPolicy(scope, p => p.RequireAuthenticatedUser().RequireAssertion(c => c.User.HasApiScope(scope)));

    public static string? GetUserId(this ClaimsPrincipal principal) => principal.FindFirst("sub")?.Value;

    /// <summary>"scope" is a space-delimited string in OpenIddict access tokens (RFC 9068); tolerate arrays too.</summary>
    public static bool HasApiScope(this ClaimsPrincipal principal, string scope) =>
        principal.FindAll("scope")
            .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Contains(scope, StringComparer.Ordinal);
}

internal sealed class AuthorityRewriteHandler(Uri publicBase, Uri internalBase) : DelegatingHandler(new HttpClientHandler())
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is { } uri &&
            uri.AbsoluteUri.StartsWith(publicBase.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
        {
            request.RequestUri = new Uri(internalBase, uri.PathAndQuery);
        }
        return base.SendAsync(request, cancellationToken);
    }
}
