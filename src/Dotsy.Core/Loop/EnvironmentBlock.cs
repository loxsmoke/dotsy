using System.Runtime.InteropServices;
using System.Text;
using Dotsy.Core.Config;
using Dotsy.Core.Git;

namespace Dotsy.Core.Loop;

public static class EnvironmentBlock
{
    public static string Build(DotsyConfig config, string cwd, GitContext? git = null)
    {
        if (!config.Agent.InjectEnvironment)
            return "";

        var sb = new StringBuilder();
        sb.AppendLine("<env>");
        sb.AppendLine($"  os: {RuntimeInformation.OSDescription}");
        sb.AppendLine($"  shell: {DetectShell()}");
        sb.AppendLine($"  dotnet: {Environment.Version}");
        sb.AppendLine($"  cwd: {cwd}");
        sb.AppendLine($"  date: {DateTime.UtcNow:yyyy-MM-dd}");

        if (config.Agent.InjectGitStatus && git is not null)
        {
            if (!string.IsNullOrEmpty(git.Branch))
                sb.AppendLine($"  git_branch: {git.Branch} ({git.ShortSha})");
            if (git.ModifiedCount > 0 || git.UntrackedCount > 0)
                sb.AppendLine($"  git_status: {git.ModifiedCount} modified, {git.UntrackedCount} untracked");
        }

        sb.Append("</env>");
        return sb.ToString();
    }

    private static string DetectShell()
    {
        if (OperatingSystem.IsWindows())
        {
            return Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
        }
        return Environment.GetEnvironmentVariable("SHELL") ?? "/bin/sh";
    }
}
