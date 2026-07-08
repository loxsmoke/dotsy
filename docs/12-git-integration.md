## 12. Git Integration

### 12.1 Repository Context

On session start, `GitContext` uses LibGit2Sharp to:
- Detect if cwd is inside a git repository.
- Read the current branch name and short HEAD SHA.
- Enumerate tracked files for the repo-map index.

This information is injected into the system prompt preamble:
```
Repository: /path/to/repo
Branch: main (a3f21bc)
```

### 12.2 Diff Context

The `Read` tool, when given a directory path, can optionally include a git diff summary alongside the listing. This is controlled by the tool's `include_diff` input parameter (not a CLI flag).

### 12.3 Auto-Commit (opt-in)

When `agent.auto_commit = true`:
- The affected files from an `Edit`/`Write`/`MultiEdit` turn are staged (`git add`); if no paths are supplied, everything is staged (`git add *`).
- After each agent turn that produced file changes, a commit is created with message: `agent: <first line of assistant reply>` (truncated to 72 chars).
- Auto-commit uses LibGit2Sharp's `Commit()` with the user's configured git identity (`user.name` / `user.email`), falling back to `dotsy <dotsy@localhost>` when either is unset.
- If the repo has uncommitted changes at session start and auto-commit is enabled, warn the user before proceeding.

### 12.4 Checkpoint / Undo

Every turn that writes files creates a named git ref `refs/agent/checkpoints/<session-id>/<turn-n>`. The slash command `/undo` resets the working tree to the previous checkpoint (hard reset of tracked files only).

---

