using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using PakeStudio.Models;
using PakeStudio.Services;

namespace PakeStudio;

public partial class MainWindow : Window
{
    private readonly GitHubService _gitHub = new();
    private readonly AppSettings _settings;
    private CancellationTokenSource? _operationCancellation;
    private GitHubUser? _user;
    private WorkflowRun? _currentRun;
    private string _lastArtifactPath = "";
    private string _workflowUrl = "";
    private bool _nameWasEdited;
    private bool _autoNaming;

    public MainWindow()
    {
        InitializeComponent();
        _settings = SettingsService.Load();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        RestoreForm();

        try
        {
            var token = CredentialStore.LoadToken();
            if (!string.IsNullOrWhiteSpace(token))
            {
                await AuthenticateAsync(token, saveToken: false);
            }
        }
        catch (Exception ex)
        {
            ShowError("读取 GitHub 登录信息失败", ex);
        }

        UpdateBuildButton();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        SaveForm();
        _operationCancellation?.Cancel();
        _gitHub.Dispose();
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        var clientId = OAuthConfiguration.GetClientId();
        if (string.IsNullOrWhiteSpace(clientId))
        {
            TokenFallbackPanel.Visibility = Visibility.Visible;
            ConnectButton.Visibility = Visibility.Collapsed;
            return;
        }

        SetConnectionBusy(true);
        DeviceCodePanel.Visibility = Visibility.Visible;
        _operationCancellation?.Cancel();
        _operationCancellation = new CancellationTokenSource();
        var cancellationToken = _operationCancellation.Token;

        try
        {
            var device = await _gitHub.StartDeviceFlowAsync(clientId, cancellationToken);
            DeviceCodeText.Text = device.UserCode;
            DeviceStatusText.Text = "浏览器已打开，正在等待确认…";
            Clipboard.SetText(device.UserCode);
            OpenUrl(device.VerificationUri);

            var interval = Math.Max(device.Interval, 5);
            var expiresAt = DateTimeOffset.UtcNow.AddSeconds(device.ExpiresIn);

            while (DateTimeOffset.UtcNow < expiresAt)
            {
                await Task.Delay(TimeSpan.FromSeconds(interval), cancellationToken);
                var tokenResult = await _gitHub.PollDeviceFlowAsync(clientId, device.DeviceCode, cancellationToken);

                if (!string.IsNullOrWhiteSpace(tokenResult.AccessToken))
                {
                    await AuthenticateAsync(tokenResult.AccessToken, saveToken: true);
                    DeviceCodePanel.Visibility = Visibility.Collapsed;
                    return;
                }

                switch (tokenResult.Error)
                {
                    case "authorization_pending":
                        DeviceStatusText.Text = "正在等待 GitHub 确认…";
                        break;
                    case "slow_down":
                        interval += 5;
                        DeviceStatusText.Text = "GitHub 请求稍多，已降低检查频率…";
                        break;
                    case "access_denied":
                        throw new InvalidOperationException("你取消了 GitHub 登录。");
                    case "expired_token":
                        throw new InvalidOperationException("登录验证码已过期，请重新连接。");
                    default:
                        if (!string.IsNullOrWhiteSpace(tokenResult.Error))
                        {
                            throw new InvalidOperationException(
                                tokenResult.ErrorDescription ?? tokenResult.Error);
                        }
                        break;
                }
            }

            throw new InvalidOperationException("登录验证码已过期，请重新连接。");
        }
        catch (OperationCanceledException)
        {
            DeviceStatusText.Text = "登录已取消";
        }
        catch (Exception ex)
        {
            DeviceStatusText.Text = "连接失败";
            ShowError("GitHub 登录失败", ex);
        }
        finally
        {
            SetConnectionBusy(false);
        }
    }

    private async void ConnectWithTokenButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TokenBox.Password))
        {
            MessageBox.Show("请先粘贴 GitHub 访问令牌。", "Pake Studio", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SetConnectionBusy(true);
        try
        {
            await AuthenticateAsync(TokenBox.Password, saveToken: true);
            TokenBox.Clear();
        }
        catch (Exception ex)
        {
            ShowError("令牌连接失败", ex);
        }
        finally
        {
            SetConnectionBusy(false);
        }
    }

    private async Task AuthenticateAsync(string token, bool saveToken)
    {
        _gitHub.SetToken(token);
        _user = await _gitHub.GetCurrentUserAsync(CancellationToken.None);
        if (saveToken)
        {
            CredentialStore.SaveToken(token);
        }

        SignedOutPanel.Visibility = Visibility.Collapsed;
        SignedInPanel.Visibility = Visibility.Visible;
        SignedInUserText.Text = $"@{_user.Login}";
        HeaderAccountText.Text = $"@{_user.Login}";
        LogoutButton.Visibility = Visibility.Visible;
        UpdateBuildButton();
    }

    private void LogoutButton_Click(object sender, RoutedEventArgs e)
    {
        _operationCancellation?.Cancel();
        CredentialStore.DeleteToken();
        _user = null;
        _currentRun = null;
        SignedOutPanel.Visibility = Visibility.Visible;
        SignedInPanel.Visibility = Visibility.Collapsed;
        TokenFallbackPanel.Visibility = Visibility.Collapsed;
        ConnectButton.Visibility = Visibility.Visible;
        HeaderAccountText.Text = "尚未连接 GitHub";
        LogoutButton.Visibility = Visibility.Collapsed;
        UpdateBuildButton();
    }

    private async void BuildButton_Click(object sender, RoutedEventArgs e)
    {
        if (_user is null)
        {
            MessageBox.Show("请先连接 GitHub。", "Pake Studio", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!TryCreateBuildRequest(out var request, out var validationError))
        {
            MessageBox.Show(validationError, "请检查填写内容", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SaveForm();
        SetBuildBusy(true);
        ResultPanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        BuildProgressBar.Value = 5;
        SetBuildStatus("正在准备构建仓库", "第一次使用时，GitHub 需要创建 Pake Fork。");

        _operationCancellation?.Cancel();
        _operationCancellation = new CancellationTokenSource();
        var cancellationToken = _operationCancellation.Token;

        try
        {
            var repositoryProgress = new Progress<string>(message =>
                SetBuildStatus("正在准备构建仓库", message));
            await EnsureBuildRepositoryReadyAsync(
                _user.Login,
                repositoryProgress,
                cancellationToken);
            BuildProgressBar.Value = 25;

            SetBuildStatus("正在提交构建任务", "正在把网页配置发送到你的 GitHub Actions。");
            _currentRun = await _gitHub.DispatchBuildAsync(_user.Login, request, cancellationToken);
            _workflowUrl = _currentRun.HtmlUrl;
            OpenWorkflowButton.Visibility = Visibility.Visible;
            BuildProgressBar.Value = 38;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _currentRun = await _gitHub.GetRunAsync(_user.Login, _currentRun.Id, cancellationToken);

                if (_currentRun.Status == "queued" || _currentRun.Status == "waiting")
                {
                    SetBuildStatus("等待 GitHub 构建资源", "任务正在队列中，通常只需要等待一小会儿。");
                    BuildProgressBar.Value = Math.Max(BuildProgressBar.Value, 42);
                }
                else if (_currentRun.Status == "in_progress")
                {
                    SetBuildStatus("GitHub 正在生成应用", "云端正在下载依赖并编译 Windows 安装包，请保持网络连接。");
                    BuildProgressBar.Value = Math.Min(82, BuildProgressBar.Value + 3);
                }
                else if (_currentRun.Status == "completed")
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromSeconds(6), cancellationToken);
            }

            if (!string.Equals(_currentRun.Conclusion, "success", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"GitHub 构建没有成功，状态：{TranslateConclusion(_currentRun.Conclusion)}。可点击“在 GitHub 查看详情”检查日志。");
            }

            BuildProgressBar.Value = 90;
            SetBuildStatus("正在下载安装包", "构建成功，正在保存到“下载\\Pake Studio”。");
            _lastArtifactPath = await _gitHub.DownloadArtifactAsync(
                _user.Login,
                _currentRun.Id,
                request.Name,
                cancellationToken);

            BuildProgressBar.Value = 100;
            SetBuildStatus("生成完成", "安装包已经下载到你的电脑。");
            ResultPathText.Text = _lastArtifactPath;
            ResultPanel.Visibility = Visibility.Visible;
            _settings.LastArtifactPath = _lastArtifactPath;
            SettingsService.Save(_settings);
        }
        catch (OperationCanceledException)
        {
            SetBuildStatus("构建已取消", "如任务已经进入 GitHub 队列，取消可能需要几秒钟生效。");
        }
        catch (Exception ex)
        {
            SetBuildStatus("生成失败", HumanizeError(ex));
            ShowError("生成应用失败", ex);
        }
        finally
        {
            SetBuildBusy(false);
        }
    }

    private async void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_user is not null && _currentRun is not null &&
                _currentRun.Status is not "completed")
            {
                await _gitHub.CancelRunAsync(_user.Login, _currentRun.Id, CancellationToken.None);
            }
        }
        catch
        {
            // Cancellation is best effort. The local operation is still stopped.
        }
        finally
        {
            _operationCancellation?.Cancel();
        }
    }

    private async Task EnsureBuildRepositoryReadyAsync(
        string login,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        try
        {
            await _gitHub.EnsurePakeForkAsync(login, progress, cancellationToken);
        }
        catch (WorkflowActivationRequiredException exception)
        {
            SetBuildStatus(
                "需要完成一次 GitHub 确认",
                "GitHub 会保护新 Fork 中的工作流。确认一次后，以后无需重复操作。");
            OpenUrl(exception.ActionsUrl);

            var result = MessageBox.Show(
                "已为你打开 GitHub Actions 页面。\n\n" +
                "1. 点击页面中的绿色按钮：\n" +
                "   “I understand my workflows, go ahead and enable them”\n" +
                "2. 完成后回到 Pake Studio，点击下方“确定”。\n\n" +
                "这是 GitHub 对新 Fork 的一次性安全确认。",
                "首次使用需要确认一次",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Information);

            if (result != MessageBoxResult.OK)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            SetBuildStatus("正在确认 GitHub Actions", "确认成功后，构建会自动继续。");

            try
            {
                await _gitHub.EnsurePakeForkAsync(login, progress, cancellationToken);
            }
            catch (WorkflowActivationRequiredException)
            {
                throw new InvalidOperationException(
                    "GitHub Actions 尚未启用。请在已打开的网页点击绿色启用按钮，然后重新开始生成。");
            }
        }
    }

    private void OpenWorkflowButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_workflowUrl))
        {
            OpenUrl(_workflowUrl);
        }
    }

    private void OpenResultButton_Click(object sender, RoutedEventArgs e)
    {
        var path = !string.IsNullOrWhiteSpace(_lastArtifactPath)
            ? _lastArtifactPath
            : _settings.LastArtifactPath;

        if (!File.Exists(path))
        {
            MessageBox.Show("下载文件已经被移动或删除。", "Pake Studio", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{path}\"",
            UseShellExecute = true
        });
    }

    private void CopyDeviceCodeButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(DeviceCodeText.Text))
        {
            Clipboard.SetText(DeviceCodeText.Text);
        }
    }

    private void OpenTokenPageButton_Click(object sender, RoutedEventArgs e) =>
        OpenUrl("https://github.com/settings/tokens/new?scopes=public_repo,workflow&description=Pake%20Studio");

    private void UrlTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_nameWasEdited && Uri.TryCreate(UrlTextBox.Text.Trim(), UriKind.Absolute, out var uri))
        {
            var host = uri.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase);
            var firstPart = host.Split('.', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(firstPart))
            {
                _autoNaming = true;
                NameTextBox.Text = char.ToUpperInvariant(firstPart[0]) + firstPart[1..];
                _autoNaming = false;
            }
        }

        UpdateBuildButton();
    }

    private void NameTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_autoNaming && IsLoaded)
        {
            _nameWasEdited = true;
        }

        UpdateBuildButton();
    }

    private void SizeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CustomSizePanel is null || WidthTextBox is null || HeightTextBox is null ||
            SizeComboBox.SelectedItem is not ComboBoxItem selected)
        {
            return;
        }

        var tag = selected.Tag?.ToString() ?? "";
        CustomSizePanel.Visibility = tag == "custom" ? Visibility.Visible : Visibility.Collapsed;

        if (tag != "custom")
        {
            var values = tag.Split(',');
            if (values.Length == 2)
            {
                WidthTextBox.Text = values[0];
                HeightTextBox.Text = values[1];
            }
        }
    }

    private bool TryCreateBuildRequest(out BuildRequest request, out string error)
    {
        request = null!;
        error = "";

        if (!Uri.TryCreate(UrlTextBox.Text.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            error = "请输入以 http:// 或 https:// 开头的有效网页地址。";
            return false;
        }

        var name = NameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "请输入应用名称。";
            return false;
        }

        if (!Regex.IsMatch(name, @"^[\p{L}\p{N} _.-]{1,60}$"))
        {
            error = "应用名称只能包含文字、数字、空格、点、短横线和下划线，最长 60 个字符。";
            return false;
        }

        var iconUrl = IconTextBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(iconUrl) &&
            (!Uri.TryCreate(iconUrl, UriKind.Absolute, out var iconUri) ||
             iconUri.Scheme is not ("http" or "https")))
        {
            error = "图标地址需要是以 http:// 或 https:// 开头的有效链接，也可以直接留空。";
            return false;
        }

        if (!int.TryParse(WidthTextBox.Text, out var width) || width is < 480 or > 3840 ||
            !int.TryParse(HeightTextBox.Text, out var height) || height is < 400 or > 2160)
        {
            error = "窗口宽度需要在 480～3840 之间，高度需要在 400～2160 之间。";
            return false;
        }

        var version = VersionTextBox.Text.Trim();
        if (!Regex.IsMatch(version, @"^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$"))
        {
            error = "应用版本请使用 1.0.0 这样的格式。";
            return false;
        }

        var architecture = (ArchitectureComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "x64";
        request = new BuildRequest
        {
            Url = uri.ToString(),
            Name = name,
            IconUrl = iconUrl,
            Width = width.ToString(),
            Height = height.ToString(),
            Version = version,
            Architecture = architecture,
            Fullscreen = FullscreenCheckBox.IsChecked == true
        };
        return true;
    }

    private void RestoreForm()
    {
        UrlTextBox.Text = _settings.LastUrl;
        _autoNaming = true;
        NameTextBox.Text = _settings.LastAppName;
        _autoNaming = false;
        _nameWasEdited = !string.IsNullOrWhiteSpace(_settings.LastAppName);
        IconTextBox.Text = _settings.LastIconUrl;
        WidthTextBox.Text = _settings.Width;
        HeightTextBox.Text = _settings.Height;
        VersionTextBox.Text = _settings.Version;
        FullscreenCheckBox.IsChecked = _settings.Fullscreen;
        _lastArtifactPath = _settings.LastArtifactPath;

        foreach (var item in ArchitectureComboBox.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag?.ToString() == _settings.Architecture)
            {
                ArchitectureComboBox.SelectedItem = item;
                break;
            }
        }

        if (File.Exists(_lastArtifactPath))
        {
            ResultPathText.Text = _lastArtifactPath;
            ResultPanel.Visibility = Visibility.Visible;
        }
    }

    private void SaveForm()
    {
        _settings.LastUrl = UrlTextBox.Text.Trim();
        _settings.LastAppName = NameTextBox.Text.Trim();
        _settings.LastIconUrl = IconTextBox.Text.Trim();
        _settings.Width = WidthTextBox.Text.Trim();
        _settings.Height = HeightTextBox.Text.Trim();
        _settings.Version = VersionTextBox.Text.Trim();
        _settings.Architecture =
            (ArchitectureComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "x64";
        _settings.Fullscreen = FullscreenCheckBox.IsChecked == true;
        _settings.LastArtifactPath = _lastArtifactPath;
        SettingsService.Save(_settings);
    }

    private void SetConnectionBusy(bool busy)
    {
        ConnectButton.IsEnabled = !busy;
        TokenBox.IsEnabled = !busy;
    }

    private void SetBuildBusy(bool busy)
    {
        BuildButton.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BuildButton.IsEnabled = !busy && _user is not null;
        UrlTextBox.IsEnabled = !busy;
        NameTextBox.IsEnabled = !busy;
        IconTextBox.IsEnabled = !busy;
        SizeComboBox.IsEnabled = !busy;
        VersionTextBox.IsEnabled = !busy;
        ArchitectureComboBox.IsEnabled = !busy;
        FullscreenCheckBox.IsEnabled = !busy;
    }

    private void UpdateBuildButton()
    {
        if (BuildButton is null)
        {
            return;
        }

        BuildButton.IsEnabled = _user is not null &&
                                Uri.TryCreate(UrlTextBox.Text.Trim(), UriKind.Absolute, out _);
    }

    private void SetBuildStatus(string title, string detail)
    {
        BuildStatusText.Text = title;
        BuildDetailText.Text = detail;
    }

    private static string TranslateConclusion(string? conclusion) => conclusion switch
    {
        "failure" => "失败",
        "cancelled" => "已取消",
        "timed_out" => "超时",
        "action_required" => "需要在 GitHub 中确认",
        "startup_failure" => "启动失败",
        _ => conclusion ?? "未知"
    };

    private static string HumanizeError(Exception exception)
    {
        if (exception is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized })
        {
            return "GitHub 登录已失效，请退出后重新连接。";
        }

        if (exception is HttpRequestException { StatusCode: HttpStatusCode.Forbidden })
        {
            return "GitHub 权限不足或 API 使用频率过高，请检查授权后重试。";
        }

        return exception.Message;
    }

    private static void ShowError(string title, Exception exception) =>
        MessageBox.Show(
            HumanizeError(exception),
            title,
            MessageBoxButton.OK,
            MessageBoxImage.Error);

    private static void OpenUrl(string url)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }
}
