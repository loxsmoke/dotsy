## 2. Technology Stack

| Concern | Choice | Rationale |
|---------|--------|-----------|
| Runtime | .NET 10 | AOT-publishable single binary; `System.Threading.Channels` for async pipelines |
| TUI framework | `Terminal.Gui` 2.x | Full windowed TUI (panels, scroll, mouse); supports async rendering |
| Terminal text editing | `Terminal.Gui.Editor` | Editor-backed multiline input and scrollable text surfaces |
| Config file format | **TOML** via `Tomlyn` | Human-editable; used by **goose** and many CLI tools |
| Git | **LibGit2Sharp** | Native libgit2 bindings; equivalent to GitPython / simple-git |
| Code intelligence | **Microsoft.CodeAnalysis (Roslyn)** packages | AST parsing and symbol extraction for C# and workspace-aware code |
| Text search | `Ivy.Ripgrep` + ripgrep subprocess | Fastest cross-platform regex search; same as **opencode** / **cline** / **pi** / **continue** |
| JSON | `System.Text.Json` | Provider API payloads, MCP protocol |
| CLI argument parsing | `System.CommandLine` | Subcommands, options, completions |
| Testing | MSTest + Moq | Unit tests for loop logic, tool dispatch, compaction |

Current non-test NuGet package references:

| Package | Version | Project(s) |
|---------|---------|------------|
| `Terminal.Gui` | 2.4.10 | `src/Dotsy.Cli`, `utils/JsonlViewer` |
| `Terminal.Gui.Editor` | 2.5.4 | `src/Dotsy.Cli` |
| `Ivy.Ripgrep` | 1.0.2 | `src/Dotsy.Core` |
| `LibGit2Sharp` | 0.31.0 | `src/Dotsy.Cli`, `src/Dotsy.Core` |
| `System.CommandLine` | 2.0.9 | `src/Dotsy.Cli` |
| `Tomlyn` | 2.8.0 | `src/Dotsy.Core` |

---

