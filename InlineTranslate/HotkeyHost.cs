using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using System.Windows.Forms;

namespace InlineTranslate;

/// <summary>
/// Hidden message-only window that:
///   1. Registers a configurable global hotkey (default Alt+D) via RegisterHotKey
///   2. Receives WM_HOTKEY and triggers the translate flow
/// </summary>
sealed class HotkeyHost : Form
{
    // ── Win32 ─────────────────────────────────────────────────────────────────
    private const int WM_HOTKEY   = 0x0312;
    private const int HOTKEY_ID   = 9001;
    private const int MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll")] static extern bool RegisterHotKey  (IntPtr hWnd, int id, int mod, int vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")] static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    private const uint KEYEVENTF_KEYDOWN = 0x0000;
    private const uint KEYEVENTF_KEYUP   = 0x0002;
    private const byte VK_CONTROL        = 0x11;
    private const byte VK_C              = 0x43;

    private const byte VK_MENU           = 0x12; // Alt
    private const byte VK_SHIFT          = 0x10;
    private const byte VK_LWIN           = 0x5B;
    private const byte VK_RWIN           = 0x5C;

    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] static extern uint  GetClipboardSequenceNumber();

    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT lpPoint);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

    // ── State ─────────────────────────────────────────────────────────────────
    private volatile bool _busy = false;
    private Hotkey _hotkey;
    private bool   _registered;

    public Hotkey Hotkey => _hotkey;

    // ── Setup ─────────────────────────────────────────────────────────────────
    public HotkeyHost(Hotkey hotkey)
    {
        _hotkey = hotkey;
        this.ShowInTaskbar       = false;
        this.FormBorderStyle     = FormBorderStyle.None;
        this.Size                = new System.Drawing.Size(1, 1);
        this.WindowState         = FormWindowState.Minimized;

        // Form luôn ẩn (SetVisibleCore) nên Show() KHÔNG tạo handle
        // → phải tạo thủ công, nếu không RegisterHotKey không bao giờ chạy.
        this.CreateHandle();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (!Register())
        {
            MessageBox.Show(
                $"Không thể đăng ký phím tắt {_hotkey}.\n" +
                "Có thể phím này đã bị ứng dụng khác dùng.\n\n" +
                "Chuột phải icon ở khay hệ thống → \"Đổi phím tắt\" để chọn phím khác.",
                "Inline Translate", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private bool Register()
    {
        _registered = RegisterHotKey(this.Handle, HOTKEY_ID, _hotkey.Modifiers | MOD_NOREPEAT, (int)_hotkey.Key);
        Log.Write($"RegisterHotKey({_hotkey}) = {_registered}");
        return _registered;
    }

    private void Unregister()
    {
        if (_registered) UnregisterHotKey(this.Handle, HOTKEY_ID);
        _registered = false;
    }

    /// <summary>Tạm tắt phím tắt (khi đang mở hộp thoại đổi phím).</summary>
    public void Suspend() => Unregister();

    /// <summary>Bật lại phím tắt hiện tại.</summary>
    public bool Resume() => _registered || Register();

    /// <summary>Đổi phím tắt. Thất bại → giữ lại phím cũ và trả false.</summary>
    public bool SetHotkey(Hotkey hotkey)
    {
        var old = _hotkey;
        Unregister();
        _hotkey = hotkey;
        if (Register()) return true;
        _hotkey = old;
        Register();
        return false;
    }

    protected override void SetVisibleCore(bool value)
        => base.SetVisibleCore(false); // luôn ẩn

    // ── Message pump ─────────────────────────────────────────────────────────
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_ID)
        {
            if (!_busy)
                _ = HandleAsync();
        }
        base.WndProc(ref m);
    }

    // ── Core flow ─────────────────────────────────────────────────────────────
    private TranslatePopup? _popup;

    private async Task HandleAsync()
    {
        _busy = true;
        try
        {
            // Phím "vô hại" chen vào khi Alt/Win còn đang giữ: nếu không, app đích chỉ thấy
            // Alt nhấn rồi nhả (phím chính đã bị hotkey nuốt) → nhảy vào chế độ menu,
            // và Ctrl+C sau đó bị trượt.
            if (_hotkey.Alt || _hotkey.Win)
            {
                keybd_event(VK_MASK, 0, KEYEVENTF_KEYDOWN, UIntPtr.Zero);
                keybd_event(VK_MASK, 0, KEYEVENTF_KEYUP,   UIntPtr.Zero);
            }

            // 1. Lưu tọa độ chuột, đóng popup cũ
            GetCursorPos(out var pt);
            Log.Write($"--- hotkey; foreground={ForegroundInfo()}");
            ClosePopup();

            // 2. Lưu clipboard cũ
            string? oldClip = null;
            try { oldClip = Clipboard.GetText(); } catch { }

            // Dò song song: focus có đang ở ô nhập văn bản không
            var focusProbe = GetEditableFocusAsync();

            // Đợi user nhả phím tắt — nếu không app target nhận Ctrl+Alt+C (Teams, Chrome...)
            await WaitForModifiersReleasedAsync((int)_hotkey.Key, 800);

            // Ô mật khẩu → dừng ngay, không gửi phím, không đọc, không gửi gì ra ngoài
            var focus = await focusProbe;
            if (focus.IsPassword)
            {
                Log.Write("password field → skipped");
                _popup = TranslatePopup.Notice("Đây là ô mật khẩu nên Inline Translate không dịch.", pt.X, pt.Y);
                _popup.Show();
                return;
            }

            // 3. Copy đoạn đang bôi đen (thử lại 1 lần nếu Ctrl+C đầu bị trượt)
            uint seqStart = GetClipboardSequenceNumber();
            string selected = await CopyAsync(250);

            // Không copy được gì (không bôi đen):
            //   ô nhập (ô chat Teams, Notepad...) → lấy riêng DÒNG đang có con trỏ nhập
            //   chỗ khác → thử Ctrl+C lần nữa phòng khi lần đầu bị trượt
            AutomationElement? field = string.IsNullOrWhiteSpace(selected) ? focus.Field : null;
            if (string.IsNullOrWhiteSpace(selected) && field == null)
                selected = await CopyAsync(350);

            bool lineMode = false;
            TextPatternRange? lineRange = null;
            if (field != null)
            {
                lineMode = true;
                (selected, lineRange) = await SelectCaretLineAsync(field);
                if (lineRange == null)
                {
                    SelectLineByKeys();
                    await Task.Delay(60);
                    selected = await CopyAsync(500);
                }
            }

            // 4. Khôi phục clipboard (chỉ khi mình đã làm nó thay đổi)
            if (GetClipboardSequenceNumber() != seqStart && !string.IsNullOrEmpty(oldClip))
            {
                try { Clipboard.SetText(oldClip); } catch { }
            }

            IntPtr targetWindow = GetForegroundWindow();
            Log.Write($"copied len={selected.Length} lineMode={lineMode} foreground={ForegroundInfo()}");
            if (string.IsNullOrWhiteSpace(selected))
            {
                _popup = TranslatePopup.Notice(
                    lineMode
                        ? "Dòng đang đặt con trỏ chưa có chữ để dịch."
                        : "Chưa có văn bản nào được chọn.\nBôi đen đoạn cần dịch rồi bấm lại phím tắt.",
                    pt.X, pt.Y);
                _popup.Show();
                return;
            }
            selected = selected.Trim();
            if (selected.Length > 2000)
                selected = selected[..2000]; // giới hạn 2000 ký tự

            // 5. Đang gõ trong ô nhập (không bôi đen) → dịch rồi thay luôn nội dung, KHÔNG hiện popup.
            //    Còn lại → hiện popup ngay ở trạng thái "Đang dịch…", có kết quả thì cập nhật.
            bool replaceMode = lineMode && TranslationService.Config.ReplaceInInput;
            TranslatePopup? popup = null;
            if (!replaceMode)
            {
                popup = new TranslatePopup(selected, pt.X, pt.Y);
                _popup = popup;
                popup.Show();
            }

            var result = await TranslationService.TranslateAsync(selected);
            Log.Write($"translated: {result?.SrcLang}->{result?.DestLang} len={result?.Translated.Length}");
            if (result is null) { popup?.Close(); return; }

            if (replaceMode)
            {
                // Chỉ thay khi dịch thành công và user vẫn đang ở đúng cửa sổ + đúng ô nhập đó
                // (vd đã bấm sang chat khác trong lúc chờ → không được dán vào đó)
                bool ok = result.SrcLang != "?" && result.Translated.Length > 0;
                if (ok && GetForegroundWindow() == targetWindow && await IsStillFocusedAsync(field!))
                {
                    await ReplaceLineAsync(result.Translated, oldClip, lineRange);
                    Log.Write("replaced line");
                    return;
                }
                // Lỗi / đã chuyển cửa sổ → báo bằng popup thay vì dán nhầm chỗ
                popup = new TranslatePopup(selected, pt.X, pt.Y);
                _popup = popup;
                popup.Show();
            }

            if (popup is null || popup.IsDisposed) return; // user đã đóng trong lúc chờ
            popup.SetResult(result.Translated, result.SrcLang, result.DestLang);
            Log.Write("popup shown");
        }
        catch (Exception ex)
        {
            Log.Write($"ERROR {ex}");
        }
        finally
        {
            _busy = false;
        }
    }

    private void ClosePopup()
    {
        try { if (_popup is { IsDisposed: false }) _popup.Close(); } catch { }
        _popup = null;
    }

    /// <summary>Giả lập Ctrl+C rồi chờ clipboard đổi; trả "" nếu không có gì được copy.</summary>
    private static async Task<string> CopyAsync(int timeoutMs)
    {
        uint seqBefore = GetClipboardSequenceNumber();
        Tap(VK_CONTROL, VK_C);

        // Teams/Electron/WebView2 ghi clipboard chậm, async
        for (int waited = 0; waited < timeoutMs; waited += 25)
        {
            await Task.Delay(25);
            if (GetClipboardSequenceNumber() != seqBefore)
            {
                await Task.Delay(40); // để app ghi xong mọi format
                return await ReadClipboardTextAsync();
            }
        }
        return "";
    }

    // ── Dòng đang gõ trong ô nhập ─────────────────────────────────────────────

    /// <summary>
    /// Chọn (bôi đen) đoạn văn chứa con trỏ nhập trong ô đang focus qua UI Automation và trả về
    /// nội dung của nó. Range == null nếu app không hỗ trợ → dùng phím Home / Shift+End thay thế.
    /// </summary>
    private static async Task<(string Text, TextPatternRange? Range)> SelectCaretLineAsync(AutomationElement el)
    {
        var work = Task.Run<(string, TextPatternRange?)>(() =>
        {
            try
            {
                if (!el.TryGetCurrentPattern(TextPattern.Pattern, out var p)) return ("", null);
                var sel = ((TextPattern)p).GetSelection();
                if (sel.Length == 0) return ("", null);

                var range = sel[0].Clone();
                range.ExpandToEnclosingUnit(TextUnit.Paragraph);
                string text = range.GetText(-1);

                // Bỏ dấu xuống dòng ở cuối khỏi vùng chọn, để lúc dán không nuốt mất ngắt dòng
                for (int i = 0; i < 2 && (text.EndsWith('\n') || text.EndsWith('\r')); i++)
                {
                    range.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, -1);
                    text = range.GetText(-1);
                }

                if (!string.IsNullOrWhiteSpace(text)) range.Select();
                return (text, range);
            }
            catch (Exception ex)
            {
                Log.Write($"caret line via UIA failed: {ex.Message}");
                return ("", null);
            }
        });
        var done = await Task.WhenAny(work, Task.Delay(1000));
        return done == work ? work.Result : ("", null);
    }

    /// <summary>Chọn dòng chứa con trỏ bằng phím: Home rồi Shift+End.</summary>
    private static void SelectLineByKeys()
    {
        Tap(null, VK_HOME);
        Tap(VK_SHIFT, VK_END);
    }

    /// <summary>Thay dòng đang chọn trong ô nhập bằng text (chọn lại dòng rồi Ctrl+V).</summary>
    private static async Task ReplaceLineAsync(string text, string? oldClip, TextPatternRange? range)
    {
        await WaitForModifiersReleasedAsync(0, 500);
        try { Clipboard.SetText(text.Replace("\n", "\r\n")); } catch (ExternalException) { return; }

        // Chọn lại dòng phòng khi vùng chọn bị mất trong lúc chờ dịch
        bool reselected = false;
        if (range != null)
        {
            try { await Task.Run(() => range.Select()); reselected = true; }
            catch (Exception ex) { Log.Write($"reselect failed: {ex.Message}"); }
        }
        if (!reselected) SelectLineByKeys();
        await Task.Delay(60);

        Tap(VK_CONTROL, VK_V);
        await Task.Delay(300); // chờ app dán xong rồi mới trả clipboard cũ

        if (!string.IsNullOrEmpty(oldClip))
        {
            try { Clipboard.SetText(oldClip); } catch { }
        }
    }

    /// <summary>Ô nhập ban đầu có còn đang giữ focus không (so sánh định danh UI Automation).</summary>
    private static async Task<bool> IsStillFocusedAsync(AutomationElement field)
    {
        var check = Task.Run(() =>
        {
            try
            {
                var now = AutomationElement.FocusedElement;
                bool same = now != null && Automation.Compare(now, field);
                if (!same) Log.Write("focus moved to another control → not pasting");
                return same;
            }
            catch (Exception ex)
            {
                Log.Write($"focus re-check failed: {ex.Message}");
                return false;
            }
        });
        var done = await Task.WhenAny(check, Task.Delay(800));
        return done == check && check.Result;
    }

    /// <summary>
    /// Field = phần tử đang giữ focus nếu nó là ô nhập văn bản (null nếu không phải);
    /// IsPassword = focus đang ở ô mật khẩu.
    /// </summary>
    private static async Task<(AutomationElement? Field, bool IsPassword)> GetEditableFocusAsync()
    {
        try
        {
            var probe = Task.Run<(AutomationElement?, bool)>(() =>
            {
                try
                {
                    var el = AutomationElement.FocusedElement;
                    if (el != null && el.Current.IsPassword) return (null, true);
                    var type = el?.Current.ControlType;
                    bool editable = type == ControlType.Edit;

                    // Notepad (Win11) và các ô RichEdit báo là "Document" chứ không phải "Edit".
                    // Trang web / tin nhắn cũng là Document nhưng chỉ-đọc → phân biệt bằng IsReadOnly.
                    if (!editable && el != null && type == ControlType.Document)
                    {
                        if (el.TryGetCurrentPattern(ValuePattern.Pattern, out var vp))
                        {
                            editable = !((ValuePattern)vp).Current.IsReadOnly;
                        }
                        else if (el.TryGetCurrentPattern(TextPattern.Pattern, out var tp))
                        {
                            // Ô nhập nhiều dòng kiểu cũ không có ValuePattern → hỏi thuộc tính
                            // chỉ-đọc ngay tại vị trí con trỏ
                            var sel = ((TextPattern)tp).GetSelection();
                            editable = sel.Length > 0 &&
                                       sel[0].GetAttributeValue(TextPattern.IsReadOnlyAttribute) is false;
                        }
                    }

                    Log.Write($"focused control type={type?.ProgrammaticName} editable={editable}");
                    return (editable ? el : null, false);
                }
                catch (Exception ex)
                {
                    Log.Write($"focus probe failed: {ex.Message}");
                    return (null, false);
                }
            });
            var done = await Task.WhenAny(probe, Task.Delay(800));
            return done == probe ? probe.Result : (null, false);
        }
        catch { return (null, false); }
    }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    private static string ForegroundInfo()
    {
        try
        {
            var h = GetForegroundWindow();
            GetWindowThreadProcessId(h, out uint pid);
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            return $"{p.ProcessName}(0x{h.ToInt64():X})";
        }
        catch { return "?"; }
    }

    private static async Task WaitForModifiersReleasedAsync(int hotkeyVk, int timeoutMs)
    {
        static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
        for (int waited = 0; waited < timeoutMs; waited += 10)
        {
            if (!Down(VK_MENU) && !Down(VK_CONTROL) && !Down(VK_SHIFT) &&
                !Down(VK_LWIN) && !Down(VK_RWIN) && !Down(hotkeyVk))
                return;
            await Task.Delay(10);
        }
        // Hết timeout mà vẫn giữ phím bổ trợ → nhả giả lập để Ctrl+C không thành Ctrl+Alt+C...
        foreach (byte vk in new[] { VK_MENU, VK_SHIFT, VK_LWIN, VK_RWIN, VK_CONTROL })
            if (Down(vk))
                keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    // Clipboard có thể đang bị app khác giữ → thử lại vài lần
    private static async Task<string> ReadClipboardTextAsync()
    {
        for (int i = 0; i < 5; i++)
        {
            try { return Clipboard.GetText(); }
            catch (ExternalException) { await Task.Delay(30); }
        }
        return "";
    }

    // ── Giả lập phím qua keybd_event (không dùng SendKeys để tránh focus-shift) ──
    private const byte VK_A     = 0x41;
    private const byte VK_V     = 0x56;
    private const byte VK_HOME  = 0x24;
    private const byte VK_END   = 0x23;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const byte VK_MASK  = 0xE8; // mã phím không gán chức năng

    private static void Tap(byte? modifier, byte key)
    {
        // Home/End phải gửi kèm cờ "extended", nếu không Shift+End bị hiểu thành phím số
        uint ext = key is VK_HOME or VK_END ? KEYEVENTF_EXTENDEDKEY : 0;

        if (modifier is byte m) keybd_event(m, 0, KEYEVENTF_KEYDOWN, UIntPtr.Zero);
        keybd_event(key, 0, KEYEVENTF_KEYDOWN | ext, UIntPtr.Zero);
        Thread.Sleep(20);
        keybd_event(key, 0, KEYEVENTF_KEYUP | ext, UIntPtr.Zero);
        if (modifier is byte m2) keybd_event(m2, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────
    protected override void Dispose(bool disposing)
    {
        if (this.IsHandleCreated)
            Unregister();
        base.Dispose(disposing);
    }
}
