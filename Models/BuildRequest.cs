namespace PakeStudio.Models;

public sealed class BuildRequest
{
    public required string Url { get; init; }
    public required string Name { get; init; }
    public string IconUrl { get; init; } = "";
    public string Width { get; init; } = "1200";
    public string Height { get; init; } = "780";
    public string Version { get; init; } = "1.0.0";
    public string Architecture { get; init; } = "x64";
    public bool Fullscreen { get; init; }
}
