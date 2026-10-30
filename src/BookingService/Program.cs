using Microsoft.EntityFrameworkCore;
using Shipping.BookingService;
using Shipping.Platform;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddSecretManager(builder.Configuration["Gcp:ProjectId"], new Dictionary<string, string>
{
    ["db-password"] = "Db:Password",
});
builder.AddPlatformDefaults();

builder.Services.AddDbContext<BookingDb>((sp, o) =>
    o.UseNpgsql(sp.GetRequiredService<IConfiguration>().GetNpgsqlConnectionString("booking-service")));

builder.Services.AddJwtResourceServer(builder.Configuration, audience: "booking-api");
builder.Services.AddAuthorizationBuilder().AddScopePolicy("booking");

builder.Services.AddOutboxRelay<BookingDb>(schema: "booking");
builder.Services.AddPlatformHealth<BookingDb>();

var app = builder.Build();

await PubSubEmulator.EnsureAsync(app.Configuration, withSubscription: false, app.Logger);
await app.Services.EnsureDatabaseAsync<BookingDb>(schema: "booking", probeTable: "shipments");

app.UseAuthentication();
app.UseAuthorization();

app.MapPlatformHealth();
app.MapShipmentEndpoints();

app.Run();
