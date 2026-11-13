using System;
using System.Drawing;
using System.Windows.Forms;

namespace InlineTranslate;

/// <summary>
/// Manages tray icon + lifetime của app.
/// App chạy nền, không có cửa sổ chính.
/// </summary>
sealed class AppContext : ApplicationContext
{
    private readonly NotifyIcon  _trayIcon;
    private readonly HotkeyHost  _hotkeyHost;
    private readonly Settings    _settings;
    private ToolStripMenuItem    _hotkeyItem = null!;
    private bool                 _dialogOpen;

    public AppContext()
    {
        _settings = Settings.Load();
        TranslationService.Config = _settings;
        if (!Hotkey.TryParse(_settings.Hotkey, out var hotkey))
        {
            Log.Write($"invalid hotkey in settings: '{_settings.Hotkey}', using default");
            hotkey = Hotkey.Default;
        }

        _trayIcon = new NotifyIcon
        {
            Icon             = SystemIcons.Information,
            Visible          = true,
            ContextMenuStrip = BuildMenu(),
        };
        _trayIcon.DoubleClick += (_, _) => ShowAbout();

        _hotkeyHost = new HotkeyHost(hotkey); // tạo handle + đăng ký phím tắt
        RefreshHotkeyText();
    }

    // ── Menu ─────────────────────────────────────────────────────────────────

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip { Font = new Font("Segoe UI", 9f) };

        var header = new ToolStripMenuItem("Inline Translate") { Enabled = false };
        header.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
        menu.Items.Add(header);
        menu.Items.Add(new ToolStripSeparator());
        _hotkeyItem = new ToolStripMenuItem("Đổi phím tắt…", null, (_, _) => ChangeHotkey());
        menu.Items.Add(_hotkeyItem);
        var replaceItem = new ToolStripMenuItem("Thay thế trực tiếp dòng đang gõ")
        {
            CheckOnClick = true,
            Checked      = _settings.ReplaceInInput,
        };
        replaceItem.CheckedChanged += (_, _) =>
        {
            _settings.ReplaceInInput = replaceItem.Checked;
            _settings.Save();
        };
        menu.Items.Add(replaceItem);

        var fallbackItem = new ToolStripMenuItem("Dùng MyMemory khi Google lỗi")
        {
            CheckOnClick = true,
            Checked      = _settings.AllowMyMemoryFallback,
            ToolTipText  = "Tắt: văn bản chỉ gửi tới Google Translate",
        };
        fallbackItem.CheckedChanged += (_, _) =>
        {
            _settings.AllowMyMemoryFallback = fallbackItem.Checked;
            _settings.Save();
        };
        menu.Items.Add(fallbackItem);
        menu.Items.Add("Hướng dẫn",   null, (_, _) => ShowAbout());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Thoát",        null, (_, _) => ExitApp());
        return menu;
    }

    // ── Hotkey ───────────────────────────────────────────────────────────────

    private void RefreshHotkeyText()
    {
        _trayIcon.Text   = $"Inline Translate  •  {_hotkeyHost.Hotkey} để dịch";
        _hotkeyItem.Text = $"Đổi phím tắt…  ({_hotkeyHost.Hotkey})";
    }

    private void ChangeHotkey()
    {
        if (_dialogOpen) return;
        _dialogOpen = true;
        _hotkeyHost.Suspend(); // để hộp thoại bắt được cả phím tắt hiện tại
        try
        {
            using var dlg = new HotkeyDialog(_hotkeyHost.Hotkey);
            if (dlg.ShowDialog() != DialogResult.OK || dlg.Result == _hotkeyHost.Hotkey)
                return;

            if (_hotkeyHost.SetHotkey(dlg.Result))
            {
                _settings.Hotkey = dlg.Result.ToString();
                _settings.Save();
            }
            else
            {
                MessageBox.Show(
                    $"Không thể đăng ký phím tắt {dlg.Result}.\n" +
                    "Có thể phím này đã bị Windows hoặc ứng dụng khác dùng.\n" +
                    $"Vẫn giữ phím cũ: {_hotkeyHost.Hotkey}.",
                    "Inline Translate", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        finally
        {
            _hotkeyHost.Resume();
            RefreshHotkeyText();
            _dialogOpen = false;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void ShowAbout()
    {
        MessageBox.Show(
            "Inline Translate v1.1\n\n" +
            "• Chọn văn bản bất kỳ\n" +
            $"• Nhấn  {_hotkeyHost.Hotkey}\n" +
            "• Bản dịch hiện ngay tại chỗ\n\n" +
            "EN ↔ VI  •  tự phát hiện ngôn ngữ\n\n" +
            "Đang gõ (không bôi đen) → bấm phím tắt để thay luôn\n" +
            "dòng có con trỏ bằng bản dịch.\n\n" +
            "Đổi phím tắt: chuột phải icon → \"Đổi phím tắt…\"\n\n" +
            "Dùng Google Translate (miễn phí, cần internet)",
            "Inline Translate",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private void ExitApp()
    {
        _trayIcon.Visible = false;
        _hotkeyHost.Dispose();
        Application.Exit();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _trayIcon.Dispose();
            _hotkeyHost.Dispose();
        }
        base.Dispose(disposing);
    }
}
