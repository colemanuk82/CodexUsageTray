using System.Drawing.Drawing2D;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace CodexUsageTray;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext());
    }
}

internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon tray;
    private readonly UsageClient client = new();
    private readonly ResetDataClient resetClient = new();
    private readonly string stateFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexUsageTray", "state.json");
    private UsageSnapshot? latest;
    private GraphForm? graph;
    private AnalyticsForm? analytics;
    private bool busy;
    private readonly System.Windows.Forms.Timer refreshTimer;
    private readonly System.Windows.Forms.Timer resetTimer;
    private int refreshMinutes = 1;
    private int graphDays = 7;
    private ResetData? resetData;

    public TrayContext()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(stateFile)!);
        resetData = resetClient.LoadCached();
        tray = new NotifyIcon { Visible = true, Text = "Codex usage", Icon = MakeIcon(null) };
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) TogglePopout(); };
        tray.ContextMenuStrip = Menu();
        refreshTimer = new System.Windows.Forms.Timer { Interval = refreshMinutes * 60 * 1000 };
        refreshTimer.Tick += async (_, _) => await RefreshAsync();
        refreshTimer.Start();
        resetTimer = new System.Windows.Forms.Timer { Interval = 60 * 60 * 1000 };
        resetTimer.Tick += async (_, _) => await RefreshResetDataAsync();
        resetTimer.Start();
        _ = RefreshAsync();
        _ = RefreshResetDataAsync();
    }

    private ContextMenuStrip Menu()
    {
        var m = new ContextMenuStrip();
        m.Items.Add("Open Codex limits", null, (_, _) => ShowGraph());
        m.Items.Add("Open Codex usage", null, (_, _) => ShowAnalytics());
        m.Items.Add("Refresh now", null, async (_, _) => await RefreshAsync());
        var refreshMenu = new ToolStripMenuItem("Refresh interval");
        foreach (var minutes in new[] { 1, 5 })
        {
            var item = new ToolStripMenuItem($"{minutes} minute{(minutes == 1 ? "" : "s")}") { Checked = refreshMinutes == minutes };
            item.Click += (_, _) => SetRefreshMinutes(minutes);
            refreshMenu.DropDownItems.Add(item);
        }
        m.Items.Add(refreshMenu);
        var durationMenu = new ToolStripMenuItem("Graph duration");
        foreach (var days in new[] { 1, 7, 30 })
        {
            var item = new ToolStripMenuItem($"{days} day{(days == 1 ? "" : "s")}") { Checked = graphDays == days };
            item.Click += (_, _) => SetGraphDays(days);
            durationMenu.DropDownItems.Add(item);
        }
        m.Items.Add(durationMenu);
        var startup = new ToolStripMenuItem("Start with Windows") { Checked = StartupEnabled, CheckOnClick = true };
        startup.CheckedChanged += (_, _) => { SetStartup(startup.Checked); };
        m.Items.Add(startup);
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Exit", null, (_, _) => ExitThread());
        return m;
    }

    private async Task RefreshAsync()
    {
        if (busy) return;
        busy = true;
        try
        {
            latest = await client.GetAsync();
            if (latest != null) SaveSnapshot(latest);
            UpdateIcon();
            tray.Text = latest == null ? "Codex usage unavailable" : $"5-hour: {latest.SessionRemaining:0}% remaining\nWeekly: {latest.WeeklyRemaining:0}% remaining";
        }
        catch (Exception ex) { tray.Text = "Codex usage unavailable"; System.Diagnostics.Debug.WriteLine(ex); }
        finally { busy = false; }
    }
    private async Task RefreshResetDataAsync()
    {
        try { if (resetData != null && DateTimeOffset.UtcNow - resetData.FetchedAt < TimeSpan.FromHours(1)) { UpdateIcon(); if (graph != null && !graph.IsDisposed) graph.SetResetData(resetData); return; } resetData = await resetClient.GetAsync(); UpdateIcon(); if (graph != null && !graph.IsDisposed) graph.SetResetData(resetData); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
    }
    private void SetRefreshMinutes(int minutes) { refreshMinutes = minutes; refreshTimer.Interval = minutes * 60 * 1000; graph?.SetOptions(refreshMinutes, graphDays); analytics?.SetRefreshMinutes(refreshMinutes); }
    private void SetGraphDays(int days) { graphDays = days; graph?.SetOptions(refreshMinutes, graphDays); }

    private void UpdateIcon() { var old = tray.Icon; tray.Icon = MakeIcon(latest); old?.Dispose(); }

    private void ShowGraph()
    {
        if (analytics != null && !analytics.IsDisposed) analytics.Close();
        if (latest == null) { _ = RefreshAsync(); return; }
        if (graph != null && !graph.IsDisposed)
        {
            if (graph.WindowState == FormWindowState.Minimized) graph.WindowState = FormWindowState.Normal;
            graph.UpdateData(latest, ReadSnapshots()); graph.SetOptions(refreshMinutes, graphDays); graph.SetResetData(resetData); graph.PlaceAboveTray(); graph.BringToFront(); graph.Activate(); return;
        }
        graph = new GraphForm(latest, ReadSnapshots(), refreshMinutes, graphDays, SetRefreshMinutes, SetGraphDays, resetData, ShowAnalytics);
        graph.FormClosed += (_, _) => graph = null;
        graph.Show(); graph.PlaceAboveTray(); graph.Activate();
    }
    private void TogglePopout()
    {
        if (graph != null && !graph.IsDisposed) { graph.Close(); return; }
        if (analytics != null && !analytics.IsDisposed) { analytics.Close(); return; }
        ShowGraph();
    }
    private void ShowAnalytics()
    {
        if (graph != null && !graph.IsDisposed) graph.Close();
        if (analytics != null && !analytics.IsDisposed) { if (analytics.WindowState == FormWindowState.Minimized) analytics.WindowState = FormWindowState.Normal; analytics.PlaceAboveTray(); analytics.BringToFront(); analytics.Activate(); return; }
        analytics = new AnalyticsForm(ShowGraph, refreshMinutes, SetRefreshMinutes); analytics.FormClosed += (_, _) => analytics = null; analytics.Show(); analytics.PlaceAboveTray(); analytics.Activate();
    }

    private bool StartupEnabled => Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")?.GetValue("CodexUsageTray") != null;
    private void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled) key.SetValue("CodexUsageTray", $"\"{Application.ExecutablePath}\"");
        else key.DeleteValue("CodexUsageTray", false);
    }
    private void SaveSnapshot(UsageSnapshot s)
    {
        var all = ReadSnapshots(); all.Add(s); all = all.Where(x => x.At >= DateTimeOffset.UtcNow.AddDays(-31)).ToList();
        File.WriteAllText(stateFile, JsonSerializer.Serialize(new State { History = all }));
    }
    private List<UsageSnapshot> ReadSnapshots()
    {
        try { return JsonSerializer.Deserialize<State>(File.ReadAllText(stateFile))?.History ?? []; } catch { return []; }
    }
    protected override void ExitThreadCore() { refreshTimer.Dispose(); resetTimer.Dispose(); tray.Visible = false; tray.Dispose(); base.ExitThreadCore(); }

    private Icon MakeIcon(UsageSnapshot? s)
    {
        using var b = new Bitmap(32, 32); using var g = Graphics.FromImage(b);
        g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Color.Transparent);
        var alert = resetData?.ChancePercent > 70; var weeklyColor = alert ? Color.FromArgb(235, 65, 70) : Color.FromArgb(55, 225, 95); var sessionColor = alert ? Color.FromArgb(235, 65, 70) : Color.FromArgb(45, 155, 255); DrawBar(g, 3, 5, s?.WeeklyRemaining, weeklyColor); DrawBar(g, 3, 20, s?.SessionRemaining, sessionColor);
        return Icon.FromHandle(b.GetHicon());
    }
    private static void DrawBar(Graphics g, int x, int y, double? remaining, Color color)
    {
        using var bg = new SolidBrush(Color.FromArgb(Math.Max(18, color.R / 4), Math.Max(18, color.G / 4), Math.Max(18, color.B / 4))); g.FillRoundedRectangle(bg, x, y, 26, 7, 3);
        if (remaining.HasValue) { using var br = new SolidBrush(color); g.FillRoundedRectangle(br, x, y, Math.Max(2, 26 * (float)remaining.Value / 100), 7, 3); }
        using var outline = new Pen(color); g.DrawRoundedRectangle(outline, x, y, 26, 7, 3);
    }
    private static Color ColorFor(double? remaining) => !remaining.HasValue ? Color.Gray : remaining < 20 ? Color.IndianRed : remaining < 50 ? Color.DarkOrange : Color.MediumSeaGreen;
}

internal sealed class UsageClient
{
    private static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(15) };
    public async Task<UsageSnapshot?> GetAsync()
    {
        var authPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "auth.json");
        if (!File.Exists(authPath)) return null;
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(authPath)); var root = doc.RootElement;
        var token = root.GetProperty("tokens").GetProperty("access_token").GetString();
        if (string.IsNullOrWhiteSpace(token)) return null;
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://chatgpt.com/backend-api/wham/usage"); req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (root.GetProperty("tokens").TryGetProperty("account_id", out var account)) req.Headers.TryAddWithoutValidation("ChatGPT-Account-ID", account.GetString());
        using var res = await http.SendAsync(req); res.EnsureSuccessStatusCode(); using var data = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var r = data.RootElement; var primary = Find(r, "primary_window", "primary"); var secondary = Find(r, "secondary_window", "secondary");
        return new UsageSnapshot(DateTimeOffset.UtcNow, 100 - Used(primary), 100 - Used(secondary), ResetAt(primary), ResetAt(secondary));
    }
    private static JsonElement Find(JsonElement r, params string[] names)
    {
        foreach (var n in names) if (r.TryGetProperty(n, out var e)) return e;
        if (r.TryGetProperty("rate_limit", out var rl)) foreach (var n in names) if (rl.TryGetProperty(n, out var e)) return e;
        return default;
    }
    private static double Used(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return 0;
        foreach (var n in new[] { "used_percent", "usedPercentage", "percent_used" }) if (e.TryGetProperty(n, out var p) && p.TryGetDouble(out var v)) return Math.Clamp(v, 0, 100);
        if (e.TryGetProperty("remaining", out var rem) && e.TryGetProperty("limit", out var lim) && lim.GetDouble() > 0) return 100 - rem.GetDouble() / lim.GetDouble() * 100;
        return 0;
    }
    private static DateTimeOffset? ResetAt(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in new[] { "reset_at", "resetAt", "resets_at" })
        {
            if (!e.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var unixSeconds)) return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
            if (value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), out var parsed)) return parsed;
        }
        return null;
    }
}

internal record UsageSnapshot(DateTimeOffset At, double SessionRemaining, double WeeklyRemaining, DateTimeOffset? SessionResetAt = null, DateTimeOffset? WeeklyResetAt = null);
internal sealed class State { public bool Bars { get; set; } public List<UsageSnapshot> History { get; set; } = []; }

internal sealed class CodexUsageRecord
{
    public string SourceFile { get; set; } = ""; public string SessionId { get; set; } = ""; public string Project { get; set; } = "Unknown project"; public string Model { get; set; } = "Codex"; public DateTimeOffset At { get; set; } public long InputTokens { get; set; } public long CachedInputTokens { get; set; } public long OutputTokens { get; set; } public long ReasoningTokens { get; set; } public long TotalTokens => InputTokens + OutputTokens + ReasoningTokens;
}
internal sealed class AnalyticsCache { public int Version { get; set; } = 3; public Dictionary<string, long> Files { get; set; } = []; public List<CodexUsageRecord> Records { get; set; } = []; }
internal sealed class CodexAnalyticsStore
{
    private readonly string sessionsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions"); private readonly string cachePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexUsageTray", "analytics-cache.json");
    public List<CodexUsageRecord> Load()
    {
        var cache = ReadCache(); if (!Directory.Exists(sessionsPath)) return cache.Records; var files = Directory.EnumerateFiles(sessionsPath, "*.jsonl", SearchOption.AllDirectories).ToList(); var active = files.ToHashSet(StringComparer.OrdinalIgnoreCase); cache.Records.RemoveAll(x => !active.Contains(x.SourceFile));
        foreach (var file in files) { var stamp = File.GetLastWriteTimeUtc(file).Ticks; if (cache.Files.TryGetValue(file, out var old) && old == stamp) continue; cache.Records.RemoveAll(x => string.Equals(x.SourceFile, file, StringComparison.OrdinalIgnoreCase)); try { cache.Records.AddRange(ParseFile(file)); cache.Files[file] = stamp; } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!); File.WriteAllText(cachePath, JsonSerializer.Serialize(cache)); return cache.Records;
    }
    private AnalyticsCache ReadCache() { try { var cache = JsonSerializer.Deserialize<AnalyticsCache>(File.ReadAllText(cachePath)) ?? new AnalyticsCache(); return cache.Version == 3 ? cache : new AnalyticsCache(); } catch { return new AnalyticsCache(); } }
    private static List<CodexUsageRecord> ParseFile(string file)
    {
        var records = new List<CodexUsageRecord>(); var sessionId = Path.GetFileNameWithoutExtension(file); var project = "Unknown project"; var model = "Codex (unknown)";
        foreach (var line in ReadSharedLines(file)) { try { using var doc = JsonDocument.Parse(line); var root = doc.RootElement; if (!root.TryGetProperty("payload", out var payload)) continue; if (payload.TryGetProperty("session_id", out var sid)) sessionId = sid.GetString() ?? sessionId; if (payload.TryGetProperty("cwd", out var cwd)) project = cwd.GetString() ?? project; if (payload.TryGetProperty("model", out var modelValue) && !string.IsNullOrWhiteSpace(modelValue.GetString())) model = modelValue.GetString()!; if (payload.TryGetProperty("thread_settings", out var settings) && settings.TryGetProperty("model", out var settingsModel) && !string.IsNullOrWhiteSpace(settingsModel.GetString())) model = settingsModel.GetString()!; if (!payload.TryGetProperty("type", out var type) || type.GetString() != "token_count" || !payload.TryGetProperty("info", out var info) || !info.TryGetProperty("last_token_usage", out var usage)) continue; long Number(string name) => usage.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : 0; var at = root.TryGetProperty("timestamp", out var timestamp) && DateTimeOffset.TryParse(timestamp.GetString(), out var parsed) ? parsed : File.GetLastWriteTimeUtc(file); records.Add(new CodexUsageRecord { SourceFile = file, SessionId = sessionId, Project = project, Model = model, At = at, InputTokens = Number("input_tokens"), CachedInputTokens = Number("cached_input_tokens"), OutputTokens = Number("output_tokens"), ReasoningTokens = Number("reasoning_output_tokens") }); } catch (JsonException) { } }
        return records;
    }
    private static IEnumerable<string> ReadSharedLines(string file) { using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); using var reader = new StreamReader(stream); while (reader.ReadLine() is { } line) yield return line; }
}
internal sealed class AnalyticsChart : Panel
{
    public List<CodexUsageRecord> Records { get; set; } = []; public int Days { get; set; } = 30; public DateTime StartDate { get; set; } = DateTime.Today.AddDays(-29);
    public AnalyticsChart() { BackColor = Color.FromArgb(24, 25, 29); DoubleBuffered = true; }
    protected override void OnPaint(PaintEventArgs e)
    { base.OnPaint(e); e.Graphics.Clear(BackColor); var left = 90; var top = 76; var right = Math.Max(left + 100, Width - 38); var bottom = Math.Max(top + 100, Height - 62); var start = StartDate.Date; var daily = Enumerable.Range(0, Days).Select(i => Records.Where(x => x.At.ToLocalTime().Date == start.AddDays(i)).Sum(x => x.TotalTokens)).ToList(); var max = Math.Max(1, daily.Max()); using var grid = new Pen(Color.FromArgb(50, 55, 62)); using var text = new SolidBrush(Color.FromArgb(180, 190, 205)); using var font = new Font("Segoe UI", 18, FontStyle.Regular, GraphicsUnit.Pixel); for (var i = 0; i <= 4; i++) { var y = top + i * (bottom - top) / 4; e.Graphics.DrawLine(grid, left, y, right, y); e.Graphics.DrawString($"{max * (4 - i) / 4 / 1000000d:0.#}M", font, text, 22, y - 10); } using var barBrush = new SolidBrush(Color.FromArgb(55, 225, 95)); var slot = (right - left) / (float)Math.Max(1, Days); for (var i = 0; i < daily.Count; i++) { var h = (float)(daily[i] / max * (bottom - top)); e.Graphics.FillRectangle(barBrush, left + i * slot + 2, bottom - h, Math.Max(3, slot - 5), h); } using var heading = new Font("Segoe UI", 32, FontStyle.Bold, GraphicsUnit.Pixel); e.Graphics.DrawString("Daily Codex usage", heading, Brushes.White, 34, 22); e.Graphics.DrawString(start.ToString("dd MMM"), font, text, left, bottom + 14); var endLabel = (start.AddDays(Days - 1)).ToString("dd MMM"); var endSize = e.Graphics.MeasureString(endLabel, font); e.Graphics.DrawString(endLabel, font, text, right - endSize.Width, bottom + 14); }
}
internal sealed class ModelRate { public decimal Input { get; set; } public decimal Cached { get; set; } public decimal Output { get; set; } }
internal sealed class ModelRateCache { public DateTimeOffset FetchedAt { get; set; } public Dictionary<string, ModelRate> Rates { get; set; } = []; }
internal static class ModelCostEstimator
{
    private static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static Dictionary<string, ModelRate> rates = Defaults();
    private static string CachePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexUsageTray", "model-rates.json");
    private static Dictionary<string, ModelRate> Defaults() => new(StringComparer.OrdinalIgnoreCase) { ["gpt-5.6-luna"] = new() { Input = .20m, Cached = .02m, Output = 1.20m }, ["gpt-5.6-terra"] = new() { Input = 2m, Cached = .20m, Output = 12m }, ["gpt-5.6-sol"] = new() { Input = 4m, Cached = .40m, Output = 20m }, ["gpt-5.6"] = new() { Input = 4m, Cached = .40m, Output = 20m }, ["codex-auto-review"] = new() { Input = .20m, Cached = .02m, Output = 1.20m } };
    public static async Task SyncAsync(IEnumerable<string> models)
    {
        try
        {
            ModelRateCache cache; try { cache = JsonSerializer.Deserialize<ModelRateCache>(File.ReadAllText(CachePath)) ?? new(); } catch { cache = new(); }
            rates = Defaults(); foreach (var entry in cache.Rates) rates[entry.Key] = entry.Value;
            if (DateTimeOffset.UtcNow - cache.FetchedAt < TimeSpan.FromHours(24)) return;
            foreach (var model in models.Where(x => x.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var html = await http.GetStringAsync($"https://developers.openai.com/api/docs/models/{model}");
                var match = Regex.Match(html, @"Input</div><div[^>]*>\$(?<input>[\d.]+)</div></div><div[^>]*><div>Cached input</div><div[^>]*>\$(?<cached>[\d.]+)</div></div><div[^>]*><div>Output</div><div[^>]*>\$(?<output>[\d.]+)</div>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                if (match.Success) rates[model] = new ModelRate { Input = decimal.Parse(match.Groups["input"].Value, System.Globalization.CultureInfo.InvariantCulture), Cached = decimal.Parse(match.Groups["cached"].Value, System.Globalization.CultureInfo.InvariantCulture), Output = decimal.Parse(match.Groups["output"].Value, System.Globalization.CultureInfo.InvariantCulture) };
            }
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!); File.WriteAllText(CachePath, JsonSerializer.Serialize(new ModelRateCache { FetchedAt = DateTimeOffset.UtcNow, Rates = rates }));
        }
        catch { }
    }
    public static ModelRate? Rates(string model) => rates.TryGetValue(model, out var rate) ? rate : null;
    public static decimal Estimate(IEnumerable<CodexUsageRecord> records) { var list = records.ToList(); if (list.Count == 0 || Rates(list[0].Model) is not { } rate) return 0; var input = Math.Max(0, list.Sum(x => x.InputTokens) - list.Sum(x => x.CachedInputTokens)); var cached = list.Sum(x => x.CachedInputTokens); var output = list.Sum(x => x.OutputTokens) + list.Sum(x => x.ReasoningTokens); return input / 1_000_000m * rate.Input + cached / 1_000_000m * rate.Cached + output / 1_000_000m * rate.Output; }
    public static string DisplayModel(string model) => model.Equals("codex-auto-review", StringComparison.OrdinalIgnoreCase) ? "gpt-5.6-luna" : model;
    public static Color ColorFor(string model) => model.ToLowerInvariant() switch { "gpt-5.6-luna" => Color.FromArgb(45, 155, 255), "gpt-5.6-terra" => Color.FromArgb(255, 165, 55), "gpt-5.6-sol" or "gpt-5.6" => Color.FromArgb(180, 95, 255), "codex-auto-review" => Color.FromArgb(45, 225, 185), _ => Color.FromArgb(120, 135, 150) };
}
internal sealed class AnalyticsBreakdownPanel : Panel
{
    public List<CodexUsageRecord> Records { get; set; } = [];
    public AnalyticsBreakdownPanel() { BackColor = Color.FromArgb(18, 20, 24); DoubleBuffered = true; }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); e.Graphics.Clear(BackColor); var groups = Records.GroupBy(x => x.Model).OrderByDescending(g => g.Sum(x => x.TotalTokens)).ToList(); var pad = 34; var gap = 22; var cardTop = 66; var cardHeight = Math.Max(190, Height - cardTop - 28); var cardWidth = Math.Max(320, (Width - pad * 2 - gap) / 2); var left = new Rectangle(pad, cardTop, cardWidth, cardHeight); var right = new Rectangle(left.Right + gap, cardTop, cardWidth, cardHeight);
        using var title = new Font("Segoe UI", 34, FontStyle.Bold, GraphicsUnit.Pixel); using var heading = new Font("Segoe UI", 26, FontStyle.Bold, GraphicsUnit.Pixel); using var text = new Font("Segoe UI", 21, FontStyle.Regular, GraphicsUnit.Pixel); using var muted = new SolidBrush(Color.LightSteelBlue); using var line = new Pen(Color.FromArgb(52, 58, 68)); using var card = new SolidBrush(Color.FromArgb(24, 27, 33)); using var track = new SolidBrush(Color.FromArgb(46, 55, 65));
        e.Graphics.DrawString("Model usage & predicted API cost", title, Brushes.White, pad, 14); e.Graphics.DrawString("Estimate only — calculated from local token records using standard API rates.", text, muted, pad, 45); e.Graphics.FillRectangle(card, left); e.Graphics.FillRectangle(card, right); e.Graphics.DrawRectangle(line, left); e.Graphics.DrawRectangle(line, right);
        e.Graphics.DrawString("Model usage", heading, Brushes.White, left.X + 22, left.Y + 18); var total = Math.Max(1, groups.Sum(g => g.Sum(x => x.TotalTokens))); var x = left.X + 22; var barY = left.Y + 64; var barWidth = left.Width - 44; e.Graphics.FillRectangle(track, x, barY, barWidth, 22); foreach (var group in groups) { var width = (int)(barWidth * group.Sum(r => r.TotalTokens) / (double)total); using var brush = new SolidBrush(ModelCostEstimator.ColorFor(group.Key)); e.Graphics.FillRectangle(brush, x, barY, Math.Max(1, width), 22); x += width; } var y = barY + 46; foreach (var group in groups.Take(4)) { var tokens = group.Sum(r => r.TotalTokens); using var dot = new SolidBrush(ModelCostEstimator.ColorFor(group.Key)); e.Graphics.FillEllipse(dot, left.X + 24, y + 4, 14, 14); e.Graphics.DrawString(group.Key, text, Brushes.White, left.X + 48, y); e.Graphics.DrawString($"{tokens:N0} tokens  ·  {tokens * 100d / total:0.0}%", text, muted, left.X + 48, y + 25); y += 64; }
        e.Graphics.DrawString("Predicted API cost", heading, Brushes.White, right.X + 22, right.Y + 18); y = right.Y + 64; var estimate = 0m; foreach (var group in groups.Take(4)) { var cost = ModelCostEstimator.Estimate(group); estimate += cost; using var dot = new SolidBrush(ModelCostEstimator.ColorFor(group.Key)); e.Graphics.FillEllipse(dot, right.X + 24, y + 5, 14, 14); e.Graphics.DrawString(group.Key, text, Brushes.White, right.X + 48, y); e.Graphics.DrawString(cost == 0 ? "Rate unavailable" : $"≈ ${cost:N2}", text, muted, right.X + 48, y + 25); y += 64; } e.Graphics.DrawLine(line, right.X + 22, Math.Min(right.Bottom - 54, y + 4), right.Right - 22, Math.Min(right.Bottom - 54, y + 4)); e.Graphics.DrawString("Estimated total", heading, Brushes.White, right.X + 22, Math.Min(right.Bottom - 42, y + 16)); e.Graphics.DrawString($"≈ ${estimate:N2}", heading, Brushes.White, right.Right - 150, Math.Min(right.Bottom - 42, y + 16));
    }
}
internal sealed class AnalyticsDashboard : Panel
{
    public List<CodexUsageRecord> Records { get; set; } = []; public int Days { get; set; } = 30; public int RefreshMinutes { get; set; } = 1; public DateTime StartDate { get; set; } = DateTime.Today.AddDays(-29); public event Action? GraphRangeClicked; public event Action? RefreshClicked; public event Action? LimitsClicked; private RectangleF rangeHit; private RectangleF refreshHit; private RectangleF limitsHit;
    public AnalyticsDashboard() { BackColor = Color.Black; DoubleBuffered = true; MouseClick += (_, e) => { const float scale = 1.2f; var point = new PointF(e.X / scale, e.Y / scale); if (rangeHit.Contains(point)) GraphRangeClicked?.Invoke(); else if (refreshHit.Contains(point)) RefreshClicked?.Invoke(); else if (limitsHit.Contains(point)) LimitsClicked?.Invoke(); }; }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); e.Graphics.Clear(BackColor); const float scale = 1.2f; e.Graphics.ScaleTransform(scale, scale); var displayWidth = Width / scale; var displayHeight = Height / scale; var records = Records; var total = records.Sum(x => x.TotalTokens); var calls = records.Count; var sessions = records.Select(x => x.SessionId).Distinct().Count(); var cached = records.Sum(x => x.CachedInputTokens); var groups = records.GroupBy(x => ModelCostEstimator.DisplayModel(x.Model)).OrderByDescending(g => g.Sum(x => x.TotalTokens)).ToList();
        using var title = new Font("Segoe UI", 38, FontStyle.Bold, GraphicsUnit.Pixel); using var metric = new Font("Segoe UI", 28, FontStyle.Bold, GraphicsUnit.Pixel); using var normal = new Font("Segoe UI", 23, FontStyle.Regular, GraphicsUnit.Pixel); using var section = new Font("Segoe UI", 30, FontStyle.Bold, GraphicsUnit.Pixel); using var label = new Font("Segoe UI", 27, FontStyle.Regular, GraphicsUnit.Pixel); using var average = new Font("Segoe UI", 37, FontStyle.Regular, GraphicsUnit.Pixel); using var muted = new SolidBrush(Color.LightSteelBlue); using var grid = new Pen(Color.FromArgb(56, 62, 72)); using var green = new SolidBrush(Color.FromArgb(55, 225, 95));
        e.Graphics.DrawString("Codex usage", title, Brushes.White, 48, 36); e.Graphics.DrawString($"{FormatTokens(total)} tokens", metric, Brushes.White, 48, 105); e.Graphics.DrawString($"{FormatTokens(calls)} calls", metric, Brushes.White, 325, 105); e.Graphics.DrawString($"{FormatTokens(sessions)} sessions", metric, Brushes.White, 470, 105); e.Graphics.DrawString($"{FormatTokens(cached)} cached input tokens", metric, Brushes.White, 48, 148); e.Graphics.DrawString($"Avg/day: {FormatTokens((long)(total / (double)Math.Max(1, Days)))}", metric, Brushes.White, 48, 198);
        var graphHeadingY = 290; var plotTop = 375; var left = 120; var right = displayWidth - 60; var bottom = 600; e.Graphics.DrawString("Daily Codex usage", section, Brushes.White, 48, graphHeadingY); var rangeLabel = $"Graph: {Days} day{(Days == 1 ? "" : "s")}"; var refreshLabel = $"Refresh: {RefreshMinutes} min"; var rangeSize = e.Graphics.MeasureString(rangeLabel, normal); var refreshSize = e.Graphics.MeasureString(refreshLabel, normal); var rangeX = right - rangeSize.Width; var refreshX = rangeX - refreshSize.Width - 28; using var control = new SolidBrush(Color.FromArgb(45, 155, 255)); e.Graphics.DrawString(refreshLabel, normal, control, refreshX, graphHeadingY + 6); e.Graphics.DrawString(rangeLabel, normal, control, rangeX, graphHeadingY + 6); refreshHit = new RectangleF(refreshX - 8, graphHeadingY - 2, refreshSize.Width + 16, normal.Height + 18); rangeHit = new RectangleF(rangeX - 8, graphHeadingY - 2, rangeSize.Width + 16, normal.Height + 18); var start = StartDate.Date; var bucketHours = Days == 1 ? 1 : Days == 7 ? 6 : 24; var bucketCount = Days * 24 / bucketHours; var daily = Enumerable.Range(0, bucketCount).Select(i => { var bucketStart = start.AddHours(i * bucketHours); var bucketEnd = bucketStart.AddHours(bucketHours); return records.Where(record => { var local = record.At.ToLocalTime(); return local >= bucketStart && local < bucketEnd; }).Sum(record => record.TotalTokens); }).ToList(); var max = Math.Max(1, daily.Max()); for (var i = 0; i <= 4; i++) { var y = plotTop + i * (bottom - plotTop) / 4; e.Graphics.DrawLine(grid, left, y, right, y); e.Graphics.DrawString(FormatTokens(max * (4 - i) / 4), normal, muted, 28, y - 14); } var points = daily.Select((value, i) => new PointF(bucketCount == 1 ? (left + right) / 2f : left + i * (right - left) / (float)(bucketCount - 1), bottom - (float)(value / (double)max * (bottom - plotTop)))).ToList(); using var linePen = new Pen(Color.FromArgb(55, 225, 95), 3); for (var i = 1; i < points.Count; i++) e.Graphics.DrawLine(linePen, points[i - 1], points[i]); foreach (var point in points) e.Graphics.FillEllipse(green, point.X - 4, point.Y - 4, 8, 8); if (Days == 1) { for (var i = 0; i <= 4; i++) { var tickX = left + i * (right - left) / 4f; var tickLabel = start.AddHours(i * 6).ToString("HH:mm"); var labelSize = e.Graphics.MeasureString(tickLabel, normal); e.Graphics.DrawString(tickLabel, normal, muted, tickX - labelSize.Width / 2, bottom + 16); } } else { e.Graphics.DrawString(start.ToString("dd MMM"), normal, muted, left - 7, bottom + 16); var end = (start.AddDays(Days - 1)).ToString("dd MMM"); var endSize = e.Graphics.MeasureString(end, normal); e.Graphics.DrawString(end, normal, muted, right - endSize.Width, bottom + 16); }
        var modelsTop = 700; e.Graphics.DrawString("Model usage", section, Brushes.White, 64, modelsTop); var barX = 64; var barY = modelsTop + 44; var barWidth = displayWidth - 128; var groupTotal = Math.Max(1, groups.Sum(g => g.Sum(x => x.TotalTokens))); var x = barX; foreach (var group in groups) { var width = (int)(barWidth * group.Sum(r => r.TotalTokens) / (double)groupTotal); using var brush = new SolidBrush(ModelCostEstimator.ColorFor(group.Key)); e.Graphics.FillRectangle(brush, x, barY, Math.Max(1, width), 24); x += width; }
        var modelRows = Math.Max(1, (int)Math.Ceiling(groups.Count / 2d)); var modelRowStart = barY + 68; var modelColumnWidth = (displayWidth - 128) / 2; for (var i = 0; i < groups.Count; i++) { var group = groups[i]; var row = i / 2; var column = i % 2; var itemX = 64 + column * modelColumnWidth; var itemY = modelRowStart + row * 78; var value = group.Sum(x => x.TotalTokens); using var dot = new SolidBrush(ModelCostEstimator.ColorFor(group.Key)); e.Graphics.FillEllipse(dot, itemX, itemY + 5, 17, 17); e.Graphics.DrawString(group.Key, label, Brushes.White, itemX + 40, itemY); e.Graphics.DrawString($"{FormatTokens(value)} tokens  ·  {value * 100d / groupTotal:0.0}%", normal, muted, itemX + 40, itemY + 34); }
        var costTop = modelRowStart + modelRows * 78 + 34; e.Graphics.DrawString("Predicted API cost", section, Brushes.White, 64, costTop); var costRows = Math.Max(1, (int)Math.Ceiling(groups.Count / 2d)); var costColumnWidth = (displayWidth - 128) / 2; decimal totalCost = 0; for (var i = 0; i < groups.Count; i++) { var group = groups[i]; var row = i / 2; var column = i % 2; var itemX = 64 + column * costColumnWidth; var itemY = costTop + 54 + row * 70; var value = ModelCostEstimator.Estimate(group); totalCost += value; using var dot = new SolidBrush(ModelCostEstimator.ColorFor(group.Key)); e.Graphics.FillEllipse(dot, itemX, itemY + 5, 17, 17); e.Graphics.DrawString(group.Key, normal, Brushes.White, itemX + 31, itemY); e.Graphics.DrawString(value == 0 ? "Rate unavailable" : $"≈ ${value:N2}", normal, muted, itemX + 31, itemY + 31); } var totalY = Math.Min(costTop + 54 + costRows * 70 + 12, displayHeight - 92); e.Graphics.DrawString("Estimated total", metric, Brushes.White, 64, totalY); var totalText = $"≈ ${totalCost:N2}"; var totalSize = e.Graphics.MeasureString(totalText, metric); e.Graphics.DrawString(totalText, metric, Brushes.White, displayWidth - totalSize.Width - 52, totalY); var navBox = new RectangleF((displayWidth - 120) / 2, displayHeight - 44, 120, 28); UiIcons.DrawSwapButton(e.Graphics, navBox, UiIcons.SwitchColor); limitsHit = navBox;
    }
    private static string FormatTokens(long value) => value >= 1_000_000_000 ? $"{value / 1_000_000_000d:0.#}b" : value >= 1_000_000 ? $"{value / 1_000_000d:0.#}m" : value >= 1_000 ? $"{value / 1_000d:0.#}k" : value.ToString();
}
internal sealed class AnalyticsForm : Form
{
    private readonly CodexAnalyticsStore store = new(); private readonly AnalyticsDashboard dashboard = new(); private readonly System.Windows.Forms.Timer refreshTimer = new() { Interval = 60_000 }; private readonly Action<int> refreshChanged; private List<CodexUsageRecord> all = []; private bool refreshing; private int graphDays = 30; private int refreshMinutes = 1;
    public AnalyticsForm(Action showLimits, int refreshMinutes, Action<int> refreshChanged)
    {
        this.refreshMinutes = refreshMinutes; this.refreshChanged = refreshChanged; refreshTimer.Interval = refreshMinutes * 60_000; Text = "Codex usage"; ClientSize = new Size(1000, 1320); MinimumSize = new Size(900, 1140); StartPosition = FormStartPosition.Manual; FormBorderStyle = FormBorderStyle.None; AutoScaleMode = AutoScaleMode.None; KeyPreview = true; BackColor = Color.Black; KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); }; dashboard.Dock = DockStyle.Fill; dashboard.GraphRangeClicked += () => { graphDays = graphDays == 1 ? 7 : graphDays == 7 ? 30 : 1; Render(); }; dashboard.RefreshClicked += () => refreshChanged(refreshMinutes == 1 ? 5 : 1); dashboard.LimitsClicked += () => { Close(); showLimits(); }; Controls.Add(dashboard); refreshTimer.Tick += async (_, _) => await RefreshAsync(); Load += async (_, _) => { await RefreshAsync(); refreshTimer.Start(); }; FormClosed += (_, _) => refreshTimer.Dispose();
    }
    public void SetRefreshMinutes(int minutes) { refreshMinutes = minutes; refreshTimer.Interval = minutes * 60_000; dashboard.RefreshMinutes = minutes; dashboard.Invalidate(); }
    protected override CreateParams CreateParams { get { var parameters = base.CreateParams; parameters.ExStyle |= 0x80; return parameters; } }
    protected override void OnLoad(EventArgs e)
    {
        ShowInTaskbar = false;
        base.OnLoad(e);
    }
    protected override void SetVisibleCore(bool value)
    {
        ShowInTaskbar = false;
        base.SetVisibleCore(value);
    }
    private async Task RefreshAsync() { if (refreshing) return; try { refreshing = true; all = store.Load(); await ModelCostEstimator.SyncAsync(all.Select(x => x.Model)); ResizeForModels(); Render(); } finally { refreshing = false; } }
    private void ResizeForModels() { var modelCount = all.Select(x => ModelCostEstimator.DisplayModel(x.Model)).Distinct(StringComparer.OrdinalIgnoreCase).Count(); var rows = Math.Max(1, (int)Math.Ceiling(modelCount / 2d)); var desiredHeight = 1360 + (rows - 1) * 180; if (Height == desiredHeight) return; Height = desiredHeight; PlaceAboveTray(); }
    private (DateTime Start, int Days) PeriodWindow() => (DateTime.Today.AddDays(-graphDays + 1), graphDays);
    private void Render() { var window = PeriodWindow(); dashboard.Records = all.Where(x => x.At.ToLocalTime().Date >= window.Start && x.At.ToLocalTime().Date < window.Start.AddDays(window.Days)).ToList(); dashboard.Days = window.Days; dashboard.RefreshMinutes = refreshMinutes; dashboard.StartDate = window.Start; dashboard.Invalidate(); }
    public void PlaceAboveTray() { var area = Screen.GetWorkingArea(Cursor.Position); Height = Math.Min(Height, Math.Max(MinimumSize.Height, area.Height - 16)); Width = Math.Min(Width, Math.Max(MinimumSize.Width, area.Width - 16)); Location = new Point(Math.Max(area.Left, area.Right - Width - 8), Math.Max(area.Top, area.Bottom - Height - 8)); }
}

internal sealed class ResetData { public int ChancePercent { get; set; } public DateTimeOffset FetchedAt { get; set; } public List<ResetEvent> Events { get; set; } = []; }
internal sealed class ResetEvent { [JsonPropertyName("reset_type")] public string ResetType { get; set; } = "regular"; [JsonPropertyName("announced_at")] public DateTimeOffset AnnouncedAt { get; set; } }
internal sealed class ResetDataClient
{
    private static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly JsonSerializerOptions json = new() { PropertyNameCaseInsensitive = true };
    private string CachePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexUsageTray", "reset-data.json");
    public async Task<ResetData?> GetAsync()
    {
        var status = await http.GetFromJsonAsync<StatusEnvelope>("https://codex-reset.today/api/v1/status", json) ?? new StatusEnvelope();
        var history = await http.GetFromJsonAsync<HistoryEnvelope>("https://codex-reset.today/api/v1/resets?limit=100&order=desc", json) ?? new HistoryEnvelope();
        var result = new ResetData { ChancePercent = Math.Clamp(status.Data?.NextResetEstimate?.ChancePercent ?? 0, 0, 100), FetchedAt = DateTimeOffset.UtcNow, Events = history.Data ?? [] };
        Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!); File.WriteAllText(CachePath, JsonSerializer.Serialize(result, json)); return result;
    }
    public ResetData? LoadCached() { try { return JsonSerializer.Deserialize<ResetData>(File.ReadAllText(CachePath), json); } catch { return null; } }
    private sealed class StatusEnvelope { public StatusData? Data { get; set; } }
    private sealed class StatusData { [JsonPropertyName("next_reset_estimate")] public Forecast? NextResetEstimate { get; set; } }
    private sealed class Forecast { [JsonPropertyName("chance_percent")] public int ChancePercent { get; set; } }
    private sealed class HistoryEnvelope { public List<ResetEvent>? Data { get; set; } }
}

internal static class UiIcons
{
    public static readonly Color SwitchColor = Color.FromArgb(255, 210, 70);
    public const float SwapFontSize = 10f;
    public static void DrawSwapButton(Graphics graphics, RectangleF bounds, Color color)
    {
        using var background = new SolidBrush(Color.FromArgb(55, 46, 16)); using var border = new Pen(color, 1.5f); using var font = new Font("Segoe UI", SwapFontSize, FontStyle.Bold, GraphicsUnit.Point); using var textBrush = new SolidBrush(color); using var centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center }; graphics.FillRoundedRectangle(background, bounds.X, bounds.Y, bounds.Width, bounds.Height, 6); graphics.DrawRoundedRectangle(border, bounds.X, bounds.Y, bounds.Width, bounds.Height, 6); graphics.DrawString("Swap", font, textBrush, bounds, centered);
    }
}

internal sealed class GraphForm : Form
{
    private UsageSnapshot current; private List<UsageSnapshot> history; private readonly GraphCanvas canvas; private readonly Label weeklyValue; private readonly Label sessionValue; private readonly Panel weeklyBar; private readonly Panel sessionBar; private readonly Label sessionBarText; private readonly Label refreshValue; private readonly Label graphValue; private readonly Label resetChanceValue; private readonly Label lastResetValue; private readonly Action<int> refreshChanged; private readonly Action<int> durationChanged; private int selectedRefreshMinutes; private int selectedGraphDays;
    public GraphForm(UsageSnapshot current, List<UsageSnapshot> history, int refreshMinutes, int graphDays, Action<int> refreshChanged, Action<int> durationChanged, ResetData? resetData, Action showAnalytics)
    {
        this.current = current; this.history = history; this.refreshChanged = refreshChanged; this.durationChanged = durationChanged; selectedRefreshMinutes = refreshMinutes; selectedGraphDays = graphDays; Text = "Codex limits"; ClientSize = new Size(1000, 850); MinimumSize = new Size(900, 620); StartPosition = FormStartPosition.Manual; FormBorderStyle = FormBorderStyle.None; AutoScaleMode = AutoScaleMode.None; KeyPreview = true; BackColor = Color.Black; ForeColor = Color.White; DoubleBuffered = true; KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        var header = new Panel { Dock = DockStyle.Top, Height = 440, BackColor = Color.Black };
        header.Controls.Add(new Label { Text = "Codex limits", AutoSize = true, Location = new Point(34, 20), Font = new Font("Segoe UI", 14, FontStyle.Bold), ForeColor = Color.White });
        resetChanceValue = new Label { AutoSize = true, Location = new Point(520, 24), Font = new Font("Segoe UI", 9, FontStyle.Bold), ForeColor = Color.FromArgb(255, 190, 70) }; header.Controls.Add(resetChanceValue);
        lastResetValue = new Label { AutoSize = true, Location = new Point(520, 62), Font = new Font("Segoe UI", 8, FontStyle.Bold), ForeColor = Color.FromArgb(190, 190, 200) }; header.Controls.Add(lastResetValue);
        header.Controls.Add(new Label { Text = "Weekly meter", AutoSize = true, Location = new Point(36, 100), Font = new Font("Segoe UI", 10), ForeColor = Color.LightSteelBlue });
        weeklyValue = MetricLabel("", Color.LightSteelBlue); weeklyValue.Location = new Point(36, 150); header.Controls.Add(weeklyValue);
        var weeklyTrack = new Panel { Location = new Point(36, 205), Size = new Size(928, 32), BackColor = Color.FromArgb(25, 55, 32) }; weeklyBar = new Panel { Location = new Point(0, 0), Height = 32, BackColor = Color.FromArgb(55, 225, 95) }; weeklyTrack.Controls.Add(weeklyBar); header.Controls.Add(weeklyTrack);
        header.Controls.Add(new Label { Text = "5-hour meter", AutoSize = true, Location = new Point(36, 285), Font = new Font("Segoe UI", 10), ForeColor = Color.FromArgb(140, 190, 245) });
        sessionValue = MetricLabel("", Color.FromArgb(140, 190, 245)); sessionValue.Location = new Point(36, 335); header.Controls.Add(sessionValue);
        var sessionTrack = new Panel { Location = new Point(36, 390), Size = new Size(928, 32), BackColor = Color.FromArgb(25, 45, 70) }; sessionBar = new Panel { Location = new Point(0, 0), Height = 32, BackColor = Color.FromArgb(45, 155, 255) }; sessionTrack.Controls.Add(sessionBar); sessionBarText = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI", 10, FontStyle.Bold), ForeColor = Color.Black, BackColor = Color.Transparent }; sessionTrack.Controls.Add(sessionBarText); header.Controls.Add(sessionTrack);
        canvas = new GraphCanvas(); canvas.Dock = DockStyle.Fill; canvas.Current = current; canvas.History = history;
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 112, BackColor = Color.Black };
        refreshValue = new Label { AutoSize = true, Location = new Point(36, 14), Font = new Font("Segoe UI", 8, FontStyle.Bold), ForeColor = Color.FromArgb(45, 155, 255) }; footer.Controls.Add(refreshValue);
        graphValue = new Label { AutoSize = true, Location = new Point(440, 14), Font = new Font("Segoe UI", 8, FontStyle.Bold), ForeColor = Color.FromArgb(45, 155, 255) }; footer.Controls.Add(graphValue);
        var analyticsValue = new Button { Text = "Swap", Size = new Size(144, 34), Location = new Point((footer.Width - 144) / 2, 70), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(55, 46, 16), ForeColor = UiIcons.SwitchColor, Font = new Font("Segoe UI", UiIcons.SwapFontSize, FontStyle.Bold), UseCompatibleTextRendering = true, Cursor = Cursors.Hand }; analyticsValue.FlatAppearance.BorderColor = UiIcons.SwitchColor; analyticsValue.FlatAppearance.BorderSize = 1; footer.Resize += (_, _) => analyticsValue.Left = (footer.Width - analyticsValue.Width) / 2; analyticsValue.Click += (_, _) => { Close(); showAnalytics(); }; new ToolTip().SetToolTip(analyticsValue, "Switch to usage analytics"); footer.Controls.Add(analyticsValue); analyticsValue.BringToFront();
        refreshValue.Cursor = Cursors.Hand; graphValue.Cursor = Cursors.Hand;
        refreshValue.Click += (_, _) => refreshChanged(selectedRefreshMinutes == 1 ? 5 : 1);
        graphValue.Click += (_, _) => durationChanged(selectedGraphDays == 1 ? 7 : selectedGraphDays == 7 ? 30 : 1);
        Controls.Add(canvas); Controls.Add(footer); Controls.Add(header);
        UpdateData(current, history);
        SetOptions(refreshMinutes, graphDays);
        SetResetData(resetData);
    }
    public void SetOptions(int minutes, int days) { selectedRefreshMinutes = minutes; selectedGraphDays = days; refreshValue.Text = $"Refresh: {minutes} min"; graphValue.Text = $"Graph: {days} day{(days == 1 ? "" : "s")}"; canvas.RangeDays = days; canvas.Invalidate(); }
    protected override CreateParams CreateParams { get { var parameters = base.CreateParams; parameters.ExStyle |= 0x80; return parameters; } }
    protected override void OnLoad(EventArgs e)
    {
        ShowInTaskbar = false;
        base.OnLoad(e);
    }
    protected override void SetVisibleCore(bool value)
    {
        ShowInTaskbar = false;
        base.SetVisibleCore(value);
    }
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        var footer = Controls.OfType<Panel>().FirstOrDefault(panel => panel.Dock == DockStyle.Bottom);
        var swap = footer?.Controls.OfType<Button>().FirstOrDefault(button => button.Text == "Swap");
        if (footer != null && swap != null)
        {
            footer.Height = 136;
            swap.Size = new Size(144, 34);
            swap.Location = new Point((footer.ClientSize.Width - swap.Width) / 2, 92);
        }
    }
    public void SetResetData(ResetData? data) { resetChanceValue.Text = data == null ? "Reset chance: unavailable" : $"Next reset chance: {data.ChancePercent}% (48h)"; var latest = data?.Events.OrderByDescending(x => x.AnnouncedAt).FirstOrDefault(); lastResetValue.Text = latest == null ? "Last reset: unavailable" : $"Days since last reset: {Math.Max(0, (DateTimeOffset.UtcNow - latest.AnnouncedAt).TotalDays):0.0}"; canvas.ResetEvents = data?.Events ?? []; canvas.Invalidate(); }
    private Label MetricLabel(string name, Color color)
    {
        return new Label { Text = $"{(name == "Weekly" ? current.WeeklyRemaining : current.SessionRemaining):0}% remaining", AutoSize = true, Font = new Font("Segoe UI", 8, FontStyle.Bold), ForeColor = color };
    }
    public void UpdateData(UsageSnapshot value, List<UsageSnapshot> points)
    {
        current = value; history = points; canvas.Current = value; canvas.History = points; canvas.Invalidate();
        var weeklyText = $"{value.WeeklyRemaining:0}% remaining ({WeeklyResetText(value.WeeklyResetAt)})";
        var sessionText = $"{value.SessionRemaining:0}% remaining ({SessionResetText(value.SessionResetAt)})";
        weeklyValue.Text = weeklyText; sessionValue.Text = sessionText;
        weeklyBar.Width = Math.Max(4, (int)((Width - 72) * value.WeeklyRemaining / 100));
        sessionBar.Width = Math.Max(4, (int)((Width - 72) * value.SessionRemaining / 100)); sessionBarText.Text = "";
    }
    private static string WeeklyResetText(DateTimeOffset? resetAt)
    {
        if (resetAt is null) return "reset time unavailable";
        var remaining = resetAt.Value - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) return "resetting now";
        return $"resets in {Math.Max(0, (int)remaining.TotalDays)} days {remaining.Hours} hrs";
    }
    private static string SessionResetText(DateTimeOffset? resetAt)
    {
        if (resetAt is null) return "reset time unavailable";
        var remaining = resetAt.Value - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) return "resetting now";
        return $"resets in {Math.Max(0, (int)remaining.TotalHours)} hrs {remaining.Minutes} mins";
    }
    public void PlaceAboveTray()
    {
        var area = Screen.GetWorkingArea(Cursor.Position);
        Height = Math.Min(Height, Math.Max(MinimumSize.Height, area.Height - 16));
        Width = Math.Min(Width, Math.Max(MinimumSize.Width, area.Width - 16));
        Location = new Point(Math.Max(area.Left, area.Right - Width - 8), Math.Max(area.Top, area.Bottom - Height - 8));
    }
}

internal sealed class GraphCanvas : Panel
{
    public UsageSnapshot? Current { get; set; } public List<UsageSnapshot> History { get; set; } = []; public int RangeDays { get; set; } = 7; public List<ResetEvent> ResetEvents { get; set; } = [];
    private int hoverX = -1;
    private double? hoverValue;
    private DateTimeOffset? hoverTime;
    public GraphCanvas() { BackColor = Color.FromArgb(24, 25, 29); DoubleBuffered = true; Padding = new Padding(24); MouseMove += UpdateHover; MouseLeave += (_, _) => { hoverX = -1; hoverValue = null; hoverTime = null; Invalidate(); }; }
    private void UpdateHover(object? sender, MouseEventArgs e)
    {
        var plot = new Rectangle(120, 100, Math.Max(100, Width - 180), Math.Max(100, Height - 170));
        if (e.X < plot.Left || e.X > plot.Right || e.Y < plot.Top || e.Y > plot.Bottom) { hoverX = -1; hoverValue = null; hoverTime = null; Invalidate(); return; }
        var points = VisiblePoints();
        if (points.Count == 0) return;
        var rangeStart = DateTimeOffset.UtcNow.AddDays(-RangeDays); var point = points.OrderBy(x => Math.Abs((plot.Left + (x.At - rangeStart).TotalDays / RangeDays * plot.Width) - e.X)).First();
        hoverX = (int)Math.Clamp(plot.Left + (point.At - rangeStart).TotalDays / RangeDays * plot.Width, plot.Left, plot.Right); hoverValue = point.WeeklyRemaining; hoverTime = point.At; Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor); var plot = new Rectangle(120, 100, Math.Max(100, Width - 180), Math.Max(100, Height - 170)); using var grid = new Pen(Color.FromArgb(38, 43, 48)); using var text = new SolidBrush(Color.FromArgb(180, 190, 205)); using var font = new Font("Segoe UI", 7);
        for (int i = 0; i <= 4; i++) { var y = plot.Top + i * plot.Height / 4; e.Graphics.DrawLine(grid, plot.Left, y, plot.Right, y); e.Graphics.DrawString($"{100 - i * 25}%", font, text, 18, y - 8); }
        using var heading = new Font("Segoe UI", 10, FontStyle.Bold); e.Graphics.DrawString("Remaining usage", heading, Brushes.White, 34, 20); if (RangeDays == 1) { for (var i = 0; i <= 4; i++) { var x = plot.Left + i * plot.Width / 4f; e.Graphics.DrawLine(grid, x, plot.Top, x, plot.Bottom); var label = DateTimeOffset.UtcNow.AddHours(-24 + i * 6).ToLocalTime().ToString("HH:mm"); var labelSize = e.Graphics.MeasureString(label, font); e.Graphics.DrawString(label, font, text, x - labelSize.Width / 2, plot.Bottom + 12); } } else { e.Graphics.DrawString($"{RangeDays} day{(RangeDays == 1 ? "" : "s")} ago", font, text, plot.Left, plot.Bottom + 12); e.Graphics.DrawString("Today", font, text, plot.Right - 55, plot.Bottom + 12); }
        var rangeStart = DateTimeOffset.UtcNow.AddDays(-RangeDays);
        foreach (var reset in ResetEvents.Where(x => x.AnnouncedAt >= rangeStart && x.AnnouncedAt <= DateTimeOffset.UtcNow)) { var x = plot.Left + (float)((reset.AnnouncedAt - rangeStart).TotalDays / RangeDays) * plot.Width; using var resetPen = new Pen(reset.ResetType.Equals("banked", StringComparison.OrdinalIgnoreCase) ? Color.SaddleBrown : Color.DeepSkyBlue, 2); e.Graphics.DrawLine(resetPen, x, plot.Top, x, plot.Bottom); }
        var points = VisiblePoints();
        if (points.Count < 2) { using var empty = new SolidBrush(Color.FromArgb(150, 154, 165)); var msg = "Weekly history is collecting — check back after a few refreshes."; var size = e.Graphics.MeasureString(msg, font); e.Graphics.DrawString(msg, font, empty, plot.Left + (plot.Width - size.Width) / 2, plot.Top + plot.Height / 2 - 10); return; }
        var usageRangeStart = DateTimeOffset.UtcNow.AddDays(-RangeDays);
        PointF p(int i, double val) => new(plot.Left + (float)Math.Clamp((points[i].At - usageRangeStart).TotalDays / RangeDays, 0, 1) * plot.Width, plot.Top + (float)(100 - Math.Clamp(val, 0, 100)) * plot.Height / 100);
        using var line = new Pen(Color.MediumSeaGreen, 3); using var dot = new SolidBrush(Color.MediumSeaGreen); for (int i = 1; i < points.Count; i++) e.Graphics.DrawLine(line, p(i - 1, points[i - 1].WeeklyRemaining), p(i, points[i].WeeklyRemaining)); foreach (var point in points) { var q = p(points.IndexOf(point), point.WeeklyRemaining); e.Graphics.FillEllipse(dot, q.X - 4, q.Y - 4, 8, 8); }
        if (hoverX >= plot.Left && hoverX <= plot.Right && hoverValue.HasValue && hoverTime.HasValue)
        {
            var hoverRangeStart = DateTimeOffset.UtcNow.AddDays(-RangeDays); var dotPoint = new PointF(plot.Left + (float)Math.Clamp((hoverTime.Value - hoverRangeStart).TotalDays / RangeDays, 0, 1) * plot.Width, plot.Top + (float)(100 - Math.Clamp(hoverValue.Value, 0, 100)) * plot.Height / 100);
            using var dotOutline = new SolidBrush(Color.FromArgb(220, 255, 255, 255)); using var dotBrush = new SolidBrush(Color.FromArgb(55, 225, 95)); e.Graphics.FillEllipse(dotOutline, dotPoint.X - 7, dotPoint.Y - 7, 14, 14); e.Graphics.FillEllipse(dotBrush, dotPoint.X - 5, dotPoint.Y - 5, 10, 10);
            using var hoverFont = new Font("Segoe UI", 8, FontStyle.Bold); var label = $"{hoverValue.Value:0.0}%  •  {hoverTime.Value.ToLocalTime():HH:mm}"; var size = e.Graphics.MeasureString(label, hoverFont); var badge = new RectangleF(plot.Right - size.Width - 18, 16, size.Width + 14, size.Height + 8); using var badgeBackground = new SolidBrush(Color.FromArgb(35, 45, 39)); using var badgeBorder = new Pen(Color.FromArgb(55, 225, 95), 1); e.Graphics.FillRectangle(badgeBackground, badge); e.Graphics.DrawRectangle(badgeBorder, badge.X, badge.Y, badge.Width, badge.Height); using var labelBrush = new SolidBrush(Color.FromArgb(55, 225, 95)); e.Graphics.DrawString(label, hoverFont, labelBrush, badge.X + 7, badge.Y + 4);
        }
    }
    private List<UsageSnapshot> VisiblePoints() { var rangeStart = DateTimeOffset.UtcNow.AddDays(-RangeDays); var points = History.Where(x => x.At >= rangeStart).OrderBy(x => x.At).ToList(); if (Current != null && (points.Count == 0 || points[^1].At != Current.At)) points.Add(Current); if (points.Count == 0) return points; var interval = RangeDays == 1 ? TimeSpan.FromHours(1) : RangeDays == 7 ? TimeSpan.FromHours(6) : TimeSpan.FromDays(1); var count = (int)Math.Ceiling(TimeSpan.FromDays(RangeDays).TotalHours / interval.TotalHours); var sampled = new List<UsageSnapshot>(); for (var i = 0; i <= count; i++) { var slot = rangeStart + TimeSpan.FromTicks(interval.Ticks * i); var sample = points.LastOrDefault(x => x.At <= slot) ?? points.FirstOrDefault(x => x.At <= slot + interval); if (sample != null) sampled.Add(sample with { At = slot }); } return sampled; }
}

internal static class GraphicsExtensions
{
    public static void DrawRoundedRectangle(this Graphics g, Pen pen, float x, float y, float w, float h, float radius)
    { using var p = RoundedPath(x, y, w, h, radius); g.DrawPath(pen, p); }
    public static void FillRoundedRectangle(this Graphics g, Brush b, float x, float y, float w, float h, float radius)
    { using var p = RoundedPath(x, y, w, h, radius); g.FillPath(b, p); }
    private static GraphicsPath RoundedPath(float x, float y, float w, float h, float radius)
    { var p = new GraphicsPath(); p.AddArc(x, y, radius * 2, radius * 2, 180, 90); p.AddArc(x + w - radius * 2, y, radius * 2, radius * 2, 270, 90); p.AddArc(x + w - radius * 2, y + h - radius * 2, radius * 2, radius * 2, 0, 90); p.AddArc(x, y + h - radius * 2, radius * 2, radius * 2, 90, 90); p.CloseFigure(); return p; }
}
