using System.Drawing;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace AsgardHuntMeter;

// 대시보드(그래프/목표), 지출, 시세, 숙제, 알림 설정 — MainForm 확장 파트.
public sealed partial class MainForm
{
    private sealed class ExpenseEntry
    {
        public DateTime Time { get; set; }
        public long Amount { get; set; }
        public string Memo { get; set; } = "";
    }

    private sealed class PriceEntry
    {
        public string Item { get; set; } = "";
        public long Price { get; set; }
        public long Target { get; set; }
        public DateTime Updated { get; set; }
    }

    private sealed class HomeworkEntry
    {
        public string Name { get; set; } = "";
        public string Cycle { get; set; } = "일일";
        public bool Done { get; set; }
        public DateTime LastDone { get; set; }
        public string AutoRule { get; set; } = "";
    }

    private sealed class SessionEntry
    {
        public DateTime Time { get; set; }
        public string Tag { get; set; } = "";
        public long Sec { get; set; }
        public long Gold { get; set; }
        public double Gph { get; set; }
        public long Kills { get; set; }
        public long Exp { get; set; }
    }

    private readonly List<ExpenseEntry> _expenses = [];
    private readonly List<PriceEntry> _prices = [];
    private readonly List<HomeworkEntry> _homework = [];
    private readonly List<SessionEntry> _sessions = [];
    private readonly Dictionary<int, (long Gold, long Exp, long Kills)> _minutes = [];
    private readonly List<(DateTime Time, string Text)> _missions = [];
    private readonly HashSet<string> _missionSeen = [];
    private long _invCount;
    private long _autoExpenseGold;
    private int _autoPotionUses;
    private int _autoFoodUses;
    private long _lootValue;
    private int _lootValued;
    private int _lootUnpriced;
    private readonly Dictionary<int, (string Name, string Iid)> _slotItem = [];
    private bool _invFullAlerted;
    private bool _targetAlerted;
    private bool _sessionRecorded;
    private DateTime _lastRemoteAlert = DateTime.MinValue;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    private Panel _graphPanel = null!;
    private Label _lblDashSession = null!, _lblDashGph = null!, _lblDashNet = null!, _lblDashTarget = null!, _lblDashDps = null!, _lblInvState = null!, _lblDashItemValue = null!;
    private TextBox _txtTag = null!;
    private NumericUpDown _numTargetGph = null!;
    private ListView _sessionView = null!;
    private ListView _expenseView = null!;
    private NumericUpDown _numExpense = null!;
    private TextBox _txtExpenseMemo = null!;
    private Label _lblExpenseTotals = null!;
    private ListView _priceView = null!, _marketView = null!;
    private Label _lblMarketQuery = null!;
    private TextBox _txtMarketItem = null!;
    private NumericUpDown _numMarketPrice = null!, _numMarketTarget = null!;
    private ListView _homeworkView = null!;
    private TextBox _txtHomework = null!;
    private ComboBox _cmbCycle = null!;
    private Label _lblMissions = null!;
    private TextBox _txtDiscord = null!, _txtTgToken = null!, _txtTgChat = null!;
    private NumericUpDown _numInvSlots = null!, _numInvFree = null!;
    private NumericUpDown _numPipOpacity = null!, _numPipFont = null!, _numPotionCost = null!, _numFoodCost = null!;
    private CheckBox _chkPipTop = null!, _chkPipStart = null!, _chkAutoExpense = null!;

    private static string FeatureFile(string name) => Path.Combine(AppSettings.SettingsDir, name);

    private sealed class TimerEntry
    {
        public string Name { get; set; } = "";
        public int Current { get; set; }
        public int Max { get; set; } = 100;
        public string Cycle { get; set; } = "일일";
        public DateTime LastReset { get; set; } = DateTime.Now;
    }

    private readonly List<TimerEntry> _timers = [];
    private readonly HashSet<string> _timerAlerted = [];
    private ListView _timerView = null!;
    private TextBox _txtTimerName = null!;
    private NumericUpDown _numTimerMax = null!;
    private ComboBox _cmbTimerCycle = null!;
    private Label _lblResetCountdown = null!;

    private void BuildFeatureTabs()
    {
        LoadFeatureData();
        BuildDashboardTab();
        BuildExpenseTab();
        BuildMarketTab();
        BuildCommunityTab();
        BuildCommunityChatTab();
        BuildTimerTab();
        BuildHomeworkTab();
        BuildNotifyTab();
        BuildUpdateTab();
        ConsolidateTabs();
    }

    private void ConsolidateTabs()
    {
        GroupTabs("📜 기록", "📜 로그", "⚔ 몹 킬", "🎒 아이템");
        GroupTabs("💬 채팅", "💬 채팅", "✉ 귓말", "💬 커뮤니티 채팅");
        GroupTabs("⏳ 버프·소모품", "⏳ 버프", "🧪 소모품");
        GroupTabs("🛒 시세", "🛒 시세", "🌐 커뮤니티");
        GroupTabs("✅ 숙제·타이머", "✅ 숙제", "⏱ 타이머");
        GroupTabs("⚙ 설정", "⚙ 설정", "🔔 알림/PiP", "🔄 업데이트");
        _tabs.Multiline = false;
        if (_tabs.TabPages.Count > 0)
        {
            _tabs.SelectedIndex = 0;
        }
    }

    private void GroupTabs(string title, params string[] texts)
    {
        var pages = new List<TabPage>();
        foreach (var text in texts)
        {
            var page = _tabs.TabPages.Cast<TabPage>().FirstOrDefault(p => p.Text == text);
            if (page is not null)
            {
                _tabs.TabPages.Remove(page);
                pages.Add(page);
            }
        }
        if (pages.Count == 0)
        {
            return;
        }
        var host = new TabPage(title);
        var inner = new TabControl { Dock = DockStyle.Fill };
        foreach (var page in pages)
        {
            inner.TabPages.Add(page);
        }
        host.Controls.Add(inner);
        _tabs.TabPages.Add(host);
    }

    // ===================================================================== 대시보드

    private void BuildDashboardTab()
    {
        var tab = new TabPage("📊 대시보드");
        var top = new Panel { Dock = DockStyle.Top, Height = 112, Padding = new Padding(8, 6, 8, 2) };
        _lblDashSession = new Label { Location = new Point(8, 6), AutoSize = true, Font = new Font("Malgun Gothic", 10F, FontStyle.Bold) };
        _lblDashGph = new Label { Location = new Point(8, 30), AutoSize = true };
        _lblDashNet = new Label { Location = new Point(8, 52), AutoSize = true };
        _lblDashTarget = new Label { Location = new Point(8, 74), AutoSize = true, ForeColor = Color.FromArgb(0, 100, 90) };
        top.Controls.AddRange([_lblDashSession, _lblDashGph, _lblDashNet, _lblDashTarget]);

        top.Controls.Add(new Label { Text = "사냥터 태그:", Location = new Point(430, 8), AutoSize = true });
        _txtTag = new TextBox { Location = new Point(520, 5), Width = 150, Text = _settings.HuntTag };
        top.Controls.Add(_txtTag);
        top.Controls.Add(new Label { Text = "목표(골드/h):", Location = new Point(680, 8), AutoSize = true });
        _numTargetGph = new NumericUpDown
        {
            Location = new Point(786, 5),
            Width = 100,
            Maximum = 10_000_000_000,
            Increment = 100_000,
            Value = Math.Clamp(_settings.TargetGph, 0, 10_000_000_000),
        };
        top.Controls.Add(_numTargetGph);
        var btnSaveGoal = new Button { Text = "저장", Location = new Point(896, 4), Width = 60 };
        btnSaveGoal.Click += (_, _) => SaveFeatureSettings(showToast: true);
        top.Controls.Add(btnSaveGoal);
        var btnReport = new Button { Text = "리포트", Location = new Point(960, 4), Width = 72 };
        btnReport.Click += (_, _) => ExportSessionReport();
        top.Controls.Add(btnReport);
        var btnDonate = new Button { Text = "❤️ 후원", Location = new Point(1038, 4), Width = 72 };
        btnDonate.Click += (_, _) => OpenDonation();
        top.Controls.Add(btnDonate);
        _lblInvState = new Label { Location = new Point(430, 34), AutoSize = true, ForeColor = Color.DimGray };
        _lblDashDps = new Label { Location = new Point(430, 56), AutoSize = true, ForeColor = Color.DimGray };
        _lblDashItemValue = new Label { Location = new Point(430, 78), AutoSize = true, ForeColor = Color.FromArgb(30, 120, 80) };
        top.Controls.Add(_lblInvState);
        top.Controls.Add(_lblDashDps);
        top.Controls.Add(_lblDashItemValue);

        _graphPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(8) };
        _graphPanel.Paint += DrawMinuteGraph;

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 176, Padding = new Padding(8, 2, 8, 4) };
        var sessionTop = new Panel { Dock = DockStyle.Top, Height = 24 };
        sessionTop.Controls.Add(new Label { Dock = DockStyle.Left, Width = 160, Text = "최근 세션 기록", ForeColor = Color.DimGray, TextAlign = ContentAlignment.MiddleLeft });
        var btnGraphCsv = new Button { Dock = DockStyle.Right, Width = 90, Text = "그래프 CSV" };
        btnGraphCsv.Click += (_, _) => ExportGraphCsv();
        sessionTop.Controls.Add(btnGraphCsv);
        bottom.Controls.Add(sessionTop);
        _sessionView = new BufferedListView { Dock = DockStyle.Fill };
        _sessionView.Columns.Add("시각", 120);
        _sessionView.Columns.Add("태그", 110);
        _sessionView.Columns.Add("시간", 80);
        _sessionView.Columns.Add("골드", 120);
        _sessionView.Columns.Add("골드/h", 110);
        _sessionView.Columns.Add("킬", 70);
        _sessionView.Columns.Add("EXP", 130);
        bottom.Controls.Add(_sessionView);

        var host = new Panel { Dock = DockStyle.Fill };
        host.Controls.Add(_graphPanel);
        host.Controls.Add(bottom);
        tab.Controls.Add(host);
        tab.Controls.Add(top);
        _tabs.TabPages.Insert(0, tab);
        RefreshSessionView();
    }

    private void DrawMinuteGraph(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        var w = _graphPanel.ClientSize.Width;
        var h = _graphPanel.ClientSize.Height;
        g.Clear(Color.White);
        using var axis = new Pen(Color.FromArgb(225, 225, 225));
        g.DrawLine(axis, 44, h - 24, w - 8, h - 24);
        var pts = _minutes.OrderBy(kv => kv.Key).TakeLast(60).ToList();
        if (pts.Count == 0)
        {
            using var gray = new SolidBrush(Color.Gray);
            g.DrawString("분당 수익 그래프 — 데이터 수집 중", Font, gray, 52, h / 2 - 8);
            return;
        }
        var max = Math.Max(1, pts.Max(p => p.Value.Gold));
        var barW = Math.Max(4, (w - 60) / Math.Max(1, pts.Count));
        var chartH = h - 48;
        using var bar = new SolidBrush(Color.FromArgb(70, 140, 110));
        using var expBar = new SolidBrush(Color.FromArgb(190, 215, 235));
        for (var i = 0; i < pts.Count; i++)
        {
            var (mi, v) = pts[i];
            var bh = (int)(chartH * (v.Gold / (double)max));
            var x = 48 + i * barW;
            g.FillRectangle(expBar, x, h - 24 - (int)(chartH * Math.Min(1.0, v.Exp / (double)Math.Max(1, pts.Max(p => p.Value.Exp)))), Math.Max(2, barW - 2), (int)(chartH * Math.Min(1.0, v.Exp / (double)Math.Max(1, pts.Max(p => p.Value.Exp)))));
            g.FillRectangle(bar, x, h - 24 - bh, Math.Max(2, barW - 2), bh);
            if (i == pts.Count - 1 || i == 0)
            {
                var t = DateTimeOffset.FromUnixTimeSeconds((long)mi * 60).LocalDateTime.ToString("HH:mm");
                g.DrawString(t, Font, Brushes.Gray, x - 8, h - 20);
            }
        }
        using var gray2 = new SolidBrush(Color.FromArgb(80, 80, 80));
        g.DrawString("골드/h 최대 " + N(max), Font, gray2, 48, 4);
        g.FillRectangle(bar, w - 150, 6, 10, 10);
        g.DrawString("골드", Font, gray2, w - 136, 3);
        g.FillRectangle(expBar, w - 90, 6, 10, 10);
        g.DrawString("EXP", Font, gray2, w - 76, 3);
    }

    private void HandleMinute(JsonElement r)
    {
        var mi = (int)GetLong(r, "mi");
        if (mi <= 0)
        {
            return;
        }
        _minutes[mi] = (GetLong(r, "gold"), GetLong(r, "exp"), GetLong(r, "kills"));
        if (_minutes.Count > 360)
        {
            var oldest = _minutes.Keys.Min();
            _minutes.Remove(oldest);
        }
        _graphPanel.Invalidate();
    }

    private void ExportGraphCsv()
    {
        using var dlg = new SaveFileDialog
        {
            Title = "분당 그래프 데이터 저장",
            Filter = "CSV (*.csv)|*.csv|모든 파일 (*.*)|*.*",
            FileName = $"AsgardHuntMeter-graph-{DateTime.Now:yyyyMMdd-HHmm}.csv",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }
        try
        {
            var sb = new StringBuilder("minute,time,gold,exp,kills\n");
            foreach (var (mi, v) in _minutes.OrderBy(kv => kv.Key))
            {
                var time = DateTimeOffset.FromUnixTimeSeconds((long)mi * 60).LocalDateTime.ToString("yyyy-MM-dd HH:mm");
                sb.AppendLine($"{mi},{time},{v.Gold},{v.Exp},{v.Kills}");
            }
            File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
            _statusLabel.Text = "그래프 CSV 저장됨: " + dlg.FileName;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "그래프 CSV 저장 실패: " + ex.Message, "저장",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ExportSessionReport()
    {
        using var dlg = new SaveFileDialog
        {
            Title = "세션 리포트 저장",
            Filter = "HTML (*.html)|*.html|모든 파일 (*.*)|*.*",
            FileName = $"AsgardHuntMeter-report-{DateTime.Now:yyyyMMdd-HHmm}.html",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        var expense = _expenses.Sum(x => x.Amount) + _autoExpenseGold;
        var itemH = _lootValue * 3600.0 / Math.Max(1, _sessionSec);
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html><html lang=\"ko\"><head><meta charset=\"utf-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.AppendLine("<title>Asgard Hunt Meter 세션 리포트</title>");
        sb.AppendLine("<style>");
        sb.AppendLine("body{font-family:'Malgun Gothic',sans-serif;background:#f4f6f9;color:#202632;margin:0;padding:24px}");
        sb.AppendLine(".wrap{max-width:980px;margin:auto}.card{background:#fff;border:1px solid #e3e7ee;border-radius:12px;padding:18px;margin:14px 0}");
        sb.AppendLine("h1{font-size:22px;margin:0 0 4px}h2{font-size:16px;margin:0 0 10px;color:#33415c}");
        sb.AppendLine(".muted{color:#7a8494;font-size:12px}.grid{display:grid;grid-template-columns:repeat(4,1fr);gap:10px}");
        sb.AppendLine(".kpi{background:#f7f9fc;border:1px solid #eef1f5;border-radius:10px;padding:12px}.kpi b{display:block;font-size:18px;margin-top:4px}");
        sb.AppendLine("table{width:100%;border-collapse:collapse}th,td{padding:7px 8px;border-bottom:1px solid #eef1f5;text-align:left;font-size:13px}");
        sb.AppendLine("th{color:#6b7484;font-weight:600}td.n{text-align:right}");
        sb.AppendLine("</style></head><body><div class=\"wrap\">");
        sb.AppendLine("<div class=\"card\"><h1>⚔️ Asgard Hunt Meter 세션 리포트</h1>");
        sb.AppendLine($"<div class=\"muted\">생성 {DateTime.Now:yyyy-MM-dd HH:mm:ss} · 사냥터 {E(_settings.HuntTag)}</div>");
        sb.AppendLine("<div class=\"grid\" style=\"margin-top:14px\">");
        sb.AppendLine($"<div class=\"kpi\">세션 시간<b>{TimeSpan.FromSeconds(_sessionSec):hh\\:mm\\:ss}</b></div>");
        sb.AppendLine($"<div class=\"kpi\">골드<b>{N(_gold)}</b></div>");
        sb.AppendLine($"<div class=\"kpi\">EXP<b>{N(_exp)}</b></div>");
        sb.AppendLine($"<div class=\"kpi\">킬<b>{N(_kills)}</b></div>");
        sb.AppendLine($"<div class=\"kpi\">골드/h<b>{NH(_gph)}</b></div>");
        sb.AppendLine($"<div class=\"kpi\">EXP/h<b>{NH(_eph)}</b></div>");
        sb.AppendLine($"<div class=\"kpi\">아이템 환산<b>{N(_lootValue)}</b></div>");
        sb.AppendLine($"<div class=\"kpi\">순수익<b>{N(_gold - expense)}</b></div>");
        sb.AppendLine("</div></div>");

        sb.AppendLine("<div class=\"card\"><h2>전투·수익 요약</h2><table>");
        sb.AppendLine($"<tr><th>지출</th><td class=\"n\">{N(expense)} (수동 {N(_expenses.Sum(x => x.Amount))} / 자동 {N(_autoExpenseGold)})</td></tr>");
        sb.AppendLine($"<tr><th>아이템 환산/h</th><td class=\"n\">{NH(itemH)}</td></tr>");
        sb.AppendLine($"<tr><th>가방 자산</th><td class=\"n\">{N(BagAssetValue())}</td></tr>");
        sb.AppendLine($"<tr><th>획득 아이템 종류</th><td class=\"n\">{N(_itemStates.Count)}</td></tr>");
        sb.AppendLine("</table></div>");

        sb.AppendLine("<div class=\"card\"><h2>몹별 킬</h2><table><tr><th>몹</th><th>킬</th></tr>");
        foreach (var (name, count) in _mobs.OrderByDescending(x => x.Count).Take(20))
        {
            sb.AppendLine($"<tr><td>{E(name)}</td><td class=\"n\">{N(count)}</td></tr>");
        }
        sb.AppendLine("</table></div>");

        sb.AppendLine("<div class=\"card\"><h2>아이템</h2><table><tr><th>아이템</th><th>수량</th><th>IID</th></tr>");
        foreach (var st in _itemStates.Values.OrderByDescending(x => x.Count ?? 0).Take(30))
        {
            sb.AppendLine($"<tr><td>{E(st.Name)}</td><td class=\"n\">{N(st.Count ?? 0)}</td><td>{E(st.Iid)}</td></tr>");
        }
        sb.AppendLine("</table></div>");

        sb.AppendLine("<div class=\"card\"><h2>최근 세션</h2><table><tr><th>시각</th><th>태그</th><th>시간</th><th>골드</th><th>골드/h</th><th>킬</th><th>EXP</th></tr>");
        foreach (var s in _sessions.TakeLast(20).Reverse())
        {
            sb.AppendLine($"<tr><td>{s.Time:MM-dd HH:mm}</td><td>{E(s.Tag)}</td><td>{TimeSpan.FromSeconds(s.Sec):hh\\:mm\\:ss}</td><td class=\"n\">{N(s.Gold)}</td><td class=\"n\">{NH(s.Gph)}</td><td class=\"n\">{N(s.Kills)}</td><td class=\"n\">{N(s.Exp)}</td></tr>");
        }
        sb.AppendLine("</table></div>");

        sb.AppendLine("<div class=\"card\"><h2>분당 그래프 데이터</h2><table><tr><th>시각</th><th>골드</th><th>EXP</th><th>킬</th></tr>");
        foreach (var (mi, v) in _minutes.OrderBy(kv => kv.Key).TakeLast(120))
        {
            var time = DateTimeOffset.FromUnixTimeSeconds((long)mi * 60).LocalDateTime.ToString("MM-dd HH:mm");
            sb.AppendLine($"<tr><td>{time}</td><td class=\"n\">{N(v.Gold)}</td><td class=\"n\">{N(v.Exp)}</td><td class=\"n\">{N(v.Kills)}</td></tr>");
        }
        sb.AppendLine("</table></div>");

        sb.AppendLine("<div class=\"muted\">Asgard Hunt Meter · 패시브 관측 도구 · 게임 조작 기능 없음</div>");
        sb.AppendLine("</div></body></html>");

        try
        {
            File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
            _statusLabel.Text = "리포트 저장됨: " + dlg.FileName;
            MessageBox.Show(this, "세션 리포트를 저장했습니다.\n\n" + dlg.FileName, "리포트",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "리포트 저장 실패: " + ex.Message, "저장",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OpenDonation()
    {
        if (string.IsNullOrWhiteSpace(_settings.DonationUrl))
        {
            MessageBox.Show(this, "후원 링크는 추후 공개됩니다. 감사합니다 ❤️", "후원",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_settings.DonationUrl) { UseShellExecute = true });
        }
        catch
        {
        }
    }

    private long? PriceFor(string name)
    {
        if (name.Length == 0)
        {
            return null;
        }
        var p = _prices.FirstOrDefault(x => x.Item == name && x.Price > 0);
        if (p != null)
        {
            return p.Price;
        }
        var c = _commReports
            .Where(r => r.Item == name && r.Score >= 0 && r.Price > 0)
            .OrderBy(r => r.Price)
            .FirstOrDefault();
        return c?.Price;
    }

    private long BagAssetValue()
    {
        long total = 0;
        foreach (var st in _itemStates.Values)
        {
            if (PriceFor(st.Name) is not { } price)
            {
                continue;
            }
            total += (st.Count ?? 1) * price;
        }
        return total;
    }

    private void UpdateDashboard()
    {
        var manual = _expenses.Sum(x => x.Amount);
        var expense = manual + _autoExpenseGold;
        var itemH = _lootValue * 3600.0 / Math.Max(1, _sessionSec);
        _lblDashSession.Text = $"세션 {TimeSpan.FromSeconds(_sessionSec):hh\\:mm\\:ss}  ·  킬 {N(_kills)}  ·  아이템 {N(_items)}";
        _lblDashGph.Text = $"골드 {NH(_gph)}/h  ·  EXP {NH(_eph)}/h  ·  아이템 환산 {NH(itemH)}/h  ·  합계 {NH(_gph + itemH)}/h";
        var auto = _autoExpenseGold > 0 ? $" (자동 {N(_autoExpenseGold)}: 포션 {_autoPotionUses}회/음식 {_autoFoodUses}회)" : "";
        _lblDashNet.Text = $"순수익  {N(_gold - expense)}  (골드 {N(_gold)} − 지출 {N(expense)}){auto}";
        _lblDashItemValue.Text = $"아이템 환산 누적 {N(_lootValue)} (가격 {_lootValued}건/미산정 {_lootUnpriced}건)  ·  가방 자산 {N(BagAssetValue())}";
        var target = (long)_numTargetGph.Value;
        _lblDashTarget.Text = target > 0
            ? $"목표 {N(target)}/h  ·  달성 {(_gph * 100.0 / target):0.#}%"
            : "목표 미설정 — 목표(골드/h)를 입력하세요";
        _lblInvState.Text = $"가방 사용 칸: {N(_invCount)} / {N(_settings.InventorySlots)}";
        _lblDashDps.Text = "DPS 미터: 0x13 데미지 레이아웃 추가 매핑 필요 (현재 킬/딜량 통계는 정상)";
    }

    // ===================================================================== 지출

    private void BuildExpenseTab()
    {
        var tab = new TabPage("💸 지출");
        var top = new Panel { Dock = DockStyle.Top, Height = 42, Padding = new Padding(6, 6, 6, 2) };
        top.Controls.Add(new Label { Text = "골드:", Location = new Point(4, 9), AutoSize = true });
        _numExpense = new NumericUpDown { Location = new Point(44, 5), Width = 120, Maximum = 10_000_000_000, Increment = 1000 };
        top.Controls.Add(_numExpense);
        top.Controls.Add(new Label { Text = "메모:", Location = new Point(174, 9), AutoSize = true });
        _txtExpenseMemo = new TextBox { Location = new Point(212, 5), Width = 260 };
        top.Controls.Add(_txtExpenseMemo);
        var btnAdd = new Button { Text = "지출 추가", Location = new Point(480, 3), Width = 90 };
        btnAdd.Click += (_, _) => AddExpense();
        top.Controls.Add(btnAdd);
        _lblExpenseTotals = new Label { Location = new Point(580, 9), AutoSize = true, ForeColor = Color.FromArgb(160, 80, 0) };
        top.Controls.Add(_lblExpenseTotals);

        _expenseView = new BufferedListView { Dock = DockStyle.Fill };
        _expenseView.Columns.Add("시각", 120);
        _expenseView.Columns.Add("골드", 130);
        _expenseView.Columns.Add("메모", 420);
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 36 };
        var btnDel = new Button { Text = "선택 삭제", Location = new Point(6, 4), Width = 90 };
        btnDel.Click += (_, _) => DeleteExpense();
        bottom.Controls.Add(btnDel);
        tab.Controls.Add(_expenseView);
        tab.Controls.Add(top);
        tab.Controls.Add(bottom);
        _tabs.TabPages.Add(tab);
        RefreshExpenseView();
    }

    private void HandleUseExpense(int slot)
    {
        if (!_settings.AutoExpense || !_slotItem.TryGetValue(slot, out var item))
        {
            return;
        }
        long cost = 0;
        if (item.Iid.StartsWith("iCashUse176", StringComparison.Ordinal) && _settings.PotionUnitCost > 0)
        {
            cost = _settings.PotionUnitCost;
            _autoPotionUses++;
        }
        else if (item.Iid.StartsWith("iFood", StringComparison.Ordinal) && _settings.FoodUnitCost > 0)
        {
            cost = _settings.FoodUnitCost;
            _autoFoodUses++;
        }
        if (cost <= 0)
        {
            return;
        }
        _autoExpenseGold += cost;
        UpdateDashboard();
    }

    private void CheckHomeworkKill(string mob)
    {
        if (mob.Length == 0)
        {
            return;
        }
        var completed = new List<string>();
        foreach (var h in _homework)
        {
            if (h.Done || !h.AutoRule.StartsWith("kill:", StringComparison.Ordinal))
            {
                continue;
            }
            var key = h.AutoRule[5..];
            if (key.Length > 0 && mob.Contains(key, StringComparison.Ordinal))
            {
                h.Done = true;
                h.LastDone = DateTime.Now;
                completed.Add(h.Name);
            }
        }
        if (completed.Count > 0)
        {
            RefreshHomeworkView();
            SaveFeatureData();
            RaiseAlert("숙제 자동 완료: " + string.Join(", ", completed));
        }
    }

    private void CheckHomeworkTime()
    {
        var completed = new List<string>();
        foreach (var h in _homework)
        {
            if (h.Done || !h.AutoRule.StartsWith("time:", StringComparison.Ordinal))
            {
                continue;
            }
            if (int.TryParse(h.AutoRule[5..], out var need) && _sessionSec >= need * 60L)
            {
                h.Done = true;
                h.LastDone = DateTime.Now;
                completed.Add(h.Name);
            }
        }
        if (completed.Count > 0)
        {
            RefreshHomeworkView();
            SaveFeatureData();
            RaiseAlert("숙제 자동 완료: " + string.Join(", ", completed));
        }
    }

    private void AddExpense()
    {
        var amount = (long)_numExpense.Value;
        if (amount <= 0)
        {
            return;
        }
        _expenses.Add(new ExpenseEntry { Time = DateTime.Now, Amount = amount, Memo = _txtExpenseMemo.Text.Trim() });
        _numExpense.Value = 0;
        _txtExpenseMemo.Clear();
        RefreshExpenseView();
        SaveFeatureData();
    }

    private void DeleteExpense()
    {
        if (_expenseView.SelectedIndices.Count == 0)
        {
            return;
        }
        var idx = _expenseView.SelectedIndices[0];
        // view is newest-first
        var pos = _expenses.Count - 1 - idx;
        if (pos >= 0 && pos < _expenses.Count)
        {
            _expenses.RemoveAt(pos);
            RefreshExpenseView();
            SaveFeatureData();
        }
    }

    private void RefreshExpenseView()
    {
        _expenseView.BeginUpdate();
        _expenseView.Items.Clear();
        for (var i = _expenses.Count - 1; i >= 0; i--)
        {
            var e = _expenses[i];
            var lvi = new ListViewItem(e.Time.ToString("yyyy-MM-dd HH:mm"));
            lvi.SubItems.Add(N(e.Amount));
            lvi.SubItems.Add(e.Memo);
            _expenseView.Items.Add(lvi);
        }
        _expenseView.EndUpdate();
        var total = _expenses.Sum(x => x.Amount);
        _lblExpenseTotals.Text = $"총 지출 {N(total)}  ·  자동 {N(_autoExpenseGold)}  ·  순수익 {N(_gold - total - _autoExpenseGold)}";
        UpdateDashboard();
    }

    // ===================================================================== 시세

    private void BuildMarketTab()
    {
        var tab = new TabPage("🛒 시세");
        var top = new Panel { Dock = DockStyle.Top, Height = 76, Padding = new Padding(6, 5, 6, 2) };
        _lblMarketQuery = new Label { Location = new Point(4, 5), AutoSize = true, ForeColor = Color.DimGray };
        top.Controls.Add(_lblMarketQuery);
        top.Controls.Add(new Label { Text = "아이템:", Location = new Point(4, 32), AutoSize = true });
        _txtMarketItem = new TextBox { Location = new Point(52, 28), Width = 180 };
        top.Controls.Add(_txtMarketItem);
        top.Controls.Add(new Label { Text = "현재가:", Location = new Point(242, 32), AutoSize = true });
        _numMarketPrice = new NumericUpDown { Location = new Point(298, 28), Width = 110, Maximum = 10_000_000_000, Increment = 1000 };
        top.Controls.Add(_numMarketPrice);
        top.Controls.Add(new Label { Text = "목표가:", Location = new Point(418, 32), AutoSize = true });
        _numMarketTarget = new NumericUpDown { Location = new Point(472, 28), Width = 110, Maximum = 10_000_000_000, Increment = 1000 };
        top.Controls.Add(_numMarketTarget);
        var btnAdd = new Button { Text = "시세 저장", Location = new Point(592, 26), Width = 90 };
        btnAdd.Click += (_, _) => AddPrice();
        top.Controls.Add(btnAdd);
        top.Controls.Add(new Label
        {
            Text = "거래소에서 아이템을 조회하면 0x87/0x91 요청·응답이 아래에 기록됩니다. 목표가 이하로 내려오면 알림.",
            Location = new Point(4, 55),
            AutoSize = true,
            ForeColor = Color.DimGray,
        });

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 55f));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 45f));
        _priceView = new BufferedListView { Dock = DockStyle.Fill };
        _priceView.Columns.Add("아이템", 240);
        _priceView.Columns.Add("현재가", 130);
        _priceView.Columns.Add("목표가", 130);
        _priceView.Columns.Add("갱신", 120);
        _marketView = new BufferedListView { Dock = DockStyle.Fill };
        _marketView.Columns.Add("시각", 80);
        _marketView.Columns.Add("종류", 80);
        _marketView.Columns.Add("내용", 700);
        grid.Controls.Add(_priceView, 0, 0);
        grid.Controls.Add(_marketView, 0, 1);
        tab.Controls.Add(grid);
        tab.Controls.Add(top);
        _tabs.TabPages.Add(tab);
        RefreshPriceView();
    }

    private void AddPrice()
    {
        var item = _txtMarketItem.Text.Trim();
        if (item.Length == 0)
        {
            return;
        }
        var price = (long)_numMarketPrice.Value;
        var target = (long)_numMarketTarget.Value;
        var found = _prices.FirstOrDefault(p => p.Item == item);
        if (found is null)
        {
            _prices.Add(new PriceEntry { Item = item, Price = price, Target = target, Updated = DateTime.Now });
        }
        else
        {
            found.Price = price;
            found.Target = target;
            found.Updated = DateTime.Now;
        }
        _txtMarketItem.Clear();
        RefreshPriceView();
        RefreshItemView();
        UpdateDashboard();
        SaveFeatureData();
    }

    private void RefreshPriceView()
    {
        _priceView.BeginUpdate();
        _priceView.Items.Clear();
        foreach (var p in _prices.OrderBy(p => p.Item))
        {
            var lvi = new ListViewItem(p.Item);
            lvi.SubItems.Add(p.Price > 0 ? N(p.Price) : "-");
            lvi.SubItems.Add(p.Target > 0 ? N(p.Target) : "-");
            lvi.SubItems.Add(p.Updated.ToString("MM-dd HH:mm"));
            if (p.Target > 0 && p.Price > 0 && p.Price <= p.Target)
            {
                lvi.ForeColor = Color.FromArgb(0, 130, 60);
            }
            _priceView.Items.Add(lvi);
        }
        _priceView.EndUpdate();
    }

    private void AddMarketLog(string kind, string text)
    {
        var lvi = new ListViewItem(DateTime.Now.ToString("HH:mm:ss"));
        lvi.SubItems.Add(kind);
        lvi.SubItems.Add(text);
        _marketView.Items.Insert(0, lvi);
        if (_marketView.Items.Count > 500)
        {
            _marketView.Items.RemoveAt(_marketView.Items.Count - 1);
        }
    }

    // ===================================================================== 숙제

    private static readonly (string Name, string Cycle, string AutoRule)[] DefaultHomeworkItems =
    [
        ("길드백귀", "일일", "kill:백귀"),
        ("개인 백귀", "일일", "kill:백귀"),
        ("AP 미션", "일일", ""),
        ("호크마 1시간 사냥 (수표 600만/호크마 스톤)", "일일", "time:60"),
    ];

    private void AddDefaultHomework(bool refresh)
    {
        foreach (var (name, cycle, rule) in DefaultHomeworkItems)
        {
            if (!_homework.Any(h => h.Name == name))
            {
                _homework.Add(new HomeworkEntry { Name = name, Cycle = cycle, AutoRule = rule });
            }
        }
        if (refresh)
        {
            RefreshHomeworkView();
            SaveFeatureData();
        }
    }

    private void BuildTimerTab()
    {
        var tab = new TabPage("⏱ 타이머");
        var top = new Panel { Dock = DockStyle.Top, Height = 66, Padding = new Padding(6, 6, 6, 2) };
        top.Controls.Add(new Label { Text = "이름", Location = new Point(4, 9), AutoSize = true });
        _txtTimerName = new TextBox { Location = new Point(40, 5), Width = 170 };
        top.Controls.Add(_txtTimerName);
        top.Controls.Add(new Label { Text = "최대", Location = new Point(218, 9), AutoSize = true });
        _numTimerMax = new NumericUpDown { Location = new Point(254, 5), Width = 60, Minimum = 1, Maximum = 100000000, Value = 100000 };
        top.Controls.Add(_numTimerMax);
        _cmbTimerCycle = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(322, 5), Width = 70 };
        _cmbTimerCycle.Items.AddRange(["일일", "주간", "없음"]);
        _cmbTimerCycle.SelectedIndex = 0;
        top.Controls.Add(_cmbTimerCycle);
        var btnAdd = new Button { Text = "추가", Location = new Point(400, 3), Width = 56 };
        btnAdd.Click += (_, _) => AddTimer();
        top.Controls.Add(btnAdd);
        var btnPreset = new Button { Text = "호크마 피로도", Location = new Point(462, 3), Width = 100 };
        btnPreset.Click += (_, _) => AddHokmaPreset();
        top.Controls.Add(btnPreset);
        _lblResetCountdown = new Label { Location = new Point(4, 38), AutoSize = true, ForeColor = Color.DimGray };
        top.Controls.Add(_lblResetCountdown);

        _timerView = new BufferedListView { Dock = DockStyle.Fill };
        _timerView.Columns.Add("이름", 240);
        _timerView.Columns.Add("피로도", 100);
        _timerView.Columns.Add("다음 초기화", 130);
        _timerView.Columns.Add("남은 시간", 110);
        _timerView.Columns.Add("상태", 100);

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 38 };
        var b1 = new Button { Text = "-1", Location = new Point(6, 4), Width = 44 };
        b1.Click += (_, _) => AdjustTimer(-1);
        var b10 = new Button { Text = "-10", Location = new Point(54, 4), Width = 48 };
        b10.Click += (_, _) => AdjustTimer(-10);
        var b2 = new Button { Text = "+1", Location = new Point(106, 4), Width = 44 };
        b2.Click += (_, _) => AdjustTimer(1);
        var b20 = new Button { Text = "+10,000", Location = new Point(170, 4), Width = 64 };
        b20.Click += (_, _) => AdjustTimer(10000);
        var br = new Button { Text = "초기화", Location = new Point(240, 4), Width = 60 };
        br.Click += (_, _) => ResetTimer();
        var bd = new Button { Text = "삭제", Location = new Point(304, 4), Width = 60 };
        bd.Click += (_, _) => DeleteTimer();
        bottom.Controls.AddRange([b1, b10, b2, b20, br, bd]);
        bottom.Controls.Add(new Label
        {
            Text = "  호크마 던전 피로도는 입장할 때 -1(또는 -10)로 차감하세요. 일일/주간 초기화 시 자동 회복됩니다.",
            Location = new Point(376, 9),
            AutoSize = true,
            ForeColor = Color.DimGray,
        });

        tab.Controls.Add(_timerView);
        tab.Controls.Add(top);
        tab.Controls.Add(bottom);
        _tabs.TabPages.Add(tab);
        RefreshTimerView();
    }

    private void AddTimer()
    {
        var name = _txtTimerName.Text.Trim();
        if (name.Length == 0)
        {
            return;
        }
        _timers.Add(new TimerEntry
        {
            Name = name,
            Max = (int)_numTimerMax.Value,
            Current = (int)_numTimerMax.Value,
            Cycle = _cmbTimerCycle.SelectedItem?.ToString() ?? "일일",
            LastReset = DateTime.Now,
        });
        _txtTimerName.Clear();
        RefreshTimerView();
        SaveFeatureData();
    }

    private void AddHokmaPreset()
    {
        if (!_timers.Any(t => t.Name == "호크마 던전 피로도"))
        {
            _timers.Add(new TimerEntry
            {
                Name = "호크마 던전 피로도",
                Max = 2_520_000,
                Current = 303_900,
                Cycle = "일일",
                LastReset = DateTime.Now,
            });
            RefreshTimerView();
            SaveFeatureData();
        }
    }

    private void AdjustTimer(int delta)
    {
        if (_timerView.SelectedIndices.Count == 0)
        {
            return;
        }
        var idx = _timerView.SelectedIndices[0];
        if (idx < 0 || idx >= _timers.Count)
        {
            return;
        }
        var t = _timers[idx];
        t.Current = Math.Clamp(t.Current + delta, 0, t.Max);
        if (t.Current > t.Max / 10)
        {
            _timerAlerted.Remove(t.Name);
        }
        RefreshTimerView();
        SaveFeatureData();
    }

    private void ResetTimer()
    {
        if (_timerView.SelectedIndices.Count == 0)
        {
            return;
        }
        var idx = _timerView.SelectedIndices[0];
        if (idx >= 0 && idx < _timers.Count)
        {
            var t = _timers[idx];
            t.Current = t.Max;
            t.LastReset = DateTime.Now;
            _timerAlerted.Remove(t.Name);
            RefreshTimerView();
            SaveFeatureData();
        }
    }

    private void DeleteTimer()
    {
        if (_timerView.SelectedIndices.Count == 0)
        {
            return;
        }
        var idx = _timerView.SelectedIndices[0];
        if (idx >= 0 && idx < _timers.Count)
        {
            _timers.RemoveAt(idx);
            RefreshTimerView();
            SaveFeatureData();
        }
    }

    private static DateTime NextDailyReset(DateTime now) => now.Date.AddDays(1);
    private static DateTime NextWeeklyReset(DateTime now)
    {
        var days = ((int)DayOfWeek.Monday - (int)now.DayOfWeek + 7) % 7;
        if (days == 0)
        {
            days = 7;
        }
        return now.Date.AddDays(days);
    }

    private void UpdateTimers()
    {
        if (_timerView is null)
        {
            return;
        }
        var now = DateTime.Now;
        var changed = false;
        foreach (var t in _timers)
        {
            var due = t.Cycle switch
            {
                "일일" => t.LastReset.Date < now.Date,
                "주간" => System.Globalization.ISOWeek.GetWeekOfYear(t.LastReset) < System.Globalization.ISOWeek.GetWeekOfYear(now),
                _ => false,
            };
            if (due)
            {
                t.Current = t.Max;
                t.LastReset = now;
                _timerAlerted.Remove(t.Name);
                changed = true;
            }
            if (t.Max > 0 && t.Current <= Math.Max(1, t.Max / 10) && _timerAlerted.Add(t.Name))
            {
                RaiseAlert($"{t.Name} 피로도 {t.Current}/{t.Max}" + (t.Current == 0 ? " — 소진!" : " — 얼마 남지 않았습니다"));
            }
        }
        _lblResetCountdown.Text = $"일일 초기화까지 {NextDailyReset(now) - now:hh\\:mm\\:ss}  ·  주간 초기화까지 {(NextWeeklyReset(now) - now).Days}일 {NextWeeklyReset(now) - now:hh\\:mm\\:ss}";
        RefreshTimerView();
        if (changed)
        {
            SaveFeatureData();
        }
    }

    private void RefreshTimerView()
    {
        if (_timerView is null)
        {
            return;
        }
        var now = DateTime.Now;
        _timerView.BeginUpdate();
        _timerView.Items.Clear();
        foreach (var t in _timers)
        {
            var next = t.Cycle switch
            {
                "일일" => NextDailyReset(now),
                "주간" => NextWeeklyReset(now),
                _ => (DateTime?)null,
            };
            var lvi = new ListViewItem(t.Name);
            var pct = t.Max > 0 ? t.Current * 100.0 / t.Max : 0;
            lvi.SubItems.Add($"{N(t.Current)}/{N(t.Max)} ({pct:0.##}%)");
            lvi.SubItems.Add(next is { } n ? n.ToString("MM-dd HH:mm") : "-");
            lvi.SubItems.Add(next is { } n2 ? (n2 - now).ToString(@"hh\:mm\:ss") : "-");
            lvi.SubItems.Add(t.Current == 0 ? "소진" : t.Current <= Math.Max(1, t.Max / 10) ? "부족" : "여유");
            lvi.ForeColor = t.Current == 0 ? Color.Firebrick : t.Current <= Math.Max(1, t.Max / 10) ? Color.DarkOrange : Color.FromArgb(40, 40, 40);
            _timerView.Items.Add(lvi);
        }
        _timerView.EndUpdate();
    }

    private void BuildHomeworkTab()
    {
        var tab = new TabPage("✅ 숙제");
        var top = new Panel { Dock = DockStyle.Top, Height = 64, Padding = new Padding(6, 6, 6, 2) };
        top.Controls.Add(new Label { Text = "숙제:", Location = new Point(4, 9), AutoSize = true });
        _txtHomework = new TextBox { Location = new Point(44, 5), Width = 220 };
        top.Controls.Add(_txtHomework);
        _cmbCycle = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(274, 5), Width = 80 };
        _cmbCycle.Items.AddRange(["일일", "주간", "이벤트"]);
        _cmbCycle.SelectedIndex = 0;
        top.Controls.Add(_cmbCycle);
        var btnAdd = new Button { Text = "추가", Location = new Point(362, 3), Width = 60 };
        btnAdd.Click += (_, _) => AddHomework();
        top.Controls.Add(btnAdd);
        var btnToggle = new Button { Text = "완료 토글", Location = new Point(430, 3), Width = 86 };
        btnToggle.Click += (_, _) => ToggleHomework();
        top.Controls.Add(btnToggle);
        var btnDel = new Button { Text = "삭제", Location = new Point(522, 3), Width = 60 };
        btnDel.Click += (_, _) => DeleteHomework();
        top.Controls.Add(btnDel);
        var btnPreset = new Button { Text = "기본 숙제", Location = new Point(590, 3), Width = 90 };
        btnPreset.Click += (_, _) => AddDefaultHomework(refresh: true);
        top.Controls.Add(btnPreset);
        top.Controls.Add(new Label
        {
            Text = "보스레이드·이벤트 일퀘·AP 미션을 직접 등록하세요. 일일/주간은 자정·주 시작에 자동 초기화됩니다.",
            Location = new Point(4, 36),
            AutoSize = true,
            ForeColor = Color.DimGray,
        });

        _homeworkView = new BufferedListView { Dock = DockStyle.Fill };
        _homeworkView.Columns.Add("숙제", 360);
        _homeworkView.Columns.Add("주기", 80);
        _homeworkView.Columns.Add("상태", 150);
        _homeworkView.Columns.Add("완료 시각", 120);
        _homeworkView.DoubleClick += (_, _) => ToggleHomework();

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 86, Padding = new Padding(6, 2, 6, 2) };
        _lblMissions = new Label { Dock = DockStyle.Fill, Text = "감지된 미션(0x44): 없음", ForeColor = Color.DimGray };
        bottom.Controls.Add(_lblMissions);
        tab.Controls.Add(_homeworkView);
        tab.Controls.Add(bottom);
        tab.Controls.Add(top);
        _tabs.TabPages.Add(tab);
        RefreshHomeworkView();
    }

    private void AddHomework()
    {
        var name = _txtHomework.Text.Trim();
        if (name.Length == 0)
        {
            return;
        }
        _homework.Add(new HomeworkEntry { Name = name, Cycle = _cmbCycle.SelectedItem?.ToString() ?? "일일" });
        _txtHomework.Clear();
        RefreshHomeworkView();
        SaveFeatureData();
    }

    private void ToggleHomework()
    {
        if (_homeworkView.SelectedIndices.Count == 0)
        {
            return;
        }
        var idx = _homeworkView.SelectedIndices[0];
        if (idx < 0 || idx >= _homework.Count)
        {
            return;
        }
        var h = _homework[idx];
        h.Done = !h.Done;
        h.LastDone = h.Done ? DateTime.Now : default;
        RefreshHomeworkView();
        SaveFeatureData();
    }

    private void DeleteHomework()
    {
        if (_homeworkView.SelectedIndices.Count == 0)
        {
            return;
        }
        var idx = _homeworkView.SelectedIndices[0];
        if (idx >= 0 && idx < _homework.Count)
        {
            _homework.RemoveAt(idx);
            RefreshHomeworkView();
            SaveFeatureData();
        }
    }

    private void RefreshHomeworkView()
    {
        _homeworkView.BeginUpdate();
        _homeworkView.Items.Clear();
        foreach (var h in _homework)
        {
            var lvi = new ListViewItem(h.Name);
            lvi.SubItems.Add(h.Cycle);
            var status = h.Done
                ? "완료"
                : h.AutoRule.StartsWith("time:", StringComparison.Ordinal)
                    ? (int.TryParse(h.AutoRule[5..], out var need)
                        ? $"진행 {Math.Min(_sessionSec / 60, need)}/{need}분"
                        : "미완료")
                    : h.AutoRule.StartsWith("kill:", StringComparison.Ordinal)
                        ? "자동 감지(" + h.AutoRule[5..] + ")"
                        : "미완료";
            lvi.SubItems.Add(status);
            lvi.SubItems.Add(h.Done ? h.LastDone.ToString("MM-dd HH:mm") : "-");
            lvi.ForeColor = h.Done ? Color.Gray : Color.FromArgb(40, 40, 40);
            _homeworkView.Items.Add(lvi);
        }
        _homeworkView.EndUpdate();
    }

    private void ResetHomeworkIfNeeded()
    {
        var now = DateTime.Now;
        var changed = false;
        foreach (var h in _homework)
        {
            if (!h.Done)
            {
                continue;
            }
            var due = h.Cycle switch
            {
                "일일" => h.LastDone.Date < now.Date,
                "주간" => IsoWeek(h.LastDone) < IsoWeek(now),
                _ => false,
            };
            if (due)
            {
                h.Done = false;
                changed = true;
            }
        }
        if (changed)
        {
            RefreshHomeworkView();
            SaveFeatureData();
        }
    }

    private static int IsoWeek(DateTime t) => System.Globalization.ISOWeek.GetWeekOfYear(t);

    // ===================================================================== 알림 설정

    private TabPage _notifyTab = null!;

    private void BuildNotifyTab()
    {
        var tab = new TabPage("🔔 알림/PiP");
        _notifyTab = tab;
        var panel = new Panel { Dock = DockStyle.Top, Height = 330, Padding = new Padding(10) };
        var y = 10;
        panel.Controls.Add(new Label { Text = "디스코드 웹훅 URL", Location = new Point(10, y), AutoSize = true });
        _txtDiscord = new TextBox { Location = new Point(180, y - 3), Width = 420, Text = _settings.DiscordWebhook };
        panel.Controls.Add(_txtDiscord);
        var btnTestDiscord = new Button { Text = "테스트", Location = new Point(608, y - 5), Width = 60 };
        btnTestDiscord.Click += async (_, _) => await SendDiscordAsync(_txtDiscord.Text.Trim(), "Asgard Hunt Meter 테스트 알림");
        panel.Controls.Add(btnTestDiscord);
        y += 32;
        panel.Controls.Add(new Label { Text = "텔레그램 봇 토큰", Location = new Point(10, y), AutoSize = true });
        _txtTgToken = new TextBox { Location = new Point(180, y - 3), Width = 300, Text = _settings.TelegramToken };
        panel.Controls.Add(_txtTgToken);
        panel.Controls.Add(new Label { Text = "chat id", Location = new Point(490, y), AutoSize = true });
        _txtTgChat = new TextBox { Location = new Point(540, y - 3), Width = 120, Text = _settings.TelegramChatId };
        panel.Controls.Add(_txtTgChat);
        var btnTestTg = new Button { Text = "테스트", Location = new Point(668, y - 5), Width = 60 };
        btnTestTg.Click += async (_, _) => await SendTelegramAsync(_txtTgToken.Text.Trim(), _txtTgChat.Text.Trim(), "Asgard Hunt Meter 테스트 알림");
        panel.Controls.Add(btnTestTg);
        y += 32;
        panel.Controls.Add(new Label { Text = "가방 칸 수", Location = new Point(10, y), AutoSize = true });
        _numInvSlots = new NumericUpDown { Location = new Point(180, y - 4), Width = 80, Minimum = 1, Maximum = 2000, Value = Math.Clamp(_settings.InventorySlots, 1, 2000) };
        panel.Controls.Add(_numInvSlots);
        panel.Controls.Add(new Label { Text = "만석 임박 알림(여유 칸)", Location = new Point(270, y), AutoSize = true });
        _numInvFree = new NumericUpDown { Location = new Point(430, y - 4), Width = 60, Minimum = 0, Maximum = 200, Value = Math.Clamp(_settings.InventoryWarnFree, 0, 200) };
        panel.Controls.Add(_numInvFree);

        y += 34;
        panel.Controls.Add(new Label { Text = "PiP 투명도(%)", Location = new Point(10, y), AutoSize = true });
        _numPipOpacity = new NumericUpDown { Location = new Point(130, y - 4), Width = 60, Minimum = 30, Maximum = 100, Value = Math.Clamp(_settings.PipOpacity, 30, 100) };
        panel.Controls.Add(_numPipOpacity);
        panel.Controls.Add(new Label { Text = "글자 크기", Location = new Point(200, y), AutoSize = true });
        _numPipFont = new NumericUpDown { Location = new Point(270, y - 4), Width = 50, Minimum = 8, Maximum = 18, Value = Math.Clamp(_settings.PipFontSize, 8, 18) };
        panel.Controls.Add(_numPipFont);
        _chkPipTop = new CheckBox { Text = "항상 위", Location = new Point(340, y - 3), AutoSize = true, Checked = _settings.PipTopMost };
        panel.Controls.Add(_chkPipTop);
        _chkPipStart = new CheckBox { Text = "시작 시 PiP 표시", Location = new Point(440, y - 3), AutoSize = true, Checked = _settings.PipOnStart };
        panel.Controls.Add(_chkPipStart);
        var btnPip = new Button { Text = "PiP 켜기", Location = new Point(590, y - 5), Width = 80 };
        btnPip.Click += (_, _) => TogglePip();
        panel.Controls.Add(btnPip);

        y += 34;
        _chkAutoExpense = new CheckBox { Text = "소모품 자동 지출 계산", Location = new Point(10, y), AutoSize = true, Checked = _settings.AutoExpense };
        panel.Controls.Add(_chkAutoExpense);
        panel.Controls.Add(new Label { Text = "R포션 1회", Location = new Point(200, y), AutoSize = true });
        _numPotionCost = new NumericUpDown { Location = new Point(270, y - 4), Width = 100, Maximum = 100_000_000, Increment = 100, Value = Math.Clamp(_settings.PotionUnitCost, 0, 100_000_000) };
        panel.Controls.Add(_numPotionCost);
        panel.Controls.Add(new Label { Text = "음식 1회", Location = new Point(380, y), AutoSize = true });
        _numFoodCost = new NumericUpDown { Location = new Point(440, y - 4), Width = 100, Maximum = 100_000_000, Increment = 100, Value = Math.Clamp(_settings.FoodUnitCost, 0, 100_000_000) };
        panel.Controls.Add(_numFoodCost);

        y += 38;
        var btnSave = new Button { Text = "설정 저장", Location = new Point(180, y), Width = 100 };
        btnSave.Click += (_, _) => SaveFeatureSettings(showToast: true);
        panel.Controls.Add(btnSave);
        panel.Controls.Add(new Label
        {
            Text = "알림은 트레이 풍선 + 디스코드/텔레그램으로 동시에 전송됩니다.\nPiP는 게임 화면 위에 항상 뜨는 미니 창이며, 마우스로 위치를 옮길 수 있습니다.\n소모품 자동 지출: 아이템 사용 패킷(C2S 0x1c) × 단가로 계산합니다.",
            Location = new Point(10, y + 40),
            AutoSize = true,
            ForeColor = Color.DimGray,
        });
        tab.Controls.Add(panel);
        _tabs.TabPages.Add(tab);
    }

    private void SaveFeatureSettings(bool showToast = false)
    {
        _settings.HuntTag = _txtTag.Text.Trim();
        _settings.TargetGph = (long)_numTargetGph.Value;
        _settings.DiscordWebhook = _txtDiscord.Text.Trim();
        _settings.TelegramToken = _txtTgToken.Text.Trim();
        _settings.TelegramChatId = _txtTgChat.Text.Trim();
        _settings.InventorySlots = (int)_numInvSlots.Value;
        _settings.InventoryWarnFree = (int)_numInvFree.Value;
        _settings.PipOpacity = (int)_numPipOpacity.Value;
        _settings.PipFontSize = (int)_numPipFont.Value;
        _settings.PipTopMost = _chkPipTop.Checked;
        _settings.PipOnStart = _chkPipStart.Checked;
        _settings.AutoExpense = _chkAutoExpense.Checked;
        _settings.PotionUnitCost = (long)_numPotionCost.Value;
        _settings.FoodUnitCost = (long)_numFoodCost.Value;
        _settings.CommunityUrl = AppSettings.DefaultCommunityUrl;
        _settings.CommunityWorld = _txtCommWorld.Text.Trim();
        _settings.CommunityNickname = _txtCommNick.Text.Trim();
        _settings.CommunityAutoSubmit = _chkCommAuto.Checked;
        if (_chkUpdateAuto is not null)
        {
            _settings.CheckUpdatesOnStart = _chkUpdateAuto.Checked;
        }
        if (_pip is { Visible: true })
        {
            _pip.ApplySettings(_settings.PipOpacity, _settings.PipFontSize, _settings.PipTopMost);
        }
        _settings.Save();
        if (showToast)
        {
            _statusLabel.Text = "설정 저장됨";
        }
    }

    private async void NotifyRemote(string msg)
    {
        if ((DateTime.Now - _lastRemoteAlert).TotalSeconds < 2)
        {
            return;
        }
        _lastRemoteAlert = DateTime.Now;
        var discord = _settings.DiscordWebhook;
        if (!string.IsNullOrWhiteSpace(discord))
        {
            await SendDiscordAsync(discord, msg);
        }
        var token = _settings.TelegramToken;
        var chat = _settings.TelegramChatId;
        if (!string.IsNullOrWhiteSpace(token) && !string.IsNullOrWhiteSpace(chat))
        {
            await SendTelegramAsync(token, chat, msg);
        }
    }

    private static async Task SendDiscordAsync(string url, string msg)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }
        try
        {
            var body = JsonSerializer.Serialize(new { content = msg });
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            await Http.PostAsync(url, content);
        }
        catch
        {
        }
    }

    private static async Task SendTelegramAsync(string token, string chat, string msg)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(chat))
        {
            return;
        }
        try
        {
            var url = $"https://api.telegram.org/bot{token}/sendMessage?chat_id={Uri.EscapeDataString(chat)}&text={Uri.EscapeDataString(msg)}";
            await Http.GetStringAsync(url);
        }
        catch
        {
        }
    }

    // ===================================================================== 상태/이벤트 연동

    private void UpdateFeatureStatus()
    {
        UpdateDashboard();
        CheckHomeworkTime();
        var slots = _settings.InventorySlots;
        var free = _settings.InventoryWarnFree;
        if (slots > 0 && _invCount > 0)
        {
            if (_invCount >= slots - free)
            {
                if (!_invFullAlerted)
                {
                    _invFullAlerted = true;
                    RaiseAlert($"가방 만석 임박: {N(_invCount)}/{N(slots)}칸 사용");
                }
            }
            else if (_invCount < slots - free - 5)
            {
                _invFullAlerted = false;
            }
        }
        var target = (long)_numTargetGph.Value;
        if (target > 0 && _sessionSec >= 60)
        {
            if (_gph >= target)
            {
                if (!_targetAlerted)
                {
                    _targetAlerted = true;
                    RaiseAlert($"수익 목표 달성! {NH(_gph)}/h (목표 {N(target)}/h)");
                }
            }
            else if (_gph < target * 0.9)
            {
                _targetAlerted = false;
            }
        }
        ResetHomeworkIfNeeded();
    }

    private void HandleFeatureEvent(string kind, JsonElement r, DateTime time)
    {
        switch (kind)
        {
            case "mission":
            {
                var text = GetStr(r, "text");
                if (text.Length == 0 || !_missionSeen.Add(text))
                {
                    return;
                }
                _missions.Add((time, text));
                if (_missions.Count > 50)
                {
                    _missions.RemoveAt(0);
                }
                var last = _missions.TakeLast(6).Select(m => m.Text);
                _lblMissions.Text = "감지된 미션(0x44): " + string.Join(" · ", last);
                break;
            }
            case "market_req":
            {
                var act = GetLong(r, "act");
                var q = GetStr(r, "q");
                _lblMarketQuery.Text = q.Length > 0 ? $"최근 조회: {q} (act {act})" : $"최근 거래소 요청 act={act}";
                AddMarketLog("요청", q.Length > 0 ? $"act={act} q={q}" : $"act={act}");
                break;
            }
            case "market_res":
            {
                AddMarketLog("응답", $"act={GetLong(r, "act")} len={GetLong(r, "len")}");
                break;
            }
            case "market":
            {
                var item = GetStr(r, "item");
                var price = GetLong(r, "price");
                var listings = GetLong(r, "listings");
                AddMarketLog("시세", $"{item} {N(price)}" + (listings > 0 ? $" (매물 {listings})" : ""));
                if (item.Length > 0 && price > 0)
                {
                    var p = _prices.FirstOrDefault(x => x.Item == item);
                    if (p is null)
                    {
                        _prices.Add(new PriceEntry { Item = item, Price = price, Updated = time });
                    }
                    else
                    {
                        p.Price = price;
                        p.Updated = time;
                        if (p.Target > 0 && price <= p.Target)
                        {
                            RaiseAlert($"시세 목표 달성: {item} {N(price)} (목표 {N(p.Target)})");
                        }
                    }
                    RefreshPriceView();
                    RefreshItemView();
                    UpdateDashboard();
                    SaveFeatureData();
                    SubmitCommunityPrice(item, price);
                }
                break;
            }
        }
    }

    private sealed class CommunityReport
    {
        public string Id { get; set; } = "";
        public string Item { get; set; } = "";
        public long Price { get; set; }
        public string World { get; set; } = "";
        public string Reporter { get; set; } = "";
        public string Badge { get; set; } = "";
        public string Color { get; set; } = "";
        public string At { get; set; } = "";
        public int Up { get; set; }
        public int Down { get; set; }
        public int Score { get; set; }
    }

    private TextBox _txtCommUrl = null!, _txtCommWorld = null!, _txtCommNick = null!, _txtCommPw = null!, _txtCommFilter = null!;
    private Label _lblCommStatus = null!;
    private Label _lblIconPack = null!;
    private ListView _commView = null!;
    private CheckBox _chkCommAuto = null!;
    private readonly List<CommunityReport> _commReports = [];
    private string _lastCommSubmit = "";
    private static readonly JsonSerializerOptions CommJson = new() { PropertyNameCaseInsensitive = true };

    private sealed class CommunityChat
    {
        public string Id { get; set; } = "";
        public string Nickname { get; set; } = "";
        public string Text { get; set; } = "";
        public string At { get; set; } = "";
        public string Badge { get; set; } = "";
        public string Color { get; set; } = "";
    }

    private TabPage _chatTab = null!;
    private ListView _communityChatView = null!;
    private TextBox _txtCommunityChat = null!;
    private CheckBox _chkChatAuto = null!;
    private Label _lblContribution = null!;
    private DateTime _lastChatFetch = DateTime.MinValue;
    private readonly List<CommunityChat> _communityChat = [];

    private string CommunityBase => _settings.CommunityUrl.Trim().TrimEnd('/');
    private const string PublicIconPackBase = "https://qopwa1234.github.io/asgard-hunt-meter";

    private void BuildCommunityTab()
    {
        var tab = new TabPage("🌐 커뮤니티");
        var top = new Panel { Dock = DockStyle.Top, Height = 150, Padding = new Padding(6, 6, 6, 2) };
        top.Controls.Add(new Label { Text = "서버 URL", Location = new Point(4, 9), AutoSize = true });
        _txtCommUrl = new TextBox { Location = new Point(70, 5), Width = 250, Text = _settings.CommunityUrl };
        top.Controls.Add(_txtCommUrl);
        _txtCommUrl.ReadOnly = true;
        _txtCommUrl.TabStop = false;
        _txtCommUrl.BackColor = SystemColors.Control;
        top.Controls.Add(new Label { Text = "고정", Location = new Point(326, 9), AutoSize = true, ForeColor = Color.DimGray });
        top.Controls.Add(new Label { Text = "월드", Location = new Point(380, 9), AutoSize = true });
        _txtCommWorld = new TextBox { Location = new Point(416, 5), Width = 80, Text = _settings.CommunityWorld };
        top.Controls.Add(_txtCommWorld);
        var btnHealth = new Button { Text = "연결 확인", Location = new Point(504, 3), Width = 84 };
        btnHealth.Click += async (_, _) => await CheckCommunityAsync();
        top.Controls.Add(btnHealth);

        top.Controls.Add(new Label { Text = "닉네임", Location = new Point(4, 41), AutoSize = true });
        _txtCommNick = new TextBox { Location = new Point(60, 37), Width = 120, Text = _settings.CommunityNickname };
        top.Controls.Add(_txtCommNick);
        top.Controls.Add(new Label { Text = "비번", Location = new Point(190, 41), AutoSize = true });
        _txtCommPw = new TextBox { Location = new Point(232, 37), Width = 120, UseSystemPasswordChar = true };
        top.Controls.Add(_txtCommPw);
        var btnLogin = new Button { Text = "로그인", Location = new Point(360, 35), Width = 70 };
        btnLogin.Click += async (_, _) => await CommunityAuthAsync(register: false);
        top.Controls.Add(btnLogin);
        var btnRegister = new Button { Text = "가입", Location = new Point(436, 35), Width = 60 };
        btnRegister.Click += async (_, _) => await CommunityAuthAsync(register: true);
        top.Controls.Add(btnRegister);
        _lblCommStatus = new Label { Location = new Point(504, 41), AutoSize = true, ForeColor = Color.DimGray, Text = "미로그인" };
        top.Controls.Add(_lblCommStatus);

        _chkCommAuto = new CheckBox { Text = "내 조회 가격 자동 제보", Location = new Point(4, 72), AutoSize = true, Checked = _settings.CommunityAutoSubmit };
        top.Controls.Add(_chkCommAuto);
        top.Controls.Add(new Label { Text = "아이템 필터", Location = new Point(180, 73), AutoSize = true });
        _txtCommFilter = new TextBox { Location = new Point(254, 69), Width = 180 };
        top.Controls.Add(_txtCommFilter);
        var btnRefresh = new Button { Text = "새로고침", Location = new Point(442, 67), Width = 80 };
        btnRefresh.Click += async (_, _) => await RefreshCommunityAsync(_txtCommFilter.Text.Trim());
        top.Controls.Add(btnRefresh);

        top.Controls.Add(new Label { Text = "아이콘 팩", Location = new Point(4, 102), AutoSize = true });
        var btnPackGet = new Button { Text = "최신 팩 받기", Location = new Point(70, 97), Width = 100 };
        btnPackGet.Click += async (_, _) => await DownloadIconPackAsync();
        top.Controls.Add(btnPackGet);
        var btnPackImport = new Button { Text = "ZIP 가져오기", Location = new Point(176, 97), Width = 100 };
        btnPackImport.Click += (_, _) => ImportIconPackDialog();
        top.Controls.Add(btnPackImport);
        var btnPackExport = new Button { Text = "ZIP 내보내기", Location = new Point(282, 97), Width = 100 };
        btnPackExport.Click += (_, _) => ExportIconPackDialog();
        top.Controls.Add(btnPackExport);
        var btnPackUpload = new Button { Text = "서버 업로드", Location = new Point(388, 97), Width = 100 };
        btnPackUpload.Click += async (_, _) => await UploadIconPackAsync();
        top.Controls.Add(btnPackUpload);
        _lblIconPack = new Label { Location = new Point(498, 102), AutoSize = true, ForeColor = Color.DimGray, Text = "아이콘 팩 대기" };
        top.Controls.Add(_lblIconPack);

        _commView = new BufferedListView { Dock = DockStyle.Fill };
        _commView.Columns.Add("아이템", 260);
        _commView.Columns.Add("가격", 120);
        _commView.Columns.Add("신고자", 90);
        _commView.Columns.Add("👍", 46);
        _commView.Columns.Add("👎", 46);
        _commView.Columns.Add("신뢰도", 70);
        _commView.Columns.Add("월드", 60);
        _commView.Columns.Add("시각", 130);

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 36 };
        var btnUp = new Button { Text = "👍 신뢰", Location = new Point(6, 4), Width = 80 };
        btnUp.Click += async (_, _) => await VoteCommunityAsync(1);
        var btnDown = new Button { Text = "👎 부정", Location = new Point(92, 4), Width = 80 };
        btnDown.Click += async (_, _) => await VoteCommunityAsync(-1);
        bottom.Controls.Add(btnUp);
        bottom.Controls.Add(btnDown);
        bottom.Controls.Add(new Label
        {
            Text = "  평가는 로그인이 필요합니다. 신뢰도 = 👍 / (👍+👎).",
            Location = new Point(180, 9),
            AutoSize = true,
            ForeColor = Color.DimGray,
        });

        tab.Controls.Add(_commView);
        tab.Controls.Add(top);
        tab.Controls.Add(bottom);
        _tabs.TabPages.Add(tab);
        UpdateCommunityStatus();
    }

    private void BuildCommunityChatTab()
    {
        var tab = new TabPage("💬 커뮤니티 채팅");
        _chatTab = tab;
        var top = new Panel { Dock = DockStyle.Top, Height = 38, Padding = new Padding(6, 6, 6, 2) };
        _lblContribution = new Label
        {
            Location = new Point(4, 9),
            AutoSize = false,
            Width = 236,
            Height = 22,
            AutoEllipsis = true,
            ForeColor = Color.DimGray,
            Text = "기여도: 로그인 필요",
        };
        top.Controls.Add(_lblContribution);
        var btnMe = new Button { Text = "내 기여도", Location = new Point(246, 3), Width = 80 };
        btnMe.Click += async (_, _) => await RefreshContributionAsync();
        top.Controls.Add(btnMe);
        var btnShop = new Button { Text = "🏪 상점", Location = new Point(332, 3), Width = 80 };
        btnShop.Click += (_, _) => OpenShop();
        top.Controls.Add(btnShop);
        _chkChatAuto = new CheckBox { Text = "5초 자동 새로고침", Location = new Point(426, 6), AutoSize = true, Checked = true };
        top.Controls.Add(_chkChatAuto);
        var btnRefresh = new Button { Text = "새로고침", Location = new Point(572, 3), Width = 80 };
        btnRefresh.Click += async (_, _) => await RefreshCommunityChatAsync();
        top.Controls.Add(btnRefresh);

        _communityChatView = new BufferedListView { Dock = DockStyle.Fill };
        _communityChatView.Columns.Add("시각", 80);
        _communityChatView.Columns.Add("닉네임", 120);
        _communityChatView.Columns.Add("내용", 760);

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 38 };
        _txtCommunityChat = new TextBox { Location = new Point(6, 6), Width = 820 };
        _txtCommunityChat.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                await SendCommunityChatAsync();
            }
        };
        bottom.Controls.Add(_txtCommunityChat);
        var btnSend = new Button { Text = "전송", Location = new Point(834, 4), Width = 70 };
        btnSend.Click += async (_, _) => await SendCommunityChatAsync();
        bottom.Controls.Add(btnSend);
        bottom.Controls.Add(new Label { Text = "  로그인 후 전송 가능", Location = new Point(910, 9), AutoSize = true, ForeColor = Color.DimGray });

        tab.Controls.Add(_communityChatView);
        tab.Controls.Add(top);
        tab.Controls.Add(bottom);
        _tabs.TabPages.Add(tab);
    }

    private async Task RefreshCommunityChatAsync()
    {
        try
        {
            var text = await Http.GetStringAsync(CommunityBase + "/api/chat?limit=100");
            var list = JsonSerializer.Deserialize<List<CommunityChat>>(text, CommJson) ?? [];
            _communityChat.Clear();
            _communityChat.AddRange(list);
            _communityChatView.BeginUpdate();
            _communityChatView.Items.Clear();
            foreach (var c in _communityChat)
            {
                var lvi = new ListViewItem(DateTimeOffset.TryParse(c.At, out var at) ? at.LocalDateTime.ToString("HH:mm:ss") : c.At);
                lvi.SubItems.Add(string.IsNullOrWhiteSpace(c.Badge) ? c.Nickname : c.Badge + " " + c.Nickname);
                lvi.SubItems.Add(c.Text);
                if (!string.IsNullOrWhiteSpace(c.Color)) lvi.ForeColor = ParseHexColor(c.Color, lvi.ForeColor);
                _communityChatView.Items.Add(lvi);
            }
            _communityChatView.EndUpdate();
            if (_communityChatView.Items.Count > 0)
            {
                _communityChatView.EnsureVisible(_communityChatView.Items.Count - 1);
            }
            _lastChatFetch = DateTime.Now;
        }
        catch (Exception ex)
        {
            _lblContribution.Text = "채팅 서버 오류: " + ex.Message;
        }
    }

    private async Task SendCommunityChatAsync()
    {
        var text = _txtCommunityChat.Text.Trim();
        if (text.Length == 0)
        {
            return;
        }
        if (string.IsNullOrEmpty(_settings.CommunityToken))
        {
            _lblContribution.Text = "로그인이 필요합니다";
            return;
        }
        try
        {
            var body = JsonSerializer.Serialize(new { text });
            using var req = new HttpRequestMessage(HttpMethod.Post, CommunityBase + "/api/chat")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            req.Headers.Add("X-Token", _settings.CommunityToken);
            await Http.SendAsync(req);
            _txtCommunityChat.Clear();
            await RefreshCommunityChatAsync();
        }
        catch (Exception ex)
        {
            _lblContribution.Text = "전송 실패: " + ex.Message;
        }
    }

    private async Task RefreshContributionAsync()
    {
        if (string.IsNullOrEmpty(_settings.CommunityToken))
        {
            _lblContribution.Text = "기여도: 로그인 필요";
            return;
        }
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, CommunityBase + "/api/me");
            req.Headers.Add("X-Token", _settings.CommunityToken);
            var resp = await Http.SendAsync(req);
            var text = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(text);
            var r = doc.RootElement;
            var points = r.TryGetProperty("points", out var p) ? p.GetInt32() : 0;
            _lblContribution.Text = $"포인트 {points:N0}P · 기여도 {r.GetProperty("contribution").GetInt32()} · 제보 {r.GetProperty("reports").GetInt32()} · 평가 {r.GetProperty("votesReceived").GetInt32()}";
        }
        catch (Exception ex)
        {
            _lblContribution.Text = "기여도 조회 실패: " + ex.Message;
        }
    }

    private void OpenShop()
    {
        if (string.IsNullOrWhiteSpace(CommunityBase))
        {
            MessageBox.Show(this, "커뮤니티 서버 주소를 먼저 설정하세요.", "상점",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (string.IsNullOrWhiteSpace(_settings.CommunityToken))
        {
            _lblContribution.Text = "상점: 로그인 필요";
            return;
        }
        using var form = new ShopForm(CommunityBase, _settings.CommunityToken,
            () => _ = RefreshContributionAsync());
        form.ShowDialog(this);
    }

    private static Color ParseHexColor(string? value, Color fallback)
    {
        try
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : ColorTranslator.FromHtml(value);
        }
        catch
        {
            return fallback;
        }
    }

    private void MaybeRefreshCommunityChat()
    {
        if (_chkChatAuto is null || !_chkChatAuto.Checked || _tabs.SelectedTab != _chatTab)
        {
            return;
        }
        if ((DateTime.Now - _lastChatFetch).TotalSeconds < 5)
        {
            return;
        }
        _lastChatFetch = DateTime.Now;
        _ = RefreshCommunityChatAsync();
    }

    private void UpdateCommunityStatus()    {
        _lblCommStatus.Text = string.IsNullOrEmpty(_settings.CommunityToken)
            ? "미로그인 (익명 제보 가능)"
            : "로그인: " + _settings.CommunityNickname;
    }

    private async Task CheckCommunityAsync()
    {
        try
        {
            var resp = await Http.GetStringAsync(CommunityBase + "/api/health");
            using var doc = JsonDocument.Parse(resp);
            _lblCommStatus.Text = "연결 OK · 제보 " + doc.RootElement.GetProperty("reports").GetInt32();
        }
        catch (Exception ex)
        {
            _lblCommStatus.Text = "연결 실패: " + ex.Message;
        }
    }

    private static string IconDir => Path.Combine(AppSettings.SettingsDir, "icons");

    private void ImportIconPackDialog()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "아이콘 팩 가져오기 (IID.png ZIP)",
            Filter = "아이콘 팩 (*.zip)|*.zip|모든 파일 (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            using var fs = File.OpenRead(dlg.FileName);
            var n = ExtractIconZip(fs);
            AfterIconPackImport(n, "ZIP 가져오기");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "아이콘 팩 가져오기 실패: " + ex.Message, "아이콘 팩", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ExportIconPackDialog()
    {
        if (!Directory.Exists(IconDir) || Directory.GetFiles(IconDir, "*.png").Length == 0)
        {
            MessageBox.Show(this, "내보낼 아이콘이 없습니다. 먼저 아이콘을 모아주세요.", "아이콘 팩", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var dlg = new SaveFileDialog
        {
            Title = "아이콘 팩 내보내기",
            Filter = "아이콘 팩 (*.zip)|*.zip",
            FileName = $"asgard-icons-{DateTime.Now:yyyyMMdd}.zip",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            using var zip = ZipFile.Open(dlg.FileName, ZipArchiveMode.Create);
            var files = Directory.GetFiles(IconDir, "*.png");
            foreach (var f in files) zip.CreateEntryFromFile(f, Path.GetFileName(f), CompressionLevel.Optimal);
            var manifest = zip.CreateEntry("manifest.json", CompressionLevel.Optimal);
            using var w = new StreamWriter(manifest.Open(), new UTF8Encoding(false));
            w.Write(JsonSerializer.Serialize(new
            {
                app = "AsgardHuntMeter",
                generated = DateTimeOffset.UtcNow,
                count = files.Length,
                naming = "IID.png (예: iFood06.png, aFood06.png)",
            }));
            _lblIconPack.Text = $"ZIP 내보내기: {files.Length}개";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "아이콘 팩 내보내기 실패: " + ex.Message, "아이콘 팩", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task DownloadIconPackAsync()
    {
        _lblIconPack.Text = "최신 팩 확인 중...";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var sources = new List<(string Base, string Label)>();
        if (!string.IsNullOrWhiteSpace(CommunityBase)) sources.Add((CommunityBase, "서버"));
        if (!sources.Any(s => string.Equals(s.Base, PublicIconPackBase, StringComparison.OrdinalIgnoreCase)))
            sources.Add((PublicIconPackBase, "GitHub"));

        Exception? lastError = null;
        foreach (var (baseUrl, label) in sources)
        {
            try
            {
                string json;
                string source;
                try
                {
                    json = await http.GetStringAsync(baseUrl + "/api/icons/latest");
                    source = label + " 서버";
                }
                catch
                {
                    // GitHub Pages 같은 정적 호스팅 폴백: /icons/latest.json
                    json = await http.GetStringAsync(baseUrl + "/icons/latest.json");
                    source = label + " Pages";
                }
                using var doc = JsonDocument.Parse(json);
                var url = doc.RootElement.GetProperty("url").GetString() ?? "";
                if (url.StartsWith('/')) url = baseUrl + url;
                else if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) url = baseUrl + "/icons/" + url;
                var version = doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
                var bytes = await http.GetByteArrayAsync(url);
                using var ms = new MemoryStream(bytes);
                var n = ExtractIconZip(ms);
                AfterIconPackImport(n, source + " 팩 " + version);
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }
        _lblIconPack.Text = "팩 다운로드 실패: " + (lastError?.Message ?? "알 수 없는 오류");
    }

    private async Task UploadIconPackAsync()
    {
        if (string.IsNullOrEmpty(_settings.CommunityToken))
        {
            MessageBox.Show(this, "아이콘 팩 업로드는 커뮤니티 로그인이 필요합니다.", "아이콘 팩", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var dlg = new OpenFileDialog
        {
            Title = "아이콘 팩 업로드 (ZIP)",
            Filter = "아이콘 팩 (*.zip)|*.zip",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _lblIconPack.Text = "업로드 중...";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        try
        {
            using var form = new MultipartFormDataContent();
            var bytes = await File.ReadAllBytesAsync(dlg.FileName);
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            form.Add(content, "file", Path.GetFileName(dlg.FileName));
            form.Add(new StringContent("icon-pack"), "name");
            form.Add(new StringContent(DateTime.Now.ToString("yyyyMMdd")), "version");
            using var req = new HttpRequestMessage(HttpMethod.Post, CommunityBase + "/api/icons/upload") { Content = form };
            req.Headers.Add("X-Token", _settings.CommunityToken);
            var resp = await http.SendAsync(req);
            var text = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean())
            {
                var count = doc.RootElement.TryGetProperty("count", out var c) ? c.GetInt32() : 0;
                _lblIconPack.Text = $"업로드 완료: {count}개";
            }
            else
            {
                _lblIconPack.Text = doc.RootElement.TryGetProperty("error", out var er) ? er.GetString() ?? "업로드 실패" : "업로드 실패";
            }
        }
        catch (Exception ex)
        {
            _lblIconPack.Text = "업로드 실패: " + ex.Message;
        }
    }

    private int ExtractIconZip(Stream stream)
    {
        Directory.CreateDirectory(IconDir);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var n = 0;
        foreach (var e in zip.Entries)
        {
            var name = Path.GetFileName(e.FullName.Replace('\\', '/'));
            if (!name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Contains("..", StringComparison.Ordinal)) continue;
            if (e.Length <= 8 || e.Length > 2 * 1024 * 1024) continue;
            using var es = e.Open();
            using var ms = new MemoryStream();
            es.CopyTo(ms);
            var b = ms.ToArray();
            if (b.Length < 8 || b[0] != 0x89 || b[1] != 0x50 || b[2] != 0x4E || b[3] != 0x47) continue;
            File.WriteAllBytes(Path.Combine(IconDir, name), b);
            n++;
        }
        return n;
    }

    private void AfterIconPackImport(int n, string label)
    {
        _iconCache.Clear();
        RefreshItemView();
        RebuildBuffView();
        _lblIconPack.Text = $"{label}: {n}개 아이콘 적용";
    }

    private async Task CommunityAuthAsync(bool register)
    {
        var nick = _txtCommNick.Text.Trim();
        var pw = _txtCommPw.Text;
        if (nick.Length < 2 || pw.Length < 4)
        {
            _lblCommStatus.Text = "닉네임 2자+, 비번 4자+";
            return;
        }
        try
        {
            var body = JsonSerializer.Serialize(new { nickname = nick, password = pw });
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            var resp = await Http.PostAsync(CommunityBase + (register ? "/api/register" : "/api/login"), content);
            var text = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("token", out var tokenEl))
            {
                _settings.CommunityUrl = AppSettings.DefaultCommunityUrl;
                _settings.CommunityNickname = doc.RootElement.GetProperty("nickname").GetString() ?? nick;
                _settings.CommunityToken = tokenEl.GetString() ?? "";
                _settings.Save();
                _lblCommStatus.Text = (register ? "가입/로그인 완료: " : "로그인 완료: ") + _settings.CommunityNickname;
            }
            else
            {
                _lblCommStatus.Text = doc.RootElement.TryGetProperty("error", out var er) ? er.GetString() ?? "실패" : "실패";
            }
        }
        catch (Exception ex)
        {
            _lblCommStatus.Text = "서버 오류: " + ex.Message;
        }
    }

    private async Task RefreshCommunityAsync(string filter)
    {
        try
        {
            var url = CommunityBase + "/api/prices?limit=300&item=" + Uri.EscapeDataString(filter);
            var text = await Http.GetStringAsync(url);
            var list = JsonSerializer.Deserialize<List<CommunityReport>>(text, CommJson) ?? [];
            _commReports.Clear();
            _commReports.AddRange(list);
            _commView.BeginUpdate();
            _commView.Items.Clear();
            foreach (var r in _commReports)
            {
                var lvi = new ListViewItem(r.Item);
                lvi.SubItems.Add(N(r.Price));
                lvi.SubItems.Add(string.IsNullOrWhiteSpace(r.Badge) ? r.Reporter : r.Badge + " " + r.Reporter);
                lvi.SubItems.Add(r.Up.ToString());
                lvi.SubItems.Add(r.Down.ToString());
                var conf = r.Up + r.Down == 0 ? "-" : Math.Round(100.0 * r.Up / (r.Up + r.Down)) + "%";
                lvi.SubItems.Add(conf);
                lvi.SubItems.Add(r.World);
                lvi.SubItems.Add(DateTimeOffset.TryParse(r.At, out var at) ? at.LocalDateTime.ToString("MM-dd HH:mm") : r.At);
                lvi.ForeColor = r.Score < 0 ? Color.Gray : ParseHexColor(r.Color, Color.FromArgb(40, 40, 40));
                _commView.Items.Add(lvi);
            }
            _commView.EndUpdate();
            _lblCommStatus.Text = $"조회 {_commReports.Count}건";
            RefreshItemView();
            UpdateDashboard();
        }
        catch (Exception ex)
        {
            _lblCommStatus.Text = "조회 실패: " + ex.Message;
        }
    }

    private async Task VoteCommunityAsync(int delta)
    {
        if (_commView.SelectedIndices.Count == 0)
        {
            _lblCommStatus.Text = "평가할 제보를 선택하세요";
            return;
        }
        if (string.IsNullOrEmpty(_settings.CommunityToken))
        {
            _lblCommStatus.Text = "평가하려면 로그인하세요";
            return;
        }
        var idx = _commView.SelectedIndices[0];
        if (idx < 0 || idx >= _commReports.Count)
        {
            return;
        }
        try
        {
            var body = JsonSerializer.Serialize(new { id = _commReports[idx].Id, delta });
            using var req = new HttpRequestMessage(HttpMethod.Post, CommunityBase + "/api/vote")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            req.Headers.Add("X-Token", _settings.CommunityToken);
            await Http.SendAsync(req);
            await RefreshCommunityAsync(_txtCommFilter.Text.Trim());
        }
        catch (Exception ex)
        {
            _lblCommStatus.Text = "평가 실패: " + ex.Message;
        }
    }

    private async void SubmitCommunityPrice(string item, long price)
    {
        if (!_chkCommAuto.Checked || item.Length == 0 || price <= 0)
        {
            return;
        }
        var key = item + "|" + price;
        if (key == _lastCommSubmit)
        {
            return;
        }
        _lastCommSubmit = key;
        try
        {
            var body = JsonSerializer.Serialize(new { item, price, world = _txtCommWorld.Text.Trim() });
            using var req = new HttpRequestMessage(HttpMethod.Post, CommunityBase + "/api/prices")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            if (!string.IsNullOrEmpty(_settings.CommunityToken))
            {
                req.Headers.Add("X-Token", _settings.CommunityToken);
            }
            await Http.SendAsync(req);
            if (_txtCommFilter.Text.Trim().Length == 0)
            {
                _txtCommFilter.Text = item;
            }
            await RefreshCommunityAsync(_txtCommFilter.Text.Trim());
        }
        catch
        {
            // 커뮤니티 서버가 꺼져 있어도 개인 기능은 계속 동작
        }
    }

    // ===================================================================== 세션 기록/저장

    private void RecordSession()
    {
        if (_sessionRecorded || _sessionSec < 60)
        {
            return;
        }
        _sessionRecorded = true;
        _sessions.Add(new SessionEntry
        {
            Time = DateTime.Now,
            Tag = _settings.HuntTag,
            Sec = _sessionSec,
            Gold = _gold,
            Gph = _gph,
            Kills = _kills,
            Exp = _exp,
        });
        if (_sessions.Count > 200)
        {
            _sessions.RemoveAt(0);
        }
        RefreshSessionView();
        SaveFeatureData();
    }

    private void RefreshSessionView()
    {
        if (_sessionView is null)
        {
            return;
        }
        _sessionView.BeginUpdate();
        _sessionView.Items.Clear();
        for (var i = _sessions.Count - 1; i >= 0; i--)
        {
            var s = _sessions[i];
            var lvi = new ListViewItem(s.Time.ToString("MM-dd HH:mm"));
            lvi.SubItems.Add(s.Tag);
            lvi.SubItems.Add(TimeSpan.FromSeconds(s.Sec).ToString(@"hh\:mm\:ss"));
            lvi.SubItems.Add(N(s.Gold));
            lvi.SubItems.Add(NH(s.Gph));
            lvi.SubItems.Add(N(s.Kills));
            lvi.SubItems.Add(N(s.Exp));
            _sessionView.Items.Add(lvi);
        }
        _sessionView.EndUpdate();
    }

    private void LoadFeatureData()
    {
        _expenses.Clear();
        _prices.Clear();
        _homework.Clear();
        _sessions.Clear();
        TryLoad(FeatureFile("expenses.json"), _expenses);
        TryLoad(FeatureFile("prices.json"), _prices);
        var hwPath = FeatureFile("homework.json");
        var hwExisted = File.Exists(hwPath);
        TryLoad(hwPath, _homework);
        if (!hwExisted)
        {
            AddDefaultHomework(refresh: false);
        }
        TryLoad(FeatureFile("sessions.json"), _sessions);
        var timerPath = FeatureFile("timers.json");
        var timerExisted = File.Exists(timerPath);
        TryLoad(timerPath, _timers);
        if (!timerExisted)
        {
            _timers.Add(new TimerEntry
            {
                Name = "호크마 던전 피로도",
                Max = 2_520_000,
                Current = 303_900,
                Cycle = "일일",
                LastReset = DateTime.Now,
            });
        }
        var hokma = _timers.FirstOrDefault(t => t.Name == "호크마 던전 피로도");
        if (hokma is not null && hokma.Max < 1_000_000)
        {
            hokma.Max = 2_520_000;
            hokma.Current = 303_900;
            hokma.LastReset = DateTime.Now;
        }
        if (hokma is null)
        {
            _timers.Add(new TimerEntry { Name = "호크마 던전 피로도", Max = 2_520_000, Current = 303_900, Cycle = "일일", LastReset = DateTime.Now });
        }
    }

    private static void TryLoad<T>(string path, List<T> target)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }
            var list = JsonSerializer.Deserialize<List<T>>(File.ReadAllText(path));
            if (list != null)
            {
                target.AddRange(list);
            }
        }
        catch
        {
        }
    }

    private void SaveFeatureData()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.SettingsDir);
            File.WriteAllText(FeatureFile("expenses.json"), JsonSerializer.Serialize(_expenses, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(FeatureFile("prices.json"), JsonSerializer.Serialize(_prices, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(FeatureFile("homework.json"), JsonSerializer.Serialize(_homework, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(FeatureFile("sessions.json"), JsonSerializer.Serialize(_sessions, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(FeatureFile("timers.json"), JsonSerializer.Serialize(_timers, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
        }
    }
}
