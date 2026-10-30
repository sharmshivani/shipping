using Google.Cloud.SecretManager.V1;
using Microsoft.Extensions.Configuration;

namespace Shipping.Platform;

public static class SecretManagerConfiguration
{
    /// <summary>
    /// Loads secrets from GCP Secret Manager into configuration at startup.
    /// No-op when <paramref name="projectId"/> is empty (local dev uses appsettings / env vars).
    /// Auth is Application Default Credentials: on GKE that is Workload Identity, no key files.
    /// Secrets are read once at startup; rotate by restarting pods (kubectl rollout restart).
    /// </summary>
    public static IConfigurationBuilder AddSecretManager(
        this IConfigurationBuilder builder,
        string? projectId,
        IReadOnlyDictionary<string, string> secretIdToConfigKey)
    {
        if (string.IsNullOrWhiteSpace(projectId)) return builder;

        var client = SecretManagerServiceClient.Create();
        var values = new Dictionary<string, string?>();
        foreach (var (secretId, configKey) in secretIdToConfigKey)
        {
            var response = client.AccessSecretVersion(new SecretVersionName(projectId, secretId, "latest"));
            values[configKey] = response.Payload.Data.ToStringUtf8();
        }

        // Added last => overrides appsettings and environment variables.
        return builder.AddInMemoryCollection(values);
    }
}
