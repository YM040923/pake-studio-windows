using System.Reflection;

namespace PakeStudio.Services;

public static class OAuthConfiguration
{
    public static string GetClientId()
    {
        var environmentValue = Environment.GetEnvironmentVariable("PAKE_STUDIO_GITHUB_CLIENT_ID");
        if (!string.IsNullOrWhiteSpace(environmentValue))
        {
            return environmentValue.Trim();
        }

        var metadata = Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "GitHubOAuthClientId");

        return metadata?.Value?.Trim() ?? "";
    }
}
