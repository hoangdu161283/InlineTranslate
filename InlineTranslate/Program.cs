using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace InlineTranslate;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Write($"UNHANDLED {e.Exception}");

        // Dev: InlineTranslate.exe --render <thư mục> → xuất ảnh PNG các trạng thái popup để kiểm tra layout
        if (args.Length == 2 && args[0] == "--render")
        {
            RenderPopups(args[1]);
            return;
        }

        Application.Run(new AppContext());
    }

    private static void RenderPopups(string dir)
    {
        Directory.CreateDirectory(dir);
        const string shortText = "Local is free forever. AI Cloud is smarter when you need it.";
        const string longText  = "Dear All,\nMicrosoft is currently experiencing a global service disruption " +
                                 "affecting Exchange Online, SharePoint Online and Microsoft Copilot 365. " +
                                 "IT is closely monitoring the situation and will update you once it is resolved.";

        void Save(TranslatePopup p, string name)
        {
            p.Show();
            Application.DoEvents();
            using var bmp = new Bitmap(p.Width, p.Height);
            p.DrawToBitmap(bmp, new Rectangle(Point.Empty, p.Size));
            bmp.Save(Path.Combine(dir, name + ".png"));
            p.Close();
        }

        Save(new TranslatePopup(shortText, 200, 200), "1-loading");

        var r = new TranslatePopup(shortText, 200, 200);
        r.SetResult("Local miễn phí mãi mãi. AI Cloud thông minh hơn khi bạn cần.", "en", "vi");
        Save(r, "2-result");

        var l = new TranslatePopup(longText, 200, 200);
        l.SetResult("Kính gửi mọi người,\nMicrosoft hiện đang gặp sự cố gián đoạn dịch vụ toàn cầu ảnh hưởng đến " +
                    "Exchange Online, SharePoint Online và Microsoft Copilot 365. Bộ phận CNTT đang theo dõi sát " +
                    "tình hình và sẽ cập nhật khi sự cố được khắc phục.", "en", "vi");
        Save(l, "3-long");

        Save(TranslatePopup.Notice("Chưa có văn bản nào được chọn.\nBôi đen đoạn cần dịch rồi bấm lại phím tắt.", 200, 200), "4-notice");
    }
}
