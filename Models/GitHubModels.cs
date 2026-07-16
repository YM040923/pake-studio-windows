using System.Text.Json.Serialization;

namespace PakeStudio.Models;

public sealed class DeviceCodeResponse
{
    [JsonPropertyName("device_code")]
    public string DeviceCode { get; set; } = "";

    [JsonPropertyName("user_code")]
    public string UserCode { get; set; } = "";

    [JsonPropertyName("verification_uri")]
    public string VerificationUri { get; set; } = "";

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("interval")]
    public int Interval { get; set; } = 5;
}

public sealed class OAuthTokenResponse
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("token_type")]
    public string? TokenType { get; set; }

    [JsonPropertyName("scope")]
    public string? Scope { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("error_description")]
    public string? ErrorDescription { get; set; }
}

public sealed class GitHubUser
{
    [JsonPropertyName("login")]
    public string Login { get; set; } = "";

    [JsonPropertyName("avatar_url")]
    public string AvatarUrl { get; set; } = "";

    [JsonPropertyName("html_url")]
    public string HtmlUrl { get; set; } = "";
}

public sealed class GitHubRepository
{
    [JsonPropertyName("full_name")]
    public string FullName { get; set; } = "";

    [JsonPropertyName("default_branch")]
    public string DefaultBranch { get; set; } = "main";

    [JsonPropertyName("html_url")]
    public string HtmlUrl { get; set; } = "";
}

public sealed class WorkflowRunsResponse
{
    [JsonPropertyName("workflow_runs")]
    public List<WorkflowRun> WorkflowRuns { get; set; } = [];
}

public sealed class WorkflowRun
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("conclusion")]
    public string? Conclusion { get; set; }

    [JsonPropertyName("html_url")]
    public string HtmlUrl { get; set; } = "";

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ArtifactsResponse
{
    [JsonPropertyName("artifacts")]
    public List<GitHubArtifact> Artifacts { get; set; } = [];
}

public sealed class GitHubArtifact
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("expired")]
    public bool Expired { get; set; }

    [JsonPropertyName("archive_download_url")]
    public string ArchiveDownloadUrl { get; set; } = "";
}
