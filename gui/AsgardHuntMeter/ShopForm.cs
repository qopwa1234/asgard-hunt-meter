using System.Drawing;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace AsgardHuntMeter;

/// <summary>커뮤니티 기여도 포인트로 배지/칭호/닉네임 색을 구매하는 창.</summary>
public sealed class ShopForm : Form
{
    private sealed class ShopItem
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public int Price { get; set; }
        public string Kind { get; set; } = "";
        public string Value { get; set; } = "";
        public bool Owned { get; set; }
    }

    private sealed class Leader
    {
        public string Nickname { get; set; } = "";
        public int Points { get; set; }
        public string Badge { get; set; } = "";
        public string Title { get; set; } = "";
        public string Color { get; set; } = "";
        public int Reports { get; set; }
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private readonly string _baseUrl;
    private readonly string _token;
    private readonly Action? _onChanged;
    private readonly Label _lblPoints = new();
    private readonly Label _lblStatus = new();
    private readonly ListView _items = new();
    private readonly ListView _leaders = new();
    private readonly Button _btnBuy = new();
    private readonly List<ShopItem> _catalog = [];

    public ShopForm(string baseUrl, string token, Action? onChanged = null)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _token = token;
        _onChanged = onChanged;

        Text = "기여도 상점";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(820, 620);
        MinimumSize = new Size(700, 520);
        Font = new Font("Malgun Gothic", 9F);
        BackColor = Color.FromArgb(246, 248, 251);

        var top = new Panel { Dock = DockStyle.Top, Height = 54, Padding = new Padding(12, 10, 12, 4) };
        _lblPoints.Text = "포인트: -";
        _lblPoints.AutoSize = true;
        _lblPoints.Font = new Font("Malgun Gothic", 11F, FontStyle.Bold);
        _lblPoints.Location = new Point(12, 16);
        top.Controls.Add(_lblPoints);
        var btnRefresh = new Button { Text = "새로고침", Location = new Point(700, 12), Width = 90 };
        btnRefresh.Click += async (_, _) => await ReloadAsync();
        top.Controls.Add(btnRefresh);
        top.Controls.Add(new Label
        {
            Text = "시세 제보 +10P · 아이콘 팩 +25P · 좋아요 받으면 +2P (가입 +5P)",
            Location = new Point(180, 20),
            AutoSize = true,
            ForeColor = Color.DimGray,
        });

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 330,
            SplitterWidth = 6,
            Panel1MinSize = 180,
            Panel2MinSize = 120,
        };
        _items.Dock = DockStyle.Fill;
        _items.View = View.Details;
        _items.FullRowSelect = true;
        _items.HideSelection = false;
        _items.MultiSelect = false;
        _items.Columns.Add("상품", 190);
        _items.Columns.Add("가격", 70, HorizontalAlignment.Right);
        _items.Columns.Add("상태", 70);
        _items.Columns.Add("설명", 430);
        _items.SelectedIndexChanged += (_, _) => UpdateBuyState();
        split.Panel1.Controls.Add(_items);

        var leaderTop = new Label
        {
            Text = "기여도 랭킹 TOP 10",
            Dock = DockStyle.Top,
            Height = 24,
            ForeColor = Color.DimGray,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(4, 0, 0, 0),
        };
        _leaders.Dock = DockStyle.Fill;
        _leaders.View = View.Details;
        _leaders.FullRowSelect = true;
        _leaders.HideSelection = false;
        _leaders.MultiSelect = false;
        _leaders.Columns.Add("#", 40);
        _leaders.Columns.Add("닉네임", 260);
        _leaders.Columns.Add("포인트", 90, HorizontalAlignment.Right);
        _leaders.Columns.Add("제보", 80, HorizontalAlignment.Right);
        split.Panel2.Controls.Add(_leaders);
        split.Panel2.Controls.Add(leaderTop);

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(12, 8, 12, 8) };
        _lblStatus.AutoSize = true;
        _lblStatus.ForeColor = Color.DimGray;
        _lblStatus.Location = new Point(12, 15);
        _lblStatus.Text = "불러오는 중...";
        bottom.Controls.Add(_lblStatus);
        _btnBuy.Text = "구매";
        _btnBuy.Width = 90;
        _btnBuy.Enabled = false;
        _btnBuy.Dock = DockStyle.Right;
        _btnBuy.Click += async (_, _) => await BuySelectedAsync();
        bottom.Controls.Add(_btnBuy);
        var btnClose = new Button { Text = "닫기", Width = 80, Dock = DockStyle.Right };
        btnClose.Click += (_, _) => Close();
        bottom.Controls.Add(btnClose);

        Controls.Add(split);
        Controls.Add(top);
        Controls.Add(bottom);
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        _lblStatus.Text = "불러오는 중...";
        _btnBuy.Enabled = false;
        try
        {
            using var shopReq = new HttpRequestMessage(HttpMethod.Get, _baseUrl + "/api/shop");
            if (!string.IsNullOrWhiteSpace(_token)) shopReq.Headers.Add("X-Token", _token);
            using var shopResp = await Http.SendAsync(shopReq);
            var shopText = await shopResp.Content.ReadAsStringAsync();
            using var shopDoc = JsonDocument.Parse(shopText);
            var root = shopDoc.RootElement;
            var points = root.TryGetProperty("points", out var p) ? p.GetInt32() : 0;
            _lblPoints.Text = $"보유 포인트: {points:N0}P";

            _catalog.Clear();
            if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in items.EnumerateArray())
                {
                    _catalog.Add(new ShopItem
                    {
                        Id = el.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                        Name = el.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "",
                        Description = el.TryGetProperty("description", out var de) ? de.GetString() ?? "" : "",
                        Price = el.TryGetProperty("price", out var pr) ? pr.GetInt32() : 0,
                        Kind = el.TryGetProperty("kind", out var ki) ? ki.GetString() ?? "" : "",
                        Value = el.TryGetProperty("value", out var va) ? va.GetString() ?? "" : "",
                        Owned = el.TryGetProperty("owned", out var ow) && ow.GetBoolean(),
                    });
                }
            }
            _items.BeginUpdate();
            _items.Items.Clear();
            foreach (var it in _catalog)
            {
                var lvi = new ListViewItem(it.Name) { Tag = it };
                lvi.SubItems.Add(it.Price.ToString("N0") + "P");
                lvi.SubItems.Add(it.Owned ? "보유" : "");
                lvi.SubItems.Add(it.Description);
                if (it.Owned) lvi.ForeColor = Color.Gray;
                _items.Items.Add(lvi);
            }
            _items.EndUpdate();

            var lbText = await Http.GetStringAsync(_baseUrl + "/api/leaderboard?limit=10");
            var leaders = JsonSerializer.Deserialize<List<Leader>>(lbText, JsonOpts) ?? [];
            _leaders.BeginUpdate();
            _leaders.Items.Clear();
            var rank = 0;
            foreach (var u in leaders)
            {
                rank++;
                var name = string.Join(" ", new[] { u.Badge, u.Title, u.Nickname }.Where(s => !string.IsNullOrWhiteSpace(s)));
                var lvi = new ListViewItem(rank.ToString());
                lvi.SubItems.Add(name);
                lvi.SubItems.Add(u.Points.ToString("N0") + "P");
                lvi.SubItems.Add(u.Reports.ToString());
                if (!string.IsNullOrWhiteSpace(u.Color))
                {
                    lvi.ForeColor = ParseColor(u.Color, lvi.ForeColor);
                }
                _leaders.Items.Add(lvi);
            }
            _leaders.EndUpdate();

            _lblStatus.Text = string.IsNullOrWhiteSpace(_token) ? "로그인하면 포인트를 사용할 수 있습니다." : "상품을 선택하세요.";
            UpdateBuyState();
        }
        catch (Exception ex)
        {
            _lblStatus.Text = "상점을 불러오지 못했습니다: " + ex.Message;
        }
    }

    private void UpdateBuyState()
    {
        var item = _items.SelectedItems.Count > 0 ? _items.SelectedItems[0].Tag as ShopItem : null;
        _btnBuy.Enabled = item is { Owned: false } && !string.IsNullOrWhiteSpace(_token);
        if (item is not null)
        {
            _lblStatus.Text = item.Owned
                ? "이미 보유한 상품입니다."
                : $"{item.Name} · {item.Price:N0}P";
        }
    }

    private async Task BuySelectedAsync()
    {
        if (_items.SelectedItems.Count == 0 || _items.SelectedItems[0].Tag is not ShopItem item)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(_token))
        {
            MessageBox.Show(this, "커뮤니티 로그인이 필요합니다.", "상점",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (MessageBox.Show(this, $"{item.Name}을(를) {item.Price:N0}P로 구매할까요?", "상점",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        _btnBuy.Enabled = false;
        _lblStatus.Text = "구매 처리 중...";
        try
        {
            var body = JsonSerializer.Serialize(new { id = item.Id });
            using var req = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/api/shop/buy")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            req.Headers.Add("X-Token", _token);
            using var resp = await Http.SendAsync(req);
            var text = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err))
            {
                MessageBox.Show(this, err.GetString() ?? "구매 실패", "상점",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _lblStatus.Text = "구매 실패";
                return;
            }
            MessageBox.Show(this, "구매 완료! 채팅/시세에 표시됩니다.", "상점",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            _onChanged?.Invoke();
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "구매 실패: " + ex.Message, "상점",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            _lblStatus.Text = "구매 실패";
        }
        finally
        {
            UpdateBuyState();
        }
    }

    private static Color ParseColor(string value, Color fallback)
    {
        try
        {
            return ColorTranslator.FromHtml(value);
        }
        catch
        {
            return fallback;
        }
    }
}
