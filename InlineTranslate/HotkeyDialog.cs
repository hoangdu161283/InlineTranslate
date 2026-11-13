using System;
using System.Drawing;
using System.Windows.Forms;

namespace InlineTranslate;

/// <summary>Hộp thoại bắt tổ hợp phím mới: nhấn phím → hiện → Lưu.</summary>
sealed class HotkeyDialog : Form
{
    private readonly Label  _combo;
    private readonly Label  _hint;
    private readonly Button _ok;

    public Hotkey Result { get; private set; }

    public HotkeyDialog(Hotkey current)
    {
        Result = current;

        Text            = "Inline Translate – Đổi phím tắt";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        MinimizeBox     = false;
        ShowInTaskbar   = true;
        TopMost         = true;
        StartPosition   = FormStartPosition.CenterScreen;
        ClientSize      = new Size(360, 170);
        Font            = new Font("Segoe UI", 9f);

        Controls.Add(new Label
        {
            Text     = "Nhấn tổ hợp phím mới (ví dụ Ctrl+Shift+T, Alt+D, F8):",
            Location = new Point(16, 14),
            AutoSize = true,
        });

        _combo = new Label
        {
            Text        = current.ToString(),
            Font        = new Font("Segoe UI", 16f, FontStyle.Bold),
            TextAlign   = ContentAlignment.MiddleCenter,
            BorderStyle = BorderStyle.FixedSingle,
            Location    = new Point(16, 42),
            Size        = new Size(328, 48),
        };
        Controls.Add(_combo);

        _hint = new Label
        {
            Text      = "Cần ít nhất Ctrl / Alt / Shift kèm 1 phím, hoặc phím F1–F12.",
            ForeColor = SystemColors.GrayText,
            Location  = new Point(16, 96),
            Size      = new Size(328, 18),
        };
        Controls.Add(_hint);

        _ok = new Button { Text = "Lưu", Location = new Point(178, 128), Size = new Size(80, 28), TabStop = false };
        _ok.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        Controls.Add(_ok);

        var cancel = new Button { Text = "Hủy", Location = new Point(264, 128), Size = new Size(80, 28), TabStop = false };
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        Controls.Add(cancel);
    }

    // Bắt mọi phím (kể cả tổ hợp Alt) trước khi control con xử lý
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        var key = keyData & Keys.KeyCode;

        if (keyData == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); return true; }
        if (keyData == Keys.Enter)  { if (_ok.Enabled) { DialogResult = DialogResult.OK; Close(); } return true; }
        if (Hotkey.IsModifierKey(key)) return true;

        var hk = new Hotkey(
            (keyData & Keys.Control) != 0,
            (keyData & Keys.Alt)     != 0,
            (keyData & Keys.Shift)   != 0,
            false, key);

        _combo.Text = hk.ToString();
        if (hk.IsValid)
        {
            Result          = hk;
            _ok.Enabled     = true;
            _hint.ForeColor = SystemColors.GrayText;
        }
        else
        {
            _ok.Enabled     = false;
            _hint.ForeColor = Color.Firebrick;
        }
        return true;
    }
}
