using Terminal.Gui;
using TGAttribute = Terminal.Gui.Attribute;

namespace Dotsy.Cli.Tui;

public static class DemoRunner
{
    // ══ help ══════════════════════════════════════════════════════════════════

    public static Task ShowHelp(AgentWindow app, CancellationToken ct)
    {
        app.WriteConvo(
            """
            Available demo commands:

              work      ·  streaming response, thinking, tool calls, file edits
              longwork  ·  very long commands, paths, and output lines for truncation/scroll testing
              error     ·  tool failure with error message displayed
              ask       ·  tool-approval prompt requiring your input
              help      ·  show this message
              exit      ·  exit the application

            """);
        return Task.CompletedTask;
    }

    // ══ work ══════════════════════════════════════════════════════════════════

    public static async Task RunWork(AgentWindow app, CancellationToken ct)
    {
        app.StartSpinner("waiting");
        await Delay(900, ct);
        app.StopSpinner("streaming");

        await Stream(app,
            "<thinking>\n" +
            "The user wants me to implement IFooService.\n" +
            "I'll read the interface, grep for usages, then write the implementation.\n" +
            "</thinking>\n\n", 14, ct);

        await Stream(app, "I'll implement `IFooService` in three steps:\n\n", 22, ct);

        // ── Step 1: read ──────────────────────────────────────────────────────
        app.WriteConvoBullet("Read interface definition");

        var t1 = app.AddTool("read", "src/IFooService.cs");
        await TickTool(app, t1, 1_100, ct);
        app.SetToolOutput(t1, TextCells(
            "namespace Dotsy.Core.Services;\n\n" +
            "public interface IFooService\n" +
            "{\n" +
            "    Task<Result> GetAsync(int id, CancellationToken ct);\n" +
            "    Task SaveAsync(Model m, CancellationToken ct);\n" +
            "    Task DeleteAsync(int id, CancellationToken ct);\n" +
            "}\n", Palette.Normal));
        app.UpdateTool(t1, "OK", 1);
        app.WriteConvoSubtask("src/IFooService.cs  →  3 methods");

        // ── Step 2: grep ──────────────────────────────────────────────────────
        app.WriteConvoBullet("Scan existing references");

        var t2 = app.AddTool("grep", "IFooService");
        await TickTool(app, t2, 600, ct);
        app.SetToolOutput(t2, TextCells(
            "src/Program.cs:42:          IFooService svc = new FooService();\n" +
            "src/Tests/FooTests.cs:15:   IFooService mock = new MockFooService();\n" +
            "src/Tests/FooTests.cs:28:   // relies on IFooService contract\n" +
            "src/Tests/FooTests.cs:44:   svc.GetAsync(id, ct);\n", Palette.Normal));
        app.UpdateTool(t2, "OK", 0);
        app.WriteConvoSubtask("4 references in 2 files — no namespace import needed");

        // ── Step 3: edit ──────────────────────────────────────────────────────
        app.WriteConvoBullet("Write implementation");

        var diffCells = BuildDiffCells();
        var t3 = app.AddTool("edit", "src/FooService.cs");
        await TickTool(app, t3, 1_800, ct);
        app.SetToolOutput(t3, diffCells);
        app.UpdateTool(t3, "OK", 2);

        app.WriteConvo("\n");
        await Stream(app, "Changes to `src/FooService.cs`:\n\n", 22, ct);

        app.WriteConvoDiffHdr("@@ -0,0 +1,16 @@\n");
        app.WriteConvoDiffAdd( 1, "using Dotsy.Core.Services;\n");
        app.WriteConvoDiffAdd( 2, "\n");
        app.WriteConvoDiffAdd( 3, "public class FooService : IFooService\n");
        app.WriteConvoDiffAdd( 4, "{\n");
        app.WriteConvoDiffAdd( 5, "    private readonly IRepository _repo;\n");
        app.WriteConvoDiffAdd( 6, "    public FooService(IRepository repo) => _repo = repo;\n");
        app.WriteConvoDiffAdd( 7, "\n");
        app.WriteConvoDiffAdd( 8, "    public Task<Result> GetAsync(int id, CancellationToken ct)\n");
        app.WriteConvoDiffAdd( 9, "        => _repo.FindAsync(id, ct);\n");
        app.WriteConvoDiffAdd(10, "\n");
        app.WriteConvoDiffAdd(11, "    public Task SaveAsync(Model m, CancellationToken ct)\n");
        app.WriteConvoDiffAdd(12, "        => _repo.UpsertAsync(m, ct);\n");
        app.WriteConvoDiffAdd(13, "\n");
        app.WriteConvoDiffAdd(14, "    public Task DeleteAsync(int id, CancellationToken ct)\n");
        app.WriteConvoDiffAdd(15, "        => _repo.DeleteAsync(id, ct);\n");
        app.WriteConvoDiffAdd(16, "}\n");
        app.WriteConvo("\n");

        app.WriteConvoFileChange("src/FooService.cs", 16, 0);
        app.AddFileDiff("src/FooService.cs", 16, 0, FileChangeType.Modified, diffCells);
        app.WriteConvoSubtask("All 3 methods implemented  ·  select file below or tool on right to inspect diff");
        app.WriteConvo("\n");
        app.SetStatus("idle");
    }

    // ══ error ══════════════════════════════════════════════════════════════════

    public static async Task RunError(AgentWindow app, CancellationToken ct)
    {
        app.StartSpinner("waiting");
        await Delay(700, ct);
        app.StopSpinner("streaming");

        app.WriteConvoBullet("Run test suite");

        var t1 = app.AddTool("shell", "dotnet test");
        await Delay(400, ct);
        app.UpdateTool(t1, "RUNNING", 0);
        await Delay(500, ct);
        app.UpdateTool(t1, "RUNNING", 1);
        await Delay(500, ct);
        app.SetToolOutput(t1, TextCells(
            "Build FAILED\n" +
            "  src/FooService.cs(1,7): error CS0234:\n" +
            "    The type or namespace name 'IFooService' does not exist\n" +
            "    in the namespace 'Dotsy.Core.Services'.\n", Palette.Err));
        app.UpdateTool(t1, "ERR", 2);

        app.WriteConvoSubtask("Build failed — 1 error");
        app.WriteConvoError("\n[ERROR]  CS0234 — 'IFooService' not found in namespace 'Dotsy.Core.Services'.\n\n");
        app.SetStatus("ERR");

        await Delay(600, ct);
        await Stream(app, "Missing `using` directive. Fixing now.\n\n", 22, ct);

        app.WriteConvoBullet("Fix missing using directive");

        var t2 = app.AddTool("edit", "src/FooService.cs");
        await TickTool(app, t2, 900, ct);

        app.WriteConvoDiffHdr("@@ -1,3 +1,4 @@\n");
        app.WriteConvoDiffAdd(1, "using Dotsy.Core.Services;\n");
        app.WriteConvoDiffCtx(2, "\n");
        app.WriteConvoDiffCtx(3, "public class FooService : IFooService\n");
        app.WriteConvoDiffCtx(4, "{\n");
        app.WriteConvo("\n");

        app.SetToolOutput(t2, TextCells(
            "+ using Agent.Services;\n\n" +
            "  public class FooService : IFooService\n" +
            "  {\n  ...\n", Palette.Normal));
        app.UpdateTool(t2, "OK", 1);

        app.WriteConvoSubtask("Added `using Agent.Services;` — build should now succeed");
        app.WriteConvo("\n");
        app.SetStatus("idle");
    }

    // ══ ask ════════════════════════════════════════════════════════════════════

    public static async Task RunAsk(AgentWindow app, CancellationToken ct)
    {
        app.StartSpinner("waiting");
        await Delay(600, ct);
        app.StopSpinner("streaming");

        app.WriteConvoBullet("Clean stale build artifacts before rebuild");
        await Stream(app, "Requesting permission to delete `obj/` and `bin/`.\n\n", 22, ct);

        app.SetStatus("awaiting approval");
        var choice = await app.ShowApproval("shell", "rm -rf obj/ bin/");

        var label = choice switch
        {
            ApprovalChoice.AllowOnce   => "allowed (this time)",
            ApprovalChoice.AlwaysAllow => "allowed (always)",
            ApprovalChoice.Deny        => "denied",
            _                          => "?"
        };
        app.WriteConvo($"[Tool {label}]\n\n");

        if (choice == ApprovalChoice.Deny)
        {
            await Stream(app, "Skipping clean step — running tests with existing artifacts.\n\n", 22, ct);

            app.WriteConvoBullet("Run tests (cached build)");
            var tFail = app.AddTool("shell", "dotnet test");
            await TickTool(app, tFail, 1_200, ct);
            app.SetToolOutput(tFail, TextCells("Test run for Dotsy.Core.Tests.dll\nPassed!  42 tests (42 passed, 0 failed) in 1.2s\n", Palette.Success));
            app.UpdateTool(tFail, "OK", 1);

            app.WriteConvoSubtask("42 / 42 tests passed");
            app.WriteConvo("\n");
            app.SetStatus("idle");
            return;
        }

        app.SetStatus("streaming");

        app.WriteConvoBullet("Remove stale artifacts");
        var t1 = app.AddTool("shell", "rm -rf obj/ bin/");
        await TickTool(app, t1, 900, ct);
        app.SetToolOutput(t1, TextCells("removed obj/\nremoved bin/\n", Palette.Normal));
        app.UpdateTool(t1, "OK", 1);
        app.WriteConvoSubtask("obj/ and bin/ deleted");

        app.WriteConvoBullet("Rebuild");
        var t2 = app.AddTool("shell", "dotnet build -c Release");
        await TickTool(app, t2, 1_500, ct);
        app.SetToolOutput(t2, TextCells("Build succeeded.\n  0 Warning(s)\n  0 Error(s)\n", Palette.Success));
        app.UpdateTool(t2, "OK", 2);
        app.WriteConvoSubtask("Release build succeeded");

        app.WriteConvoBullet("Run tests");
        var t3 = app.AddTool("shell", "dotnet test");
        await TickTool(app, t3, 1_200, ct);
        app.SetToolOutput(t3, TextCells("Test run for Dotsy.Core.Tests.dll\nPassed!  42 tests (42 passed, 0 failed) in 1.1s\n", Palette.Success));
        app.UpdateTool(t3, "OK", 1);
        app.WriteConvoSubtask("42 / 42 tests passed on clean build");

        app.WriteConvo("\n");
        app.SetStatus("idle");
    }

    // ══ longwork ══════════════════════════════════════════════════════════════

    public static async Task RunLongWork(AgentWindow app, CancellationToken ct)
    {
        app.StartSpinner("waiting");
        await Delay(800, ct);
        app.StopSpinner("streaming");

        await Stream(app,
            "<thinking>\n" +
            "The user wants a comprehensive refactor of the authentication subsystem. " +
            "I need to read all relevant files, run the linter with verbose flags, " +
            "patch several deeply nested source files, and regenerate the API surface docs.\n" +
            "This will involve long shell commands and files with very long paths.\n" +
            "</thinking>\n\n", 8, ct);

        await Stream(app,
            "Refactoring the authentication subsystem across the monorepo. " +
            "I'll read configs, lint, patch implementation files, and regenerate docs.\n\n", 20, ct);

        // ── Step 1: read a deeply nested config ───────────────────────────────
        app.WriteConvoBullet("Read authentication configuration");

        const string cfgPath =
            "src/enterprise/platform/services/authentication/subsystems/oauth2/" +
            "providers/internal/config/AuthenticationSubsystemOAuth2InternalProviderConfiguration.json";

        var t1 = app.AddTool("read", cfgPath);
        await TickTool(app, t1, 1_200, ct);
        app.SetToolOutput(t1, TextCells(
            "{\n" +
            "  \"provider\": \"internal-oauth2\",\n" +
            "  \"tokenEndpoint\": \"https://auth.internal.example.corp/oauth2/v3/token\",\n" +
            "  \"introspectionEndpoint\": \"https://auth.internal.example.corp/oauth2/v3/introspect\",\n" +
            "  \"clientId\": \"enterprise-platform-authentication-subsystem-internal-client-001\",\n" +
            "  \"scopes\": [\"openid\", \"profile\", \"email\", \"groups\", \"offline_access\"],\n" +
            "  \"tokenTtlSeconds\": 3600,\n" +
            "  \"refreshTtlSeconds\": 86400\n" +
            "}\n",
            Palette.Normal));
        app.UpdateTool(t1, "OK", 1);
        app.WriteConvoSubtask(cfgPath + "  →  7 keys");

        // ── Step 2: lint with a long command ──────────────────────────────────
        app.WriteConvoBullet("Lint authentication subsystem with full diagnostics");

        const string lintCmd =
            "dotnet format src/enterprise/platform/services/authentication/AuthenticationSubsystem.csproj " +
            "--verify-no-changes --verbosity diagnostic --severity info " +
            "--exclude src/enterprise/platform/services/authentication/subsystems/legacy " +
            "--report /tmp/lint-reports/auth-subsystem-format-report-full.json";

        var t2 = app.AddTool("shell", lintCmd);
        await TickTool(app, t2, 2_000, ct);
        app.SetToolOutput(t2, TextCells(
            "Determining projects to format...\n" +
            "  Formatted 0 of 42 files in AuthenticationSubsystem.csproj.\n" +
            "  Formatted 0 of 18 files in AuthenticationSubsystem.OAuth2.csproj.\n" +
            "  Formatted 0 of 11 files in AuthenticationSubsystem.Saml.csproj.\n" +
            "Format complete — no changes required. Report written to /tmp/lint-reports/auth-subsystem-format-report-full.json\n",
            Palette.Normal));
        app.UpdateTool(t2, "OK", 2);
        app.WriteConvoSubtask("71 files checked — 0 formatting issues");

        // ── Step 3: long grep with a long result line ─────────────────────────
        app.WriteConvoBullet("Find all callers of the legacy token validator");

        const string grepCmd =
            "grep -rn --include='*.cs' 'LegacyTokenValidationMiddleware\\|ILegacyTokenValidator\\|ValidateLegacyJwt' " +
            "src/enterprise/platform/services/authentication/ " +
            "src/enterprise/platform/services/gateway/ " +
            "src/enterprise/platform/services/identity/";

        var t3 = app.AddTool("grep", grepCmd);
        await TickTool(app, t3, 900, ct);
        app.SetToolOutput(t3, TextCells(
            "src/enterprise/platform/services/authentication/subsystems/legacy/middleware/LegacyTokenValidationMiddleware.cs:1: public class LegacyTokenValidationMiddleware : IMiddleware, ILegacyTokenValidator\n" +
            "src/enterprise/platform/services/authentication/subsystems/legacy/middleware/LegacyTokenValidationMiddleware.cs:47:     public async Task<TokenValidationResult> ValidateLegacyJwt(string rawToken, LegacyJwtValidationOptions options, CancellationToken cancellationToken)\n" +
            "src/enterprise/platform/services/gateway/pipeline/RequestAuthorizationPipeline.cs:112:     var result = await _legacyValidator.ValidateLegacyJwt(token, LegacyJwtValidationOptions.Default, ct);\n" +
            "src/enterprise/platform/services/identity/handlers/ExternalIdentityResolutionHandler.cs:88:     services.AddSingleton<ILegacyTokenValidator, LegacyTokenValidationMiddleware>();\n",
            Palette.Normal));
        app.UpdateTool(t3, "OK", 1);
        app.WriteConvoSubtask("4 references across 3 directories — middleware, gateway, identity");

        // ── Step 4: edit a long-path file ─────────────────────────────────────
        app.WriteConvoBullet("Patch legacy middleware — add telemetry and deprecation warning");

        const string editPath =
            "src/enterprise/platform/services/authentication/subsystems/legacy/" +
            "middleware/LegacyTokenValidationMiddleware.cs";

        var diffCells4 = BuildLongDiffCells();
        var t4 = app.AddTool("edit", editPath);
        await TickTool(app, t4, 2_200, ct);
        app.SetToolOutput(t4, diffCells4);
        app.UpdateTool(t4, "OK", 2);

        app.WriteConvo("\n");
        await Stream(app, "Changes to `" + editPath + "`:\n\n", 18, ct);

        app.WriteConvoDiffHdr("@@ -44,6 +44,18 @@ public class LegacyTokenValidationMiddleware\n");
        app.WriteConvoDiffCtx(44, "    {");
        app.WriteConvoDiffCtx(45, "        // Called by RequestAuthorizationPipeline and ExternalIdentityResolutionHandler");
        app.WriteConvoDiffCtx(46, "        public async Task<TokenValidationResult> ValidateLegacyJwt(string rawToken, LegacyJwtValidationOptions options, CancellationToken cancellationToken)");
        app.WriteConvoDiffAdd(47, "        {");
        app.WriteConvoDiffAdd(48, "            _telemetry.TrackEvent(\"LegacyTokenValidation.Invoked\", new Dictionary<string, string> { [\"source\"] = nameof(LegacyTokenValidationMiddleware) });");
        app.WriteConvoDiffAdd(49, "            _logger.LogWarning(\"[DEPRECATED] LegacyTokenValidationMiddleware.ValidateLegacyJwt is deprecated as of v4.2.0 — migrate callers to IModernTokenValidator (AuthenticationSubsystem.OAuth2).\");");
        app.WriteConvoDiffAdd(50, "            using var activity = _activitySource.StartActivity(\"ValidateLegacyJwt\", ActivityKind.Internal);");
        app.WriteConvoDiffAdd(51, "            activity?.SetTag(\"auth.legacy\", true);");
        app.WriteConvoDiffAdd(52, "            activity?.SetTag(\"auth.token.length\", rawToken?.Length ?? 0);");
        app.WriteConvoDiffDel(47, "        {");
        app.WriteConvoDiffCtx(53, "            var decoded = _jwtDecoder.Decode(rawToken, options.SigningKeys, validateExpiry: options.ValidateExpiry);");
        app.WriteConvoDiffCtx(54, "            if (decoded is null) return TokenValidationResult.Fail(\"invalid_token\");");
        app.WriteConvoDiffCtx(55, "            return TokenValidationResult.Ok(decoded.Claims);");
        app.WriteConvo("\n");

        app.WriteConvoFileChange(editPath, 5, 1);
        app.AddFileDiff(editPath, 5, 1, FileChangeType.Modified, diffCells4);
        app.WriteConvoSubtask("Telemetry + deprecation log added  ·  1 line removed");

        // ── Step 5: second long-path new file ─────────────────────────────────
        app.WriteConvoBullet("Add migration guide document");

        const string docPath =
            "src/enterprise/platform/services/authentication/subsystems/legacy/" +
            "docs/migration/LegacyTokenValidationMiddleware-to-ModernOAuth2TokenValidator-MigrationGuide.md";

        var diffCells5 = BuildDocDiffCells();
        var t5 = app.AddTool("write", docPath);
        await TickTool(app, t5, 1_000, ct);
        app.SetToolOutput(t5, diffCells5);
        app.UpdateTool(t5, "OK", 1);

        app.WriteConvo("\n");
        await Stream(app, "Created `" + docPath + "`:\n\n", 18, ct);
        app.WriteConvoDiffHdr("@@ -0,0 +1,12 @@\n");
        for (int i = 1; i <= 12; i++)
            app.WriteConvoDiffAdd(i, MigrationDocLine(i));
        app.WriteConvo("\n");

        app.WriteConvoFileChange(docPath, 12, 0);
        app.AddFileDiff(docPath, 12, 0, FileChangeType.Added, diffCells5);
        app.WriteConvoSubtask("Migration guide written — 12 lines");

        app.WriteConvo("\n");
        app.SetStatus("idle");
    }

    private static string MigrationDocLine(int i) => i switch
    {
        1  => "# Migration Guide: LegacyTokenValidationMiddleware → ModernOAuth2TokenValidator",
        2  => "",
        3  => "## Affected callers",
        4  => "- `RequestAuthorizationPipeline` (src/enterprise/platform/services/gateway/pipeline/RequestAuthorizationPipeline.cs:112)",
        5  => "- `ExternalIdentityResolutionHandler` (src/enterprise/platform/services/identity/handlers/ExternalIdentityResolutionHandler.cs:88)",
        6  => "",
        7  => "## Steps",
        8  => "1. Replace `ILegacyTokenValidator` injection with `IModernTokenValidator` from `AuthenticationSubsystem.OAuth2`.",
        9  => "2. Swap call site: `ValidateLegacyJwt(token, opts, ct)` → `ValidateTokenAsync(token, ModernValidationOptions.Default, ct)`.",
        10 => "3. Remove the `LegacyJwtValidationOptions` import — `ModernValidationOptions` covers all cases.",
        11 => "4. Run `dotnet test src/enterprise/platform/services/ --filter Category=Auth` to verify.",
        12 => "5. Open a PR targeting `enterprise/platform/auth-modernisation` and add label `legacy-removal`.",
        _  => ""
    };

    private static List<List<Cell>> BuildLongDiffCells()
    {
        const int PadWidth = 220;
        var lines = new List<List<Cell>>();

        void AddRow(int ln, char indicator, string text,
            TGAttribute numAttr, TGAttribute lineAttr)
        {
            var line = new List<Cell>();
            Cell C(char ch, TGAttribute a) => new(a, false, new System.Text.Rune(ch));
            var indAttr = indicator == ' ' ? Palette.Normal : lineAttr;
            line.Add(C(' ', indAttr)); line.Add(C(' ', indAttr));
            foreach (var ch in ln.ToString().PadLeft(4)) line.Add(C(ch, numAttr));
            line.Add(C(' ', lineAttr));
            line.Add(C(indicator, lineAttr));
            line.Add(C(' ', lineAttr));
            foreach (var ch in text) line.Add(C(ch, lineAttr));
            if (indicator != ' ')
                while (line.Count < PadWidth) line.Add(C(' ', lineAttr));
            lines.Add(line);
        }

        void Hdr(string text)
        {
            var line = new List<Cell>();
            foreach (var ch in text)
                line.Add(new Cell(Palette.DiffHdr, false, new System.Text.Rune(ch)));
            lines.Add(line);
        }

        var add = (new TGAttribute(ColorName16.BrightGreen, ColorName16.Green),
                   new TGAttribute(ColorName16.BrightGreen, ColorName16.Green));
        var del = (new TGAttribute(ColorName16.BrightRed, ColorName16.Red),
                   new TGAttribute(ColorName16.BrightRed, ColorName16.Red));
        var ctx = (new TGAttribute(ColorName16.DarkGray, ColorName16.Black), Palette.DiffCtx);

        Hdr("@@ -44,6 +44,18 @@ public class LegacyTokenValidationMiddleware");
        AddRow(44, ' ', "    {", ctx.Item1, ctx.Item2);
        AddRow(45, ' ', "        // Called by RequestAuthorizationPipeline and ExternalIdentityResolutionHandler", ctx.Item1, ctx.Item2);
        AddRow(46, ' ', "        public async Task<TokenValidationResult> ValidateLegacyJwt(string rawToken, LegacyJwtValidationOptions options, CancellationToken cancellationToken)", ctx.Item1, ctx.Item2);
        AddRow(47, '-', "        {", del.Item1, del.Item2);
        AddRow(47, '+', "        {", add.Item1, add.Item2);
        AddRow(48, '+', "            _telemetry.TrackEvent(\"LegacyTokenValidation.Invoked\", new Dictionary<string, string> { [\"source\"] = nameof(LegacyTokenValidationMiddleware) });", add.Item1, add.Item2);
        AddRow(49, '+', "            _logger.LogWarning(\"[DEPRECATED] LegacyTokenValidationMiddleware.ValidateLegacyJwt is deprecated as of v4.2.0 — migrate callers to IModernTokenValidator (AuthenticationSubsystem.OAuth2).\");", add.Item1, add.Item2);
        AddRow(50, '+', "            using var activity = _activitySource.StartActivity(\"ValidateLegacyJwt\", ActivityKind.Internal);", add.Item1, add.Item2);
        AddRow(51, '+', "            activity?.SetTag(\"auth.legacy\", true);", add.Item1, add.Item2);
        AddRow(52, '+', "            activity?.SetTag(\"auth.token.length\", rawToken?.Length ?? 0);", add.Item1, add.Item2);
        AddRow(53, ' ', "            var decoded = _jwtDecoder.Decode(rawToken, options.SigningKeys, validateExpiry: options.ValidateExpiry);", ctx.Item1, ctx.Item2);
        AddRow(54, ' ', "            if (decoded is null) return TokenValidationResult.Fail(\"invalid_token\");", ctx.Item1, ctx.Item2);
        AddRow(55, ' ', "            return TokenValidationResult.Ok(decoded.Claims);", ctx.Item1, ctx.Item2);
        return lines;
    }

    private static List<List<Cell>> BuildDocDiffCells()
    {
        const int PadWidth = 220;
        var lines = new List<List<Cell>>();
        var add = (new TGAttribute(ColorName16.BrightGreen, ColorName16.Green),
                   new TGAttribute(ColorName16.BrightGreen, ColorName16.Green));

        void Hdr(string text)
        {
            var line = new List<Cell>();
            foreach (var ch in text)
                line.Add(new Cell(Palette.DiffHdr, false, new System.Text.Rune(ch)));
            lines.Add(line);
        }

        void AddRow(int ln, string text)
        {
            var line = new List<Cell>();
            Cell C(char ch, TGAttribute a) => new(a, false, new System.Text.Rune(ch));
            line.Add(C(' ', add.Item1)); line.Add(C(' ', add.Item1));
            foreach (var ch in ln.ToString().PadLeft(4)) line.Add(C(ch, add.Item1));
            line.Add(C(' ', add.Item2));
            line.Add(C('+', add.Item2));
            line.Add(C(' ', add.Item2));
            foreach (var ch in text) line.Add(C(ch, add.Item2));
            while (line.Count < PadWidth) line.Add(C(' ', add.Item2));
            lines.Add(line);
        }

        Hdr("@@ -0,0 +1,12 @@");
        for (int i = 1; i <= 12; i++)
            AddRow(i, MigrationDocLine(i));
        return lines;
    }

    // ══ Helpers ════════════════════════════════════════════════════════════════

    private static async Task Stream(AgentWindow app, string text, int msPerChar, CancellationToken ct)
    {
        foreach (var ch in text)
        {
            ct.ThrowIfCancellationRequested();
            app.WriteConvo(ch.ToString());
            await Task.Delay(msPerChar, ct);
        }
    }

    private static async Task TickTool(AgentWindow app, int idx, int totalMs, CancellationToken ct)
    {
        const int tick = 500;
        var elapsed = 0;
        while (elapsed < totalMs)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(tick, ct);
            elapsed += tick;
            app.UpdateTool(idx, "RUNNING", elapsed / 1000);
        }
    }

    private static Task Delay(int ms, CancellationToken ct) => Task.Delay(ms, ct);

    // Build a List<List<Cell>> from plain text with a single colour attribute
    internal static List<List<Cell>> TextCells(string text, TGAttribute attr) =>
        Cell.StringToLinesOfCells(text, attr);

    // Diff output for the RunWork FooService.cs edit (inspection panel)
    // Format mirrors AddDiffLine: 2-indent + 4-linenum + space + indicator + space + content + bg-pad
    internal static List<List<Cell>> BuildDiffCells()
    {
        const int PadWidth = 160;
        var lines = new List<List<Cell>>();

        void AddRow(int ln, char indicator, string text,
            TGAttribute numAttr, TGAttribute lineAttr)
        {
            var line = new List<Cell>();
            Cell C(char ch, TGAttribute a) => new(a, false, new System.Text.Rune(ch));
            var indAttr = indicator == ' ' ? Palette.Normal : lineAttr;
            line.Add(C(' ', indAttr)); line.Add(C(' ', indAttr));
            foreach (var ch in ln.ToString().PadLeft(4)) line.Add(C(ch, numAttr));
            line.Add(C(' ', lineAttr));
            line.Add(C(indicator, lineAttr));
            line.Add(C(' ', lineAttr));
            foreach (var ch in text) line.Add(C(ch, lineAttr));
            if (indicator != ' ')
                while (line.Count < PadWidth) line.Add(C(' ', lineAttr));
            lines.Add(line);
        }

        void Hdr(string text)
        {
            var line = new List<Cell>();
            foreach (var ch in text)
                line.Add(new Cell(Palette.DiffHdr, false, new System.Text.Rune(ch)));
            lines.Add(line);
        }

        var add = (new TGAttribute(ColorName16.BrightGreen, ColorName16.Green),
                   new TGAttribute(ColorName16.BrightGreen, ColorName16.Green));

        Hdr("@@ -0,0 +1,16 @@");
        AddRow( 1, '+', "using Dotsy.Core.Services;",                                      add.Item1, add.Item2);
        AddRow( 2, '+', "",                                                            add.Item1, add.Item2);
        AddRow( 3, '+', "public class FooService : IFooService",                      add.Item1, add.Item2);
        AddRow( 4, '+', "{",                                                           add.Item1, add.Item2);
        AddRow( 5, '+', "    private readonly IRepository _repo;",                    add.Item1, add.Item2);
        AddRow( 6, '+', "    public FooService(IRepository repo) => _repo = repo;",   add.Item1, add.Item2);
        AddRow( 7, '+', "",                                                            add.Item1, add.Item2);
        AddRow( 8, '+', "    public Task<Result> GetAsync(int id, CancellationToken ct)", add.Item1, add.Item2);
        AddRow( 9, '+', "        => _repo.FindAsync(id, ct);",                        add.Item1, add.Item2);
        AddRow(10, '+', "",                                                            add.Item1, add.Item2);
        AddRow(11, '+', "    public Task SaveAsync(Model m, CancellationToken ct)",   add.Item1, add.Item2);
        AddRow(12, '+', "        => _repo.UpsertAsync(m, ct);",                       add.Item1, add.Item2);
        AddRow(13, '+', "",                                                            add.Item1, add.Item2);
        AddRow(14, '+', "    public Task DeleteAsync(int id, CancellationToken ct)",  add.Item1, add.Item2);
        AddRow(15, '+', "        => _repo.DeleteAsync(id, ct);",                      add.Item1, add.Item2);
        AddRow(16, '+', "}",                                                           add.Item1, add.Item2);
        return lines;
    }
}
