using Microsoft.EntityFrameworkCore;
using Shipping.PaymentService;
using Shipping.Platform;

var builder = WebApplication.CreateBuilder(args);

var provider = (builder.Configuration["Payments:Provider"] ?? "fake").ToLowerInvariant();

var secrets = new Dictionary<string, string> { ["db-password"] = "Db:Password" };
if (provider == "razorpay")
{
    secrets["razorpay-key-id"] = "Payments:Razorpay:KeyId";
    secrets["razorpay-key-secret"] = "Payments:Razorpay:KeySecret";
    secrets["razorpay-webhook-secret"] = "Payments:Razorpay:WebhookSecret";
}
builder.Configuration.AddSecretManager(builder.Configuration["Gcp:ProjectId"], secrets);
builder.AddPlatformDefaults();

builder.Services.AddDbContext<PaymentDb>((sp, o) =>
    o.UseNpgsql(sp.GetRequiredService<IConfiguration>().GetNpgsqlConnectionString("payment-service")));

builder.Services.AddJwtResourceServer(builder.Configuration, audience: "payment-api");
builder.Services.AddAuthorizationBuilder().AddScopePolicy("payment");

// Payment gateway selection. "fake" = local/demo, "razorpay" = real (test or live keys).
switch (provider)
{
    case "razorpay":
        builder.Services.Configure<RazorpayOptions>(builder.Configuration.GetSection("Payments:Razorpay"));
        builder.Services.AddHttpClient<IPaymentGateway, RazorpayGateway>((sp, http) =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<RazorpayOptions>>().Value;
            http.BaseAddress = new Uri(opts.BaseUrl);
            http.Timeout = TimeSpan.FromSeconds(15);
        });
        break;
    case "fake":
        builder.Services.AddSingleton<IPaymentGateway, FakeGateway>();
        break;
    default:
        throw new InvalidOperationException($"Unknown Payments:Provider '{provider}' (use 'fake' or 'razorpay').");
}

builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient<IBookingClient, BookingClient>(http =>
{
    http.BaseAddress = new Uri(builder.Configuration["Services:Booking"] ?? "http://booking-service");
    http.Timeout = TimeSpan.FromSeconds(10);
});

builder.Services.AddScoped<PaymentWorkflow>();
builder.Services.AddOutboxRelay<PaymentDb>(schema: "payment");
builder.Services.AddPlatformHealth<PaymentDb>();

var app = builder.Build();

await PubSubEmulator.EnsureAsync(app.Configuration, withSubscription: false, app.Logger);
await app.Services.EnsureDatabaseAsync<PaymentDb>(schema: "payment", probeTable: "payments");

app.UseAuthentication();
app.UseAuthorization();

app.MapPlatformHealth();
app.MapPaymentEndpoints();

app.Run();
