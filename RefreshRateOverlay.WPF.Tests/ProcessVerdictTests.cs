using System.Diagnostics;
using RefreshRateOverlay.WPF.Services;

namespace RefreshRateOverlay.WPF.Tests;

/// <summary>
/// Per-program verdicts: the user's right-click choices, the AI's answers, how
/// the planner applies them, and the AI classifier against a fake backend (the
/// real Claude CLI is never called from tests).
/// </summary>
public class ProcessVerdictTests
{
    private sealed class MemoryStore : IProcessVerdictStore
    {
        public List<ProcessVerdict> Saved { get; private set; } = new();
        public List<ProcessVerdict> Load() => Saved.ToList();
        public void Save(IReadOnlyCollection<ProcessVerdict> verdicts) => Saved = verdicts.ToList();
    }

    private const string Track = @"C:\Program Files\opentrack\opentrack.exe";
    private const string Send = @"C:\Apps\localsend.exe";

    private static ProcessSnapshot P(string name, string? path = null, int pid = 100, double cpu = 0.0) =>
        new(name, pid, path ?? $@"C:\Apps\{name}.exe", 1, ProcessPriorityClass.Normal, cpu);

    private static OptimizerDecision One(ProcessSnapshot p, ProcessVerdictCache cache) =>
        GameOptimizerPlanner.Plan("eldenring.exe", DateTime.Now, new[] { p }, 999, Array.Empty<string>(), verdicts: cache.All)
            .Decisions.Single();

    private static AiVerdict Ai(bool protect, bool confident = true) => new(protect, "Game companion", "why", DateTime.Now, confident);

    // ---- cache ----

    [Fact]
    public void Cache_UserChoiceWins_ClearingItFallsBackToTheAiAnswer_AndPersists()
    {
        var store = new MemoryStore();
        var cache = new ProcessVerdictCache(store);
        cache.SetAi(new[] { (Track, "opentrack", Ai(protect: true)) });
        cache.SetUser(Track, "opentrack", false);

        var reloaded = new ProcessVerdictCache(store);
        Assert.False(reloaded.All[Track.ToUpperInvariant()].UserProtect); // keyed case-insensitively
        Assert.True(reloaded.All[Track].Ai!.Protect);

        reloaded.SetUser(Track, "opentrack", null);
        Assert.Null(reloaded.All[Track].UserProtect);
        Assert.NotNull(reloaded.All[Track].Ai);

        reloaded.SetUser(Send, "localsend", true);
        reloaded.SetUser(Send, "localsend", null);
        Assert.False(reloaded.Contains(Send)); // nothing left to remember
    }

    // ---- planner ----

    [Fact]
    public void UserProtect_BeatsTheDefaultThrottle()
    {
        var cache = new ProcessVerdictCache(new MemoryStore());
        cache.SetUser(Track, "opentrack", true);
        var d = One(P("opentrack", Track, cpu: 2.0), cache);
        Assert.Equal(OptimizerRole.Protected, d.Role);
        Assert.Equal(GameOptimizerPlanner.UserProtectReason, d.Reason);
    }

    [Fact]
    public void UserThrottle_BeatsANameListProtection()
    {
        const string discord = @"C:\Users\me\AppData\Local\Discord\Discord.exe";
        var cache = new ProcessVerdictCache(new MemoryStore());
        cache.SetUser(discord, "Discord", false);
        var d = One(P("Discord", discord), cache);
        Assert.Equal(OptimizerRole.WouldThrottle, d.Role);
        Assert.Equal(GameOptimizerPlanner.UserThrottleReason, d.Reason);
    }

    [Fact]
    public void UserThrottle_NeverAppliesToTheGameOrAService()
    {
        const string game = @"D:\Games\eldenring.exe";
        var cache = new ProcessVerdictCache(new MemoryStore());
        cache.SetUser(game, "eldenring", false);
        Assert.Equal(OptimizerRole.Game, One(P("eldenring", game), cache).Role);

        const string svc = @"C:\Windows\svc.exe";
        cache.SetUser(svc, "svc", false);
        Assert.Equal(OptimizerRole.Protected, One(new ProcessSnapshot("svc", 5, svc, 0, ProcessPriorityClass.Normal, 0.0), cache).Role);
    }

    [Fact]
    public void AiProtect_Protects_WithItsReasonAsTheNote()
    {
        var cache = new ProcessVerdictCache(new MemoryStore());
        cache.SetAi(new[] { (Track, "opentrack", Ai(protect: true)) });
        var d = One(P("opentrack", Track), cache);
        Assert.Equal(OptimizerRole.Protected, d.Role);
        Assert.Equal(GameOptimizerPlanner.AiProtectReason, d.Reason);
        Assert.Equal("AI (Game companion): why", d.Note);
        Assert.Equal(Track, d.Path);
    }

    [Fact]
    public void AiThrottle_FallsThroughToTheRules_ButKeepsTheNote()
    {
        var cache = new ProcessVerdictCache(new MemoryStore());
        cache.SetAi(new[] { (Send, "localsend", Ai(protect: false)) });
        var d = One(P("localsend", Send), cache);
        Assert.Equal(OptimizerRole.WouldThrottle, d.Role);
        Assert.Equal(GameOptimizerPlanner.BackgroundReason, d.Reason);
        Assert.NotNull(d.Note);
    }

    [Fact]
    public void AiProtect_DoesNotOverrideASetPriority_OrAHardRule()
    {
        var cache = new ProcessVerdictCache(new MemoryStore());
        cache.SetAi(new[] { (@"C:\Apps\rustc.exe", "rustc", Ai(protect: true)) });
        // Hard protection rules come first; the AI only lifts things out of the default throttle.
        var nv = One(P("nvcontainer"), cache);
        Assert.Equal("GPU / audio / input helper", nv.Reason);
    }

    [Fact]
    public void VrDescendantRule_DoesNotUndoAUserThrottle()
    {
        var cache = new ProcessVerdictCache(new MemoryStore());
        cache.SetUser(@"C:\Apps\helper.exe", "helper", false);
        var r = GameOptimizerPlanner.Plan("g.exe", DateTime.Now, new[]
        {
            P("vrserver", pid: 1),
            P("helper", pid: 2) with { ParentPid = 1 },
        }, 999, Array.Empty<string>(), verdicts: cache.All);
        Assert.Equal(GameOptimizerPlanner.UserThrottleReason, r.Decisions.Single(d => d.Pid == 2).Reason);
    }

    // ---- classifier ----

    private sealed class FakeBackend : IAiClassifierBackend
    {
        public List<(List<AiCandidate> Batch, bool Web)> Calls { get; } = new();
        public Func<AiCandidate, bool, AiAnswer>? Answer { get; set; }
        public Exception? Throw { get; set; }

        public Task<IReadOnlyList<AiAnswer>> ClassifyAsync(IReadOnlyList<AiCandidate> batch, bool webSearch, CancellationToken ct)
        {
            Calls.Add((batch.ToList(), webSearch));
            if (Throw is not null) throw Throw;
            return Task.FromResult<IReadOnlyList<AiAnswer>>(batch.Select(c => Answer!(c, webSearch)).ToList());
        }
    }

    private static OptimizerReport Report(ProcessVerdictCache cache, params ProcessSnapshot[] ps) =>
        GameOptimizerPlanner.Plan("eldenring.exe", DateTime.Now, ps, 999, Array.Empty<string>(), verdicts: cache.All);

    private static readonly Func<string, (string?, string?, string?)> NoInfo = _ => (null, null, null);

    [Fact]
    public async Task Classifier_AsksOnlyAboutUnknownBackgroundPrograms_Once()
    {
        var cache = new ProcessVerdictCache(new MemoryStore());
        var backend = new FakeBackend { Answer = (c, _) => new AiAnswer(c.Path, c.Name == "opentrack", "Other", "r") };
        var ai = new AiProcessClassifier(backend, cache);
        var report = Report(cache,
            P("opentrack", Track, pid: 1),
            P("opentrack", Track, pid: 2),          // same program twice: asked once
            P("rustc", pid: 3),                     // build tool: has a rule
            P("nvcontainer", pid: 4),               // protected by name
            P("eldenring", pid: 5));                // the game

        Assert.True(await ai.ClassifyAsync(report, new Dictionary<int, string>(), NoInfo));
        Assert.Equal(new[] { Track }, backend.Calls.Single().Batch.Select(c => c.Path));
        Assert.False(backend.Calls.Single().Web);
        Assert.True(cache.All[Track].Ai!.Protect);

        // Re-planned with the answer, there is nothing left to ask.
        Assert.False(await ai.ClassifyAsync(Report(cache, P("opentrack", Track, pid: 1)), new Dictionary<int, string>(), NoInfo));
        Assert.Single(backend.Calls);
    }

    [Fact]
    public async Task Classifier_SearchesTheWeb_OnlyForProgramsItWasUnsureAbout()
    {
        var cache = new ProcessVerdictCache(new MemoryStore());
        var backend = new FakeBackend
        {
            Answer = (c, web) => new AiAnswer(c.Path, web && c.Name == "simpro3", "Other", web ? "found it" : "guess",
                Confident: web || c.Name != "simpro3"),
        };
        var ai = new AiProcessClassifier(backend, cache);
        await ai.ClassifyAsync(Report(cache, P("simpro3", pid: 1), P("localsend", Send, pid: 2)), new Dictionary<int, string>(), NoInfo);

        Assert.Equal(2, backend.Calls.Count);
        Assert.Equal(new[] { "simpro3" }, backend.Calls[1].Batch.Select(c => c.Name));
        Assert.True(backend.Calls[1].Web);
        var simpro = cache.All[@"C:\Apps\simpro3.exe"].Ai!;
        Assert.True(simpro.Protect);
        Assert.Equal("found it", simpro.Reason);
        Assert.False(cache.All[Send].Ai!.Protect);
    }

    [Fact]
    public async Task Classifier_Failure_IsReported_AndNotRetriedEveryPass()
    {
        var cache = new ProcessVerdictCache(new MemoryStore());
        var backend = new FakeBackend { Throw = new InvalidOperationException("not logged in") };
        var ai = new AiProcessClassifier(backend, cache);
        var report = Report(cache, P("localsend", Send));

        Assert.False(await ai.ClassifyAsync(report, new Dictionary<int, string>(), NoInfo));
        Assert.Equal("not logged in", ai.LastError);
        Assert.False(await ai.ClassifyAsync(report, new Dictionary<int, string>(), NoInfo));
        Assert.Single(backend.Calls);
        Assert.False(cache.Contains(Send)); // rules still apply: throttled
    }

    [Fact]
    public async Task Classifier_SendsTheParentAndInstalledProgramName()
    {
        var cache = new ProcessVerdictCache(new MemoryStore());
        var backend = new FakeBackend { Answer = (c, _) => new AiAnswer(c.Path, false, "Other", "r") };
        var ai = new AiProcessClassifier(backend, cache);
        await ai.ClassifyAsync(Report(cache, P("opentrack", Track, pid: 7)), new Dictionary<int, string> { [7] = "explorer" },
            _ => ("opentrack project", "Head tracking", "opentrack 2023 (opentrack)"));

        var c = backend.Calls.Single().Batch.Single();
        Assert.Equal("explorer", c.ParentName);
        Assert.Equal("opentrack 2023 (opentrack)", c.InstalledAs);
        string prompt = ClaudeCliClassifierBackend.BuildPrompt(new[] { c });
        Assert.Contains("installed program=opentrack 2023 (opentrack)", prompt);
        Assert.Contains("publisher=opentrack project", prompt);
        Assert.Contains("started by=explorer", prompt);
    }

    [Fact]
    public void InstalledPrograms_DeepestFolderWins()
    {
        var installed = new List<(string, string)>
        {
            (@"C:\Program Files\Oculus\", "Oculus (Meta)"),
            (@"C:\Program Files\Oculus\Support\oculus-runtime\", "Oculus Runtime (Meta)"),
        };
        Assert.Equal("Oculus Runtime (Meta)", InstalledPrograms.Find(installed, @"C:\Program Files\Oculus\Support\oculus-runtime\OVRServer_x64.exe"));
        Assert.Equal("Oculus (Meta)", InstalledPrograms.Find(installed, @"C:\Program Files\Oculus\other.exe"));
        Assert.Null(InstalledPrograms.Find(installed, @"C:\Elsewhere\x.exe"));
    }

    [Fact]
    public void ClaudeOutput_IsParsed_AndErrorsThrow()
    {
        const string ok = """
            {"type":"result","is_error":false,"result":"…","structured_output":{"programs":[
              {"path":"C:\\a.exe","protect":true,"confident":false,"category":"VR","reason":"streamer"},
              {"path":"C:\\b.exe","protect":false,"category":"Other","reason":"tool"},
              {"path":"","protect":true,"category":"VR","reason":"dropped"}]}}
            """;
        var answers = ClaudeCliClassifierBackend.ParseOutput(ok);
        Assert.Equal(2, answers.Count);
        Assert.Equal(new AiAnswer(@"C:\a.exe", true, "VR", "streamer", Confident: false), answers[0]);
        Assert.True(answers[1].Confident); // missing flag = confident

        Assert.Throws<InvalidOperationException>(() => ClaudeCliClassifierBackend.ParseOutput("""{"is_error":true,"result":"Not logged in"}"""));
        Assert.Throws<InvalidOperationException>(() => ClaudeCliClassifierBackend.ParseOutput("""{"is_error":false,"result":"x"}"""));
    }

    // ---- panel ----

    [Fact]
    public void Panel_RightClickMenu_RaisesTheVerdict_ForThatProgram() => StaTestHelper.Run(() =>
    {
        var cache = new ProcessVerdictCache(new MemoryStore());
        var panel = new OptimizerPanelWindow();
        var raised = new List<(string Path, string Name, bool? Protect)>();
        panel.VerdictRequested += (_, v) => raised.Add(v);
        panel.Update(Report(cache, P("eldenring", pid: 1), P("localsend", Send, pid: 2)), OptimizerMode.DryRun);

        Assert.False(panel.RowMenus.ContainsKey("eldenring")); // the game has no menu
        var items = panel.RowMenus["localsend"].Items.Cast<System.Windows.Controls.MenuItem>().ToList();
        Assert.Equal(new[] { "Always protect localsend", "Always throttle localsend", "Use the automatic rules" },
            items.Select(i => (string)i.Header));
        Assert.False(items[2].IsEnabled); // nothing chosen yet

        items[0].RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
        Assert.Equal((Send, "localsend", (bool?)true), raised.Single());
    });

    [Fact]
    public void Panel_AskAiBox_RaisesItsSetting_AndShowsStatus() => StaTestHelper.Run(() =>
    {
        var panel = new OptimizerPanelWindow();
        var raised = new List<bool>();
        panel.AskAiChanged += (_, v) => raised.Add(v);
        panel.SetAskAi(true);
        Assert.Empty(raised); // setting it from the INI is not a user change

        panel.AskAiBox.IsChecked = false;
        panel.AskAiBox.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Assert.Equal(new[] { false }, raised);

        panel.SetAiStatus("asking AI about 3 program(s)…");
        Assert.Equal("asking AI about 3 program(s)…", panel.AiStatusText);
    });
}
