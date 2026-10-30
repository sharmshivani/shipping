using System.Net;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using OpenIddict.Validation.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Shipping.AuthService;

public sealed record SignupRequest(string? Email, string? Password, string? Name);

/// <summary>
/// Signup + login UI (the "authorize" page of the OIDC flow) and a JSON signup API.
/// Login itself is NOT an API call: clients redirect the browser to /connect/authorize (authorization code + PKCE).
/// </summary>
public static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var account = app.MapGroup("/account");

        account.MapGet("/login", (HttpContext ctx, IAntiforgery af, string? returnUrl) =>
            Page(AccountPages.Login(Token(ctx, af), returnUrl, null)));

        account.MapPost("/login", async (HttpContext ctx, IAntiforgery af, SignInManager<AppUser> signIn, UserManager<AppUser> users) =>
        {
            await af.ValidateRequestAsync(ctx);
            var form = await ctx.Request.ReadFormAsync();
            var email = form["email"].ToString().Trim();
            var returnUrl = form["returnUrl"].ToString();

            var user = await users.FindByEmailAsync(email);
            if (user is not null)
            {
                var result = await signIn.PasswordSignInAsync(user, form["password"].ToString(), isPersistent: false, lockoutOnFailure: true);
                if (result.Succeeded) return Results.LocalRedirect(SafeLocal(returnUrl));
                if (result.IsLockedOut) return Page(AccountPages.Login(Token(ctx, af), returnUrl, "Account locked. Try again in a few minutes."));
            }
            return Page(AccountPages.Login(Token(ctx, af), returnUrl, "Invalid email or password."));
        });

        account.MapGet("/register", (HttpContext ctx, IAntiforgery af, string? returnUrl) =>
            Page(AccountPages.Register(Token(ctx, af), returnUrl, null)));

        account.MapPost("/register", async (HttpContext ctx, IAntiforgery af, SignInManager<AppUser> signIn, UserManager<AppUser> users) =>
        {
            await af.ValidateRequestAsync(ctx);
            var form = await ctx.Request.ReadFormAsync();
            var returnUrl = form["returnUrl"].ToString();

            var user = new AppUser
            {
                UserName = form["email"].ToString().Trim(),
                Email = form["email"].ToString().Trim(),
                DisplayName = form["name"].ToString().Trim(),
            };
            var result = await users.CreateAsync(user, form["password"].ToString());
            if (!result.Succeeded)
                return Page(AccountPages.Register(Token(ctx, af), returnUrl, string.Join(" ", result.Errors.Select(e => e.Description))));

            await signIn.SignInAsync(user, isPersistent: false);
            return Results.LocalRedirect(SafeLocal(returnUrl));
        });

        // JSON signup for non-browser clients (mobile apps, scripts). Login still happens via OIDC.
        app.MapPost("/api/auth/signup", async (SignupRequest req, UserManager<AppUser> users) =>
        {
            if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrEmpty(req.Password))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["email/password"] = new[] { "Email and password are required." } });

            var user = new AppUser { UserName = req.Email.Trim(), Email = req.Email.Trim(), DisplayName = req.Name?.Trim() };
            var result = await users.CreateAsync(user, req.Password);
            if (!result.Succeeded)
                return Results.ValidationProblem(result.Errors.GroupBy(e => e.Code)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.Description).ToArray()));

            return Results.Created("/api/auth/me", new { id = user.Id, email = user.Email });
        }).AllowAnonymous();

        // Protected by an OIDC access token validated locally by OpenIddict.
        app.MapGet("/api/auth/me", (System.Security.Claims.ClaimsPrincipal user) => Results.Ok(new
        {
            sub = user.FindFirst(Claims.Subject)?.Value,
            email = user.FindFirst(Claims.Email)?.Value,
            name = user.FindFirst(Claims.Name)?.Value,
        })).RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme });

        return app;
    }

    private static string Token(HttpContext ctx, IAntiforgery af) => af.GetAndStoreTokens(ctx).RequestToken ?? "";

    private static IResult Page(string html) => Results.Content(html, "text/html; charset=utf-8");

    /// <summary>Only allow redirects back into this site (open-redirect protection).</summary>
    private static string SafeLocal(string? url) =>
        !string.IsNullOrEmpty(url) && url[0] == '/' && (url.Length == 1 || (url[1] != '/' && url[1] != '\\')) ? url : "/";
}

internal static class AccountPages
{
    public static string Login(string token, string? returnUrl, string? error) => Layout("Sign in", $"""
        <form method="post" action="/account/login">
          <input type="hidden" name="__RequestVerificationToken" value="{E(token)}">
          <input type="hidden" name="returnUrl" value="{E(returnUrl)}">
          <label>Email<input name="email" type="email" required autofocus></label>
          <label>Password<input name="password" type="password" required></label>
          <button>Sign in</button>
          {Err(error)}
          <p class="alt">No account? <a href="/account/register?returnUrl={Uri.EscapeDataString(returnUrl ?? "/")}">Create one</a></p>
        </form>
        """);

    public static string Register(string token, string? returnUrl, string? error) => Layout("Create account", $"""
        <form method="post" action="/account/register">
          <input type="hidden" name="__RequestVerificationToken" value="{E(token)}">
          <input type="hidden" name="returnUrl" value="{E(returnUrl)}">
          <label>Name<input name="name" required></label>
          <label>Email<input name="email" type="email" required></label>
          <label>Password (min 8 chars)<input name="password" type="password" minlength="8" required></label>
          <button>Create account</button>
          {Err(error)}
          <p class="alt">Already registered? <a href="/account/login?returnUrl={Uri.EscapeDataString(returnUrl ?? "/")}">Sign in</a></p>
        </form>
        """);

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
    private static string Err(string? s) => string.IsNullOrEmpty(s) ? "" : $"<p class=\"err\">{E(s)}</p>";

    private static string Layout(string title, string body) => $$"""
        <!doctype html><html lang="en"><head><meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1"><title>{{title}} · Shipping</title>
        <style>
          body{font:16px system-ui,sans-serif;background:#f4f5f7;display:grid;place-items:center;min-height:100vh;margin:0}
          main{background:#fff;padding:2rem;border-radius:12px;width:min(360px,92vw);box-shadow:0 2px 12px #0001}
          h1{font-size:1.25rem;margin:0 0 1rem} label{display:block;margin:.75rem 0;font-size:.85rem;color:#444}
          input{display:block;width:100%;box-sizing:border-box;padding:.6rem;margin-top:.25rem;border:1px solid #ccd;border-radius:8px;font-size:1rem}
          button{width:100%;padding:.7rem;margin-top:.5rem;border:0;border-radius:8px;background:#1a56db;color:#fff;font-size:1rem;cursor:pointer}
          .err{color:#b42318;font-size:.9rem} .alt{font-size:.85rem;text-align:center}
        </style></head><body><main><h1>{{title}}</h1>{{body}}</main></body></html>
        """;
}
