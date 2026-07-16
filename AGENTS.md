# Pake Studio repository guidance

- Target platform: Windows 10/11 x64.
- UI framework: WPF on .NET 10.
- Keep the end-user flow non-technical: connect GitHub, paste a URL, click build, download the result.
- Do not add local Node.js, Rust, Visual Studio, or Pake CLI requirements for end users.
- All webpage-app builds must run in the user's own GitHub fork of `tw93/Pake`.
- Store GitHub credentials only in Windows Credential Manager.
- Keep GitHub API calls in `Services/GitHubService.cs`.
- Validate URLs, window sizes, versions, filenames, and API responses before use.
- Verification command: `dotnet build PakeStudio.csproj -c Release`.
