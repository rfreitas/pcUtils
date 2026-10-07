using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace RefreshRateOverlay.WPF.Services;

/// <summary>One unknown program, with what Windows says about it. This is all
/// that is sent to the AI: no window titles, no command lines, no file contents.
/// InstalledAs is the program's entry in Windows' installed-programs list, when
/// the exe sits in an installed program's folder.</summary>
internal sealed record AiCandidate(string Path, string Name, string? Company, string? Description, string? ParentName,
    string? InstalledAs = null);

/// <summary>Confident is false when the model is guessing from the name alone.</summary>
internal sealed record AiAnswer(string Path, bool Protect, string Category, string Reason, bool Confident = true);

internal interface IAiClassifierBackend
{
    /// <summary>webSearch: let the model search the web (second pass, unsure programs only).</summary>
    Task<IReadOnlyList<AiAnswer>> ClassifyAsync(IReadOnlyList<AiCandidate> batch, bool webSearch, CancellationToken ct);
}

/// <summary>
/// Windows' installed-programs list (the Uninstall registry keys, machine and
/// user, 64- and 32-bit): install folder → "DisplayName (Publisher)". A free,
/// local, reliable name for an exe that its own version info often lacks.
/// </summary>
internal static class InstalledPrograms
{
    public static List<(string Folder, string Label)> Read()
    {
        var list = new List<(string, string)>();
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
            foreach (string root in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
            {
                try
                {
                    using var key = hive.OpenSubKey(root);
                    if (key is null) continue;
                    foreach (string sub in key.GetSubKeyNames())
                    {
                        using var app = key.OpenSubKey(sub);
                        if (app?.GetValue("InstallLocation") is not string folder || folder.Trim().Trim('"').Length <= 3) continue;
                        if (app.GetValue("DisplayName") is not string name || name.Length == 0) continue;
                        string label = app.GetValue("Publisher") is string pub && pub.Length > 0 ? $"{name} ({pub})" : name;
                        list.Add((folder.Trim().Trim('"').TrimEnd('\\') + "\\", label));
                    }
                }
                catch { }
            }
        return list;
    }

    /// <summary>The installed program whose folder holds this exe; the deepest folder wins.</summary>
    public static string? Find(IReadOnlyList<(string Folder, string Label)> installed, string exePath) =>
        installed.Where(i => exePath.StartsWith(i.Folder, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(i => i.Folder.Length)
            .Select(i => i.Label)
            .FirstOrDefault();
}

/// <summary>
/// Asks an AI about programs no rule recognises, and stores the answers in the
/// verdict cache so each program is asked about once. Only programs the rules
/// would throttle as ordinary background work are asked about (Background /
/// Busy now): everything else already has a rule. The AI can only move a
/// program to Protected; until it answers, the rules apply (throttled).
/// Two passes: the model answers from what it knows, and only the programs it
/// was not confident about go to a second call with web search. One batch at a
/// time; a program whose batch failed is not retried until the app restarts.
/// </summary>
internal sealed class AiProcessClassifier
{
    public const int BatchSize = 40;

    private readonly IAiClassifierBackend _backend;
    private readonly ProcessVerdictCache _cache;
    private readonly Action<string> _log;
    private readonly HashSet<string> _attempted = new(StringComparer.OrdinalIgnoreCase);

    public AiProcessClassifier(IAiClassifierBackend backend, ProcessVerdictCache cache, Action<string>? log = null)
    {
        _backend = backend;
        _cache = cache;
        _log = log ?? (_ => { });
    }

    public bool Busy { get; private set; }

    /// <summary>Programs waiting for (or in) an AI answer, for the panel.</summary>
    public int Pending { get; private set; }

    /// <summary>Last failure, shown in the panel; null after a success.</summary>
    public string? LastError { get; private set; }

    /// <summary>Unknown programs in this report: throttled by the default rules,
    /// with a readable path, not in the cache and not tried this run.</summary>
    public List<OptimizerDecision> Unknown(OptimizerReport report) =>
        report.Decisions
            .Where(d => d.Role == OptimizerRole.WouldThrottle
                        && d.Reason is GameOptimizerPlanner.BackgroundReason or GameOptimizerPlanner.BusyReason
                        && d.Path is { Length: > 0 } p && !_cache.Contains(p) && !_attempted.Contains(p))
            .GroupBy(d => d.Path!, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

    /// <summary>
    /// Classifies the report's unknown programs, one batch per call. Returns true
    /// when the cache changed (the caller re-plans). describe fills in what Windows
    /// knows about an exe (version info), off the UI thread.
    /// </summary>
    public async Task<bool> ClassifyAsync(OptimizerReport report, IReadOnlyDictionary<int, string> parentNames,
        Func<string, (string? Company, string? Description, string? InstalledAs)> describe, CancellationToken ct = default)
    {
        if (Busy) return false;
        var unknown = Unknown(report);
        Pending = unknown.Count;
        if (unknown.Count == 0) return false;

        Busy = true;
        try
        {
            var batch = unknown.Take(BatchSize).ToList();
            foreach (var d in batch) _attempted.Add(d.Path!);

            var candidates = await Task.Run(() => batch.Select(d =>
            {
                var (company, description, installedAs) = describe(d.Path!);
                return new AiCandidate(d.Path!, d.Name, company, description,
                    parentNames.TryGetValue(d.Pid, out string? parent) ? parent : null, installedAs);
            }).ToList(), ct);
            var byPath = candidates.ToDictionary(c => c.Path, StringComparer.OrdinalIgnoreCase);

            var sw = Stopwatch.StartNew();
            var answers = (await _backend.ClassifyAsync(candidates, webSearch: false, ct))
                .Where(a => byPath.ContainsKey(a.Path))
                .GroupBy(a => a.Path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            // Second pass, with web search, only for what the model was guessing about.
            var unsure = candidates.Where(c => !answers.TryGetValue(c.Path, out var a) || !a.Confident).ToList();
            if (unsure.Count > 0)
            {
                try
                {
                    foreach (var a in await _backend.ClassifyAsync(unsure, webSearch: true, ct))
                        if (byPath.ContainsKey(a.Path)) answers[a.Path] = a;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log($"Optimizer AI: web-search pass failed, keeping first answers: {ex.Message}");
                }
            }

            var stored = answers.Values
                .Select(a => (a.Path, byPath[a.Path].Name, new AiVerdict(a.Protect, a.Category, a.Reason, DateTime.Now, a.Confident)))
                .ToList();
            _cache.SetAi(stored);
            LastError = null;
            Pending = Math.Max(0, unknown.Count - stored.Count);
            _log($"Optimizer AI: classified {stored.Count}/{candidates.Count} program(s) in {sw.ElapsedMilliseconds} ms " +
                 $"({unsure.Count} needed a web search); " +
                 $"protect: {string.Join(", ", stored.Where(s => s.Item3.Protect).Select(s => s.Name).DefaultIfEmpty("none"))}.");
            return stored.Count > 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastError = ex.Message;
            _log($"Optimizer AI: classification failed: {ex.Message}");
            return false;
        }
        finally { Busy = false; }
    }

    /// <summary>Company and description from the exe's version resource, and its
    /// entry in Windows' installed-programs list.</summary>
    public static (string? Company, string? Description, string? InstalledAs) DescribeExe(
        string path, IReadOnlyList<(string Folder, string Label)> installed)
    {
        string? installedAs = InstalledPrograms.Find(installed, path);
        try
        {
            var v = FileVersionInfo.GetVersionInfo(path);
            static string? N(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
            return (N(v.CompanyName), N(v.FileDescription) ?? N(v.ProductName), installedAs);
        }
        catch { return (null, null, installedAs); }
    }
}

/// <summary>
/// Runs the local Claude Code CLI once per batch: print mode, no MCP servers
/// (their tool definitions alone were ~30k tokens and none of them knows about
/// processes), no slash commands, no settings or session saved, and a fixed
/// JSON schema for the answer. A short system prompt replaces Claude Code's own.
/// First pass: no tools, ~1.5k tokens (measured ~3 s, under $0.01 list price).
/// Web pass: WebSearch only (measured ~11 s, ~$0.04 for one program).
/// </summary>
internal sealed class ClaudeCliClassifierBackend : IAiClassifierBackend
{
    public static readonly string[] Categories =
    {
        "VR", "Input / controller", "Audio / voice", "Capture / streaming", "Game companion", "Overlay",
        "Hardware control", "Background app", "Development", "Other",
    };

    internal const string SystemPrompt =
        "You classify Windows programs for a game optimizer on a gaming PC (often VR). While a game runs, " +
        "the optimizer throttles every ordinary background program: Below normal CPU priority, efficiency cores " +
        "only, very low disk priority. A throttled program still runs; it only yields when the game needs the CPU. " +
        "For each program decide whether it must be PROTECTED from that. Protect only when throttling could " +
        "plausibly hurt the game, VR, or what the player does in real time while playing: VR runtimes, streamers " +
        "and overlays; input, controller, head-tracking or input-remapping software; voice chat and audio " +
        "processing; game capture and streaming; companions that exchange data with the running game in real " +
        "time (telemetry dashboards, motion rigs, wheel/force-feedback software, sim tools); in-game overlays; " +
        "hardware control that must react promptly. Do NOT protect ordinary background work: launchers' " +
        "background parts, updaters, sync and backup clients, browsers, messaging apps, editors, terminals, " +
        "developer tools, utilities, tray apps. Decide from the name, path, publisher, description and installed " +
        "program name. Set confident to false when you do not actually know the program and are guessing from " +
        "its name. If a program is unknown and nothing suggests a real-time link to the game, do not protect it. " +
        "Answer only through the structured output, one entry per program, with the path exactly as given and " +
        "a short reason.";

    internal const string WebSearchNote =
        " You may search the web to identify programs you do not know; set confident to true only if you found " +
        "what the program is.";

    private static readonly string Schema = JsonSerializer.Serialize(new
    {
        type = "object",
        properties = new
        {
            programs = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        path = new { type = "string" },
                        protect = new { type = "boolean" },
                        confident = new { type = "boolean" },
                        category = new { type = "string", @enum = Categories },
                        reason = new { type = "string" },
                    },
                    required = new[] { "path", "protect", "confident", "category", "reason" },
                },
            },
        },
        required = new[] { "programs" },
    });

    private readonly string _exe;
    private readonly TimeSpan _timeout;

    public ClaudeCliClassifierBackend(string exe, TimeSpan? timeout = null)
    {
        _exe = exe;
        _timeout = timeout ?? TimeSpan.FromSeconds(120);
    }

    /// <summary>claude.exe on PATH, or Claude Code's default install folder; null if neither.</summary>
    public static string? FindClaude()
    {
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(dir.Trim(), "claude.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe");
        return File.Exists(local) ? local : null;
    }

    internal static string BuildPrompt(IReadOnlyList<AiCandidate> batch)
    {
        var sb = new StringBuilder("Programs:\n");
        int i = 1;
        foreach (var c in batch)
        {
            sb.Append(i++).Append(". path=").Append(c.Path).Append(" | name=").Append(c.Name);
            if (c.Company is not null) sb.Append(" | publisher=").Append(c.Company);
            if (c.Description is not null) sb.Append(" | description=").Append(c.Description);
            if (c.InstalledAs is not null) sb.Append(" | installed program=").Append(c.InstalledAs);
            if (c.ParentName is not null) sb.Append(" | started by=").Append(c.ParentName);
            sb.Append('\n');
        }
        return sb.ToString();
    }

    public async Task<IReadOnlyList<AiAnswer>> ClassifyAsync(IReadOnlyList<AiCandidate> batch, bool webSearch, CancellationToken ct)
    {
        string workDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RefreshRateOverlay.WPF", "ai");
        Directory.CreateDirectory(workDir); // neutral folder: no project CLAUDE.md gets picked up

        var psi = new ProcessStartInfo(_exe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workDir,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (string a in new[]
        {
            "-p", "--strict-mcp-config", "--disable-slash-commands", "--no-session-persistence",
            "--setting-sources", "", "--model", "sonnet", "--output-format", "json",
            "--json-schema", Schema, "--system-prompt", webSearch ? SystemPrompt + WebSearchNote : SystemPrompt,
        })
            psi.ArgumentList.Add(a);
        // Only web search, never file, shell or MCP tools.
        foreach (string a in webSearch ? new[] { "--tools", "WebSearch", "--allowedTools", "WebSearch" } : new[] { "--tools", "" })
            psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("could not start claude");
        await proc.StandardInput.WriteAsync(BuildPrompt(batch));
        proc.StandardInput.Close();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeout);
        var stdout = proc.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = proc.StandardError.ReadToEndAsync(timeout.Token);
        try { await proc.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException($"claude did not answer within {_timeout.TotalSeconds:0} s");
        }

        string output = await stdout;
        if (proc.ExitCode != 0 && output.Length == 0)
            throw new InvalidOperationException($"claude exited {proc.ExitCode}: {Trim(await stderr)}");
        return ParseOutput(output);
    }

    /// <summary>Reads claude's --output-format json result: structured_output.programs.</summary>
    internal static IReadOnlyList<AiAnswer> ParseOutput(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("is_error", out var err) && err.ValueKind == JsonValueKind.True)
            throw new InvalidOperationException("claude reported an error: " +
                (root.TryGetProperty("result", out var r) ? Trim(r.ToString()) : "unknown"));
        if (!root.TryGetProperty("structured_output", out var so) || !so.TryGetProperty("programs", out var programs)
            || programs.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("claude returned no structured answer");

        var answers = new List<AiAnswer>();
        foreach (var p in programs.EnumerateArray())
        {
            if (p.TryGetProperty("path", out var path) && path.GetString() is { Length: > 0 } pathText
                && p.TryGetProperty("protect", out var protect) && protect.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                string category = p.TryGetProperty("category", out var c) ? c.GetString() ?? "Other" : "Other";
                string reason = p.TryGetProperty("reason", out var rs) ? rs.GetString() ?? "" : "";
                bool confident = !p.TryGetProperty("confident", out var cf) || cf.ValueKind != JsonValueKind.False;
                answers.Add(new AiAnswer(pathText, protect.GetBoolean(), category, reason, confident));
            }
        }
        return answers;
    }

    private static string Trim(string s) => s.Length > 300 ? s[..300] + "…" : s.Trim();
}
