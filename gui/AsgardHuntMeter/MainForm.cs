using System.Drawing;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace AsgardHuntMeter;

public sealed partial class MainForm : Form
{
    // ---- 상태 (코어 status 라인이 authoritative, 이벤트는 로그/목록용) ----
    private readonly AppSettings _settings;
    private readonly CoreProcess _core = new();
    private long _gold, _exp, _fame, _kills, _items;
    private long _goldMsgs, _expMsgs, _fameMsgs, _itemMsgs, _chats, _uses;
    private double _gph, _eph;
    private long _paceGold;
    private double _paceGph;
    private long _sessionSec;
    private long _framesS2c, _framesC2s;
    private string _keyState = "-";
    private bool _exitRequested;
    private bool _running;

    private sealed record EventRow(DateTime Time, string Kind, string Detail);
    private readonly List<EventRow> _events = [];
    private const int MaxEvents = 3000;

    private sealed class ItemState
    {
        public string Name = "";
        public string Iid = "";
        public long? Count;
        public long? Cur;
        public long? Max;
        public int Slot;
        public DateTime Updated;
        public int Acquired;
    }

    private readonly Dictionary<string, ItemState> _itemStates = [];
    private readonly Dictionary<int, long> _useBySlot = [];
    private readonly List<(string Name, string Iid, int Slot, long? Count, long? Cur, long? Max)> _invRows = [];
    private readonly HashSet<string> _alerted = [];
    private const long FoodWarnDefault = 20;
    private const long RPotWarnPct = 5;
    private readonly List<(string Name, long Count)> _mobs = [];

    // ---- 채팅/귓말/버프/그룹 ----
    private sealed record ChatRow(
        DateTime Time,
        string Kind,
        int Ch,
        string Channel,
        string Player,
        string Text,
        bool Out,
        bool Whisper,
        string Dir,
        string Peer);

    private readonly List<ChatRow> _chatRows = [];
    private const int MaxChatRows = 5000;
    private readonly HashSet<string> _groupPlayers = [];

    private sealed class BuffState
    {
        public string Name = "";
        public string Iid = "";
        public DateTime? Expires;
        public long Val;
        public double Rate;
        public bool Warnable;
        public bool Warned;
        public DateTime LastSeen;
    }

    private readonly Dictionary<string, BuffState> _buffs = [];
    private readonly Dictionary<string, (int Hp, int Mp)> _party = [];
    private System.Windows.Forms.Timer _uiTimer = null!;
    private string _myName = "";

    // ---- 컨트롤 ----
    private ToolStrip _tool = null!;
    private ToolStripButton _btnStart = null!, _btnStop = null!;
    private ToolStripLabel _lblCoreState = null!;

    private Label _lblGold = null!, _lblGoldRate = null!, _lblExp = null!, _lblExpRate = null!, _lblFame = null!;
    private Label _lblKills = null!, _lblMobs = null!, _lblPace = null!;
    private Label _lblItems = null!, _lblItemKinds = null!, _lblItemMsgs = null!;
    private Label _lblSession = null!, _lblFrames = null!, _lblKey = null!, _lblOutDir = null!;

    private TabControl _tabs = null!;
    private ListView _logView = null!;
    private ComboBox _logFilter = null!;
    private ListView _itemView = null!, _mobView = null!, _chatView = null!, _useView = null!, _invView = null!;
    private ListView _whisperView = null!, _buffView = null!, _groupLootView = null!;
    private FlowLayoutPanel _partyPanel = null!;
    private ComboBox _chatFilter = null!;
    private TextBox _txtKeywords = null!;
    private CheckBox _chkKeywordAlert = null!, _chkWhisperAlways = null!, _chkShowSys = null!;
    private NumericUpDown _numBuffWarn = null!;
    private Label _lblGroupPlayers = null!;
    private TextBox _txtHost = null!, _txtCore = null!, _txtStderr = null!;
    private NumericUpDown _numPort = null!;
    private CheckBox _chkAuto = null!, _chkTray = null!;
    private Label _lblCoreResolved = null!;
    private ToolStripStatusLabel _statusLabel = null!;
    private StatusStrip _statusStrip = null!;
    private NotifyIcon _tray = null!;
    private PipForm? _pip;
    private ToolStripButton _btnPip = null!;
    private bool _trayHintShown;

    private sealed class BufferedListView : ListView
    {
        private static readonly Color HeaderBack = Color.FromArgb(241, 244, 248);
        private static readonly Color HeaderText = Color.FromArgb(70, 78, 90);
        private static readonly Color RowAlt = Color.FromArgb(249, 250, 252);
        private static readonly Color SelectedBack = Color.FromArgb(226, 238, 255);

        public BufferedListView()
        {
            DoubleBuffered = true;
            View = View.Details;
            FullRowSelect = true;
            HideSelection = false;
            MultiSelect = false;
            OwnerDraw = true;
            BorderStyle = BorderStyle.None;
            BackColor = Color.White;
            Font = new Font("Malgun Gothic", 9.2F);
            SmallImageList = new ImageList { ImageSize = new Size(1, 26) };
            DrawColumnHeader += OnDrawHeader;
            DrawSubItem += OnDrawSubItem;
            DrawItem += (_, _) => { };
        }

        private void OnDrawHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
        {
            using var bg = new SolidBrush(HeaderBack);
            e.Graphics.FillRectangle(bg, e.Bounds);
            using var line = new Pen(Color.FromArgb(218, 223, 230));
            e.Graphics.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            using var f = new Font("Malgun Gothic", 9F, FontStyle.Bold);
            var text = e.Header?.Text ?? "";
            TextRenderer.DrawText(e.Graphics, text, f,
                new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 10, e.Bounds.Height),
                HeaderText, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }

        private void OnDrawSubItem(object? sender, DrawListViewSubItemEventArgs e)
        {
            var selected = e.Item?.Selected == true;
            var bg = selected ? SelectedBack : (e.ItemIndex % 2 == 0 ? Color.White : RowAlt);
            if (e.Item is { BackColor: var itemBack } && itemBack != Color.Empty && itemBack != Color.White)
            {
                bg = itemBack;
            }
            using var b = new SolidBrush(bg);
            e.Graphics.FillRectangle(b, e.Bounds);
            var color = e.Item?.ForeColor ?? Color.FromArgb(40, 40, 40);
            if (e.ColumnIndex == 0)
            {
                if (e.Item?.Tag is Image img)
                {
                    var size = Math.Max(8, Math.Min(e.Bounds.Height - 6, 22));
                    var x = e.Bounds.X + (e.Bounds.Width - size) / 2;
                    var y = e.Bounds.Y + (e.Bounds.Height - size) / 2;
                    e.Graphics.DrawImage(img, new Rectangle(x, y, size, size));
                    return;
                }
                using var iconFont = new Font("Segoe UI Emoji", 12F);
                TextRenderer.DrawText(e.Graphics, e.SubItem?.Text ?? "", iconFont, e.Bounds, color,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
                return;
            }
            TextRenderer.DrawText(e.Graphics, e.SubItem?.Text ?? "", Font,
                new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 10, e.Bounds.Height),
                color, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }
    }

    public MainForm()
    {
        _settings = AppSettings.Load();

        Text = "Asgard Hunt Meter";
        Font = new Font("Malgun Gothic", 9F);
        BackColor = Color.FromArgb(244, 246, 249);
        MinimumSize = new Size(1000, 700);
        Size = new Size(1120, 780);
        StartPosition = FormStartPosition.CenterScreen;

        BuildToolbar();
        BuildStats();
        BuildTabs();
        BuildStatusStrip();
        BuildTray();

        _uiTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _uiTimer.Tick += (_, _) => OnUiTick();
        _uiTimer.Start();

        _core.LineReceived += line => SafeInvoke(() => HandleLine(line));
        _core.ErrorLine += line => SafeInvoke(() => AppendStderr(line));

        Load += (_, _) => OnFormLoad();
        FormClosing += OnFormClosing;

        Controls.Add(_tabs);
        Controls.Add(_statsPanel);
        Controls.Add(_tool);
        Controls.Add(_statusStrip);
    }

    // ===================================================================== UI

    private Panel _statsPanel = null!;

    private void BuildToolbar()
    {
        _tool = new ToolStrip
        {
            GripStyle = ToolStripGripStyle.Hidden,
            RenderMode = ToolStripRenderMode.System,
            Padding = new Padding(4, 2, 4, 2),
        };
        _btnStart = new ToolStripButton("▶ 청취 시작")
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            Padding = new Padding(8, 0, 8, 0),
        };
        _btnStart.Click += (_, _) => OnStart();
        _btnStop = new ToolStripButton("■ 중지")
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            Padding = new Padding(8, 0, 8, 0),
            Enabled = false,
        };
        _btnStop.Click += (_, _) => OnStop();
        _lblCoreState = new ToolStripLabel("코어 대기 중") { Padding = new Padding(12, 0, 0, 0) };
        _btnPip = new ToolStripButton("PiP")
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            Padding = new Padding(8, 0, 8, 0),
            ToolTipText = "게임 위에 미니 오버레이 표시",
        };
        _btnPip.Click += (_, _) => TogglePip();
        _tool.Items.Add(_btnStart);
        _tool.Items.Add(_btnStop);
        _tool.Items.Add(new ToolStripSeparator());
        _tool.Items.Add(_btnPip);
        _tool.Items.Add(new ToolStripSeparator());
        _tool.Items.Add(_lblCoreState);
        _tool.Dock = DockStyle.Top;
    }

    private void BuildStats()
    {
        _statsPanel = new Panel { Dock = DockStyle.Top, Height = 150, Padding = new Padding(6, 6, 6, 0), BackColor = Color.FromArgb(244, 246, 249) };
        var tlp = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            BackColor = Color.FromArgb(244, 246, 249),
        };
        for (var i = 0; i < 4; i++)
        {
            tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25f));
        }
        tlp.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        // 수익
        var g1 = MakeCard("수익", Color.FromArgb(192, 138, 0), out var f1);
        _lblGold = AddMetric(f1, "골드: 0");
        _lblGoldRate = AddMetric(f1, "시간당: 0");
        _lblExp = AddMetric(f1, "EXP: 0");
        _lblExpRate = AddMetric(f1, "시간당: 0");
        _lblFame = AddMetric(f1, "명성: 0");

        // 전투
        var g2 = MakeCard("전투", Color.FromArgb(192, 57, 43), out var f2);
        _lblKills = AddMetric(f2, "킬: 0");
        _lblMobs = AddMetric(f2, "몹 종류: 0");
        _lblPace = AddMetric(f2, "최근 5분 골드: 0");
        AddMetric(f2, "");

        // 아이템
        var g3 = MakeCard("아이템", Color.FromArgb(30, 142, 90), out var f3);
        _lblItems = AddMetric(f3, "획득 누적: 0");
        _lblItemKinds = AddMetric(f3, "종류: 0");
        _lblItemMsgs = AddMetric(f3, "획득 알림: 0");
        AddMetric(f3, "");

        // 세션
        var g4 = MakeCard("세션", Color.FromArgb(44, 111, 184), out var f4);
        _lblSession = AddMetric(f4, "시간: 00:00:00");
        _lblFrames = AddMetric(f4, "프레임: 0");
        _lblKey = AddMetric(f4, "키: -");
        _lblOutDir = AddMetric(f4, "저장: -");

        tlp.Controls.Add(g1, 0, 0);
        tlp.Controls.Add(g2, 1, 0);
        tlp.Controls.Add(g3, 2, 0);
        tlp.Controls.Add(g4, 3, 0);
        _statsPanel.Controls.Add(tlp);
    }

    private static Panel MakeCard(string title, Color accent, out FlowLayoutPanel flow)
    {
        var box = new Panel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(4, 2, 4, 4),
            BackColor = Color.White,
            Padding = new Padding(10, 24, 8, 4),
        };
        var t = new Label
        {
            Text = title,
            Font = new Font("Malgun Gothic", 9.5F, FontStyle.Bold),
            ForeColor = accent,
            Location = new Point(12, 6),
            AutoSize = true,
            BackColor = Color.White,
        };
        box.Controls.Add(t);
        flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(2, 0, 2, 2),
            BackColor = Color.White,
        };
        box.Controls.Add(flow);
        box.Paint += (s, e) =>
        {
            var p = (Panel)s!;
            using var pen = new Pen(Color.FromArgb(226, 229, 234));
            e.Graphics.DrawRectangle(pen, 0, 0, p.Width - 1, p.Height - 1);
            using var b = new SolidBrush(accent);
            e.Graphics.FillRectangle(b, 1, 1, p.Width - 2, 3);
        };
        return box;
    }

    private static Label AddMetric(FlowLayoutPanel flow, string text)
    {
        var l = new Label
        {
            Text = text,
            AutoSize = true,
            Margin = new Padding(0, 2, 0, 2),
            ForeColor = Color.FromArgb(30, 30, 30),
        };
        flow.Controls.Add(l);
        return l;
    }

    private void BuildTabs()
    {
        _tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(14, 6), Multiline = true };
        _tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
        _tabs.ItemSize = new Size(112, 28);
        _tabs.DrawItem += OnDrawTab;

        // --- 실시간 로그
        var logTab = new TabPage("📜 로그");
        var logTop = new Panel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(4, 5, 4, 2) };
        logTop.Controls.Add(new Label { Text = "필터:", Location = new Point(4, 8), AutoSize = true });
        _logFilter = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location = new Point(48, 4),
            Width = 130,
        };
        _logFilter.Items.AddRange(["전체", "골드", "EXP/명성", "아이템", "킬", "채팅", "기타"]);
        _logFilter.SelectedIndex = 0;
        _logFilter.SelectedIndexChanged += (_, _) => RebuildLog();
        logTop.Controls.Add(_logFilter);
        var btnLogExport = new Button { Text = "로그 저장…", Location = new Point(186, 2), Width = 96, Height = 26 };
        btnLogExport.Click += (_, _) => ExportLogs();
        logTop.Controls.Add(btnLogExport);
        _logView = new BufferedListView { Dock = DockStyle.Fill };
        _logView.Columns.Add("시각", 90);
        _logView.Columns.Add("종류", 70);
        _logView.Columns.Add("내용", 800);
        logTab.Controls.Add(_logView);
        logTab.Controls.Add(logTop);

        // --- 아이템
        var itemTab = new TabPage("🎒 아이템");
        _itemView = new BufferedListView { Dock = DockStyle.Fill };
        _itemView.Columns.Add("", 30);
        _itemView.Columns.Add("아이템", 220);
        _itemView.Columns.Add("현재 수량", 100);
        _itemView.Columns.Add("단가", 100);
        _itemView.Columns.Add("가치", 110);
        _itemView.Columns.Add("슬롯", 50);
        _itemView.Columns.Add("내부ID", 170);
        _itemView.Columns.Add("획득(신규)", 80);
        itemTab.Controls.Add(_itemView);

        // --- 몹 킬
        var mobTab = new TabPage("⚔ 몹 킬");
        _mobView = new BufferedListView { Dock = DockStyle.Fill };
        _mobView.Columns.Add("몹", 320);
        _mobView.Columns.Add("킬 수", 100);
        mobTab.Controls.Add(_mobView);

        // --- 채팅
        var chatTab = new TabPage("💬 채팅");
        var chatTop = new Panel { Dock = DockStyle.Top, Height = 62, Padding = new Padding(4, 4, 4, 2) };
        chatTop.Controls.Add(new Label { Text = "채널:", Location = new Point(4, 9), AutoSize = true });
        _chatFilter = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location = new Point(46, 5),
            Width = 100,
        };
        _chatFilter.Items.AddRange(["전체", "일반", "확성기", "그룹", "길드", "귓말", "통합", "연합", "시스템", "기타"]);
        _chatFilter.SelectedIndex = 0;
        _chatFilter.SelectedIndexChanged += (_, _) => RebuildChatView();
        chatTop.Controls.Add(_chatFilter);

        chatTop.Controls.Add(new Label { Text = "키워드:", Location = new Point(158, 9), AutoSize = true });
        _txtKeywords = new TextBox
        {
            Location = new Point(208, 5),
            Width = 240,
            Text = _settings.ChatKeywords,
        };
        chatTop.Controls.Add(_txtKeywords);
        _chkKeywordAlert = new CheckBox
        {
            Text = "키워드 알림",
            Location = new Point(458, 5),
            AutoSize = true,
            Checked = _settings.KeywordAlert,
        };
        chatTop.Controls.Add(_chkKeywordAlert);
        _chkWhisperAlways = new CheckBox
        {
            Text = "귓말 항상 알림",
            Location = new Point(566, 5),
            AutoSize = true,
            Checked = _settings.WhisperAlwaysAlert,
        };
        chatTop.Controls.Add(_chkWhisperAlways);
        _chkShowSys = new CheckBox
        {
            Text = "시스템 메시지 표시",
            Location = new Point(566, 34),
            AutoSize = true,
            Checked = _settings.ShowSystemMessages,
        };
        _chkShowSys.CheckedChanged += (_, _) => RebuildChatView();
        chatTop.Controls.Add(_chkShowSys);
        var btnExport = new Button { Text = "내보내기…", Location = new Point(700, 3), Width = 90 };
        btnExport.Click += (_, _) => ExportChats();
        chatTop.Controls.Add(btnExport);
        chatTop.Controls.Add(new Label
        {
            Text = "게임 채팅창에 없는 귓말 모아보기는 [귓말] 탭에서 확인하세요.",
            Location = new Point(4, 36),
            AutoSize = true,
            ForeColor = Color.DimGray,
        });
        _chatView = new BufferedListView { Dock = DockStyle.Fill };
        _chatView.Columns.Add("시각", 80);
        _chatView.Columns.Add("채널", 80);
        _chatView.Columns.Add("발신자", 130);
        _chatView.Columns.Add("내용", 760);
        chatTab.Controls.Add(_chatView);
        chatTab.Controls.Add(chatTop);

        // --- 귓말 모아보기
        var whisperTab = new TabPage("✉ 귓말");
        var whisperNote = new Label
        {
            Dock = DockStyle.Top,
            Height = 26,
            Text = "  받은/보낸 귓말 전체 모아보기 — 게임 채팅창에는 귓말 전용 뷰가 없습니다.",
            ForeColor = Color.DimGray,
        };
        _whisperView = new BufferedListView { Dock = DockStyle.Fill };
        _whisperView.Columns.Add("시각", 80);
        _whisperView.Columns.Add("방향", 60);
        _whisperView.Columns.Add("상대", 130);
        _whisperView.Columns.Add("내용", 760);
        whisperTab.Controls.Add(_whisperView);
        whisperTab.Controls.Add(whisperNote);

        // --- 버프
        var buffTab = new TabPage("⏳ 버프");
        var buffTop = new Panel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(4, 5, 4, 2) };
        buffTop.Controls.Add(new Label { Text = "만료 임박 알림 기준(분):", Location = new Point(4, 8), AutoSize = true });
        _numBuffWarn = new NumericUpDown
        {
            Location = new Point(160, 4),
            Width = 60,
            Minimum = 1,
            Maximum = 60,
            Value = Math.Clamp(_settings.BuffWarnMinutes, 1, 60),
        };
        buffTop.Controls.Add(_numBuffWarn);
        buffTop.Controls.Add(new Label
        {
            Text = "버프가 갱신되면 자동 재무장. 남은 시간은 서버가 준 값 기준입니다.",
            Location = new Point(236, 8),
            AutoSize = true,
            ForeColor = Color.DimGray,
        });
        _buffView = new BufferedListView { Dock = DockStyle.Fill };
        _buffView.Columns.Add("", 30);
        _buffView.Columns.Add("버프", 270);
        _buffView.Columns.Add("남은 시간", 100);
        _buffView.Columns.Add("만료 시각", 100);
        _buffView.Columns.Add("값", 80);
        _buffView.Columns.Add("ID", 200);
        buffTab.Controls.Add(_buffView);
        buffTab.Controls.Add(buffTop);

        // --- 그룹 (파티원 HP + 득템)
        var groupTab = new TabPage("👥 그룹");
        _lblGroupPlayers = new Label
        {
            Dock = DockStyle.Top,
            Height = 26,
            Text = "  그룹원: (파티 상태/그룹 채팅 수신 시 자동 학습)",
            ForeColor = Color.DimGray,
        };
        var groupGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        groupGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 34f));
        groupGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 66f));
        _partyPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = Color.White,
            Padding = new Padding(4),
        };
        _groupLootView = new BufferedListView { Dock = DockStyle.Fill };
        _groupLootView.Columns.Add("시각", 80);
        _groupLootView.Columns.Add("구분", 70);
        _groupLootView.Columns.Add("플레이어", 130);
        _groupLootView.Columns.Add("아이템", 380);
        _groupLootView.Columns.Add("수량", 70);
        groupGrid.Controls.Add(_partyPanel, 0, 0);
        groupGrid.Controls.Add(_groupLootView, 0, 1);
        groupTab.Controls.Add(groupGrid);
        groupTab.Controls.Add(_lblGroupPlayers);

        // --- 소모품
        var useTab = new TabPage("🧪 소모품");
        var useNote = new Label
        {
            Dock = DockStyle.Top,
            Height = 26,
            Text = "  가방 현황(수량/충전)과 아이템 사용 횟수 — R포션 잔여량·요리 수량 임계치 알림이 동작합니다.",
            ForeColor = Color.DimGray,
        };
        var useGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        useGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 55f));
        useGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 45f));
        _invView = new BufferedListView { Dock = DockStyle.Fill };
        _invView.Columns.Add("", 30);
        _invView.Columns.Add("아이템", 260);
        _invView.Columns.Add("슬롯", 60);
        _invView.Columns.Add("수량", 90);
        _invView.Columns.Add("충전 (현재 / 최대)", 240);
        _useView = new BufferedListView { Dock = DockStyle.Fill };
        _useView.Columns.Add("슬롯", 80);
        _useView.Columns.Add("사용 횟수", 110);
        useGrid.Controls.Add(_invView, 0, 0);
        useGrid.Controls.Add(_useView, 0, 1);
        useTab.Controls.Add(useGrid);
        useTab.Controls.Add(useNote);

        // --- 설정
        var setTab = new TabPage("⚙ 설정");
        var setPanel = new Panel { Dock = DockStyle.Top, Height = 240, Padding = new Padding(10, 10, 10, 4) };
        var y = 10;
        setPanel.Controls.Add(new Label { Text = "서버 호스트", Location = new Point(10, y), AutoSize = true });
        _txtHost = new TextBox { Location = new Point(130, y - 3), Width = 220, Text = _settings.Host, PlaceholderText = "예: game.example.com" };
        setPanel.Controls.Add(_txtHost);
        _txtHost.ReadOnly = true;
        _txtHost.TabStop = false;
        _txtHost.BackColor = SystemColors.Control;
        setPanel.Controls.Add(new Label
        {
            Text = "관리 서버 주소입니다 (고정, 변경 불가).",
            Location = new Point(360, y),
            AutoSize = true,
            ForeColor = Color.DimGray,
        });
        y += 32;
        setPanel.Controls.Add(new Label { Text = "포트 (고정)", Location = new Point(10, y), AutoSize = true });
        _numPort = new NumericUpDown
        {
            Location = new Point(130, y - 3),
            Width = 90,
            Minimum = 1,
            Maximum = 65535,
            Value = Math.Clamp(_settings.Port, 1, 65535),
        };
        setPanel.Controls.Add(_numPort);
        _numPort.Enabled = false;
        y += 32;
        setPanel.Controls.Add(new Label { Text = "코어 경로 (고정)", Location = new Point(10, y), AutoSize = true });
        _txtCore = new TextBox { Location = new Point(130, y - 3), Width = 460, Text = _settings.CorePath };
        setPanel.Controls.Add(_txtCore);
        _txtCore.ReadOnly = true;
        _txtCore.TabStop = false;
        _txtCore.BackColor = SystemColors.Control;
        var btnBrowse = new Button { Text = "찾기...", Location = new Point(600, y - 4), Width = 70 };
        btnBrowse.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog { Filter = "asgard-core.exe|asgard-core.exe|실행 파일 (*.exe)|*.exe" };
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                _txtCore.Text = dlg.FileName;
            }
        };
        setPanel.Controls.Add(btnBrowse);
        btnBrowse.Enabled = false;
        btnBrowse.Visible = false;
        y += 32;
        _lblCoreResolved = new Label
        {
            Location = new Point(130, y),
            AutoSize = true,
            ForeColor = Color.DimGray,
            Text = "(앱에 포함된 코어 사용)",
        };
        setPanel.Controls.Add(_lblCoreResolved);
        y += 32;
        _chkAuto = new CheckBox
        {
            Text = "프로그램 시작 시 자동으로 청취 시작",
            Location = new Point(130, y),
            AutoSize = true,
            Checked = _settings.AutoStart,
        };
        setPanel.Controls.Add(_chkAuto);
        y += 28;
        _chkTray = new CheckBox
        {
            Text = "닫으면 트레이로 최소화 (백그라운드 상시 운영)",
            Location = new Point(130, y),
            AutoSize = true,
            Checked = _settings.MinimizeToTray,
        };
        setPanel.Controls.Add(_chkTray);
        y += 32;
        var btnSave = new Button { Text = "설정 저장", Location = new Point(130, y), Width = 100 };
        btnSave.Click += (_, _) => SaveSettingsFromUi(showToast: true);
        setPanel.Controls.Add(btnSave);
        var lblSettingsPath = new Label
        {
            Location = new Point(250, y + 4),
            AutoSize = true,
            ForeColor = Color.DimGray,
            Text = AppSettings.SettingsFile,
        };
        setPanel.Controls.Add(lblSettingsPath);

        var errLabel = new Label { Dock = DockStyle.Top, Height = 22, Text = "  코어/ dumpcap 진단 메시지", ForeColor = Color.DimGray };
        _txtStderr = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.FromArgb(250, 250, 250),
            Font = new Font("Consolas", 8.5F),
        };
        setTab.Controls.Add(_txtStderr);
        setTab.Controls.Add(errLabel);
        setTab.Controls.Add(setPanel);

        _tabs.TabPages.Add(logTab);
        _tabs.TabPages.Add(itemTab);
        _tabs.TabPages.Add(mobTab);
        _tabs.TabPages.Add(chatTab);
        _tabs.TabPages.Add(whisperTab);
        _tabs.TabPages.Add(buffTab);
        _tabs.TabPages.Add(groupTab);
        _tabs.TabPages.Add(useTab);
        _tabs.TabPages.Add(setTab);
        BuildFeatureTabs();
    }

    private void OnDrawTab(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _tabs.TabPages.Count)
        {
            return;
        }
        var page = _tabs.TabPages[e.Index];
        var selected = _tabs.SelectedIndex == e.Index;
        var rect = e.Bounds;
        using var bg = new SolidBrush(selected ? Color.White : Color.FromArgb(236, 239, 244));
        e.Graphics.FillRectangle(bg, rect);
        if (selected)
        {
            using var accent = new SolidBrush(Color.FromArgb(52, 120, 246));
            e.Graphics.FillRectangle(accent, rect.X, rect.Bottom - 3, rect.Width, 3);
        }
        using var f = new Font("Malgun Gothic", 9F, selected ? FontStyle.Bold : FontStyle.Regular);
        TextRenderer.DrawText(e.Graphics, page.Text, f, rect,
            selected ? Color.FromArgb(30, 36, 48) : Color.FromArgb(100, 106, 116),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    private void BuildStatusStrip()    {
        _statusLabel = new ToolStripStatusLabel("대기 중 — [▶ 청취 시작]을 누르세요. (관리자 권한 필요)");
        var strip = new StatusStrip();
        strip.Items.Add(_statusLabel);
        _statusStrip = strip;
    }

    private void BuildTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("열기", null, (_, _) => RestoreFromTray());
        menu.Items.Add("청취 시작", null, (_, _) => OnStart());
        menu.Items.Add("중지", null, (_, _) => OnStop());
        menu.Items.Add("PiP 표시/숨기기", null, (_, _) => TogglePip());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("종료", null, (_, _) =>
        {
            _exitRequested = true;
            Close();
        });
        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Asgard Hunt Meter",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => RestoreFromTray();
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void TogglePip()
    {
        if (_pip is { Visible: true })
        {
            _pip.Hide();
            return;
        }
        ShowPip();
    }

    private void ShowPip()
    {
        if (_pip is null)
        {
            _pip = new PipForm();
            _pip.SettingsRequested += () =>
            {
                RestoreFromTray();
                _tabs.SelectedTab = _notifyTab;
            };
        }
        _pip.ApplySettings(_settings.PipOpacity, _settings.PipFontSize, _settings.PipTopMost);
        _pip.Show();
        _pip.BringToFront();
        UpdatePip();
    }

    private void UpdatePip()
    {
        if (_pip is null || !_pip.Visible)
        {
            return;
        }
        double? potPct = null;
        foreach (var (_, iid, _, _, cur, max) in _invRows)
        {
            if (iid.StartsWith("iCashUse176", StringComparison.Ordinal) && cur is { } c && max is { } mx && mx > 0)
            {
                potPct = c * 100.0 / mx;
                break;
            }
        }
        var now = DateTime.Now;
        var nextBuff = "";
        var next = _buffs.Values
            .Where(b => b.Expires is not null && b.Expires > now)
            .OrderBy(b => b.Expires)
            .FirstOrDefault();
        if (next?.Expires is { } e)
        {
            nextBuff = $"{next.Name} {TimeSpan.FromSeconds((e - now).TotalSeconds):mm\\:ss}";
        }
        _pip.UpdateValues(_settings.HuntTag, (long)_gph, _eph, _kills, potPct, nextBuff);
    }

    // ===================================================================== 동작

    private async void OnFormLoad()
    {
        try
        {
            Directory.CreateDirectory(Path.Combine(AppSettings.SettingsDir, "icons"));
        }
        catch
        {
        }

        var resolved = CoreProcess.FindCoreExe(_settings.CorePath);
        if (string.IsNullOrEmpty(resolved))
        {
            _lblCoreResolved.Text = "구성 요소 확인 중...";
            var prepared = await CoreProvisioner.EnsureAsync(
                message => _lblCoreResolved.Text = message);
            resolved = prepared ?? "";
            if (string.IsNullOrEmpty(resolved))
            {
                _lblCoreResolved.Text = "구성 요소 준비 실패 — 인터넷 연결을 확인해 주세요.";
            }
        }
        if (!string.IsNullOrEmpty(resolved))
        {
            _lblCoreResolved.Text = "사용: " + resolved;
        }
        _txtCore.Text = _settings.CorePath;

        try
        {
            if (!Directory.Exists(IconDir) || Directory.GetFiles(IconDir, "*.png").Length == 0)
            {
                _ = DownloadIconPackAsync();
            }
        }
        catch
        {
        }

        if (_settings.AutoStart && !string.IsNullOrEmpty(resolved))
        {
            OnStart();
        }
        if (_settings.PipOnStart)
        {
            ShowPip();
        }
        StartStartupUpdateCheck();
    }

    private void SaveSettingsFromUi(bool showToast)
    {
        _settings.Host = AppSettings.DefaultHost;
        _settings.Port = AppSettings.DefaultPort;
        _settings.CorePath = AppSettings.ManagedCorePath;
        _settings.AutoStart = _chkAuto.Checked;
        _settings.MinimizeToTray = _chkTray.Checked;
        _settings.ChatKeywords = _txtKeywords.Text.Trim();
        _settings.KeywordAlert = _chkKeywordAlert.Checked;
        _settings.WhisperAlwaysAlert = _chkWhisperAlways.Checked;
        _settings.ShowSystemMessages = _chkShowSys.Checked;
        _settings.BuffWarnMinutes = (int)_numBuffWarn.Value;
        SaveFeatureSettings();
        _settings.Save();
        if (showToast)
        {
            _statusLabel.Text = "설정 저장됨: " + AppSettings.SettingsFile;
        }
    }

    private async void OnStart()
    {
        if (_running)
        {
            return;
        }
        try
        {
            SaveSettingsFromUi(showToast: false);
            if (string.IsNullOrWhiteSpace(_settings.Host))
            {
                MessageBox.Show(this,
                    "필수 설정을 불러오지 못했습니다. 앱을 다시 실행해 주세요.",
                    "설정 오류", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var exe = CoreProcess.FindCoreExe(_settings.CorePath);
            if (string.IsNullOrEmpty(exe))
            {
                _btnStart.Enabled = false;
                _lblCoreState.Text = "구성 요소 준비 중...";
                exe = await CoreProvisioner.EnsureAsync(message =>
                {
                    _lblCoreState.Text = message;
                    _lblCoreResolved.Text = message;
                }) ?? "";
            }
            if (string.IsNullOrEmpty(exe))
            {
                MessageBox.Show(this,
                    "필수 구성 요소를 준비하지 못했습니다.\n인터넷 연결을 확인한 뒤 다시 시도해 주세요.",
                    "준비 실패", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _lblCoreState.Text = "대기";
                return;
            }

            _settings.CorePath = AppSettings.ManagedCorePath;
            _settings.Save();
            _txtCore.Text = _settings.CorePath;
            _lblCoreResolved.Text = "사용: " + exe;

            _core.ExePath = exe;
            _core.Host = _settings.Host;
            _core.Port = _settings.Port;
            _core.Key = _settings.Key;
            _core.Start();

            _running = true;
            _sessionRecorded = false;
            _minutes.Clear();
            _autoExpenseGold = 0;
            _autoPotionUses = 0;
            _autoFoodUses = 0;
            _lootValue = 0;
            _lootValued = 0;
            _lootUnpriced = 0;
            _graphPanel?.Invalidate();
            _btnStart.Enabled = false;
            _btnStop.Enabled = true;
            _lblCoreState.Text = "코어 실행 중 (관리자 권한 필요)";
            _statusLabel.Text = "청취 중...";
            _lblOutDir.Text = "저장: " + Path.GetFileName(_core.OutDir ?? "-");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "시작 실패", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            if (!_running)
            {
                _btnStart.Enabled = true;
            }
        }
    }

    private void OnStop()
    {
        _core.Stop();
        _running = false;
        _btnStart.Enabled = true;
        _btnStop.Enabled = false;
        _lblCoreState.Text = "코어 중지됨";
        _statusLabel.Text = "중지됨";
        RecordSession();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        SaveSettingsFromUi(showToast: false);
        if (!_exitRequested && _settings.MinimizeToTray && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _tray.ShowBalloonTip(2500, "Asgard Hunt Meter",
                    "트레이에서 계속 실행 중입니다. 아이콘을 더블클릭하면 다시 열립니다.", ToolTipIcon.Info);
            }
            return;
        }
        _core.Dispose();
        _uiTimer.Stop();
        _uiTimer.Dispose();
        _pip?.Close();
        _pip?.Dispose();
        RecordSession();
        SaveFeatureData();
        _tray.Visible = false;
        _tray.Dispose();
    }

    private void SafeInvoke(Action a)
    {
        try
        {
            if (IsDisposed)
            {
                return;
            }
            if (InvokeRequired)
            {
                BeginInvoke(a);
            }
            else
            {
                a();
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    // ===================================================================== JSON 처리

    private void HandleLine(string line)
    {
        if (line.Length < 2 || line[0] != '{')
        {
            return;
        }
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var type = GetStr(root, "type");
            switch (type)
            {
                case "hello":
                    _lblCoreState.Text = $"코어 v{GetStr(root, "version")} 실행 중";
                    break;
                case "status":
                    ApplyStatus(root);
                    break;
                case "ev":
                    ApplyEvent(root);
                    break;
                case "minute":
                    HandleMinute(root);
                    break;
                case "key":
                    _keyState = GetStr(root, "kind") switch
                    {
                        "restored" => $"복원됨 ({GetStr(root, "key")}, {GetStr(root, "ctr")})",
                        "verified" => $"검증됨 ({GetStr(root, "key")}, {GetStr(root, "ctr")})",
                        "exchange" => "교환 감지",
                        _ => _keyState,
                    };
                    UpdateStats();
                    break;
                case "end":
                    _lblCoreState.Text = "코어 종료됨";
                    break;
            }
        }
        catch (JsonException)
        {
            // JSON이 아닌 진단 라인은 무시
        }
    }

    private void ApplyStatus(JsonElement r)
    {
        _gold = GetLong(r, "gold");
        _exp = GetLong(r, "exp");
        _fame = GetLong(r, "fame");
        _kills = GetLong(r, "kills");
        _items = GetLong(r, "items");
        _goldMsgs = GetLong(r, "goldMsgs");
        _expMsgs = GetLong(r, "expMsgs");
        _fameMsgs = GetLong(r, "fameMsgs");
        _itemMsgs = GetLong(r, "itemMsgs");
        _chats = GetLong(r, "chats");
        _uses = GetLong(r, "uses");
        _gph = GetDouble(r, "gph");
        _eph = GetDouble(r, "eph");
        _paceGold = GetLong(r, "paceGold");
        _paceGph = GetDouble(r, "paceGph");
        _sessionSec = GetLong(r, "sec");
        _framesS2c = GetLong(r, "framesS2c");
        _framesC2s = GetLong(r, "framesC2s");

        // 서버가 보내는 top 목록으로 목록 탭 갱신 (이벤트 유실 대비)
        if (r.TryGetProperty("topItems", out var ti) && ti.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in ti.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() < 3)
                {
                    continue;
                }
                var name = e[0].GetString() ?? "";
                var count = e[1].GetInt64();
                var iid = e[2].GetString() ?? "";
                var key = iid.Length > 0 ? iid : name;
                if (!_itemStates.ContainsKey(key))
                {
                    _itemStates[key] = new ItemState
                    {
                        Name = name,
                        Iid = iid,
                        Acquired = (int)Math.Min(int.MaxValue, count),
                        Updated = DateTime.Now,
                    };
                }
            }
            RefreshItemView();
        }
        if (r.TryGetProperty("topMobs", out var tm) && tm.ValueKind == JsonValueKind.Array)
        {
            _mobs.Clear();
            foreach (var e in tm.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() < 2)
                {
                    continue;
                }
                _mobs.Add((e[0].GetString() ?? "", e[1].GetInt64()));
            }
            RefreshMobView();
        }
        if (r.TryGetProperty("groupPlayers", out var gp) && gp.ValueKind == JsonValueKind.Array)
        {
            var next = new HashSet<string>();
            foreach (var e in gp.EnumerateArray())
            {
                var n = e.GetString() ?? "";
                if (n.Length > 0 && n != _myName)
                {
                    next.Add(n);
                }
            }
            if (!next.SetEquals(_groupPlayers))
            {
                _groupPlayers.Clear();
                foreach (var n in next)
                {
                    _groupPlayers.Add(n);
                }
                UpdateGroupPlayersLabel();
            }
        }
        if (r.TryGetProperty("inv", out var inv) && inv.ValueKind == JsonValueKind.Array)        {
            _invRows.Clear();
            foreach (var e in inv.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() < 6)
                {
                    continue;
                }
                _invRows.Add((
                    e[0].GetString() ?? "",
                    e[1].GetString() ?? "",
                    (int)e[2].GetInt64(),
                    OptLong(e[3]),
                    OptLong(e[4]),
                    OptLong(e[5])));
                _slotItem[(int)e[2].GetInt64()] = (e[0].GetString() ?? "", e[1].GetString() ?? "");
            }
            RefreshInvView();
            EvaluateAlerts();
        }
        _invCount = GetLong(r, "invCount");
        UpdateFeatureStatus();
        UpdateStats();
    }

    private void ApplyEvent(JsonElement r)
    {
        var kind = GetStr(r, "kind");
        var ts = GetLong(r, "t");
        var time = ts > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(ts).LocalDateTime
            : DateTime.Now;
        var detail = kind switch
        {
            "gold" => $"+{N(GetLong(r, "amount"))}",
            "exp" => $"+{N(GetLong(r, "amount"))}",
            "fame" => $"+{N(GetLong(r, "amount"))}",
            "kill" => KillDetail(r),
            "item" => ItemDetail(r),
            "item_upd" => ItemDetail(r),
            "itemmsg" => $"{GetStr(r, "player")} → {GetStr(r, "name")} ×{GetLong(r, "count")}",
            "chat" => ChatDetail(r),
            "whisper" => WhisperDetail(r),
            "gchat" => $"{GetStr(r, "player")}: {GetStr(r, "text")}",
            "party" => $"{GetStr(r, "player")} HP {GetLong(r, "hp")}% / MP {GetLong(r, "mp")}%",
            "group_member" => $"그룹원: {GetStr(r, "name")}",
            "sys" => GetStr(r, "text"),
            "use" => $"슬롯 {GetLong(r, "slot")} 사용",
            "buff" => BuffDetail(r),
            "buff_end" => $"{GetStr(r, "name")} 만료",
            "market_req" => $"요청 act={GetLong(r, "act")} {GetStr(r, "q")}",
            "market_res" => $"응답 act={GetLong(r, "act")} len={GetLong(r, "len")}",
            _ => GetStr(r, "text"),
        };

        if (kind is "item" or "item_upd")
        {
            var iidValue = GetStr(r, "iid");
            var name = GetStr(r, "name");
            var key = iidValue.Length > 0 ? iidValue : name;
            if (key.Length > 0)
            {
                if (!_itemStates.TryGetValue(key, out var st))
                {
                    st = new ItemState { Name = name, Iid = iidValue };
                    _itemStates[key] = st;
                }
                if (kind == "item")
                {
                    st.Acquired++;
                }
                if (name.Length > 0)
                {
                    st.Name = name;
                }
                if (iidValue.Length > 0)
                {
                    st.Iid = iidValue;
                }
                st.Slot = (int)GetLong(r, "slot");
                if (r.TryGetProperty("count", out var cEl) && cEl.ValueKind == JsonValueKind.Number)
                {
                    st.Count = cEl.GetInt64();
                }
                if (r.TryGetProperty("cur", out var curEl2) && curEl2.ValueKind == JsonValueKind.Number)
                {
                    st.Cur = curEl2.GetInt64();
                }
                if (r.TryGetProperty("max", out var maxEl) && maxEl.ValueKind == JsonValueKind.Number)
                {
                    st.Max = maxEl.GetInt64();
                }
                st.Updated = DateTime.Now;
                if (kind == "item")
                {
                    var qty = st.Count ?? 1;
                    if (PriceFor(st.Name) is { } unit)
                    {
                        _lootValue += qty * unit;
                        _lootValued++;
                    }
                    else
                    {
                        _lootUnpriced++;
                    }
                }
                _slotItem[st.Slot] = (st.Name, st.Iid);
                RefreshItemView();
            }
        }
        if (kind == "use")
        {
            var slot = (int)GetLong(r, "slot");
            _useBySlot[slot] = _useBySlot.GetValueOrDefault(slot) + 1;
            HandleUseExpense(slot);
            RefreshUseView();
        }
        if (kind == "kill")
        {
            CheckHomeworkKill(GetStr(r, "mob"));
        }
        if (kind is "chat" or "whisper" or "gchat" or "sys" or "itemmsg")
        {
            HandleChatEvent(kind, r, time);
        }
        if (kind is "mission" or "market" or "dmg" or "market_req" or "market_res")
        {
            HandleFeatureEvent(kind, r, time);
        }
        if (kind == "buff")
        {
            HandleBuff(r, time);
        }
        else if (kind == "buff_end")
        {
            RemoveBuff(GetStr(r, "name"));
        }
        else if (kind == "itemmsg")
        {
            AddGroupLoot(r, time);
        }
        else if (kind == "party")
        {
            HandleParty(r);
        }
        else if (kind == "group_member")
        {
            AddGroupPlayer(GetStr(r, "name"));
        }

        var row = new EventRow(time, kind, detail);
        _events.Add(row);
        if (_events.Count > MaxEvents)
        {
            _events.RemoveRange(0, _events.Count - MaxEvents);
        }
        if (FilterMatch(_logFilter.SelectedItem?.ToString() ?? "전체", kind))
        {
            AddLogRow(row);
        }
    }

    private static string ChatDetail(JsonElement r)
    {
        var ch = (int)GetLong(r, "ch");
        var player = GetStr(r, "player");
        var text = GetStr(r, "text");
        return player.Length > 0 ? $"[{ChannelName(ch)}] {player}: {text}" : $"[{ChannelName(ch)}] {text}";
    }

    private static string WhisperDetail(JsonElement r)
    {
        var outFlag = GetStr(r, "dir") == "out";
        var from = GetStr(r, "from");
        var peer = GetStr(r, "peer");
        var text = GetStr(r, "text");
        return outFlag ? $"→ {peer}: {text}" : $"← {from}: {text}";
    }

    private static string BuffDetail(JsonElement r)
    {
        var name = GetStr(r, "name");
        var on = GetBool(r, "on");
        if (!on)
        {
            return $"해제 {name}";
        }
        var remain = r.TryGetProperty("remain", out var rem) && rem.ValueKind == JsonValueKind.Number
            ? rem.GetDouble()
            : 0;
        return remain > 0 ? $"적용 {name} ({TimeSpan.FromSeconds(remain):mm\\:ss})" : $"적용 {name}";
    }

    private string KillDetail(JsonElement r)
    {
        var mob = GetStr(r, "mob");
        var total = GetLong(r, "kills");
        var obj = GetStr(r, "obj");
        var who = mob.Length > 0 ? mob : $"(미확인 {obj})";
        return $"{who}  [{N(total)}킬]";
    }

    private string ItemDetail(JsonElement r)
    {
        var name = GetStr(r, "name");
        var iid = GetStr(r, "iid");
        var slot = GetLong(r, "slot");
        var display = name.Length > 0 ? name : iid;
        var hasCur = r.TryGetProperty("cur", out var curEl) && curEl.ValueKind == JsonValueKind.Number;
        var cur = hasCur ? curEl.GetInt64() : 0;
        var max = GetLong(r, "max");
        if (hasCur && max > 0)
        {
            var pct = cur * 100.0 / max;
            return $"{display}: {N(cur)}/{N(max)} ({pct.ToString("0.#", CultureInfo.InvariantCulture)}%)";
        }
        var count = GetLong(r, "count");
        if (count > 0)
        {
            return $"{display}: {N(count)}개 (슬롯 {slot})";
        }
        return $"{display} (슬롯 {slot})";
    }

    private static bool FilterMatch(string filter, string kind) => filter switch
    {
        "전체" => true,
        "골드" => kind == "gold",
        "EXP/명성" => kind is "exp" or "fame",
        "아이템" => kind is "item" or "itemmsg" or "item_upd",
        "킬" => kind == "kill",
        "채팅" => kind is "chat" or "sys" or "whisper" or "gchat" or "group_member",
        _ => kind is "use" or "buff" or "buff_end" or "market_req" or "market_res",
    };

    private static string KindLabel(string kind) => kind switch
    {
        "gold" => "골드",
        "exp" => "EXP",
        "fame" => "명성",
        "kill" => "킬",
        "item" => "아이템",
        "item_upd" => "아이템갱신",
        "itemmsg" => "그룹아이템",
        "chat" => "채팅",
        "whisper" => "귓말",
        "gchat" => "그룹채팅",
        "group_member" => "그룹원",
        "sys" => "시스템",
        "use" => "사용",
        "buff" => "버프",
        "buff_end" => "버프만료",
        "market_req" => "거래소→",
        "market_res" => "←거래소",
        _ => kind,
    };

    private static Color KindColor(string kind) => kind switch
    {
        "gold" => Color.FromArgb(150, 110, 0),
        "exp" => Color.FromArgb(0, 90, 200),
        "fame" => Color.FromArgb(130, 60, 170),
        "item" => Color.FromArgb(0, 130, 60),
        "item_upd" => Color.FromArgb(90, 150, 90),
        "itemmsg" => Color.FromArgb(0, 110, 90),
        "kill" => Color.FromArgb(95, 95, 95),
        "chat" => Color.FromArgb(40, 40, 40),
        "whisper" => Color.FromArgb(160, 60, 0),
        "gchat" => Color.FromArgb(0, 110, 90),
        "group_member" => Color.FromArgb(90, 120, 90),
        "sys" => Color.FromArgb(120, 120, 120),
        "use" => Color.FromArgb(180, 90, 0),
        "buff" => Color.FromArgb(0, 120, 120),
        "buff_end" => Color.FromArgb(170, 90, 90),
        "market_req" => Color.FromArgb(190, 100, 0),
        "market_res" => Color.FromArgb(150, 120, 30),
        _ => Color.FromArgb(70, 70, 70),
    };

    private void AddLogRow(EventRow row)
    {
        var lvi = new ListViewItem(row.Time.ToString("HH:mm:ss"));
        lvi.SubItems.Add(KindLabel(row.Kind));
        lvi.SubItems.Add(row.Detail);
        lvi.ForeColor = KindColor(row.Kind);
        _logView.Items.Insert(0, lvi);
        if (_logView.Items.Count > 2000)
        {
            _logView.Items.RemoveAt(_logView.Items.Count - 1);
        }
    }

    private void RebuildLog()
    {
        _logView.BeginUpdate();
        _logView.Items.Clear();
        var filter = _logFilter.SelectedItem?.ToString() ?? "전체";
        for (var i = _events.Count - 1; i >= 0; i--)
        {
            var row = _events[i];
            if (FilterMatch(filter, row.Kind))
            {
                var lvi = new ListViewItem(row.Time.ToString("HH:mm:ss"));
                lvi.SubItems.Add(KindLabel(row.Kind));
                lvi.SubItems.Add(row.Detail);
                lvi.ForeColor = KindColor(row.Kind);
                _logView.Items.Add(lvi);
                if (_logView.Items.Count > 2000)
                {
                    break;
                }
            }
        }
        _logView.EndUpdate();
    }

    // ===================================================================== 채팅/귓말/버프

    private static string ChannelName(int ch) => ch switch
    {
        0 => "일반",
        3 => "시스템",
        6 => "확성기",
        14 => "기타",
        100 => "통합",
        101 => "길드",
        102 => "연합",
        _ => "ch" + ch,
    };

    private string CurrentChatFilter => _chatFilter.SelectedItem?.ToString() ?? "전체";

    private static bool ChatFilterMatch(string filter, ChatRow r) => filter switch
    {
        "전체" => true,
        "일반" => !r.Whisper && r.Channel == "일반",
        "확성기" => r.Channel == "확성기",
        "그룹" => r.Channel == "그룹",
        "길드" => r.Channel == "길드",
        "귓말" => r.Whisper,
        "통합" => r.Channel == "통합",
        "연합" => r.Channel == "연합",
        "시스템" => r.Channel == "시스템",
        "기타" => !r.Whisper && r.Channel.StartsWith("ch", StringComparison.Ordinal),
        _ => true,
    };

    private bool ShouldShowChat(ChatRow row)
    {
        if (!ChatFilterMatch(CurrentChatFilter, row))
        {
            return false;
        }
        // 시스템(전투/스킬 잡음 포함)은 기본 숨김 — 필요할 때만 체크/필터로 표시.
        if (row.Channel == "시스템" && CurrentChatFilter == "전체" && !_chkShowSys.Checked)
        {
            return false;
        }
        return true;
    }

    private void HandleChatEvent(string kind, JsonElement r, DateTime time)
    {
        ChatRow row;
        switch (kind)
        {
            case "chat":
            {
                var ch = (int)GetLong(r, "ch");
                var player = GetStr(r, "player");
                var outFlag = GetBool(r, "out");
                if (outFlag && player.Length > 0)
                {
                    LearnMyName(player);
                }
                row = new ChatRow(time, kind, ch, ChannelName(ch), player, GetStr(r, "text"), outFlag, false, "", "");
                break;
            }
            case "gchat":
            {
                var player = GetStr(r, "player");
                var outFlag = GetBool(r, "out");
                if (outFlag && player.Length > 0)
                {
                    LearnMyName(player);
                }
                else
                {
                    AddGroupPlayer(player);
                }
                row = new ChatRow(time, kind, 0, "그룹", player, GetStr(r, "text"), outFlag, false, "", "");
                break;
            }
            case "whisper":
            {
                var dir = GetStr(r, "dir");
                var from = GetStr(r, "from");
                var peer = GetStr(r, "peer");
                var outFlag = dir == "out";
                if (outFlag && from.Length > 0)
                {
                    LearnMyName(from);
                }
                var other = outFlag ? (peer.Length > 0 ? peer : "?") : from;
                row = new ChatRow(time, kind, 0, "귓말", outFlag ? "나" : from, GetStr(r, "text"), outFlag, true, dir, other);
                break;
            }
            case "itemmsg":
            {
                var player = GetStr(r, "player");
                var item = GetStr(r, "name");
                var count = GetLong(r, "count");
                row = new ChatRow(time, kind, 0, "획득", player, $"{item} ×{N(count)}", false, false, "", "");
                break;
            }
            default:
            {
                row = new ChatRow(time, "sys", 0, "시스템", "", GetStr(r, "text"), false, false, "", "");
                break;
            }
        }

        _chatRows.Add(row);
        if (_chatRows.Count > MaxChatRows)
        {
            _chatRows.RemoveRange(0, _chatRows.Count - MaxChatRows);
        }

        var keywordHit = false;
        if (kind is "chat" or "whisper" or "gchat")
        {
            keywordHit = IsKeywordHit(row.Text, out var kw);
            if (row.Whisper && _chkWhisperAlways.Checked)
            {
                RaiseAlert($"귓말 {(row.Out ? "→ " + row.Peer : row.Player)}: {row.Text}");
            }
            else if (keywordHit)
            {
                RaiseAlert($"키워드 '{kw}' — {(row.Player.Length > 0 ? row.Player : "?")}: {row.Text}");
            }
        }

        if (row.Whisper)
        {
            AddWhisperRow(row);
        }
        if (ShouldShowChat(row))
        {
            AddChatViewRow(row, keywordHit);
        }
    }

    private void LearnMyName(string name)
    {
        if (name.Length == 0 || _myName == name)
        {
            return;
        }
        _myName = name;
        _groupPlayers.Remove(name);
        UpdateGroupPlayersLabel();
    }

    private bool IsKeywordHit(string text, out string keyword)
    {
        keyword = "";
        if (!_chkKeywordAlert.Checked || text.Length == 0)
        {
            return false;
        }
        foreach (var k in _txtKeywords.Text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            if (text.Contains(k, StringComparison.OrdinalIgnoreCase))
            {
                keyword = k;
                return true;
            }
        }
        return false;
    }

    private ListViewItem BuildChatItem(ChatRow row, bool highlight)
    {
        var lvi = new ListViewItem(row.Time.ToString("HH:mm:ss"));
        lvi.SubItems.Add(row.Channel);
        lvi.SubItems.Add(row.Player);
        lvi.SubItems.Add(row.Text);
        if (highlight)
        {
            lvi.BackColor = Color.FromArgb(255, 244, 170);
            lvi.ForeColor = Color.FromArgb(80, 50, 0);
        }
        else
        {
            lvi.ForeColor = row.Whisper ? Color.FromArgb(160, 60, 0) : Color.FromArgb(40, 40, 40);
        }
        return lvi;
    }

    private void AddChatViewRow(ChatRow row, bool highlight)
    {
        _chatView.Items.Insert(0, BuildChatItem(row, highlight));
        if (_chatView.Items.Count > 3000)
        {
            _chatView.Items.RemoveAt(_chatView.Items.Count - 1);
        }
    }

    private void AddWhisperRow(ChatRow row)
    {
        var lvi = new ListViewItem(row.Time.ToString("HH:mm:ss"));
        lvi.SubItems.Add(row.Dir == "out" ? "보냄" : "받음");
        lvi.SubItems.Add(row.Peer);
        lvi.SubItems.Add(row.Text);
        lvi.ForeColor = row.Dir == "out" ? Color.FromArgb(0, 90, 200) : Color.FromArgb(160, 60, 0);
        _whisperView.Items.Insert(0, lvi);
        if (_whisperView.Items.Count > 3000)
        {
            _whisperView.Items.RemoveAt(_whisperView.Items.Count - 1);
        }
    }

    private void RebuildChatView()
    {
        _chatView.BeginUpdate();
        _chatView.Items.Clear();
        var added = 0;
        for (var i = _chatRows.Count - 1; i >= 0 && added < 3000; i--)
        {
            var row = _chatRows[i];
            if (!ShouldShowChat(row))
            {
                continue;
            }
            var hit = (row.Kind is "chat" or "whisper" or "gchat") && IsKeywordHit(row.Text, out _);
            _chatView.Items.Add(BuildChatItem(row, hit));
            added++;
        }
        _chatView.EndUpdate();
    }

    private void ExportLogs()
    {
        if (_events.Count == 0)
        {
            MessageBox.Show(this, "내보낼 로그가 없습니다.", "로그 저장", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var dlg = new SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv|텍스트 (*.txt)|*.txt",
            FileName = "events_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv",
            InitialDirectory = AppSettings.SettingsDir,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }
        var csv = Path.GetExtension(dlg.FileName).Equals(".csv", StringComparison.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        if (csv)
        {
            sb.AppendLine("시각,종류,내용");
            foreach (var r in _events)
            {
                sb.AppendLine(string.Join(",", CsvField(r.Time.ToString("yyyy-MM-dd HH:mm:ss")), CsvField(r.Kind), CsvField(r.Detail)));
            }
        }
        else
        {
            foreach (var r in _events)
            {
                sb.AppendLine($"{r.Time:yyyy-MM-dd HH:mm:ss}\t{r.Kind}\t{r.Detail}");
            }
        }
        try
        {
            File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
            _statusLabel.Text = "로그 저장됨: " + dlg.FileName;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "로그 저장 실패", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ExportChats()
    {
        if (_chatRows.Count == 0)
        {
            MessageBox.Show(this, "내보낼 채팅이 없습니다.", "내보내기", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var dlg = new SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv|텍스트 (*.txt)|*.txt",
            FileName = "chat_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv",
            InitialDirectory = AppSettings.SettingsDir,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }
        var csv = Path.GetExtension(dlg.FileName).Equals(".csv", StringComparison.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        if (csv)
        {
            sb.AppendLine("시각,채널,발신자,내용,귓말방향,상대");
            foreach (var r in _chatRows)
            {
                if (r.Channel == "시스템" && !_chkShowSys.Checked)
                {
                    continue;
                }
                sb.AppendLine(string.Join(",",
                    CsvField(r.Time.ToString("HH:mm:ss")),
                    CsvField(r.Channel),
                    CsvField(r.Player),
                    CsvField(r.Text),
                    CsvField(r.Whisper ? r.Dir : ""),
                    CsvField(r.Whisper ? r.Peer : "")));
            }
        }
        else
        {
            foreach (var r in _chatRows)
            {
                if (r.Channel == "시스템" && !_chkShowSys.Checked)
                {
                    continue;
                }
                sb.AppendLine($"{r.Time:HH:mm:ss}\t{r.Channel}\t{r.Player}\t{r.Text}");
            }
        }
        try
        {
            File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
            _statusLabel.Text = "채팅 내보냄: " + dlg.FileName;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "내보내기 실패", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static string CsvField(string s)
    {
        if (s.Contains('"') || s.Contains(',') || s.Contains('\n') || s.Contains('\r'))
        {
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }
        return s;
    }

    private void AddGroupPlayer(string name)
    {
        if (name.Length == 0 || name == _myName)
        {
            return;
        }
        if (_groupPlayers.Add(name))
        {
            UpdateGroupPlayersLabel();
        }
    }

    private void UpdateGroupPlayersLabel()
    {
        _lblGroupPlayers.Text = _groupPlayers.Count == 0
            ? "  그룹원: (그룹 채팅 수신 시 자동 학습)"
            : "  그룹원(" + _groupPlayers.Count + "): " + string.Join(", ", _groupPlayers);
    }

    private void HandleParty(JsonElement r)
    {
        var name = GetStr(r, "player");
        if (name.Length == 0)
        {
            return;
        }
        var hp = (int)GetLong(r, "hp");
        var mp = (int)GetLong(r, "mp");
        _party[name] = (hp, mp);
        // 0x3a HP/MP 오프셋은 100% 검증 전이므로 알림은 보류(명단만 신뢰).
        const bool partyHpAlerts = false;
        if (partyHpAlerts && name != _myName)
        {
            var key = "php:" + name;
            if (hp == 0)
            {
                if (_alerted.Add(key + ":dead"))
                {
                    RaiseAlert($"{name} 사망/다운 (HP 0%)");
                }
            }
            else if (hp <= 25)
            {
                if (_alerted.Add(key))
                {
                    RaiseAlert($"{name} HP {hp}% — 위험");
                }
            }
            else if (hp >= 40)
            {
                _alerted.Remove(key);
                _alerted.Remove(key + ":dead");
            }
        }
        RefreshPartyView();
    }

    private void RefreshPartyView()
    {
        _partyPanel.SuspendLayout();
        foreach (Control c in _partyPanel.Controls)
        {
            c.Dispose();
        }
        _partyPanel.Controls.Clear();
        foreach (var (name, (hp, mp)) in _party.OrderByDescending(kv => kv.Value.Hp))
        {
            _partyPanel.Controls.Add(CreatePartyCard(name, hp, mp));
        }
        _partyPanel.ResumeLayout();
    }

    private Panel CreatePartyCard(string name, int hp, int mp)
    {
        var card = new Panel
        {
            Width = Math.Max(240, _partyPanel.ClientSize.Width - 26),
            Height = 56,
            BackColor = Color.White,
            Margin = new Padding(3),
        };
        card.Paint += (_, e) =>
        {
            var g = e.Graphics;
            using var border = new Pen(Color.FromArgb(228, 231, 236));
            g.DrawRectangle(border, 0, 0, card.Width - 1, card.Height - 1);
            using var nameFont = new Font("Malgun Gothic", 9.5F, FontStyle.Bold);
            using var nameBrush = new SolidBrush(Color.FromArgb(40, 40, 40));
            g.DrawString(name, nameFont, nameBrush, 10, 5);
            var barW = card.Width - 20;
            using var bg = new SolidBrush(Color.FromArgb(236, 238, 241));
            g.FillRectangle(bg, 10, 28, barW, 10);
            var pct = Math.Clamp(hp / 100.0, 0, 1);
            using var fill = new SolidBrush(hp <= 30 ? Color.IndianRed : hp <= 60 ? Color.Orange : Color.FromArgb(70, 180, 120));
            g.FillRectangle(fill, 10, 28, (int)(barW * pct), 10);
            using var small = new Font("Malgun Gothic", 8F);
            g.DrawString($"HP {hp}%  ·  MP {mp}%", small, Brushes.Gray, 10, 40);
        };
        return card;
    }

    private void AddGroupLoot(JsonElement r, DateTime time)    {
        var player = GetStr(r, "player");
        var item = GetStr(r, "name");
        if (player.Length == 0 || item.Length == 0)
        {
            return;
        }
        var count = GetLong(r, "count");
        var isMe = player == _myName;
        var isGroup = !isMe && _groupPlayers.Contains(player);
        var lvi = new ListViewItem(time.ToString("HH:mm:ss"));
        lvi.SubItems.Add(isMe ? "나" : isGroup ? "그룹원" : "기타");
        lvi.SubItems.Add(player);
        lvi.SubItems.Add(item);
        lvi.SubItems.Add(N(count));
        if (isGroup)
        {
            lvi.ForeColor = Color.FromArgb(0, 110, 90);
        }
        _groupLootView.Items.Insert(0, lvi);
        if (_groupLootView.Items.Count > 2000)
        {
            _groupLootView.Items.RemoveAt(_groupLootView.Items.Count - 1);
        }
    }

    // ===================================================================== 버프

    private void HandleBuff(JsonElement r, DateTime time)
    {
        var name = GetStr(r, "name");
        if (name.Length == 0)
        {
            return;
        }
        if (!GetBool(r, "on"))
        {
            RemoveBuff(name);
            return;
        }
        var iid = GetStr(r, "iid");
        var key = iid.Length > 0 ? iid : name;
        var val = GetLong(r, "val");
        var rate = GetDouble(r, "rate");
        var hasRemain = r.TryGetProperty("remain", out var remEl) && remEl.ValueKind == JsonValueKind.Number;
        var remain = hasRemain ? remEl.GetDouble() : 0;
        if (!_buffs.TryGetValue(key, out var st))
        {
            st = new BuffState { Name = name, Iid = iid };
            _buffs[key] = st;
        }
        st.Name = name;
        st.Iid = iid;
        st.Val = val;
        st.Rate = rate;
        st.LastSeen = time;
        var warnSec = (double)_numBuffWarn.Value * 60;
        // val 은 대부분 "남은 초"지만 일부 프레임은 다른 필드를 물어오므로 4시간 상한으로 거른다.
        const double maxTracked = 14_400;
        double? seconds = val >= 5 && val <= maxTracked
            ? val
            : hasRemain && remain > 1 && remain <= maxTracked
                ? remain
                : null;
        if (seconds is { } sec)
        {
            if (sec > warnSec)
            {
                st.Warnable = true;
                st.Warned = false;
            }
            st.Expires = time.AddSeconds(sec);
        }
    }

    private void RemoveBuff(string keyOrName)
    {
        if (keyOrName.Length == 0)
        {
            return;
        }
        if (_buffs.Remove(keyOrName))
        {
            return;
        }
        foreach (var k in _buffs.Where(kv => kv.Value.Name == keyOrName).Select(kv => kv.Key).ToList())
        {
            _buffs.Remove(k);
        }
    }

    private void OnUiTick()
    {
        if (IsDisposed)
        {
            return;
        }
        RebuildBuffView();
        UpdatePip();
        MaybeRefreshCommunityChat();
        UpdateTimers();
    }

    private void RebuildBuffView()
    {
        var now = DateTime.Now;
        var warnSec = (double)_numBuffWarn.Value * 60;
        _buffView.BeginUpdate();
        _buffView.Items.Clear();
        foreach (var (key, st) in _buffs.ToList())
        {
            if (st.Expires is null && (now - st.LastSeen).TotalMinutes > 60)
            {
                _buffs.Remove(key);
                continue;
            }
            if (st.Expires is { } e0 && (e0 - now).TotalSeconds <= 0)
            {
                _buffs.Remove(key);
                continue;
            }
            var remain = st.Expires is { } e ? (e - now).TotalSeconds : (double?)null;
            if (st.Warnable && !st.Warned && remain is { } r2 && r2 <= warnSec)
            {
                st.Warned = true;
                RaiseAlert($"{st.Name} 만료 {TimeSpan.FromSeconds(r2):mm\\:ss} 전");
            }
            var lvi = new ListViewItem(BuffIcon(st.Iid, st.Name));
            lvi.Tag = LoadItemIcon(st.Iid, st.Name);
            lvi.SubItems.Add(st.Name);
            if (st.Expires is { } exp)
            {
                lvi.SubItems.Add(remain is { } rr ? TimeSpan.FromSeconds(rr).ToString(@"mm\:ss") : "-");
                lvi.SubItems.Add(exp.ToString("HH:mm:ss"));
                lvi.ForeColor = remain is { } rv && rv <= warnSec
                    ? Color.Firebrick
                    : Color.FromArgb(0, 110, 110);
            }
            else
            {
                lvi.SubItems.Add("-");
                lvi.SubItems.Add("-");
                lvi.ForeColor = Color.FromArgb(90, 90, 90);
            }
            lvi.SubItems.Add(st.Val > 0 ? st.Val.ToString() : "-");
            lvi.SubItems.Add(st.Iid);
            _buffView.Items.Add(lvi);
        }
        _buffView.EndUpdate();
    }

    private void RefreshItemView()
    {
        _itemView.BeginUpdate();
        _itemView.Items.Clear();
        foreach (var st in _itemStates.Values.OrderByDescending(s => s.Updated))
        {
            var lvi = new ListViewItem(ItemIcon(st.Iid, st.Name));
            lvi.Tag = LoadItemIcon(st.Iid, st.Name);
            lvi.SubItems.Add(st.Name.Length > 0 ? st.Name : st.Iid);
            var qty = st.Cur is { } cu && st.Max is { } mx && mx > 0
                ? $"{N(cu)} / {N(mx)}"
                : st.Count is { } cnt
                    ? N(cnt)
                    : "-";
            lvi.SubItems.Add(qty);
            var unit = PriceFor(st.Name);
            lvi.SubItems.Add(unit is { } u ? N(u) : "-");
            var qtyValue = st.Count ?? 1;
            lvi.SubItems.Add(unit is { } u2 ? N(qtyValue * u2) : "-");
            lvi.SubItems.Add(st.Slot > 0 ? st.Slot.ToString(CultureInfo.InvariantCulture) : "-");
            lvi.SubItems.Add(st.Iid);
            lvi.SubItems.Add(N(st.Acquired));
            lvi.ForeColor = ItemColor(st.Iid);
            _itemView.Items.Add(lvi);
        }
        _itemView.EndUpdate();
        _lblItemKinds.Text = $"종류: {N(_itemStates.Count)}";
    }

    private readonly Dictionary<string, Image?> _iconCache = [];

    /// <summary>아이템 아이콘 PNG를 앱/icons 또는 %APPDATA%\AsgardHuntMeter\icons 에서 찾는다.
    /// 파일명은 내부ID(iFood06.png) 또는 한글명(구운빵.png). 없으면 null → 이모지 폴백.</summary>
    private Image? LoadItemIcon(string iid, string name)
    {
        var key = iid.Length > 0 ? iid : name;
        if (key.Length == 0)
        {
            return null;
        }
        if (_iconCache.TryGetValue(key, out var cached))
        {
            return cached;
        }
        foreach (var dir in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, "icons"),
                     Path.Combine(AppSettings.SettingsDir, "icons"),
                 })
        {
            foreach (var file in new[] { key + ".png", name + ".png" })
            {
                var path = Path.Combine(dir, file);
                if (!File.Exists(path))
                {
                    continue;
                }
                try
                {
                    using var fs = File.OpenRead(path);
                    using var src = Image.FromStream(fs);
                    var img = new Bitmap(src);
                    _iconCache[key] = img;
                    return img;
                }
                catch
                {
                }
            }
        }
        _iconCache[key] = null;
        return null;
    }

    private static string BuffIcon(string iid, string name)
    {
        var k = iid.Length > 0 ? iid : name;
        if (k.Contains("Food", StringComparison.OrdinalIgnoreCase) || k.Contains("Cook", StringComparison.OrdinalIgnoreCase))
        {
            return "🍞";
        }
        if (k.Contains("HP", StringComparison.OrdinalIgnoreCase) || k.Contains("Heal", StringComparison.OrdinalIgnoreCase))
        {
            return "❤️";
        }
        if (k.Contains("MP", StringComparison.OrdinalIgnoreCase) || k.Contains("Mana", StringComparison.OrdinalIgnoreCase))
        {
            return "💙";
        }
        if (k.Contains("Speed", StringComparison.OrdinalIgnoreCase) || k.Contains("Haste", StringComparison.OrdinalIgnoreCase))
        {
            return "💨";
        }
        if (k.Contains("Power", StringComparison.OrdinalIgnoreCase) || k.Contains("Attack", StringComparison.OrdinalIgnoreCase) || k.Contains("Damage", StringComparison.OrdinalIgnoreCase))
        {
            return "⚔️";
        }
        if (k.Contains("Guard", StringComparison.OrdinalIgnoreCase) || k.Contains("Shield", StringComparison.OrdinalIgnoreCase) || k.Contains("Defense", StringComparison.OrdinalIgnoreCase))
        {
            return "🛡️";
        }
        if (k.Contains("Exp", StringComparison.OrdinalIgnoreCase) || k.Contains("Gold", StringComparison.OrdinalIgnoreCase))
        {
            return "✨";
        }
        return "🔹";
    }

    private static string ItemIcon(string iid, string name)    {
        var k = iid.Length > 0 ? iid : name;
        if (k.StartsWith("iCashUse", StringComparison.Ordinal) || k.Contains("Potion", StringComparison.Ordinal))
        {
            return "🧪";
        }
        if (k.StartsWith("iFood", StringComparison.Ordinal))
        {
            return "🍞";
        }
        if (k.Contains("Stone", StringComparison.Ordinal) || k.Contains("Celestium", StringComparison.Ordinal))
        {
            return "💎";
        }
        if (k.StartsWith("iRune", StringComparison.Ordinal))
        {
            return "🔮";
        }
        if (k.Contains("FM_TICKET", StringComparison.Ordinal))
        {
            return "🎟";
        }
        if (k.Contains("SeriesUpgrade", StringComparison.Ordinal) || k.Contains("Dragon", StringComparison.Ordinal))
        {
            return "💠";
        }
        return "📦";
    }

    private static Color ItemColor(string iid)
    {
        if (iid.StartsWith("iCashUse", StringComparison.Ordinal))
        {
            return Color.FromArgb(170, 60, 60);
        }
        if (iid.StartsWith("iFood", StringComparison.Ordinal))
        {
            return Color.FromArgb(170, 110, 20);
        }
        if (iid.Contains("Stone", StringComparison.Ordinal))
        {
            return Color.FromArgb(40, 90, 160);
        }
        if (iid.StartsWith("iRune", StringComparison.Ordinal))
        {
            return Color.FromArgb(120, 60, 160);
        }
        return Color.FromArgb(50, 50, 50);
    }

    private void RefreshMobView()
    {
        _mobView.BeginUpdate();
        _mobView.Items.Clear();
        foreach (var (name, count) in _mobs)
        {
            var lvi = new ListViewItem(name.Length > 0 ? name : "(이름 미확인)");
            lvi.SubItems.Add(N(count));
            _mobView.Items.Add(lvi);
        }
        _mobView.EndUpdate();
        _lblMobs.Text = $"몹 종류: {N(_mobs.Count)}";
    }

    private void RefreshUseView()
    {
        _useView.BeginUpdate();
        _useView.Items.Clear();
        foreach (var (slot, count) in _useBySlot.OrderByDescending(kv => kv.Value))
        {
            var lvi = new ListViewItem("슬롯 " + slot);
            lvi.SubItems.Add(N(count));
            _useView.Items.Add(lvi);
        }
        _useView.EndUpdate();
    }

    private void RefreshInvView()
    {
        _invView.BeginUpdate();
        _invView.Items.Clear();
        foreach (var (name, iid, slot, count, cur, max) in _invRows)
        {
            var lvi = new ListViewItem(ItemIcon(iid, name));
            lvi.Tag = LoadItemIcon(iid, name);
            lvi.SubItems.Add(name.Length > 0 ? name : iid);
            lvi.SubItems.Add(slot.ToString(CultureInfo.InvariantCulture));
            lvi.SubItems.Add(count is { } c ? N(c) : "-");
            var chg = "-";
            var warn = false;
            if (cur is { } cu && max is { } mx && mx > 0)
            {
                var pct = cu * 100.0 / mx;
                chg = $"{N(cu)} / {N(mx)}  ({pct.ToString("0.#", CultureInfo.InvariantCulture)}%)";
                warn = pct <= RPotWarnPct * 2;
            }
            else if (count is { } c2 && IsFood(iid) && c2 <= FoodWarnDefault)
            {
                warn = true;
            }
            lvi.SubItems.Add(chg);
            lvi.ForeColor = warn ? Color.Firebrick : ItemColor(iid);
            _invView.Items.Add(lvi);
        }
        _invView.EndUpdate();
    }

    /// <summary>R포션 잔여량(5% 이하)·요리 수량(기본 20개 이하/0개) 알림. 회복 시 재무장.</summary>
    private void EvaluateAlerts()
    {
        foreach (var (name, iid, _, count, cur, max) in _invRows)
        {
            if (cur is { } cu && max is { } mx && mx > 0)
            {
                var pct = cu * 100.0 / mx;
                var key = "point:" + iid;
                if (pct <= RPotWarnPct && _alerted.Add(key))
                {
                    RaiseAlert($"{name} 잔여 {pct.ToString("0.#", CultureInfo.InvariantCulture)}% ({N(cu)}/{N(mx)}) — 충전하세요!");
                }
                else if (pct >= 10.0)
                {
                    _alerted.Remove(key);
                }
            }
            if (IsFood(iid) && count is { } c)
            {
                var key = "food:" + iid;
                if (c == 0)
                {
                    if (_alerted.Add(key + ":zero"))
                    {
                        RaiseAlert($"{name} 소진! (0개)");
                    }
                }
                else if (c <= FoodWarnDefault)
                {
                    if (_alerted.Add(key))
                    {
                        RaiseAlert($"{name} {N(c)}개 남음 — 보충하세요!");
                    }
                }
                else
                {
                    _alerted.Remove(key);
                    _alerted.Remove(key + ":zero");
                }
            }
        }
    }

    private static bool IsFood(string iid) => iid.StartsWith("iFood", StringComparison.Ordinal);

    private void RaiseAlert(string msg)
    {
        _statusLabel.Text = "⚠ " + msg;
        AppendSysRow("⚠ " + msg);
        try
        {
            _tray.ShowBalloonTip(4000, "Asgard Hunt Meter", msg, ToolTipIcon.Warning);
        }
        catch
        {
        }
        NotifyRemote(msg);
    }

    private void AppendSysRow(string msg)
    {
        var row = new EventRow(DateTime.Now, "sys", msg);
        _events.Add(row);
        if (_events.Count > MaxEvents)
        {
            _events.RemoveRange(0, _events.Count - MaxEvents);
        }
        AddLogRow(row);
        var chat = new ChatRow(DateTime.Now, "sys", 0, "시스템", "", msg, false, false, "", "");
        _chatRows.Add(chat);
        if (_chatRows.Count > MaxChatRows)
        {
            _chatRows.RemoveRange(0, _chatRows.Count - MaxChatRows);
        }
        if (ShouldShowChat(chat))
        {
            AddChatViewRow(chat, false);
        }
    }

    private void AppendStderr(string line)
    {
        _txtStderr.AppendText(line + Environment.NewLine);
        if (_txtStderr.Lines.Length > 1500)
        {
            var keep = _txtStderr.Lines.Skip(_txtStderr.Lines.Length - 1000).ToArray();
            _txtStderr.Lines = keep;
        }
    }

    private void UpdateStats()
    {
        _lblGold.Text = $"골드: {N(_gold)}";
        _lblGoldRate.Text = $"시간당: {NH(_gph)}";
        _lblExp.Text = $"EXP: {N(_exp)}";
        _lblExpRate.Text = $"시간당: {NH(_eph)}";
        _lblFame.Text = $"명성: {N(_fame)}";
        _lblKills.Text = $"킬: {N(_kills)}";
        _lblPace.Text = $"최근 5분 골드: {N(_paceGold)} ({NH(_paceGph)}/h)";
        _lblItems.Text = $"획득 누적: {N(_items)}";
        _lblItemMsgs.Text = $"획득 알림: {N(_itemMsgs)}";
        _lblSession.Text = "시간: " + TimeSpan.FromSeconds(_sessionSec).ToString(@"hh\:mm\:ss");
        _lblFrames.Text = $"프레임: {N(_framesS2c + _framesC2s)}";
        _lblKey.Text = "키: " + _keyState;
        UpdatePip();
    }

    // ===================================================================== 숫자/문자열 유틸

    private static string GetStr(JsonElement r, string name) =>
        r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    private static long GetLong(JsonElement r, string name)
    {
        if (r.TryGetProperty(name, out var v))
        {
            if (v.ValueKind == JsonValueKind.Number)
            {
                return v.GetInt64();
            }
            if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var n))
            {
                return n;
            }
        }
        return 0;
    }

    private static double GetDouble(JsonElement r, string name)
    {
        if (r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number)
        {
            return v.GetDouble();
        }
        return 0;
    }

    private static bool GetBool(JsonElement r, string name) =>
        r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static long? OptLong(JsonElement e) =>
        e.ValueKind == JsonValueKind.Number ? e.GetInt64() : null;

    private static string N(long v) => v.ToString("N0", CultureInfo.InvariantCulture);

    private static string NH(double v)
    {
        if (v >= 1_000_000_000)
        {
            return (v / 1_000_000_000).ToString("0.##", CultureInfo.InvariantCulture) + "B";
        }
        if (v >= 1_000_000)
        {
            return (v / 1_000_000).ToString("0.##", CultureInfo.InvariantCulture) + "M";
        }
        if (v >= 1_000)
        {
            return (v / 1_000).ToString("0.#", CultureInfo.InvariantCulture) + "K";
        }
        return v.ToString("0", CultureInfo.InvariantCulture);
    }
}
