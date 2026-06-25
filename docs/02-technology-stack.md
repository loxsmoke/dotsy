## 2. Technology Stack

| Concern | Choice | Rationale |
|---------|--------|-----------|
| Runtime | .NET 9 | AOT-publishable single binary; `System.Threading.Channels` for async pipelines |
| TUI framework | **Terminal.Gui v2** (`gui.cs`) | Full windowed TUI (panels, scroll, mouse); supports async rendering |
| Rich text / fallback | **Spectre.Console** | Markup, tables, progress bars used outside the main TUI window |
| HTTP client | `HttpClient` + `System.Net.Http.Json` | Built-in; used for provider API calls and MCP |
| Streaming | Server-Sent Events via `HttpClient` | Provider streaming without third-party dependency |
| Config file format | **TOML** via `Tomlyn` | Human-editable; used by **goose** and many CLI tools |
| Git | **LibGit2Sharp** | Native libgit2 bindings; equivalent to GitPython / simple-git |
| Code intelligence | **Microsoft.CodeAnalysis (Roslyn)** | AST parsing and symbol extraction for C# files |
| Text search | **ripgrep** subprocess | Fastest cross-platform regex search; same as **opencode** / **cline** / **pi** / **continue** |
| SQLite | `Microsoft.Data.Sqlite` | Skills index, session history, tag cache |
| JSON | `System.Text.Json` | Provider API payloads, MCP protocol |
| Dependency injection | `Microsoft.Extensions.DependencyInjection` | Wires providers, tools, session services |
| CLI argument parsing | `System.CommandLine` | Subcommands, options, completions |
| Testing | MSTest + Moq | Unit tests for loop logic, tool dispatch, compaction |

---

