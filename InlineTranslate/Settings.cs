using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows.Forms;

namespace InlineTranslate;

/// <summary>Tổ hợp phím tắt toàn cục, ví dụ "Alt+Q", "Ctrl+Shift+T", "F8".</summary>
sealed record Hotkey(bool Ctrl, bool Alt, bool Shift, bool Win, Keys Key)
{
    public static readonly Hotkey Default = new(false, true, false, false, Keys.Q);

    /// <summary>Cờ MOD_* cho RegisterHotKey.</summary>
    public int Modifiers =>
        (Alt ? 0x1 : 0) | (Ctrl ? 0x2 : 0) | (Shift ? 0x4 : 0) | (Win ? 0x8 : 0);

    public bool HasModifier => Ctrl || Alt || Shift || Win;

    /// <summary>Hợp lệ khi có ít nhất 1 phím bổ trợ, hoặc là phím F1..F24.</summary>
    public bool IsValid =>
        Key != Keys.None && !IsModifierKey(Key) &&
        (HasModifier || (Key >= Keys.F1 && Key <= Keys.F24));

    public static bool IsModifierKey(Keys k) => k is
        Keys.ControlKey or Keys.LControlKey or Keys.RControlKey or
        Keys.Menu or Keys.LMenu or Keys.RMenu or
        Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey or
        Keys.LWin or Keys.RWin;

    public override string ToString()
    {
        var parts = new List<string>();
        if (Ctrl)  parts.Add("Ctrl");
        if (Alt)   parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        if (Win)   parts.Add("Win");
        parts.Add(Key >= Keys.D0 && Key <= Keys.D9
            ? ((char)('0' + (Key - Keys.D0))).ToString()
            : Key.ToString());
        return string.Join("+", parts);
    }

    public static bool TryParse(string? s, out Hotkey hotkey)
    {
        hotkey = Default;
        if (string.IsNullOrWhiteSpace(s)) return false;

        bool ctrl = false, alt = false, shift = false, win = false;
        Keys key = Keys.None;
        foreach (var raw in s.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": ctrl  = true; continue;
                case "alt":                  alt   = true; continue;
                case "shift":                shift = true; continue;
                case "win": case "windows":  win   = true; continue;
            }
            if (key != Keys.None) return false; // chỉ được 1 phím chính
            if (raw.Length == 1 && char.IsDigit(raw[0]))
                key = Keys.D0 + (raw[0] - '0');
            else if (!Enum.TryParse(raw, ignoreCase: true, out key) || int.TryParse(raw, out _))
                return false;
        }

        var hk = new Hotkey(ctrl, alt, shift, win, key);
        if (!hk.IsValid) return false;
        hotkey = hk;
        return true;
    }
}

/// <summary>Cài đặt lưu tại %APPDATA%\InlineTranslate\settings.json.</summary>
sealed class Settings
{
    public string Hotkey { get; set; } = InlineTranslate.Hotkey.Default.ToString();

    /// <summary>Đang gõ trong ô nhập, không bôi đen → thay luôn nội dung bằng bản dịch.</summary>
    public bool ReplaceInInput { get; set; } = true;

    /// <summary>
    /// Provider = auto: khi Google lỗi (vd HTTP 429) có được tự chuyển sang MyMemory không.
    /// Tắt → văn bản chỉ gửi tới Google; Google lỗi thì báo lỗi.
    /// </summary>
    public bool AllowMyMemoryFallback { get; set; } = true;

    /// <summary>auto | google | mymemory | azure (xem TranslationService).</summary>
    public string Provider { get; set; } = "auto";

    /// <summary>Key Azure Translator (tùy chọn). Có key → "auto" sẽ dùng Azure.</summary>
    public string? AzureKey { get; set; }
    public string? AzureRegion { get; set; }

    /// <summary>Email (tùy chọn) gửi kèm MyMemory để nâng hạn mức 5.000 → 50.000 ký tự/ngày.</summary>
    public string? MyMemoryEmail { get; set; }

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "InlineTranslate", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception ex) { Log.Write($"settings load failed: {ex.Message}"); }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Log.Write($"settings save failed: {ex.Message}"); }
    }
}
