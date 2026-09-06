using System.Drawing.Drawing2D;

namespace CodexUsageTray;

internal sealed class AnalyticsDashboard : Panel
{
    public const int LogicalHeight = 862;
    private const int PageSize = 3;
    private int page;
    private float displayScale = 1;
    private float LayoutScale => displayScale;
    private readonly Button previous = UiIcons.CreateSwapButton();
    private readonly Button next = UiIcons.CreateSwapButton();
    public List<CodexUsageRecord> Records { get; set; } = [];
    public int Days { get; set; } = 30;
    public int RefreshMinutes { get; set; } = 1;
    public DateTime StartDate { get; set; } = DateTime.Today.AddDays(-29);
    public event Action? GraphRangeClicked;
    public event Action? RefreshClicked;
    public event Action? LimitsClicked;
    private readonly Button range = UiIcons.CreateSwapButton();
    private readonly Button refresh = UiIcons.CreateSwapButton();
    private readonly Button limits = UiIcons.CreateSwapButton();

    public AnalyticsDashboard()
    {
        DoubleBuffered = true;
        range.Click += (_, _) => GraphRangeClicked?.Invoke();
        refresh.Click += (_, _) => RefreshClicked?.Invoke();
        limits.Click += (_, _) => LimitsClicked?.Invoke();
        limits.Text = "Swap";
        previous.Text = "Previous";
        next.Text = "Next";
        previous.Click += (_, _) => { page--; UpdateLayout(); };
        next.Click += (_, _) => { page++; UpdateLayout(); };
        Controls.AddRange([range, refresh, limits, previous, next]);
        Resize += (_, _) => UpdateLayout();
    }

    public void SetDisplayScale(float scale)
    {
        displayScale = Math.Clamp(scale, 1, 2);
        UpdateLayout();
    }

    public void UpdateLayout()
    {
        var count = Records.Select(x => ModelCostEstimator.DisplayModel(x.Model)).Distinct().Count();
        var pages = Math.Max(1, (count + PageSize - 1) / PageSize);
        page = Math.Clamp(page, 0, pages - 1);
        previous.Enabled = page > 0;
        next.Enabled = page < pages - 1;
        previous.Visible = previous.Enabled;
        next.Visible = next.Enabled;
        range.Text = $"Range: {Days}d";
        refresh.Text = $"Refresh: {RefreshMinutes}m";
        void Place(Button button, int x, int y, int width)
        {
            button.SetBounds((int)(x * LayoutScale), (int)(y * LayoutScale), (int)(width * LayoutScale), (int)(34 * LayoutScale));
            if (Math.Abs(button.Font.Size - 14 * LayoutScale) > 0.01f || button.Font.Unit != GraphicsUnit.Pixel)
                button.Font = new Font("Segoe UI", 14 * LayoutScale, FontStyle.Bold, GraphicsUnit.Pixel);
        }
        Place(range, 24, 238, 130);
        Place(refresh, 164, 238, 155);
        Place(limits, 225, LogicalHeight - 50, 150);
        Place(previous, 364, LogicalHeight - 50, 100);
        Place(next, 474, LogicalHeight - 50, 100);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        var theme = ThemeManager.Current;
        g.Clear(theme.Background);
        g.ScaleTransform(LayoutScale, LayoutScale);
        const int contentWidth = 600;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        using var title = new Font("Segoe UI", 25, FontStyle.Bold, GraphicsUnit.Pixel);
        using var number = new Font("Segoe UI", 24, FontStyle.Bold, GraphicsUnit.Pixel);
        using var heading = new Font("Segoe UI", 17, FontStyle.Bold, GraphicsUnit.Pixel);
        using var body = new Font("Segoe UI", 14, FontStyle.Regular, GraphicsUnit.Pixel);
        using var text = new SolidBrush(theme.Text);
        using var muted = new SolidBrush(theme.Muted);
        using var surface = new SolidBrush(theme.Panel);
        using var grid = new Pen(Color.FromArgb(55, theme.Muted));
        using var accent = new SolidBrush(theme.Good);
        using var clipped = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        g.DrawString("Codex usage", title, text, 24, 20);
        g.DrawString($"{StartDate:dd MMM} – {StartDate.AddDays(Days - 1):dd MMM yyyy}  ·  Local session activity", body, muted, 24, 56);
        var total = Records.Sum(x => x.TotalTokens);
        var cardWidth = (contentWidth - 60) / 2f;
        void Card(string caption, string value, float x, float y)
        {
            g.FillRoundedRectangle(surface, x, y, cardWidth, 62, 10);
            g.DrawString(caption, body, muted, x + 14, y + 7);
            g.DrawString(value, number, text, x + 14, y + 25);
        }
        Card("Total tokens", FormatTokens(total), 24, 88);
        Card("Calls / sessions", $"{FormatTokens(Records.Count)} / {Records.Select(x => x.SessionId).Distinct().Count():N0}", 36 + cardWidth, 88);
        Card("Cached input tokens", FormatTokens(Records.Sum(x => x.CachedInputTokens)), 24, 160);
        Card("Average tokens / day", FormatTokens(total / Math.Max(1, Days)), 36 + cardWidth, 160);
        g.DrawString("Cumulative token usage", heading, text, 24, 291);
        var plot = new RectangleF(76, 334, Math.Max(100, contentWidth - 108), 134);
        var hours = Days == 1 ? 1 : Days == 7 ? 6 : 24;
        var buckets = new long[Days * 24 / hours];
        foreach (var record in Records)
        {
            var index = (int)Math.Floor((record.At.LocalDateTime - StartDate.Date).TotalHours / hours);
            if (index >= 0 && index < buckets.Length) buckets[index] += record.TotalTokens;
        }
        for (var i = 1; i < buckets.Length; i++) buckets[i] += buckets[i - 1];
        var max = Math.Max(1, buckets.Max());
        for (var i = 0; i <= 4; i++)
        {
            var y = plot.Top + i * plot.Height / 4;
            g.DrawLine(grid, plot.Left, y, plot.Right, y);
            g.DrawString(FormatTokens(max * (4 - i) / 4), body, muted, 24, y - 9);
        }
        var points = buckets.Select((value, i) => new PointF(plot.Left + i * plot.Width / Math.Max(1, buckets.Length - 1), plot.Bottom - value / (float)max * plot.Height)).ToArray();
        using var line = new Pen(theme.Good, 2.5f);
        if (Records.Count > 0 && points.Length > 1) g.DrawLines(line, points);
        else g.DrawString("No session activity in this period", body, muted, plot.Left + 10, plot.Top + 54);
        g.DrawString(StartDate.ToString(Days == 1 ? "HH:mm" : "dd MMM"), body, muted, plot.Left, plot.Bottom + 10);
        var end = Days == 1 ? "24:00" : StartDate.AddDays(Days - 1).ToString("dd MMM");
        g.DrawString(end, body, muted, plot.Right - g.MeasureString(end, body).Width, plot.Bottom + 10);
        g.DrawString("Model breakdown", heading, text, 24, 516);
        var groups = Records.GroupBy(x => ModelCostEstimator.DisplayModel(x.Model)).OrderByDescending(x => x.Sum(r => r.TotalTokens)).ToList();
        g.DrawString($"Page {page + 1} / {Math.Max(1, (groups.Count + PageSize - 1) / PageSize)}", body, muted, 478, 520);
        decimal totalCost = groups.Sum(group => ModelCostEstimator.Estimate(group));
        var visibleGroups = groups.Skip(page * PageSize).Take(PageSize).ToList();
        for (var i = 0; i < visibleGroups.Count; i++)
        {
            var group = visibleGroups[i];
            var y = 550 + i * 64;
            var tokens = group.Sum(x => x.TotalTokens);
            var cost = ModelCostEstimator.Estimate(group);
            g.FillRoundedRectangle(surface, 24, y, contentWidth - 48, 56, 8);
            using var dot = new SolidBrush(ModelCostEstimator.ColorFor(group.Key));
            g.FillEllipse(dot, 36, y + 13, 8, 8);
            g.DrawString(group.Key, heading, text, new RectangleF(52, y + 5, contentWidth - 230, 24), clipped);
            g.DrawString($"{FormatTokens(tokens)} tokens · {tokens * 100d / Math.Max(1, total):0.0}%", body, muted, 52, y + 30);
            var costText = cost == 0 ? "Rate unavailable" : $"≈ ${cost:N2}";
            g.DrawString(costText, body, muted, contentWidth - 38 - g.MeasureString(costText, body).Width, y + 18);
        }
        if (groups.Count == 0) g.DrawString("Model details appear after your first session.", body, muted, 24, 558);
        var summaryY = 550 + PageSize * 64;
        g.DrawString($"Estimated API cost  ≈ ${totalCost:N2}", heading, text, 24, summaryY + 4);
        g.DrawString("Estimate only · not subscription billing", body, muted, 24, summaryY + 28);
    }

    private static string FormatTokens(long value) => value >= 1_000_000_000 ? $"{value / 1_000_000_000d:0.#}B" : value >= 1_000_000 ? $"{value / 1_000_000d:0.#}M" : value >= 1_000 ? $"{value / 1_000d:0.#}K" : value.ToString("N0");
}
