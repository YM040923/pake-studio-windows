namespace PakeStudio.Services;

public sealed class WorkflowActivationRequiredException(string actionsUrl)
    : InvalidOperationException(
        "GitHub 需要你对新 Fork 中的工作流进行一次安全确认。")
{
    public string ActionsUrl { get; } = actionsUrl;
}
