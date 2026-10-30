using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Shipping.Platform;

public static class PlatformHosting
{
    public static WebApplicationBuilder AddPlatformDefaults(this WebApplicationBuilder builder)
    {
        builder.Services.AddProblemDetails();
        if (!builder.Environment.IsDevelopment())
        {
            // JSON logs are parsed into structured entries by Cloud Logging on GKE.
            builder.Logging.ClearProviders();
            builder.Logging.AddJsonConsole(o => { o.IncludeScopes = true; o.TimestampFormat = "O"; });
        }
        return builder;
    }

    /// <summary>
    /// /healthz/live  -> process is up (used by the load balancer + liveness probe; never touches the DB)
    /// /healthz/ready -> SELECT 1 against Cloud SQL (used by the readiness probe)
    /// </summary>
    public static IEndpointRouteBuilder MapPlatformHealth(this IEndpointRouteBuilder app)
    {
        app.MapHealthChecks("/healthz/live", new HealthCheckOptions { Predicate = _ => false });
        app.MapHealthChecks("/healthz/ready", new HealthCheckOptions { Predicate = r => r.Tags.Contains("ready") });
        return app;
    }
}
