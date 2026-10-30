using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Shipping.AuthService;
using Shipping.Platform;
using static OpenIddict.Abstractions.OpenIddictConstants;

var builder = WebApplication.CreateBuilder(args);

// Secret Manager (no-op locally). jwt-key = RSA private key (PEM) that SIGNS tokens.
// jwt-encryption-key = 256-bit key that encrypts authorization codes / refresh tokens.
builder.Configuration.AddSecretManager(builder.Configuration["Gcp:ProjectId"], new Dictionary<string, string>
{
    ["db-password"] = "Db:Password",
    ["jwt-key"] = "Auth:SigningKeyPem",
    ["jwt-encryption-key"] = "Auth:EncryptionKey",
    ["carrier-client-secret"] = "Seed:CarrierClientSecret",
});
builder.AddPlatformDefaults();

var issuer = builder.Configuration["Auth:Issuer"] ?? throw new InvalidOperationException("Auth:Issuer is required");
var allowPasswordGrant = builder.Configuration.GetValue<bool>("Auth:EnablePasswordGrant");   // DEV ONLY, for curl/.http testing

builder.Services.AddDbContext<AuthDb>((sp, o) =>
{
    o.UseNpgsql(sp.GetRequiredService<IConfiguration>().GetNpgsqlConnectionString("auth-service"));
    o.UseOpenIddict();
});

builder.Services.AddDataProtection().SetApplicationName("shipping-auth").PersistKeysToDbContext<AuthDb>();

builder.Services.AddIdentity<AppUser, IdentityRole>(o =>
    {
        o.User.RequireUniqueEmail = true;
        o.Password.RequiredLength = 8;
        o.Lockout.MaxFailedAccessAttempts = 5;
    })
    .AddEntityFrameworkStores<AuthDb>()
    .AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(o =>
{
    o.Cookie.Name = "shipping.session";
    o.LoginPath = "/account/login";
    o.AccessDeniedPath = "/account/login";
    o.ExpireTimeSpan = TimeSpan.FromHours(8);
    o.SlidingExpiration = true;
});
builder.Services.AddAntiforgery();

builder.Services.AddOpenIddict()
    .AddCore(o => o.UseEntityFrameworkCore().UseDbContext<AuthDb>())
    .AddServer(o =>
    {
        o.SetIssuer(new Uri(issuer));

        o.SetAuthorizationEndpointUris("/connect/authorize")
         .SetTokenEndpointUris("/connect/token")
         .SetUserInfoEndpointUris("/connect/userinfo")
         .SetEndSessionEndpointUris("/connect/logout");
        // Discovery (/.well-known/openid-configuration) and JWKS (/.well-known/jwks) are served by default.

        // OAuth 2.0 / OIDC flows
        o.AllowAuthorizationCodeFlow().RequireProofKeyForCodeExchange();   // browser users (PKCE mandatory)
        o.AllowRefreshTokenFlow();
        o.AllowClientCredentialsFlow();                                    // service-to-service / carrier system
        if (allowPasswordGrant) o.AllowPasswordFlow();                     // dev convenience only

        o.RegisterScopes(Scopes.OpenId, Scopes.Profile, Scopes.Email, Scopes.OfflineAccess,
            "booking", "payment", "status.read", "status.write");

        o.SetAccessTokenLifetime(TimeSpan.FromMinutes(30)).SetRefreshTokenLifetime(TimeSpan.FromDays(7));

        // Access tokens are plain signed JWTs (JWS) so any API can validate them from the JWKS alone.
        o.DisableAccessTokenEncryption();

        var pem = builder.Configuration["Auth:SigningKeyPem"];
        var encryptionKey = builder.Configuration["Auth:EncryptionKey"];
        if (!string.IsNullOrWhiteSpace(pem) && !string.IsNullOrWhiteSpace(encryptionKey))
        {
            var rsa = RSA.Create();
            rsa.ImportFromPem(pem);   // accepts PKCS#1 and PKCS#8
            var kid = Convert.ToHexString(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()))[..16];
            o.AddSigningKey(new RsaSecurityKey(rsa) { KeyId = kid });
            o.AddEncryptionKey(new SymmetricSecurityKey(Convert.FromBase64String(encryptionKey)));
        }
        else if (builder.Environment.IsDevelopment())
        {
            o.AddEphemeralSigningKey();        // new keys on every start: fine for one local instance
            o.AddEphemeralEncryptionKey();
        }
        else
        {
            throw new InvalidOperationException("Auth:SigningKeyPem and Auth:EncryptionKey (Secret Manager: jwt-key, jwt-encryption-key) are required outside Development.");
        }

        o.UseAspNetCore()
         .EnableAuthorizationEndpointPassthrough()
         .EnableTokenEndpointPassthrough()
         .EnableUserInfoEndpointPassthrough()
         .EnableEndSessionEndpointPassthrough()
         .DisableTransportSecurityRequirement();   // TLS terminates at the load balancer
    })
    .AddValidation(o =>
    {
        o.UseLocalServer();   // lets auth-service protect its own /api/auth/me
        o.UseAspNetCore();
    });

builder.Services.AddAuthorization();
builder.Services.AddPlatformHealth<AuthDb>();

var app = builder.Build();

await app.Services.EnsureDatabaseAsync<AuthDb>(schema: "auth", probeTable: "users");
await Seeder.SeedAsync(app.Services, app.Configuration, allowPasswordGrant);

app.UseAuthentication();
app.UseAuthorization();

app.MapPlatformHealth();
app.MapOidcEndpoints();
app.MapAccountEndpoints();

app.Run();
