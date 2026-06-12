## 20. Environment Context Injection

### 20.1 Injected Fields

The environment block is assembled by `EnvironmentBlock.Build(SessionContext)` and wrapped in `<env>` XML tags:

```
<env>
OS: Windows 11 (10.0.26200)
Shell: PowerShell 7.4
.NET: 9.0.5
Working directory: C:\dev\myapp
Date: 2026-05-20
Git branch: main
Git status: 2 modified, 1 untracked
</env>
```

| Field | Source | Notes |
|-------|--------|-------|
| OS | `Environment.OSVersion` | Platform + version string |
| Shell | `COMSPEC` (Windows) or `SHELL` (Unix); fallback `cmd.exe` / `sh` | Executable name only, not full path |
| .NET runtime | `Environment.Version` | Helps the model understand C# idioms |
| Working directory | `Directory.GetCurrentDirectory()` | Refreshed per turn |
| Date | `DateTimeOffset.UtcNow.ToString("yyyy-MM-dd")` | Rounded to day for cache stability |
| Git branch | `LibGit2Sharp.Repository.Head.FriendlyName` | Empty string if not a git repo |
| Git status | `repo.RetrieveStatus()` -- modified + untracked counts | Omitted if repo is clean |

The block does **not** include full git diff (available via the `read` tool with `--include-diff`), environment variable values (security risk), or user-specific paths beyond cwd.

### 20.2 Update Frequency

The environment block is rebuilt once per turn. Fields that rarely change (OS, Shell, .NET) sit in the **static prefix** zone for prompt-cache efficiency. Fields that change per turn (cwd, git branch, date) are in the **dynamic suffix**. The date is rounded to the day -- not hour or minute -- so consecutive calls within the same day hit the same cache (**goose** approach).

### 20.3 Opt-Out

```toml
[agent]
inject_environment = true   # set false to suppress the <env> block entirely
inject_git_status  = true   # set false to omit git branch/status (faster in large repos)
```

Both flags have `DOTSY_INJECT_ENVIRONMENT=0` and `DOTSY_INJECT_GIT_STATUS=0` env-var equivalents.

---

