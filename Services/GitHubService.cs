using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using PakeStudio.Models;

namespace PakeStudio.Services;

public sealed class GitHubService : IDisposable
{
    private const string ApiVersion = "2022-11-28";
    private const string PakeOwner = "tw93";
    private const string PakeRepository = "Pake";
    private const string WorkflowFile = "pake-cli.yaml";

    private readonly HttpClient _client;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private string? _token;

    public GitHubService()
    {
        _client = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All
        })
        {
            BaseAddress = new Uri("https://api.github.com/"),
            Timeout = TimeSpan.FromSeconds(60)
        };

        _client.DefaultRequestHeaders.UserAgent.ParseAdd("PakeStudio/0.1");
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", ApiVersion);
    }

    public void SetToken(string token)
    {
        _token = token.Trim();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
    }

    public async Task<DeviceCodeResponse> StartDeviceFlowAsync(string clientId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/device/code")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["scope"] = "read:user public_repo workflow"
            })
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _client.SendAsync(request, cancellationToken);
        return await ReadRequiredAsync<DeviceCodeResponse>(response, cancellationToken);
    }

    public async Task<OAuthTokenResponse> PollDeviceFlowAsync(
        string clientId,
        string deviceCode,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/oauth/access_token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["device_code"] = deviceCode,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
            })
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _client.SendAsync(request, cancellationToken);
        return await ReadRequiredAsync<OAuthTokenResponse>(response, cancellationToken);
    }

    public Task<GitHubUser> GetCurrentUserAsync(CancellationToken cancellationToken) =>
        GetRequiredAsync<GitHubUser>("user", cancellationToken);

    public async Task<GitHubRepository> EnsurePakeForkAsync(
        string login,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var existing = await TryGetRepositoryAsync(login, cancellationToken);
        if (existing is not null)
        {
            progress?.Report("已找到你的 Pake 构建仓库");
            await TrySyncForkAsync(login, existing.DefaultBranch, cancellationToken);
            await EnableWorkflowAsync(login, cancellationToken);
            return existing;
        }

        progress?.Report("正在为你创建 Pake 构建仓库");
        using (var response = await _client.PostAsync(
                   $"repos/{PakeOwner}/{PakeRepository}/forks",
                   JsonContent.Create(new { default_branch_only = true }),
                   cancellationToken))
        {
            if (response.StatusCode is not HttpStatusCode.Accepted and not HttpStatusCode.UnprocessableEntity)
            {
                await ThrowGitHubErrorAsync(response, cancellationToken);
            }
        }

        for (var attempt = 0; attempt < 45; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            existing = await TryGetRepositoryAsync(login, cancellationToken);
            if (existing is not null)
            {
                progress?.Report("构建仓库准备完成");
                await TrySyncForkAsync(login, existing.DefaultBranch, cancellationToken);
                await EnableWorkflowAsync(login, cancellationToken);
                return existing;
            }
        }

        throw new InvalidOperationException("GitHub 创建仓库超时，请稍后重试。");
    }

    public async Task<WorkflowRun> DispatchBuildAsync(
        string login,
        BuildRequest build,
        CancellationToken cancellationToken)
    {
        var previousRunIds = (await GetRecentRunsAsync(login, cancellationToken))
            .Select(run => run.Id)
            .ToHashSet();
        var dispatchedAt = DateTimeOffset.UtcNow.AddSeconds(-5);

        var body = new
        {
            @ref = "main",
            inputs = new Dictionary<string, object>
            {
                ["platform"] = "windows-latest",
                ["url"] = build.Url,
                ["name"] = build.Name,
                ["icon"] = build.IconUrl,
                ["width"] = build.Width,
                ["height"] = build.Height,
                ["min_width"] = "",
                ["min_height"] = "",
                ["app_version"] = build.Version,
                ["fullscreen"] = build.Fullscreen,
                ["hide_title_bar"] = false,
                ["multi_arch"] = false,
                ["targets"] = build.Architecture
            }
        };

        using (var response = await _client.PostAsync(
                   $"repos/{login}/{PakeRepository}/actions/workflows/{WorkflowFile}/dispatches",
                   JsonContent.Create(body),
                   cancellationToken))
        {
            if (response.StatusCode != HttpStatusCode.NoContent)
            {
                await ThrowGitHubErrorAsync(response, cancellationToken);
            }
        }

        for (var attempt = 0; attempt < 45; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            var runs = await GetRecentRunsAsync(login, cancellationToken);
            var newRun = runs.FirstOrDefault(run =>
                !previousRunIds.Contains(run.Id) && run.CreatedAt >= dispatchedAt);
            if (newRun is not null)
            {
                return newRun;
            }
        }

        throw new InvalidOperationException("构建任务已经提交，但暂时没有出现在 GitHub 队列中。");
    }

    private async Task TrySyncForkAsync(
        string login,
        string branch,
        CancellationToken cancellationToken)
    {
        using var response = await _client.PostAsync(
            $"repos/{login}/{PakeRepository}/merge-upstream",
            JsonContent.Create(new { branch }),
            cancellationToken);

        // 200 = updated/already current. 409 = fork contains conflicting user changes.
        // A conflict should not block ordinary builds; the existing fork can still run.
        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Conflict)
        {
            return;
        }

        // Older GitHub Enterprise/API variants may not expose merge-upstream.
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity)
        {
            return;
        }

        await ThrowGitHubErrorAsync(response, cancellationToken);
    }

    public Task<WorkflowRun> GetRunAsync(string login, long runId, CancellationToken cancellationToken) =>
        GetRequiredAsync<WorkflowRun>(
            $"repos/{login}/{PakeRepository}/actions/runs/{runId}",
            cancellationToken);

    public async Task CancelRunAsync(string login, long runId, CancellationToken cancellationToken)
    {
        using var response = await _client.PostAsync(
            $"repos/{login}/{PakeRepository}/actions/runs/{runId}/cancel",
            null,
            cancellationToken);

        if (response.StatusCode is not HttpStatusCode.Accepted and not HttpStatusCode.Conflict)
        {
            await ThrowGitHubErrorAsync(response, cancellationToken);
        }
    }

    public async Task<string> DownloadArtifactAsync(
        string login,
        long runId,
        string appName,
        CancellationToken cancellationToken)
    {
        var artifacts = await GetRequiredAsync<ArtifactsResponse>(
            $"repos/{login}/{PakeRepository}/actions/runs/{runId}/artifacts",
            cancellationToken);

        var artifact = artifacts.Artifacts.FirstOrDefault(item =>
                           !item.Expired && item.Name.EndsWith("-Windows", StringComparison.OrdinalIgnoreCase))
                       ?? artifacts.Artifacts.FirstOrDefault(item => !item.Expired)
                       ?? throw new InvalidOperationException("构建成功，但没有找到可下载的 Windows 安装包。");

        using var request = new HttpRequestMessage(HttpMethod.Get, artifact.ArchiveDownloadUrl);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            await ThrowGitHubErrorAsync(response, cancellationToken);
        }

        var downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads",
            "Pake Studio");
        Directory.CreateDirectory(downloads);

        var safeName = SanitizeFileName(appName);
        var outputPath = Path.Combine(downloads, $"{safeName}-{DateTime.Now:yyyyMMdd-HHmmss}.zip");

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = File.Create(outputPath);
        await input.CopyToAsync(output, cancellationToken);
        return outputPath;
    }

    private async Task EnableWorkflowAsync(string login, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Put,
                $"repos/{login}/{PakeRepository}/actions/workflows/{WorkflowFile}/enable");
            using var response = await _client.SendAsync(request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                return;
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                continue;
            }

            await ThrowGitHubErrorAsync(response, cancellationToken);
        }

        throw new InvalidOperationException("构建仓库已创建，但 GitHub Actions 工作流尚未准备完成。");
    }

    private async Task<GitHubRepository?> TryGetRepositoryAsync(string login, CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync($"repos/{login}/{PakeRepository}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        return await ReadRequiredAsync<GitHubRepository>(response, cancellationToken);
    }

    private async Task<List<WorkflowRun>> GetRecentRunsAsync(string login, CancellationToken cancellationToken)
    {
        var result = await GetRequiredAsync<WorkflowRunsResponse>(
            $"repos/{login}/{PakeRepository}/actions/workflows/{WorkflowFile}/runs?event=workflow_dispatch&per_page=20",
            cancellationToken);
        return result.WorkflowRuns;
    }

    private async Task<T> GetRequiredAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(path, cancellationToken);
        return await ReadRequiredAsync<T>(response, cancellationToken);
    }

    private async Task<T> ReadRequiredAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            await ThrowGitHubErrorAsync(response, cancellationToken);
        }

        return await response.Content.ReadFromJsonAsync<T>(_jsonOptions, cancellationToken)
               ?? throw new InvalidOperationException("GitHub 返回了空响应。");
    }

    private static async Task ThrowGitHubErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        string message;

        try
        {
            using var json = JsonDocument.Parse(text);
            message = json.RootElement.TryGetProperty("message", out var value)
                ? value.GetString() ?? text
                : text;
        }
        catch
        {
            message = text;
        }

        throw new HttpRequestException(
            $"GitHub 请求失败（{(int)response.StatusCode}）：{message}",
            null,
            response.StatusCode);
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(invalid.Contains(character) ? '_' : character);
        }

        var result = builder.ToString().Trim();
        return string.IsNullOrWhiteSpace(result) ? "Pake-App" : result;
    }

    public void Dispose() => _client.Dispose();
}
