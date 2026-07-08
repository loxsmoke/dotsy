## 18. Headless and Non-Interactive Mode

### 18.1 Invocation

```
dotsy run -p "prompt"                        # single-shot, print result and exit
dotsy run -p "prompt" --resume <session-id>  # continue a prior session headlessly
dotsy run -f instructions.md                 # prompt from file (-f is read when -p is absent)
dotsy run -p "/compact" --resume <id>        # headless manual compaction of a session
```

TTY detection (`Console.IsInputRedirected`) switches to headless mode when stdin is redirected even
without `-p`, but the prompt itself must still be supplied via `-p` or `-f` — piped stdin is not read
as the prompt. A run with no prompt exits 1 with "No prompt provided. Use -p or -f."

### 18.2 Output Format

`--output-format` accepts:

| Value | Description |
|-------|-------------|
| `text` (default) | Final assistant response to stdout; progress to stderr |
| `json` | One JSON object on exit: `{ result, sessionId, inputTokens, outputTokens, durationMs }` |
| `stream-json` | Newline-delimited JSON, one event per line: same schema as `LoopEvent` records (§7.3); includes `api_retry` events |

`stream-json` is the recommended format for CI pipelines — it enables real-time progress monitoring and structured failure detection.

### 18.3 Exit Codes

| Code | Meaning | Source |
|------|---------|--------|
| 0 | Loop ended normally (`Done` tool, completed, or other non-error `EndReason`) | `LoopEnded` |
| 1 | Empty prompt, or the loop ended with `EndReason.Error` | validation / `LoopEnded` |
| 2 | Configuration error (provider could not be resolved — bad/missing key, unknown provider) | `ProviderRegistry.Resolve` throw |
| 4 | Context limit exceeded (`EndReason.ContextTooSmall`) | `LoopEnded` |
| 130 | Cancelled (`EndReason.Cancelled`) | `LoopEnded` |

In headless mode, `--yolo` disables all permission checks. A tool that still needs approval and has
no matching `allow` rule fails with a permission-denied **tool result** (the model receives the error
and may adapt); it does not currently map to a dedicated process exit code.

### 18.4 CI Integration

**Flags for clean CI runs** (implemented on `dotsy run`):

```
--bare                    # parsed as "skip project config and hooks" (see note below)
--no-history              # don't write JSONL session file (equivalent to DOTSY_NO_HISTORY=1)
--yolo                    # skip all permission prompts
--max-turns 50            # hard cap to prevent runaway sessions (root option)
```

`--max-turns`, `--model`, and `--provider` are root options (accepted before or via `run`).
`--bare` is currently parsed but not yet wired through to config/AGENTS loading (see §9.3). There is
no `--allowed-tools` or `--dangerously-skip-permissions` flag today; restrict tools/permissions via
config (`[permissions]`) or `--yolo`.

**GitHub Actions example:**

```yaml
- name: Run agent
  run: dotsy run -p "${{ github.event.inputs.prompt }}" --bare --output-format json
  env:
    ANTHROPIC_API_KEY: ${{ secrets.ANTHROPIC_API_KEY }}
```

All config knobs have `DOTSY_<SECTION>_<KEY>` environment-variable equivalents (§5.1) so CI pipelines need no config files.

---

