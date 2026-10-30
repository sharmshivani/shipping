using System.Collections.Immutable;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Shipping.AuthService;

/// <summary>
/// The OIDC / OAuth 2.0 endpoints. OpenIddict validates the protocol messages; these handlers only decide
/// WHO the user is and WHICH claims go into which token. Flows implemented:
///   authorization_code + PKCE (browser)   refresh_token   client_credentials (machines)   password (dev only)
/// </summary>
public static class OidcEndpoints
{
    public static IEndpointRouteBuilder MapOidcEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapMethods("/connect/authorize", new[] { "GET", "POST" }, Authorize);
        app.MapPost("/connect/token", Exchange);
        app.MapMethods("/connect/userinfo", new[] { "GET", "POST" }, UserInfo);
        app.MapMethods("/connect/logout", new[] { "GET", "POST" }, Logout);
        return app;
    }

    private static async Task<IResult> Authorize(HttpContext ctx, IOpenIddictScopeManager scopeManager, UserManager<AppUser> users)
    {
        var request = ctx.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

        // Is there a logged-in user (cookie)? If not, bounce to /account/login and come back here afterwards.
        var cookie = await ctx.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        if (!cookie.Succeeded)
        {
            var parameters = ctx.Request.HasFormContentType
                ? ctx.Request.Form.ToList()
                : ctx.Request.Query.ToList();

            return Results.Challenge(
                new AuthenticationProperties
                {
                    RedirectUri = ctx.Request.PathBase + ctx.Request.Path + QueryString.Create(parameters),
                },
                new[] { IdentityConstants.ApplicationScheme });
        }

        var user = await users.GetUserAsync(cookie.Principal!)
            ?? throw new InvalidOperationException("The user details cannot be retrieved.");

        var principal = await CreatePrincipalAsync(user, request.GetScopes(), users, scopeManager);
        return Results.SignIn(principal, properties: null, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private static async Task<IResult> Exchange(
        HttpContext ctx,
        IOpenIddictApplicationManager applications,
        IOpenIddictScopeManager scopeManager,
        UserManager<AppUser> users,
        SignInManager<AppUser> signIn)
    {
        var request = ctx.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

        if (request.IsClientCredentialsGrantType())
        {
            // Machine identity: no user, the "subject" is the client itself.
            var application = await applications.FindByClientIdAsync(request.ClientId!)
                ?? throw new InvalidOperationException("The application details cannot be found.");

            var identity = new ClaimsIdentity(TokenValidationParameters.DefaultAuthenticationType, Claims.Name, Claims.Role);
            identity.SetClaim(Claims.Subject, await applications.GetClientIdAsync(application));
            identity.SetClaim(Claims.Name, await applications.GetDisplayNameAsync(application));
            identity.SetScopes(request.GetScopes());
            identity.SetResources(await ListResourcesAsync(scopeManager, identity.GetScopes()));
            identity.SetDestinations(static _ => new[] { Destinations.AccessToken });

            return Results.SignIn(new ClaimsPrincipal(identity), properties: null,
                authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        if (request.IsPasswordGrantType())
        {
            var user = await users.FindByEmailAsync(request.Username!) ?? await users.FindByNameAsync(request.Username!);
            if (user is null || !(await signIn.CheckPasswordSignInAsync(user, request.Password!, lockoutOnFailure: true)).Succeeded)
                return Fail(Errors.InvalidGrant, "The username/password couple is invalid.");

            var principal = await CreatePrincipalAsync(user, request.GetScopes(), users, scopeManager);
            return Results.SignIn(principal, properties: null, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        if (request.IsAuthorizationCodeGrantType() || request.IsRefreshTokenGrantType())
        {
            // OpenIddict already validated the code / refresh token and rebuilt the principal it stored.
            var result = await ctx.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
            var principal = result.Principal!;

            var user = await users.FindByIdAsync(principal.GetClaim(Claims.Subject)!);
            if (user is null) return Fail(Errors.InvalidGrant, "The token is no longer valid.");
            if (!await signIn.CanSignInAsync(user)) return Fail(Errors.InvalidGrant, "The user is no longer allowed to sign in.");

            foreach (var claim in principal.Claims) claim.SetDestinations(GetDestinations(claim));
            return Results.SignIn(principal, properties: null, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        throw new InvalidOperationException("The specified grant type is not supported.");
    }

    private static async Task<IResult> UserInfo(HttpContext ctx, UserManager<AppUser> users)
    {
        var principal = (await ctx.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)).Principal;
        var user = principal is null ? null : await users.FindByIdAsync(principal.GetClaim(Claims.Subject)!);
        if (principal is null || user is null)
        {
            return Results.Challenge(
                new AuthenticationProperties(new Dictionary<string, string?>
                {
                    [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.InvalidToken,
                    [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = "The access token is invalid or its user no longer exists.",
                }),
                new[] { OpenIddictServerAspNetCoreDefaults.AuthenticationScheme });
        }

        var claims = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [Claims.Subject] = await users.GetUserIdAsync(user),
        };
        if (principal.HasScope(Scopes.Email)) claims[Claims.Email] = (await users.GetEmailAsync(user)) ?? "";
        if (principal.HasScope(Scopes.Profile)) claims[Claims.Name] = user.DisplayName ?? (await users.GetUserNameAsync(user)) ?? "";
        return Results.Ok(claims);
    }

    private static async Task<IResult> Logout(SignInManager<AppUser> signIn)
    {
        await signIn.SignOutAsync();   // clear the login cookie, then let OpenIddict redirect to post_logout_redirect_uri
        return Results.SignOut(new AuthenticationProperties { RedirectUri = "/" },
            new[] { OpenIddictServerAspNetCoreDefaults.AuthenticationScheme });
    }

    // ------------------------------------------------------------------ helpers

    private static async Task<ClaimsPrincipal> CreatePrincipalAsync(
        AppUser user, IEnumerable<string> scopes, UserManager<AppUser> users, IOpenIddictScopeManager scopeManager)
    {
        var identity = new ClaimsIdentity(TokenValidationParameters.DefaultAuthenticationType, Claims.Name, Claims.Role);
        identity.SetClaim(Claims.Subject, await users.GetUserIdAsync(user))
                .SetClaim(Claims.Email, await users.GetEmailAsync(user))
                .SetClaim(Claims.Name, user.DisplayName ?? await users.GetUserNameAsync(user));

        identity.SetScopes(scopes);
        // Scope -> API audience mapping comes from the scope registry (see Seeder): booking -> booking-api, etc.
        identity.SetResources(await ListResourcesAsync(scopeManager, identity.GetScopes()));
        identity.SetDestinations(GetDestinations);
        return new ClaimsPrincipal(identity);
    }

    private static async Task<List<string>> ListResourcesAsync(IOpenIddictScopeManager scopeManager, ImmutableArray<string> scopes)
    {
        var resources = new List<string>();
        await foreach (var resource in scopeManager.ListResourcesAsync(scopes))
            resources.Add(resource);
        return resources;
    }

    /// <summary>Decides which tokens each claim is copied into (access token = what APIs see; id token = what the client app sees).</summary>
    private static IEnumerable<string> GetDestinations(Claim claim)
    {
        var scopes = claim.Subject!.GetScopes();
        switch (claim.Type)
        {
            case Claims.Subject:
                yield return Destinations.AccessToken;
                yield return Destinations.IdentityToken;
                yield break;

            case Claims.Name:
                yield return Destinations.AccessToken;
                if (scopes.Contains(Scopes.Profile)) yield return Destinations.IdentityToken;
                yield break;

            case Claims.Email:
                yield return Destinations.AccessToken;
                if (scopes.Contains(Scopes.Email)) yield return Destinations.IdentityToken;
                yield break;

            default:
                yield return Destinations.AccessToken;
                yield break;
        }
    }

    private static IResult Fail(string error, string description) =>
        Results.Forbid(
            new AuthenticationProperties(new Dictionary<string, string?>
            {
                [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
                [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
            }),
            new[] { OpenIddictServerAspNetCoreDefaults.AuthenticationScheme });
}
