## 18. Headless and Non-Interactive Mode

### 18.1 Invocation

```
dotsy run -p "prompt"                        # single-shot, print result and exit
dotsy run -p "prompt" --resume <session-id>  # continue a prior session headlessly
echo "prompt" | dotsy run -p                 # piped stdin
dotsy run -p -f instructions.md             # prompt from file
```

TTY detection (`Console.IsInputRedirected`) automatically switches to headless output when stdin is piped, even without `-p`.

### 18.2 Output Format

`--output-format` accepts:

| Value | Description |
|-------|-------------|
| `text` (default) | Final assistant response to stdout; progress to stderr |
| `json` | One JSON object on exit: `{ result, sessionId, inputTokens, outputTokens, durationMs }` |
| `stream-json` | Newline-delimited JSON, one event per line: same schema as `LoopEvent` records (§7.3); includes `api_retry` events |

`stream-json` is the recommended format for CI pipelines — it enables real-time progress monitoring and structured failure detection.

### 18.3 Exit Codes

| Code | Meaning |
|------|---------|
| 0 | Task completed successfully (`done` tool called or loop ended normally) |
| 1 | Task failed (agent returned error, reflection limit exceeded, model stuck) |
| 2 | Configuration error (bad API key, unknown model, missing required config) |
| 3 | Permission denied (tool blocked and no-tty — cannot prompt for approval) |
| 4 | Context limit exceeded after compaction retry |
| 130 | Interrupted by Ctrl+C |

In headless mode, tools requiring approval that cannot be auto-approved (no matching `allow` rule) return exit code 3 rather than blocking. `--yolo` / `--dangerously-skip-permissions` disables all permission checks.

### 18.4 CI Integration

**Flags for clean CI runs:**

```
--bare                    # skip AGENTS.md, .dotsy/config.toml project overrides, hooks
--no-history              # don't write JSONL session file (equivalent to DOTSY_NO_HISTORY=1)
--yolo                    # skip all permission prompts
--allowed-tools read,grep # restrict the tool set to a safe subset
--max-turns 50            # hard cap to prevent runaway sessions
```

**GitHub Actions example:**

```yaml
- name: Run agent
  run: dotsy run -p "${{ github.event.inputs.prompt }}" --bare --output-format json
  env:
    ANTHROPIC_API_KEY: ${{ secrets.ANTHROPIC_API_KEY }}
```

All config knobs have `DOTSY_<SECTION>_<KEY>` environment-variable equivalents (§5.1) so CI pipelines need no config files.

---

