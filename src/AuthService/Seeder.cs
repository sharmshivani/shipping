using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Shipping.AuthService;

/// <summary>
/// Registers OAuth clients and scopes at startup (upsert, so config changes are applied on every deploy).
///   dashboard    public client  - authorization code + PKCE + refresh (the browser SPA)
///   carrier-sim  confidential   - client_credentials, scope status.write (stands in for a carrier / ops system)
///   dev-cli      public client  - password grant, ONLY when Auth:EnablePasswordGrant=true (local testing)
/// Scope -> audience: booking -> booking-api, payment -> payment-api, status.* -> status-api.
/// </summary>
public static class Seeder
{
    public static async Task SeedAsync(IServiceProvider services, IConfiguration cfg, bool allowPasswordGrant)
    {
        await using var scope = services.CreateAsyncScope();
        var scopes = scope.ServiceProvider.GetRequiredService<IOpenIddictScopeManager>();
        var apps = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();

        foreach (var (name, audience) in new[]
                 {
                     ("booking", "booking-api"), ("payment", "payment-api"),
                     ("status.read", "status-api"), ("status.write", "status-api"),
                 })
        {
            var descriptor = new OpenIddictScopeDescriptor { Name = name, DisplayName = name, Resources = { audience } };
            var existing = await scopes.FindByNameAsync(name);
            if (existing is null) await scopes.CreateAsync(descriptor); else await scopes.UpdateAsync(existing, descriptor);
        }

        // Origins that may host the dashboard, e.g. "https://shipping.example.com;http://localhost:8080"
        var origins = (cfg["Seed:DashboardOrigins"] ?? "http://localhost:8080")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(o => o.TrimEnd('/')).ToArray();

        var dashboard = new OpenIddictApplicationDescriptor
        {
            ClientId = "dashboard",
            ClientType = ClientTypes.Public,
            ConsentType = ConsentTypes.Implicit,   // first-party app: no consent screen
            DisplayName = "Shipping dashboard",
            Permissions =
            {
                Permissions.Endpoints.Authorization, Permissions.Endpoints.Token, Permissions.Endpoints.EndSession,
                Permissions.GrantTypes.AuthorizationCode, Permissions.GrantTypes.RefreshToken,
                Permissions.ResponseTypes.Code,
                Permissions.Scopes.Email, Permissions.Scopes.Profile,
                Permissions.Prefixes.Scope + "booking", Permissions.Prefixes.Scope + "payment",
                Permissions.Prefixes.Scope + "status.read",
            },
            Requirements = { Requirements.Features.ProofKeyForCodeExchange },
        };
        foreach (var origin in origins)
        {
            dashboard.RedirectUris.Add(new Uri(origin + "/"));
            dashboard.PostLogoutRedirectUris.Add(new Uri(origin + "/"));
        }
        await UpsertAsync(apps, dashboard);

        var carrierSecret = cfg["Seed:CarrierClientSecret"];
        if (!string.IsNullOrWhiteSpace(carrierSecret))
        {
            await UpsertAsync(apps, new OpenIddictApplicationDescriptor
            {
                ClientId = "carrier-sim",
                ClientSecret = carrierSecret,
                ClientType = ClientTypes.Confidential,
                DisplayName = "Carrier simulator (client credentials)",
                Permissions =
                {
                    Permissions.Endpoints.Token, Permissions.GrantTypes.ClientCredentials,
                    Permissions.Prefixes.Scope + "status.write", Permissions.Prefixes.Scope + "status.read",
                },
            });
        }

        if (allowPasswordGrant)
        {
            await UpsertAsync(apps, new OpenIddictApplicationDescriptor
            {
                ClientId = "dev-cli",
                ClientType = ClientTypes.Public,
                DisplayName = "Local testing client (password grant, DEV ONLY)",
                Permissions =
                {
                    Permissions.Endpoints.Token, Permissions.GrantTypes.Password,
                    Permissions.Scopes.Email, Permissions.Scopes.Profile,
                    Permissions.Prefixes.Scope + "booking", Permissions.Prefixes.Scope + "payment",
                    Permissions.Prefixes.Scope + "status.read",
                },
            });
        }
    }

    private static async Task UpsertAsync(IOpenIddictApplicationManager apps, OpenIddictApplicationDescriptor descriptor)
    {
        var existing = await apps.FindByClientIdAsync(descriptor.ClientId!);
        if (existing is null) await apps.CreateAsync(descriptor); else await apps.UpdateAsync(existing, descriptor);
    }
}
