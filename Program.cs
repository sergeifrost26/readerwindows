using System.Drawing;
using System.Drawing.Imaging;
using System.Speech.Synthesis;
using System.Windows.Forms;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace ScreenReader;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

// Затемнённый экран: выделяешь мышкой область
class SelectForm : Form
{
    public Rectangle Result;
    Point start;
    Rectangle rect;
    bool drag;

    public SelectForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = SystemInformation.VirtualScreen;
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Color.Black;
        Opacity = 0.35;
        Cursor = Cursors.Cross;
        DoubleBuffered = true;
        KeyPreview = true;
        Shown += (s, e) => Bounds = SystemInformation.VirtualScreen;
        KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) DialogResult = DialogResult.Cancel; };
        MouseDown += (s, e) => { start = e.Location; drag = true; };
        MouseMove += (s, e) => { if (drag) { rect = Norm(start, e.Location); Invalidate(); } };
        MouseUp += (s, e) =>
        {
            drag = false;
            if (rect.Width > 20 && rect.Height > 20)
            {
                Result = new Rectangle(PointToScreen(rect.Location), rect.Size);
                DialogResult = DialogResult.OK;
            }
            else DialogResult = DialogResult.Cancel;
        };
    }

    static Rectangle Norm(Point a, Point b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var f = new Font("Segoe UI", 16);
        e.Graphics.DrawString("Выдели область мышкой (Esc - отмена)", f, Brushes.White, 20, 20);
        if (rect.Width > 0)
        {
            using var p = new Pen(Color.Lime, 3);
            e.Graphics.DrawRectangle(p, rect);
        }
    }
}

class MainForm : Form
{
    static readonly string[] DefaultSkip =
    {
        "присоедин", "joined", "вошел", "вошёл", "зашел", "зашёл", "вступил", "с нами",
        "подарил", "подарок", "отправил", "поделил", "shared", "поставил лайк",
        "поставила лайк", "liked", "подписался на", "подписалась на", "followed"
    };

    readonly Button btnSelect = new() { Text = "1. Выделить область экрана", Width = 480, Height = 36 };
    readonly Label lblRegion = new() { Text = "Область не выбрана", AutoSize = true };
    readonly Button btnStart = new() { Text = "2. Старт", Width = 480, Height = 36, Enabled = false };
    readonly ComboBox cmbVoice = new() { Width = 480, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly ComboBox cmbLang = new() { Width = 480, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly TrackBar trkRate = new() { Width = 480, Minimum = -5, Maximum = 8, Value = 1 };
    readonly CheckBox chkInvert = new() { Text = "Светлый текст на тёмном фоне (инвертировать)", AutoSize = true };
    readonly PictureBox pic = new() { Width = 480, Height = 150, SizeMode = PictureBoxSizeMode.Zoom, BorderStyle = BorderStyle.FixedSingle };
    readonly TextBox txtSkip = new() { Width = 480, Height = 110, Multiline = true, ScrollBars = ScrollBars.Vertical };
    readonly TextBox txtLog = new() { Width = 480, Height = 150, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    readonly SpeechSynthesizer synth = new();
    readonly List<VoiceInfo> voices = new();
    readonly List<Windows.Globalization.Language> langs = new();
    readonly List<(string text, DateTime t)> spoken = new();
    readonly string skipFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenReader", "skip.txt");

    OcrEngine? engine;
    Rectangle region;
    bool running, busy, primed;
    int pending;
    List<string> prev = new();

    public MainForm()
    {
        Text = "Читалка области экрана";
        ClientSize = new Size(510, 830);
        TopMost = true;

        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, AutoScroll = true, Padding = new Padding(10)
        };
        flow.Controls.AddRange(new Control[]
        {
            btnSelect, lblRegion, btnStart,
            new Label { Text = "Голос:", AutoSize = true }, cmbVoice,
            new Label { Text = "Язык распознавания текста:", AutoSize = true }, cmbLang,
            new Label { Text = "Скорость речи:", AutoSize = true }, trkRate,
            chkInvert,
            new Label { Text = "Что видит программа:", AutoSize = true }, pic,
            new Label { Text = "Не читать строки со словами (по одному на строку):", AutoSize = true }, txtSkip,
            new Label { Text = "Что прочитано:", AutoSize = true }, txtLog
        });
        Controls.Add(flow);

        foreach (var v in synth.GetInstalledVoices().Where(v => v.Enabled).Select(v => v.VoiceInfo))
        {
            voices.Add(v);
            cmbVoice.Items.Add($"{v.Name} ({v.Culture.Name})");
        }
        int vi = voices.FindIndex(v => v.Culture.TwoLetterISOLanguageName == "ru");
        if (voices.Count > 0) cmbVoice.SelectedIndex = vi >= 0 ? vi : 0;

        foreach (var l in OcrEngine.AvailableRecognizerLanguages)
        {
            langs.Add(l);
            cmbLang.Items.Add(l.DisplayName);
        }
        int li = langs.FindIndex(l => l.LanguageTag.StartsWith("ru"));
        if (langs.Count > 0) cmbLang.SelectedIndex = li >= 0 ? li : 0;

        txtSkip.Text = File.Exists(skipFile)
            ? File.ReadAllText(skipFile)
            : string.Join(Environment.NewLine, DefaultSkip);
        txtSkip.TextChanged += (s, e) =>
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(skipFile)!);
                File.WriteAllText(skipFile, txtSkip.Text);
            }
            catch { }
        };

        synth.Rate = trkRate.Value;
        trkRate.ValueChanged += (s, e) => synth.Rate = trkRate.Value;
        cmbVoice.SelectedIndexChanged += (s, e) => ApplyVoice();
        cmbLang.SelectedIndexChanged += (s, e) => engine = null;
        ApplyVoice();
        synth.SpeakCompleted += (s, e) => pending = Math.Max(0, pending - 1);

        btnSelect.Click += async (s, e) => await SelectRegion();
        btnStart.Click += (s, e) => Toggle();
        timer.Tick += async (s, e) => await Cycle();
        FormClosing += (s, e) => { timer.Stop(); synth.SpeakAsyncCancelAll(); };

        Shown += (s, e) =>
        {
            if (langs.Count == 0)
                MessageBox.Show("Не найдено языков распознавания текста.\n\nУстанови язык: Параметры Windows → Время и язык → Язык и регион → Добавить язык → Русский (с галочкой «Оптическое распознавание текста»), затем перезапусти программу.", "Читалка");
        };
    }

    void ApplyVoice()
    {
        try { if (cmbVoice.SelectedIndex >= 0) synth.SelectVoice(voices[cmbVoice.SelectedIndex].Name); }
        catch { }
    }

    async Task SelectRegion()
    {
        if (running) Toggle();
        WindowState = FormWindowState.Minimized;
        await Task.Delay(300);
        using var f = new SelectForm();
        var r = f.ShowDialog();
        WindowState = FormWindowState.Normal;
        if (r == DialogResult.OK)
        {
            region = f.Result;
            lblRegion.Text = $"Область: {region.Width}×{region.Height} пикс.";
            btnStart.Enabled = true;
            using var bmp = Capture();
            pic.Image?.Dispose();
            pic.Image = (Image)bmp.Clone();
        }
    }

    void Toggle()
    {
        running = !running;
        if (running)
        {
            primed = false; prev = new(); pending = 0;
            timer.Start();
            btnStart.Text = "Стоп";
        }
        else
        {
            timer.Stop();
            synth.SpeakAsyncCancelAll();
            pending = 0;
            btnStart.Text = "2. Старт";
        }
    }

    Bitmap Capture()
    {
        var bmp = new Bitmap(region.Width, region.Height);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(region.Location, Point.Empty, region.Size);
        return bmp;
    }

    byte[] Prepare(Bitmap src)
    {
        int w = src.Width * 2, h = src.Height * 2;
        using var dst = new Bitmap(w, h);
        using (var g = Graphics.FromImage(dst))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            if (chkInvert.Checked)
            {
                var cm = new ColorMatrix(new float[][]
                {
                    new float[] { -1, 0, 0, 0, 0 },
                    new float[] { 0, -1, 0, 0, 0 },
                    new float[] { 0, 0, -1, 0, 0 },
                    new float[] { 0, 0, 0, 1, 0 },
                    new float[] { 1, 1, 1, 0, 1 }
                });
                using var ia = new ImageAttributes();
                ia.SetColorMatrix(cm);
                g.DrawImage(src, new Rectangle(0, 0, w, h), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
            }
            else g.DrawImage(src, 0, 0, w, h);
        }
        using var ms = new MemoryStream();
        dst.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    async Task Cycle()
    {
        if (!running || busy) return;
        busy = true;
        try
        {
            using var bmp = Capture();
            pic.Image?.Dispose();
            pic.Image = (Image)bmp.Clone();

            engine ??= cmbLang.SelectedIndex >= 0
                ? OcrEngine.TryCreateFromLanguage(langs[cmbLang.SelectedIndex])
                : OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine == null) { Log("Нет движка распознавания"); return; }

            var bytes = Prepare(bmp);
            using var ras = new InMemoryRandomAccessStream();
            using (var dw = new DataWriter(ras))
            {
                dw.WriteBytes(bytes);
                await dw.StoreAsync();
                dw.DetachStream();
            }
            ras.Seek(0);
            var dec = await BitmapDecoder.CreateAsync(ras);
            using var sb = await dec.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            var res = await engine.RecognizeAsync(sb);
            var lines = res.Lines.Select(l => l.Text.Trim()).Where(t => t.Length > 1).ToList();
            HandleLines(lines);
        }
        catch (Exception ex) { Log("Ошибка: " + ex.Message); }
        finally { busy = false; }
    }

    void HandleLines(List<string> lines)
    {
        var skip = txtSkip.Lines.Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 0).ToList();
        var cur = lines.Where(l => { var low = l.ToLowerInvariant(); return !skip.Any(s => low.Contains(s)); }).ToList();

        // Первый проход: запоминаем то, что уже на экране, но не читаем
        if (!primed) { primed = true; prev = cur; return; }

        // Новые строки = те, которых не было в прошлом кадре
        var unmatched = new List<string>(prev);
        var fresh = new List<string>();
        foreach (var l in cur)
        {
            int idx = unmatched.FindIndex(p => Similar(p, l) >= 0.8);
            if (idx >= 0) unmatched.RemoveAt(idx); else fresh.Add(l);
        }

        spoken.RemoveAll(x => (DateTime.Now - x.t).TotalSeconds > 30);
        foreach (var l in fresh)
        {
            if (spoken.Any(x => Similar(x.text, l) >= 0.85)) continue; // защита от ошибок распознавания
            spoken.Add((l, DateTime.Now));
            Say(l);
        }
        prev = cur;
    }

    void Say(string text)
    {
        Log(text);
        if (pending > 6) { synth.SpeakAsyncCancelAll(); pending = 0; }
        pending++;
        synth.SpeakAsync(text);
    }

    void Log(string line)
    {
        if (txtLog.TextLength > 8000) txtLog.Text = txtLog.Text[^4000..];
        txtLog.AppendText(line + Environment.NewLine);
    }

    static string Norm(string s) => new string(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    static double Similar(string x, string y)
    {
        var a = Norm(x); var b = Norm(y);
        if (a.Length == 0 || b.Length == 0) return 0;
        int n = a.Length, m = b.Length;
        var d = new int[n + 1, m + 1];
        for (int i = 0; i <= n; i++) d[i, 0] = i;
        for (int j = 0; j <= m; j++) d[0, j] = j;
        for (int i = 1; i <= n; i++)
            for (int j = 1; j <= m; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                                   d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return 1.0 - (double)d[n, m] / Math.Max(n, m);
    }
}
