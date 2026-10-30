using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Shipping.AuthService;

public sealed class AppUser : IdentityUser
{
    public string? DisplayName { get; set; }
}

/// <summary>
/// Everything lives in the "auth" schema: ASP.NET Core Identity (auth.users = the diagram's "User accounts"),
/// OpenIddict clients/tokens/authorizations, and the shared Data Protection keys
/// (so cookies + anti-forgery tokens work across multiple auth-service replicas).
/// </summary>
public sealed class AuthDb(DbContextOptions<AuthDb> options)
    : IdentityDbContext<AppUser>(options), IDataProtectionKeyContext
{
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema("auth");
        builder.Entity<AppUser>().ToTable("users");
    }
}
