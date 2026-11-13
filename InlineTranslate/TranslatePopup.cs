using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace InlineTranslate;

/// <summary>
/// Borderless popup hiện bản dịch gần con trỏ chuột.
/// Hiện ngay ở trạng thái "Đang dịch…", có kết quả thì gọi SetResult().
/// Không chiếm focus của app đang dùng; tự đóng sau 7s, hoặc khi click ngoài / Escape.
/// </summary>
sealed class TranslatePopup : Form
{
    // ── Màu sắc (Catppuccin Mocha) ────────────────────────────────────────────
    private static readonly Color BgMain    = Color.FromArgb(30,  30,  46);   // #1E1E2E
    private static readonly Color BgHeader  = Color.FromArgb(49,  50,  68);   // #313244
    private static readonly Color BgFooter  = Color.FromArgb(24,  24,  37);   // #181825
    private static readonly Color FgMain    = Color.FromArgb(205, 214, 244);  // #CDD6F4
    private static readonly Color FgMuted   = Color.FromArgb(147, 153, 178);  // #9399B2
    private static readonly Color FgAccent  = Color.FromArgb(137, 180, 250);  // #89B4FA
    private static readonly Color Divider   = Color.FromArgb(69,  71,  90);   // #45475A
    private static readonly Color HoverRed  = Color.FromArgb(243, 139, 168);  // #F38BA8

    // ── Layout ────────────────────────────────────────────────────────────────
    private const int MaxWidth     = 440;
    private const int MinWidth     = 220;
    private const int PadH         = 14;
    private const int CornerRadius = 10;
    private const int AutoCloseSec = 7;
    private const int NoticeSec    = 3;

    // ── Fields ────────────────────────────────────────────────────────────────
    private readonly string   _original;
    private readonly bool     _isNotice;
    private string?           _translated;      // null = đang dịch
    private string            _srcLang  = "";
    private string            _destLang = "";
    private readonly int      _spawnX, _spawnY;
    private readonly System.Windows.Forms.Timer _timer;       // đếm ngược 1s
    private readonly System.Windows.Forms.Timer _watchTimer;  // click ngoài / Escape
    private int               _countdown;
    private Label?            _footerLabel;
    private bool              _mouseWasDown;

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    private const int VK_LBUTTON = 0x01, VK_RBUTTON = 0x02, VK_ESCAPE = 0x1B;

    // ── Constructors ──────────────────────────────────────────────────────────

    /// <summary>Popup dịch, mở ở trạng thái "Đang dịch…".</summary>
    public TranslatePopup(string original, int spawnX, int spawnY)
        : this(original, isNotice: false, spawnX, spawnY) { }

    /// <summary>Popup thông báo ngắn (vd chưa chọn văn bản), tự đóng sau 3s.</summary>
    public static TranslatePopup Notice(string message, int spawnX, int spawnY)
        => new(message, isNotice: true, spawnX, spawnY);

    private TranslatePopup(string text, bool isNotice, int spawnX, int spawnY)
    {
        _original  = text;
        _isNotice  = isNotice;
        _spawnX    = spawnX;
        _spawnY    = spawnY;
        _countdown = isNotice ? NoticeSec : AutoCloseSec;

        // Window props
        this.FormBorderStyle = FormBorderStyle.None;
        this.ShowInTaskbar   = false;
        this.TopMost         = true;
        this.BackColor       = BgMain;
        this.Opacity         = 0.96;
        this.StartPosition   = FormStartPosition.Manual;
        this.AutoSize        = false;
        this.DoubleBuffered  = true;

        // Rounded corners (Windows 11 / Win10 fallback)
        SetRoundedCorners();

        BuildUI();
        AdjustSizeAndPosition();

        // Đếm ngược: chạy ngay với thông báo; với bản dịch thì chờ SetResult()
        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += OnTick;
        if (isNotice) _timer.Start();

        // Popup không giữ focus nên không nhận Deactivate/KeyDown
        // → tự theo dõi chuột + Escape toàn cục
        _mouseWasDown = IsMouseDown();
        _watchTimer = new System.Windows.Forms.Timer { Interval = 50 };
        _watchTimer.Tick += OnWatch;
        _watchTimer.Start();
    }

    // Không cướp focus của app đang dùng (để user gõ / bấm phím tắt tiếp được)
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x00000080, WS_EX_TOPMOST = 0x00000008;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
            return cp;
        }
    }

    /// <summary>Điền kết quả dịch vào popup đang ở trạng thái "Đang dịch…".</summary>
    public void SetResult(string translated, string srcLang, string destLang)
    {
        if (IsDisposed) return;
        _translated = translated;
        _srcLang    = srcLang;
        _destLang   = destLang;

        SuspendLayout();
        foreach (Control c in Controls) c.Dispose();
        Controls.Clear();
        BuildUI();
        ResumeLayout(true);
        AdjustSizeAndPosition();

        _countdown = AutoCloseSec;
        _timer.Start();
    }

    // ── UI ────────────────────────────────────────────────────────────────────
    // Bố cục: 1 bảng dọc (mỗi hàng tự cao theo nội dung); header/footer là bảng 2 cột
    // [chữ bên trái | nút bên phải]. Không dùng Dock bên trong FlowLayoutPanel vì bị đè lên nhau.
    private TableLayoutPanel _main = null!;

    private void BuildUI()
    {
        _main = new TableLayoutPanel
        {
            Location     = Point.Empty,
            ColumnCount  = 1,
            AutoSize     = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize  = new Size(MinWidth, 0),
            BackColor    = BgMain,
            Padding      = Padding.Empty,
            Margin       = Padding.Empty,
        };
        _main.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        this.Controls.Add(_main);

        // ── Header ──────────────────────────────────────────────────────────
        string title = _isNotice            ? "Inline Translate"
                     : _translated is null  ? "Đang dịch…"
                     : $"{LangDisplay(_srcLang)}  →  {LangDisplay(_destLang)}";
        AddRow(MakeBar(BgHeader, new Padding(PadH, 6, PadH - 4, 6),
            MakeBarLabel(title, FgMain, BgHeader, new Font("Segoe UI", 9f, FontStyle.Bold)),
            MakeCloseButton()));
        AddRow(MakeDivider(Divider));

        if (_isNotice)
        {
            AddRow(MakeBodyLabel(_original));
            return;
        }

        // ── Original text ───────────────────────────────────────────────────
        string origShort = _original.Length > 140
            ? _original[..140].TrimEnd() + "…"
            : _original;
        AddRow(new Label
        {
            Text        = origShort,
            ForeColor   = FgMuted,
            BackColor   = BgMain,
            Font        = new Font("Segoe UI", 9f, FontStyle.Italic),
            MaximumSize = new Size(MaxWidth, 0),
            AutoSize    = true,
            Padding     = new Padding(PadH, 8, PadH, 6),
            Margin      = Padding.Empty,
            UseMnemonic = false,
        });
        AddRow(MakeDivider(BgHeader));

        // ── Translated text ──────────────────────────────────────────────────
        var body = MakeBodyLabel(_translated ?? "Đang dịch…");
        if (_translated is null) body.ForeColor = FgMuted;
        AddRow(body);

        if (_translated is null) return;

        // ── Footer ──────────────────────────────────────────────────────────
        _footerLabel = MakeBarLabel(FooterText(), FgMuted, BgFooter, new Font("Segoe UI", 8f));

        var copyBtn = MakeBarLabel("Copy", FgAccent, BgFooter, new Font("Segoe UI", 8f, FontStyle.Bold));
        copyBtn.Cursor = Cursors.Hand;
        copyBtn.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(_translated);
                copyBtn.Text = "✓ Đã copy";
                copyBtn.ForeColor = Color.FromArgb(166, 227, 161); // green
            }
            catch { }
        };
        AddRow(MakeBar(BgFooter, new Padding(PadH, 5, PadH, 6), _footerLabel, copyBtn));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private void AddRow(Control c)
    {
        _main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _main.Controls.Add(c, 0, _main.RowCount++);
    }

    /// <summary>Thanh ngang 2 cột: trái co giãn, phải vừa nội dung; kéo hết chiều rộng popup.</summary>
    private static TableLayoutPanel MakeBar(Color bg, Padding padding, Control left, Control right)
    {
        var bar = new TableLayoutPanel
        {
            ColumnCount  = 2,
            RowCount     = 1,
            AutoSize     = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor       = AnchorStyles.Left | AnchorStyles.Right,
            BackColor    = bg,
            Padding      = padding,
            Margin       = Padding.Empty,
        };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.Anchor  = AnchorStyles.Left;
        right.Anchor = AnchorStyles.Right;
        bar.Controls.Add(left,  0, 0);
        bar.Controls.Add(right, 1, 0);
        return bar;
    }

    private static Label MakeBarLabel(string text, Color fg, Color bg, Font font)
        => new Label
        {
            Text        = text,
            ForeColor   = fg,
            BackColor   = bg,
            Font        = font,
            AutoSize    = true,
            Margin      = new Padding(0, 0, 12, 0),
            UseMnemonic = false,
        };

    private static Label MakeBodyLabel(string text)
        => new Label
        {
            Text        = text,
            ForeColor   = FgMain,
            BackColor   = BgMain,
            Font        = new Font("Segoe UI", 11f),
            MaximumSize = new Size(MaxWidth, 0),
            AutoSize    = true,
            Padding     = new Padding(PadH, 10, PadH, 12),
            Margin      = Padding.Empty,
            UseMnemonic = false,
        };

    private Label MakeCloseButton()
    {
        var btn = new Label
        {
            Text      = "✕",
            ForeColor = FgMuted,
            BackColor = BgHeader,
            Font      = new Font("Segoe UI", 10f),
            AutoSize  = true,
            Cursor    = Cursors.Hand,
            Margin    = Padding.Empty,
            Padding   = new Padding(4, 0, 4, 0),
        };
        btn.Click      += (_, _) => SafeClose();
        btn.MouseEnter += (_, _) => btn.ForeColor = HoverRed;
        btn.MouseLeave += (_, _) => btn.ForeColor = FgMuted;
        return btn;
    }

    private static Panel MakeDivider(Color color)
        => new Panel
        {
            BackColor = color,
            Height    = 1,
            Anchor    = AnchorStyles.Left | AnchorStyles.Right,
            Margin    = Padding.Empty,
        };

    private static string LangDisplay(string code) => code switch
    {
        "vi"  => "Tiếng Việt",
        "en"  => "English",
        "zh"  => "Chinese",
        "ja"  => "Japanese",
        "ko"  => "Korean",
        "fr"  => "French",
        "de"  => "German",
        "th"  => "Thai",
        "auto"=> "Auto",
        _     => code.ToUpper(),
    };

    private string FooterText()
        => $"Esc / click ngoài để đóng  •  Tự đóng sau {_countdown}s";

    // ── Position ──────────────────────────────────────────────────────────────
    private void AdjustSizeAndPosition()
    {
        _main.PerformLayout();
        var size = _main.GetPreferredSize(Size.Empty);
        int w = size.Width, h = size.Height;
        this.ClientSize = size;
        _main.Size      = size;

        var screen = Screen.FromPoint(new Point(_spawnX, _spawnY)).WorkingArea;
        int x = _spawnX + 18;
        int y = _spawnY + 18;

        if (x + w > screen.Right  - 8) x = _spawnX - w - 8;
        if (y + h > screen.Bottom - 8) y = _spawnY - h - 8;

        x = Math.Max(screen.Left  + 4, x);
        y = Math.Max(screen.Top   + 4, y);

        this.Location = new Point(x, y);
    }

    // ── Rounded corners (Win11 DwmSetWindowAttribute, fallback: Region) ───────
    private void SetRoundedCorners()
    {
        try
        {
            // Windows 11: DWMWCP_ROUND = 2
            int attr  = 33; // DWMWA_WINDOW_CORNER_PREFERENCE
            int value = 2;  // DWMWCP_ROUND
            DwmSetWindowAttribute(this.Handle, attr, ref value, sizeof(int));
        }
        catch
        {
            // Windows 10 fallback: GraphicsPath region
            this.Load += (_, _) =>
            {
                var path = RoundedRect(this.ClientRectangle, CornerRadius);
                this.Region = new Region(path);
            };
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int attr, ref int pvAttr, int cbAttr);

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(r.X,             r.Y,              d, d, 180, 90);
        path.AddArc(r.Right - d,     r.Y,              d, d, 270, 90);
        path.AddArc(r.Right - d,     r.Bottom - d,     d, d,   0, 90);
        path.AddArc(r.X,             r.Bottom - d,     d, d,  90, 90);
        path.CloseFigure();
        return path;
    }

    // ── Timers ────────────────────────────────────────────────────────────────
    private void OnTick(object? sender, EventArgs e)
    {
        _countdown--;
        if (_footerLabel is { IsDisposed: false }) _footerLabel.Text = FooterText();
        if (_countdown <= 0) SafeClose();
    }

    private static bool IsMouseDown()
        => (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0 || (GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0;

    // Escape, hoặc click mới ở ngoài popup → đóng
    private void OnWatch(object? sender, EventArgs e)
    {
        if ((GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0) { SafeClose(); return; }

        bool down = IsMouseDown();
        if (down && !_mouseWasDown && !this.Bounds.Contains(Cursor.Position))
        {
            SafeClose();
            return;
        }
        _mouseWasDown = down;
    }

    private void SafeClose()
    {
        _timer.Stop();
        _watchTimer.Stop();
        if (!IsDisposed) this.Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _watchTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}
