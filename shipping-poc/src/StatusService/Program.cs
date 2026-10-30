using Microsoft.EntityFrameworkCore;
using Shipping.Platform;
using Shipping.StatusService;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddSecretManager(builder.Configuration["Gcp:ProjectId"], new Dictionary<string, string>
{
    ["db-password"] = "Db:Password",
});
builder.AddPlatformDefaults();

builder.Services.AddDbContext<StatusDb>((sp, o) =>
    o.UseNpgsql(sp.GetRequiredService<IConfiguration>().GetNpgsqlConnectionString("status-service")));

builder.Services.AddJwtResourceServer(builder.Configuration, audience: "status-api");
builder.Services.AddAuthorizationBuilder()
    .AddScopePolicy("status.read")
    .AddScopePolicy("status.write");

builder.Services.AddScoped<StatusProjector>();
builder.Services.AddHostedService<EventSubscriber>();
builder.Services.AddPlatformHealth<StatusDb>();       // /healthz/ready = the diagram's "DB health ping (SELECT 1)"

var app = builder.Build();

await PubSubEmulator.EnsureAsync(app.Configuration, withSubscription: true, app.Logger);
await app.Services.EnsureDatabaseAsync<StatusDb>(schema: "status", probeTable: "shipment_status");

app.UseAuthentication();
app.UseAuthorization();

app.MapPlatformHealth();
app.MapStatusEndpoints();

app.Run();
