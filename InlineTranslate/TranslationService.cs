using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace InlineTranslate;

record TranslationResult(string Translated, string SrcLang, string DestLang);

/// <summary>
/// Dịch EN↔VI qua 1 trong 3 nguồn (Settings.Provider):
///   google   – endpoint miễn phí translate.googleapis.com (hay bị 429 trên mạng công ty)
///   mymemory – API công khai api.mymemory.translated.net (không cần key, ~5000 ký tự/ngày)
///   azure    – Azure Translator chính thức (cần AzureKey + AzureRegion)
///   auto     – azure nếu có key; không thì google, lỗi → mymemory
/// </summary>
static class TranslationService
{
    public static Settings Config { get; set; } = new();

    private static readonly HttpClient _http = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
    })
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    // Sau khi Google trả 429, bỏ qua Google một lúc để khỏi tốn thời gian mỗi lần dịch
    private static DateTime _googleBlockedUntil = DateTime.MinValue;
    private static readonly TimeSpan GoogleCooldown = TimeSpan.FromMinutes(30);

    static TranslationService()
    {
        _http.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
            "AppleWebKit/537.36 (KHTML, like Gecko) " +
            "Chrome/120.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Add("Accept", "*/*");
    }

    // ── Public API ───────────────────────────────────────────────────────────

    public static async Task<TranslationResult?> TranslateAsync(string text)
    {
        string provider = (Config.Provider ?? "auto").Trim().ToLowerInvariant();
        bool hasAzure = !string.IsNullOrWhiteSpace(Config.AzureKey);

        try
        {
            switch (provider)
            {
                case "azure":    return await Run("azure",    AzureAsync,    text);
                case "google":   return await Run("google",   GoogleAsync,   text);
                case "mymemory": return await Run("mymemory", MyMemoryAsync, text);
            }

            // auto
            if (hasAzure) return await Run("azure", AzureAsync, text);

            // User không cho gửi sang MyMemory → chỉ dùng Google, lỗi thì báo lỗi
            if (!Config.AllowMyMemoryFallback) return await Run("google", GoogleAsync, text);

            if (DateTime.Now >= _googleBlockedUntil)
            {
                try { return await Run("google", GoogleAsync, text); }
                catch (Exception ex)
                {
                    if (ex is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests })
                        _googleBlockedUntil = DateTime.Now + GoogleCooldown;
                    Log.Write($"google failed ({Describe(ex)}) → fallback mymemory");
                }
            }
            return await Run("mymemory", MyMemoryAsync, text);
        }
        catch (Exception ex)
        {
            Log.Write($"translate failed: {Describe(ex)}");
            string msg = ex is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests }
                ? "Dịch vụ dịch đang giới hạn số lần gọi (HTTP 429). Thử lại sau ít phút."
                : $"Lỗi dịch: {ex.Message}";
            return new TranslationResult($"[{msg}]", "?", "?");
        }
    }

    private static async Task<TranslationResult> Run(
        string name, Func<string, Task<TranslationResult>> fn, string text)
    {
        var r = await fn(text);
        Log.Write($"provider={name} {r.SrcLang}->{r.DestLang}");
        return r with { Translated = Sanitize(r.Translated, text) };
    }

    /// <summary>
    /// Bản dịch là dữ liệu từ bên ngoài và có thể được dán thẳng vào ô đang gõ → làm sạch:
    /// bỏ ký tự điều khiển, không cho nhiều dòng hơn câu gốc (tránh "Enter" lạ trong ô dòng lệnh),
    /// và chặn độ dài bất thường.
    /// </summary>
    internal static string Sanitize(string translated, string source)
    {
        var sb = new StringBuilder(translated.Length);
        foreach (char c in translated.Replace("\r\n", "\n").Replace('\r', '\n'))
        {
            if (c == '\n' || c == '\t' || !char.IsControl(c)) sb.Append(c);
        }
        string s = sb.ToString().Trim();

        static int Lines(string t) { int n = 0; foreach (char c in t) if (c == '\n') n++; return n; }
        if (Lines(s) > Lines(source.Trim().Replace("\r\n", "\n").Replace('\r', '\n')))
            s = s.Replace('\n', ' ');

        int max = Math.Max(400, source.Length * 5);
        return s.Length > max ? s[..max] : s;
    }

    private static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";

    // ── Google (endpoint miễn phí, không key) ────────────────────────────────

    private static async Task<TranslationResult> GoogleAsync(string text)
    {
        // 1 request: auto-detect + dịch sang tiếng Việt
        var (translated, detected) = await GoogleOnceAsync(text, "auto", "vi");
        string dest = "vi";

        // Văn bản gốc là tiếng Việt → dịch lại sang tiếng Anh
        if (detected == "vi")
        {
            dest = "en";
            (translated, _) = await GoogleOnceAsync(text, "vi", dest);
        }
        return new TranslationResult(translated, detected, dest);
    }

    private static async Task<(string Translated, string Detected)> GoogleOnceAsync(
        string text, string src, string dest)
    {
        string url = "https://translate.googleapis.com/translate_a/single" +
                     $"?client=gtx&sl={src}&tl={dest}&dt=t&q={Uri.EscapeDataString(text)}";
        string json = await _http.GetStringAsync(url);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Google trả về: [[["translated","original",...],...], null, "detected_lang", ...]
        var sb = new StringBuilder();
        foreach (var chunk in root[0].EnumerateArray())
        {
            if (chunk.GetArrayLength() > 0 && chunk[0].ValueKind == JsonValueKind.String)
                sb.Append(chunk[0].GetString());
        }

        string detected = src == "auto" ? "en" : src;
        if (root.GetArrayLength() > 2 && root[2].ValueKind == JsonValueKind.String)
            detected = root[2].GetString() ?? detected;

        return (sb.ToString().Trim(), detected);
    }

    // ── MyMemory (API công khai, không key, tối đa 500 byte / request) ───────

    private const int MyMemoryMaxBytes = 450;

    private static async Task<TranslationResult> MyMemoryAsync(string text)
    {
        bool isVi   = LooksVietnamese(text);
        string src  = isVi ? "vi" : "en";
        string dest = isVi ? "en" : "vi";

        // Mỗi dòng/đoạn là 1 request → gọi song song (tối đa 4) cho nhanh
        var pieces = SplitByBytes(text, MyMemoryMaxBytes);
        using var gate = new System.Threading.SemaphoreSlim(4);
        var tasks = new List<Task<string>>();
        foreach (var piece in pieces)
            tasks.Add(MyMemoryPieceAsync(piece, src, dest, gate));
        string[] parts = await Task.WhenAll(tasks);

        var sb = new StringBuilder();
        for (int i = 0; i < pieces.Count; i++)
        {
            sb.Append(parts[i]);
            sb.Append(pieces[i].EndsWith('\n') ? "\n" : " ");
        }
        return new TranslationResult(sb.ToString().Trim(), src, dest);
    }

    private static async Task<string> MyMemoryPieceAsync(
        string piece, string src, string dest, System.Threading.SemaphoreSlim gate)
    {
        if (string.IsNullOrWhiteSpace(piece)) return "";

        await gate.WaitAsync();
        try
        {
            string url = "https://api.mymemory.translated.net/get" +
                         $"?q={Uri.EscapeDataString(piece.Trim())}&langpair={src}|{dest}";
            if (!string.IsNullOrWhiteSpace(Config.MyMemoryEmail))
                url += "&de=" + Uri.EscapeDataString(Config.MyMemoryEmail.Trim());

            string json = await _http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string status = root.TryGetProperty("responseStatus", out var st) ? st.ToString() : "200";
            string translated = root.GetProperty("responseData").GetProperty("translatedText").GetString() ?? "";

            if (status != "200" || translated.StartsWith("MYMEMORY WARNING", StringComparison.OrdinalIgnoreCase))
            {
                string details = root.TryGetProperty("responseDetails", out var d) ? d.ToString() : "";
                throw new InvalidOperationException(
                    $"MyMemory từ chối (status {status}): {(details.Length > 0 ? details : translated)}");
            }
            return WebUtility.HtmlDecode(translated);
        }
        finally { gate.Release(); }
    }

    /// <summary>Có ký tự riêng của tiếng Việt (ă â đ ê ô ơ ư hoặc dấu thanh) → coi là tiếng Việt.</summary>
    private static bool LooksVietnamese(string text)
    {
        int hits = 0, letters = 0;
        foreach (char c in text)
        {
            if (!char.IsLetter(c)) continue;
            letters++;
            if ("ăâđêôơưĂÂĐÊÔƠƯàáãèéìíòóõùúýĩũÀÁÃÈÉÌÍÒÓÕÙÚÝĨŨ".IndexOf(c) >= 0 || (c >= 'Ạ' && c <= 'ỹ'))
                hits++;
        }
        return letters > 0 && hits * 100 >= letters * 3; // ≥ 3% chữ cái là ký tự đặc trưng
    }

    /// <summary>Cắt văn bản theo câu/dòng thành các đoạn ≤ maxBytes (UTF-8).</summary>
    private static List<string> SplitByBytes(string text, int maxBytes)
    {
        var result  = new List<string>();
        var current = new StringBuilder();
        int curBytes = 0;

        void Flush()
        {
            if (current.Length > 0) { result.Add(current.ToString()); current.Clear(); curBytes = 0; }
        }

        foreach (string sentence in Regex.Split(text, @"(?<=[.!?…;:]\s)|(?<=\n)"))
        {
            // Câu quá dài → cắt tiếp theo từ
            foreach (string unit in Encoding.UTF8.GetByteCount(sentence) > maxBytes
                         ? Regex.Split(sentence, @"(?<=\s)")
                         : new[] { sentence })
            {
                int b = Encoding.UTF8.GetByteCount(unit);
                if (b == 0) continue;
                if (curBytes + b > maxBytes) Flush();
                current.Append(unit);
                curBytes += b;
                if (unit.EndsWith('\n')) Flush();
            }
        }
        Flush();
        return result;
    }

    // ── Azure Translator (API chính thức, cần key) ───────────────────────────

    private static async Task<TranslationResult> AzureAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(Config.AzureKey))
            throw new InvalidOperationException("Chưa cấu hình AzureKey trong settings.json");

        var (translated, detected) = await AzureOnceAsync(text, null, "vi");
        string dest = "vi";
        if (detected == "vi")
        {
            dest = "en";
            (translated, _) = await AzureOnceAsync(text, "vi", dest);
        }
        return new TranslationResult(translated, detected, dest);
    }

    private static async Task<(string Translated, string Detected)> AzureOnceAsync(
        string text, string? from, string to)
    {
        string url = "https://api.cognitive.microsofttranslator.com/translate?api-version=3.0&to=" + to;
        if (from != null) url += "&from=" + from;

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Add("Ocp-Apim-Subscription-Key", Config.AzureKey!.Trim());
        if (!string.IsNullOrWhiteSpace(Config.AzureRegion))
            req.Headers.Add("Ocp-Apim-Subscription-Region", Config.AzureRegion.Trim());
        req.Content = new StringContent(
            JsonSerializer.Serialize(new[] { new { Text = text } }), Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req);
        string json = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Azure Translator HTTP {(int)resp.StatusCode}: {json}", null, resp.StatusCode);

        using var doc = JsonDocument.Parse(json);
        var item = doc.RootElement[0];
        string translated = item.GetProperty("translations")[0].GetProperty("text").GetString() ?? "";
        string detected = from ?? "en";
        if (item.TryGetProperty("detectedLanguage", out var dl))
            detected = dl.GetProperty("language").GetString() ?? detected;

        return (translated.Trim(), detected);
    }
}
