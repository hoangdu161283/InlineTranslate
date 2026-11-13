# Inline Translate — CLAUDE.md

Ứng dụng Windows dịch văn bản inline. Chọn text → `Alt+Q` → popup dịch hiện gần chuột.

## Stack
- **C# .NET 8**, WinForms, không dùng NuGet bên ngoài
- Google Translate unofficial API (không cần API key)
- Target: `win-x64`, self-contained single `.exe`

## Cấu trúc

```
InlineTranslate/
├── Program.cs             – Entry point [STAThread]
├── AppContext.cs          – ApplicationContext: system tray icon + menu Thoát
├── HotkeyHost.cs          – Hidden Form, RegisterHotKey(phím cấu hình được), WM_HOTKEY → flow
├── Settings.cs            – record Hotkey (parse/format) + settings.json ở %APPDATA%\InlineTranslate
├── HotkeyDialog.cs        – Hộp thoại bắt tổ hợp phím mới (menu tray → Đổi phím tắt…)
├── Log.cs                 – Log chẩn đoán ra %TEMP%\InlineTranslate.log
├── TranslationService.cs  – HttpClient → translate.googleapis.com, detect EN↔VI
├── TranslatePopup.cs      – Borderless popup: dark theme, rounded corners, timer 7s
└── InlineTranslate.csproj
```

## Flow chính (HotkeyHost.cs → HandleAsync)

```
Hotkey pressed (mặc định Alt+Q, cấu hình được)
  → chèn phím mask 0xE8 khi Alt còn giữ (tránh app đích vào chế độ menu vì thấy Alt nhấn/nhả đơn lẻ)
  → lưu cursor pos, đóng popup cũ, lưu clipboard cũ
  → đợi user nhả hết phím bổ trợ
  → CopyAsync: Ctrl+C, chờ clipboard sequence number đổi (thử lại 1 lần)
  → nếu không copy được gì VÀ focus đang ở ô nhập (GetEditableFocusAsync, UI Automation:
    Edit, hoặc Document không chỉ-đọc như Notepad) → "line mode": SelectCaretLineAsync dùng
    TextPattern lấy + bôi đen ĐOẠN chứa con trỏ nhập (fallback: phím Home, Shift+End rồi Ctrl+C)
  → khôi phục clipboard
  → không có text → TranslatePopup.Notice(...)
  → line mode + `ReplaceInInput` bật → dịch (không popup) → ReplaceLineAsync: chọn lại dòng, Ctrl+V,
    trả clipboard cũ. Dịch lỗi / user đã đổi cửa sổ → hiện popup thay vì dán.
  → còn lại → popup hiện ngay "Đang dịch…" → TranslateAsync → popup.SetResult(...)
```

Chẩn đoán: mọi bước ghi vào `%TEMP%\InlineTranslate.log`.

## Build

```bat
# Portable .exe (self-contained, ~15MB, không cần .NET trên máy đích)
build.bat

# Nhanh hơn để test trên máy dev (cần .NET cài sẵn)
cd InlineTranslate
dotnet build -c Release
# exe ở: bin\Release\net8.0-windows\InlineTranslate.exe
```

## Điểm cần lưu ý khi sửa

- **HotkeyHost**: form luôn ẩn nên `Show()` KHÔNG tạo handle — constructor phải gọi `CreateHandle()`, nếu không `RegisterHotKey` không bao giờ chạy (bug cũ: phím tắt lọt thẳng vào app đích); `_busy` flag tránh double-trigger; `Deactivate` event không dùng được cho HotkeyHost (hidden form) — chỉ dùng cho TranslatePopup
- **TranslatePopup**: không chiếm focus (`ShowWithoutActivation` + `WS_EX_NOACTIVATE`) nên KHÔNG nhận `Deactivate`/`KeyDown` — đóng khi click ngoài / Escape bằng timer 50ms đọc `GetAsyncKeyState`; `SetVisibleCore` override trên HotkeyHost để luôn ẩn
- **Clipboard**: phải chạy trên STA thread — đã đảm bảo qua `[STAThread]` ở Program.cs và `this.Invoke()`  trong HandleAsync
- **DwmSetWindowAttribute**: rounded corners Win11; fallback `Region` cho Win10
- **Google Translate**: endpoint `translate.googleapis.com/translate_a/single?client=gtx` — free, không cần key, giới hạn mềm ~500 req/giờ

## Muốn thêm tính năng

- **Đổi hotkey**: menu tray → "Đổi phím tắt…", hoặc sửa `Hotkey` trong `%APPDATA%\InlineTranslate\settings.json` (vd `"Ctrl+Alt+T"`); mặc định ở `Hotkey.Default`
- **Thêm ngôn ngữ**: sửa `LangDisplay()` trong `TranslatePopup.cs`
- **Lịch sử dịch**: thêm `List<TranslationResult>` static trong `TranslationService`, hiện qua menu tray
- **Cài đặt**: thêm `Settings.cs` dùng `System.Configuration` hoặc JSON file bên cạnh exe

## Nguồn dịch (TranslationService)

`Provider` trong `%APPDATA%\InlineTranslate\settings.json`: `auto` (mặc định) | `google` | `mymemory` | `azure`.

- `auto`: có `AzureKey` → Azure; không thì Google, lỗi (vd HTTP 429 trên mạng công ty) → MyMemory, và bỏ qua Google 30 phút.
- `mymemory`: API công khai, không key, ≤500 byte/request (đã tự cắt theo dòng/câu, gọi song song), ~5.000 ký tự/ngày (`MyMemoryEmail` → 50.000). Tự đoán VI/EN bằng ký tự có dấu.
- `azure`: cần `AzureKey` + `AzureRegion` (chưa test với key thật).

## Bảo mật (giữ nguyên khi sửa flow)

- Ô mật khẩu (`IsPassword`): dừng ngay, không gửi phím, không đọc, không gọi dịch.
- Trước khi dán bản dịch: phải còn đúng cửa sổ VÀ đúng ô nhập ban đầu (`IsStillFocusedAsync`); sai → hiện popup, không dán.
- Bản dịch là dữ liệu ngoài: luôn qua `TranslationService.Sanitize` (bỏ ký tự điều khiển, không nhiều dòng hơn câu gốc, chặn độ dài).
- `AllowMyMemoryFallback` (mặc định true, có mục trong menu tray): tắt → chế độ `auto` chỉ gửi tới Google.
- Log không được ghi nội dung văn bản, chỉ độ dài / tên app.
