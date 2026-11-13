# Inline Translate (.NET 8)

Dịch văn bản ngay trong ứng dụng. Chọn → `Alt+D` → xem bản dịch.

## Yêu cầu build
- Windows 10/11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

## Build

```bat
build.bat
```

Output: `dist\InlineTranslate.exe` — portable, **không cần cài .NET** trên máy đích.

## Cách dùng

1. Chạy `InlineTranslate.exe` → icon xuất hiện ở system tray
2. Chọn văn bản bất kỳ trong bất kỳ app nào
3. Nhấn `Alt+D` (hoặc phím tắt bạn đã đổi)
4. Popup dịch hiện gần con trỏ, tự đóng sau 7 giây

## Tính năng

| | |
|---|---|
| Hotkey | `Alt+D` mặc định — đổi được: chuột phải icon tray → **Đổi phím tắt…** |
| Ngôn ngữ | Auto-detect: EN→VI, VI→EN (và hơn 100 ngôn ngữ khác) |
| Ô đang gõ | Đang gõ trong ô nhập (ô chat, Notepad…), không bôi đen → bấm phím tắt → **dòng có con trỏ** được thay luôn bằng bản dịch. Tắt/bật ở menu tray |
| Popup | Rounded corners, dark theme, nút Copy |
| Đóng popup | Escape / click ngoài / tự đóng 7s |
| System tray | Double-click = hướng dẫn, chuột phải = menu Thoát |
| Dịch | Google Translate miễn phí; khi Google chặn (HTTP 429) tự chuyển sang MyMemory (tắt được ở menu tray: **Dùng MyMemory khi Google lỗi**). Có thể dùng Azure Translator bằng key trong `settings.json` |
| Admin | **Không cần** quyền Administrator |
| Portable | Chỉ 1 file .exe, copy sang máy khác dùng được |

## Tự khởi động cùng Windows

1. Nhấn `Win+R` → gõ `shell:startup` → Enter
2. Copy shortcut `InlineTranslate.exe` vào thư mục đó

## Cấu trúc code

```
InlineTranslate/
├── Program.cs           – Entry point
├── AppContext.cs        – System tray + app lifetime
├── HotkeyHost.cs        – Đăng ký Alt+D, nhận WM_HOTKEY, điều phối flow
├── TranslationService.cs– Gọi Google Translate API (HttpClient)
├── TranslatePopup.cs    – Borderless popup UI (WinForms)
└── InlineTranslate.csproj
```
