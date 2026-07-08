## 20. Environment Context Injection

### 20.1 Injected Fields

The environment block is assembled by `EnvironmentBlock.Build(SessionContext)` and wrapped in `<env>` XML tags:

```
<env>
  os: Microsoft Windows 10.0.26200
  shell: C:\WINDOWS\system32\cmd.exe
  dotnet: 10.0.0
  cwd: C:\dev\myapp
  date: 2026-05-20
  git_branch: main (a3f21bc)
  git_status: 2 modified, 1 untracked
</env>
```

Keys are lowercase and 2-space indented. `os`, `shell`, `dotnet`, `cwd`, and `date` are always
present; the `git_*` lines are conditional.

| Field | Source | Notes |
|-------|--------|-------|
| `os` | `RuntimeInformation.OSDescription` | Full OS description string |
| `shell` | `COMSPEC` (Windows) or `SHELL` (Unix); fallback `cmd.exe` / `/bin/sh` | The raw value (usually a full path) |
| `dotnet` | `Environment.Version` | Helps the model understand C# idioms |
| `cwd` | The session working directory passed to `Build` | Refreshed per turn |
| `date` | `DateTime.UtcNow.ToString("yyyy-MM-dd")` | Rounded to day for cache stability |
| `git_branch` | `GitContext.Branch` + short SHA | Line omitted if branch is empty / not a git repo |
| `git_status` | `GitContext` modified + untracked counts | Line omitted when there are no changes |

The block does **not** include full git diff (the `Read` tool exposes an `include_diff` parameter for
that), environment variable values (security risk), or user-specific paths beyond cwd.

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

