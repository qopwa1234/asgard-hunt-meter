using System.Drawing;
using System.Windows.Forms;

namespace AsgardHuntMeter;

// GitHub Releases 기반 업데이트 확인/설치 UI.
public sealed partial class MainForm
{
    private Label _lblUpdateCurrent = null!, _lblUpdateStatus = null!;
    private Button _btnUpdateCheck = null!, _btnUpdateInstall = null!;
    private ProgressBar _barUpdate = null!;
    private TextBox _txtUpdateNotes = null!;
    private CheckBox _chkUpdateAuto = null!;
    private LinkLabel _lnkUpdateRelease = null!;
    private UpdateInfo? _pendingUpdate;
    private bool _updateCheckStarted;

    private void BuildUpdateTab()
    {
        var tab = new TabPage("🔄 업데이트");
        var top = new Panel { Dock = DockStyle.Top, Height = 108, Padding = new Padding(10, 10, 10, 4) };

        _lblUpdateCurrent = new Label
        {
            Location = new Point(10, 12),
            AutoSize = true,
            Font = new Font("Malgun Gothic", 10F, FontStyle.Bold),
            Text = $"현재 버전  v{UpdateService.CurrentVersionText}",
        };
        top.Controls.Add(_lblUpdateCurrent);

        _chkUpdateAuto = new CheckBox
        {
            Text = "시작 시 자동 확인",
            Location = new Point(250, 12),
            AutoSize = true,
            Checked = _settings.CheckUpdatesOnStart,
        };
        top.Controls.Add(_chkUpdateAuto);

        _btnUpdateCheck = new Button { Text = "업데이트 확인", Location = new Point(390, 8), Width = 110 };
        _btnUpdateCheck.Click += async (_, _) => await CheckForUpdatesAsync(silent: false);
        top.Controls.Add(_btnUpdateCheck);

        _btnUpdateInstall = new Button { Text = "다운로드 및 설치", Location = new Point(508, 8), Width = 130, Enabled = false };
        _btnUpdateInstall.Click += async (_, _) => await InstallUpdateAsync();
        top.Controls.Add(_btnUpdateInstall);

        _lblUpdateStatus = new Label
        {
            Location = new Point(10, 42),
            AutoSize = false,
            Width = 760,
            Height = 22,
            ForeColor = Color.DimGray,
            Text = "업데이트를 확인하려면 [업데이트 확인]을 누르세요.",
        };
        top.Controls.Add(_lblUpdateStatus);

        _barUpdate = new ProgressBar
        {
            Location = new Point(10, 70),
            Width = 400,
            Height = 18,
            Visible = false,
        };
        top.Controls.Add(_barUpdate);

        _lnkUpdateRelease = new LinkLabel
        {
            Text = "릴리스 페이지 열기",
            Location = new Point(430, 70),
            AutoSize = true,
        };
        _lnkUpdateRelease.LinkClicked += (_, _) => UpdateService.OpenReleasesPage();
        top.Controls.Add(_lnkUpdateRelease);

        _txtUpdateNotes = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.White,
            Font = new Font("Malgun Gothic", 9F),
            Text = "새 버전의 변경 사항이 여기에 표시됩니다.",
        };

        tab.Controls.Add(_txtUpdateNotes);
        tab.Controls.Add(top);
        _tabs.TabPages.Add(tab);
    }

    private void StartStartupUpdateCheck()
    {
        if (_updateCheckStarted || !_settings.CheckUpdatesOnStart)
        {
            return;
        }
        _updateCheckStarted = true;
        var timer = new System.Windows.Forms.Timer { Interval = 3000 };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            await CheckForUpdatesAsync(silent: true);
        };
        timer.Start();
    }

    private async Task CheckForUpdatesAsync(bool silent)
    {
        if (!silent)
        {
            _btnUpdateCheck.Enabled = false;
            _btnUpdateInstall.Enabled = false;
            _lblUpdateStatus.Text = "최신 릴리스를 확인하는 중...";
        }
        try
        {
            var info = await UpdateService.CheckAsync();
            if (info is null)
            {
                _pendingUpdate = null;
                _lblUpdateStatus.Text = $"최신 버전을 사용 중입니다 (v{UpdateService.CurrentVersionText})";
                _txtUpdateNotes.Text = "새 버전이 없습니다.";
                _btnUpdateInstall.Enabled = false;
                return;
            }

            _pendingUpdate = info;
            _lblUpdateStatus.Text = $"새 버전 v{info.Version} 사용 가능  ·  {UpdateService.FormatSize(info.Size)}";
            _txtUpdateNotes.Text = string.IsNullOrWhiteSpace(info.Body)
                ? $"v{info.Version} 업데이트가 준비되었습니다."
                : info.Body;
            _btnUpdateInstall.Enabled = true;
            if (silent)
            {
                _tray.ShowBalloonTip(5000, "Asgard Hunt Meter 업데이트",
                    $"v{info.Version}이(가) 나왔습니다. [업데이트] 탭에서 설치할 수 있습니다.",
                    ToolTipIcon.Info);
            }
        }
        catch (Exception ex)
        {
            _lblUpdateStatus.Text = "업데이트 확인 실패: " + ex.Message;
            if (!silent)
            {
                MessageBox.Show(this,
                    "업데이트 서버에 연결하지 못했습니다.\n\n" + ex.Message,
                    "업데이트 확인", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        finally
        {
            _btnUpdateCheck.Enabled = true;
        }
    }

    private async Task InstallUpdateAsync()
    {
        if (_pendingUpdate is not { } info)
        {
            return;
        }
        var confirm = MessageBox.Show(this,
            $"v{info.Version} 업데이트를 다운로드하고 설치할까요?\n\n" +
            $"파일: {info.AssetName}\n크기: {UpdateService.FormatSize(info.Size)}\n\n" +
            "설치 후 앱이 자동으로 재시작됩니다.",
            "업데이트 설치", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (confirm != DialogResult.Yes)
        {
            return;
        }

        _btnUpdateCheck.Enabled = false;
        _btnUpdateInstall.Enabled = false;
        _barUpdate.Visible = true;
        _barUpdate.Value = 0;
        try
        {
            var progress = new Progress<double>(p =>
            {
                _barUpdate.Value = Math.Clamp((int)p, 0, 100);
                _lblUpdateStatus.Text = $"다운로드 중... {p:0}%";
            });
            var zip = await UpdateService.DownloadAsync(info, progress);
            _lblUpdateStatus.Text = "다운로드 완료 — 업데이트 적용 중...";
            if (!UpdateService.ApplyUpdate(zip, info.Version, out var message))
            {
                MessageBox.Show(this,
                    "업데이트 적용 준비에 실패했습니다.\n\n" + message +
                    "\n\n릴리스 페이지에서 ZIP을 직접 받아 압축을 풀어 덮어쓸 수 있습니다.",
                    "업데이트 실패", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            MessageBox.Show(this, message, "업데이트", MessageBoxButtons.OK, MessageBoxIcon.Information);
            _exitRequested = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "업데이트 다운로드에 실패했습니다.\n\n" + ex.Message,
                "업데이트 실패", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _lblUpdateStatus.Text = "업데이트 실패: " + ex.Message;
        }
        finally
        {
            _btnUpdateCheck.Enabled = true;
            _btnUpdateInstall.Enabled = _pendingUpdate is not null;
            _barUpdate.Visible = false;
        }
    }
}
