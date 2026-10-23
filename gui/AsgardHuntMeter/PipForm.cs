using System.Drawing;
using System.Windows.Forms;

namespace AsgardHuntMeter;

/// <summary>게임 중 항상 위에 뜨는 PiP(미니 오버레이). 마우스로 드래그해 위치를 옮긴다.</summary>
public sealed class PipForm : Form
{
    private readonly Label _lblTitle = new();
    private readonly Label _lblGold = new();
    private readonly Label _lblExp = new();
    private readonly Label _lblKills = new();
    private readonly Label _lblPot = new();
    private readonly Label _lblBuff = new();
    private readonly Label _lblTag = new();
    private readonly Panel _potBar = new();
    private readonly Button _btnClose = new();
    private readonly Button _btnGear = new();
    private double _potPct = -1;
    private Point _dragOffset;
    private bool _dragging;

    public event Action? SettingsRequested;

    public PipForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(26, 30, 36);
        Size = new Size(330, 178);
        Padding = new Padding(1);
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        Location = new Point(wa.Right - Width - 24, wa.Top + 24);

        _lblTitle.Text = "ASGARD HUNT";
        _lblTitle.ForeColor = Color.FromArgb(120, 220, 180);
        _lblTitle.Font = new Font("Malgun Gothic", 8.5F, FontStyle.Bold);
        _lblTitle.Location = new Point(12, 8);
        _lblTitle.AutoSize = true;

        _btnClose.Text = "×";
        _btnClose.Size = new Size(24, 22);
        _btnClose.Location = new Point(Width - 30, 4);
        _btnClose.FlatStyle = FlatStyle.Flat;
        _btnClose.FlatAppearance.BorderSize = 0;
        _btnClose.ForeColor = Color.Gainsboro;
        _btnClose.BackColor = Color.FromArgb(26, 30, 36);
        _btnClose.Click += (_, _) => Hide();
        _btnGear.Text = "⚙";
        _btnGear.Size = new Size(24, 22);
        _btnGear.Location = new Point(Width - 56, 4);
        _btnGear.FlatStyle = FlatStyle.Flat;
        _btnGear.FlatAppearance.BorderSize = 0;
        _btnGear.ForeColor = Color.Gainsboro;
        _btnGear.BackColor = Color.FromArgb(26, 30, 36);
        _btnGear.Click += (_, _) => SettingsRequested?.Invoke();

        _lblGold.Font = new Font("Malgun Gothic", 13F, FontStyle.Bold);
        _lblGold.ForeColor = Color.FromArgb(255, 206, 84);
        _lblGold.Location = new Point(12, 30);
        _lblGold.AutoSize = true;
        _lblGold.Text = "0/h";

        _lblExp.Font = new Font("Malgun Gothic", 10F, FontStyle.Bold);
        _lblExp.ForeColor = Color.FromArgb(120, 180, 255);
        _lblExp.Location = new Point(12, 62);
        _lblExp.AutoSize = true;
        _lblExp.Text = "EXP 0/h";

        _lblKills.Font = new Font("Malgun Gothic", 9F);
        _lblKills.ForeColor = Color.FromArgb(210, 214, 220);
        _lblKills.Location = new Point(12, 88);
        _lblKills.AutoSize = true;
        _lblKills.Text = "킬 0";

        _lblPot.Font = new Font("Malgun Gothic", 9F);
        _lblPot.ForeColor = Color.FromArgb(210, 214, 220);
        _lblPot.Location = new Point(150, 88);
        _lblPot.AutoSize = true;
        _lblPot.Text = "R포션 -";

        _potBar.Location = new Point(150, 108);
        _potBar.Size = new Size(160, 6);
        _potBar.BackColor = Color.FromArgb(60, 66, 74);
        _potBar.Paint += (_, e) =>
        {
            if (_potPct < 0)
            {
                return;
            }
            var w = (int)(_potBar.ClientSize.Width * Math.Clamp(_potPct / 100.0, 0, 1));
            var color = _potPct <= 5 ? Color.IndianRed : _potPct <= 20 ? Color.Orange : Color.FromArgb(80, 200, 140);
            using var b = new SolidBrush(color);
            e.Graphics.FillRectangle(b, 0, 0, w, _potBar.ClientSize.Height);
        };

        _lblBuff.Font = new Font("Malgun Gothic", 8.5F);
        _lblBuff.ForeColor = Color.FromArgb(180, 186, 196);
        _lblBuff.Location = new Point(12, 112);
        _lblBuff.AutoSize = true;
        _lblBuff.Text = "다음 만료: -";

        _lblTag.Font = new Font("Malgun Gothic", 8.5F);
        _lblTag.ForeColor = Color.FromArgb(140, 146, 156);
        _lblTag.Location = new Point(12, 140);
        _lblTag.AutoSize = true;
        _lblTag.Text = "";

        Controls.AddRange([_lblTitle, _btnClose, _btnGear, _lblGold, _lblExp, _lblKills, _lblPot, _potBar, _lblBuff, _lblTag]);

        MouseDown += OnDragStart;
        MouseMove += OnDragMove;
        MouseUp += (_, _) => _dragging = false;
        foreach (Control c in Controls)
        {
            if (c is Label)
            {
                c.MouseDown += OnDragStart;
                c.MouseMove += OnDragMove;
                c.MouseUp += (_, _) => _dragging = false;
            }
        }
    }

    private void OnDragStart(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
        {
            return;
        }
        _dragging = true;
        _dragOffset = e.Location;
    }

    private void OnDragMove(object? sender, MouseEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }
        var screen = PointToScreen(e.Location);
        Location = new Point(screen.X - _dragOffset.X, screen.Y - _dragOffset.Y);
    }

    public void ApplySettings(int opacityPct, int fontSize, bool topMost)
    {
        Opacity = Math.Clamp(opacityPct / 100.0, 0.3, 1.0);
        TopMost = topMost;
        _lblGold.Font = new Font("Malgun Gothic", Math.Clamp(fontSize + 3, 9, 20), FontStyle.Bold);
        _lblExp.Font = new Font("Malgun Gothic", Math.Clamp(fontSize, 8, 16), FontStyle.Bold);
        _lblKills.Font = new Font("Malgun Gothic", Math.Clamp(fontSize - 1, 7, 14));
        _lblPot.Font = new Font("Malgun Gothic", Math.Clamp(fontSize - 1, 7, 14));
        _lblBuff.Font = new Font("Malgun Gothic", Math.Clamp(fontSize - 1, 7, 14));
    }

    public void UpdateValues(string tag, long goldH, double expH, long kills, double? potPct, string nextBuff)
    {
        _lblGold.Text = $"{NH(goldH)} 골드/h";
        _lblExp.Text = $"EXP {NH(expH)}/h";
        _lblKills.Text = $"킬 {kills:N0}";
        _potPct = potPct ?? -1;
        _lblPot.Text = potPct is { } p ? $"R포션 {p:0.#}%" : "R포션 -";
        _potBar.Invalidate();
        _lblBuff.Text = "다음 만료: " + (nextBuff.Length > 0 ? nextBuff : "-");
        _lblTag.Text = tag.Length > 0 ? "사냥터: " + tag : "";
    }

    private static string NH(double v)
    {
        if (v >= 1_000_000_000)
        {
            return (v / 1_000_000_000).ToString("0.##") + "B";
        }
        if (v >= 1_000_000)
        {
            return (v / 1_000_000).ToString("0.##") + "M";
        }
        if (v >= 1_000)
        {
            return (v / 1_000).ToString("0.#") + "K";
        }
        return v.ToString("0");
    }
}
