using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using Windows.Graphics.Imaging;
using Windows.Media.Core;
using Windows.Media.Ocr;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;
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

    readonly Button btnSelect = new() { Text = "1. Выделить область экрана", Width = 380, Height = 36 };
    readonly Label lblRegion = new() { Text = "Область не выбрана", AutoSize = true };
    readonly Button btnStart = new() { Text = "2. Старт", Width = 380, Height = 36, Enabled = false };
    readonly ComboBox cmbVoice = new() { Width = 380, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly ComboBox cmbLang = new() { Width = 380, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly TrackBar trkRate = new() { Width = 380, Minimum = -5, Maximum = 8, Value = 1 };
    readonly CheckBox chkInvert = new() { Text = "Светлый текст на тёмном фоне (инвертировать)", AutoSize = true };
    readonly CheckBox chkTranslate = new() { Text = "Переводить на русский перед чтением", AutoSize = true };
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(5) };
    readonly PictureBox pic = new() { Width = 450, Height = 110, SizeMode = PictureBoxSizeMode.Zoom, BorderStyle = BorderStyle.FixedSingle };
    readonly TextBox txtSkip = new() { Width = 450, Height = 110, Multiline = true, ScrollBars = ScrollBars.Vertical };
    readonly TextBox txtLog = new() { Width = 450, Height = 130, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    readonly SpeechSynthesizer synth = new();
    readonly MediaPlayer player = new();
    readonly Queue<string> queue = new();
    readonly List<(string label, string? model, VoiceInformation? win)> voiceItems = new();
    string piperExe = "";
    readonly List<Windows.Globalization.Language> langs = new();
    readonly List<(string text, DateTime t)> spoken = new();
    readonly string skipFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenReader", "skip.txt");

    OcrEngine? engine;
    Rectangle region;
    bool running, busy, primed;
    bool speaking;
    TaskCompletionSource? current;
    List<string> prev = new();

    public MainForm()
    {
        Text = "Читалка области экрана";
        ClientSize = new Size(880, 480);
        TopMost = true;

        var left = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, AutoScroll = true, Padding = new Padding(10)
        };
        left.Controls.AddRange(new Control[]
        {
            btnSelect, lblRegion, btnStart,
            new Label { Text = "Голос:", AutoSize = true }, cmbVoice,
            new Label { Text = "Язык распознавания текста:", AutoSize = true }, cmbLang,
            new Label { Text = "Скорость речи:", AutoSize = true }, trkRate,
            chkInvert, chkTranslate
        });
        var right = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, AutoScroll = true, Padding = new Padding(10)
        };
        right.Controls.AddRange(new Control[]
        {
            new Label { Text = "Что видит программа:", AutoSize = true }, pic,
            new Label { Text = "Не читать строки со словами (по одному на строку):", AutoSize = true }, txtSkip,
            new Label { Text = "Что прочитано:", AutoSize = true }, txtLog
        });
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 410));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        table.Controls.Add(left, 0, 0);
        table.Controls.Add(right, 1, 0);
        Controls.Add(table);

        // Голоса Piper (качественные, офлайн): папка piper и voices рядом с программой
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        piperExe = Path.Combine(exeDir, "piper", "piper.exe");
        var voicesDir = Path.Combine(exeDir, "voices");
        if (File.Exists(piperExe) && Directory.Exists(voicesDir))
        {
            foreach (var m in Directory.GetFiles(voicesDir, "*.onnx").OrderBy(x => x))
                if (File.Exists(m + ".json"))
                    voiceItems.Add(("Piper (качественный): " + Path.GetFileNameWithoutExtension(m), m, null));
        }
        foreach (var v in SpeechSynthesizer.AllVoices)
            voiceItems.Add(($"Windows: {v.DisplayName} ({v.Language})", null, v));
        foreach (var it in voiceItems) cmbVoice.Items.Add(it.label);
        int vi = voiceItems.FindIndex(x => x.model != null);
        if (vi < 0) vi = voiceItems.FindIndex(x => x.win != null && x.win.Language.StartsWith("ru"));
        if (voiceItems.Count > 0) cmbVoice.SelectedIndex = vi >= 0 ? vi : 0;

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

        ApplyRate();
        trkRate.ValueChanged += (s, e) => ApplyRate();
        cmbVoice.SelectedIndexChanged += (s, e) => ApplyVoice();
        cmbLang.SelectedIndexChanged += (s, e) => engine = null;
        ApplyVoice();

        btnSelect.Click += async (s, e) => await SelectRegion();
        btnStart.Click += (s, e) => Toggle();
        timer.Tick += async (s, e) => await Cycle();
        FormClosing += (s, e) => { timer.Stop(); StopSpeech(); };

        Shown += (s, e) =>
        {
            if (!voiceItems.Any(v => v.model != null) && !voiceItems.Any(v => v.win != null && v.win.Language.StartsWith("ru")))
                MessageBox.Show("Не найден русский голос.\n\nУстанови его: Параметры Windows → Время и язык → Речь → Управление голосами → Добавить голоса → Русский. Затем перезапусти программу.", "Читалка");
            if (langs.Count == 0)
                MessageBox.Show("Не найдено языков распознавания текста.\n\nУстанови язык: Параметры Windows → Время и язык → Язык и регион → Добавить язык → Русский (с галочкой «Оптическое распознавание текста»), затем перезапусти программу.", "Читалка");
        };
    }

    void ApplyVoice()
    {
        try
        {
            if (cmbVoice.SelectedIndex >= 0 && voiceItems[cmbVoice.SelectedIndex].win != null)
                synth.Voice = voiceItems[cmbVoice.SelectedIndex].win!;
        }
        catch { }
    }

    void ApplyRate()
    {
        try { synth.Options.SpeakingRate = 1.0 + trkRate.Value * 0.1; }
        catch { }
    }

    void Enqueue(string text)
    {
        if (queue.Count > 6) queue.Clear(); // не копим очередь
        queue.Enqueue(text);
        if (!speaking) _ = SpeakLoop();
    }

    async Task SpeakLoop()
    {
        speaking = true;
        try
        {
            while (queue.Count > 0)
            {
                var text = queue.Dequeue();
                try
                {
                    var (stream, ctype) = await Synthesize(text);
                    try
                    {
                        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        current = done;
                        Windows.Foundation.TypedEventHandler<MediaPlayer, object> ended = (p, a) => done.TrySetResult();
                        Windows.Foundation.TypedEventHandler<MediaPlayer, MediaPlayerFailedEventArgs> failed = (p, a) => done.TrySetResult();
                        player.MediaEnded += ended;
                        player.MediaFailed += failed;
                        player.Source = MediaSource.CreateFromStream(stream, ctype);
                        player.Play();
                        await Task.WhenAny(done.Task, Task.Delay(30000));
                        player.MediaEnded -= ended;
                        player.MediaFailed -= failed;
                        player.Source = null;
                    }
                    finally { stream.Dispose(); }
                }
                catch (Exception ex) { Log("Ошибка голоса: " + ex.Message); }
            }
        }
        finally { speaking = false; }
    }

    async Task<(IRandomAccessStream stream, string type)> Synthesize(string text)
    {
        var item = cmbVoice.SelectedIndex >= 0 ? voiceItems[cmbVoice.SelectedIndex] : default;
        if (item.model != null)
        {
            var wav = await RunPiper(item.model, text);
            var ras = new InMemoryRandomAccessStream();
            using (var dw = new DataWriter(ras))
            {
                dw.WriteBytes(wav);
                await dw.StoreAsync();
                dw.DetachStream();
            }
            ras.Seek(0);
            return (ras, "audio/wav");
        }
        var s = await synth.SynthesizeTextToStreamAsync(text);
        return (s, s.ContentType);
    }

    async Task<byte[]> RunPiper(string model, string text)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "sr_" + Guid.NewGuid().ToString("N") + ".wav");
        var scale = (1.0 / (1.0 + trkRate.Value * 0.1)).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = piperExe,
            Arguments = $"--model \"{model}\" --output_file \"{tmp}\" --length_scale {scale}",
            WorkingDirectory = Path.GetDirectoryName(piperExe)!,
            RedirectStandardInput = true,
            StandardInputEncoding = new System.Text.UTF8Encoding(false),
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        await p.StandardInput.WriteLineAsync(text.Replace("\r", " ").Replace("\n", " "));
        p.StandardInput.Close();
        using var cts = new CancellationTokenSource(20000);
        await p.WaitForExitAsync(cts.Token);
        var bytes = await File.ReadAllBytesAsync(tmp);
        try { File.Delete(tmp); } catch { }
        return bytes;
    }

    void StopSpeech()
    {
        queue.Clear();
        try { player.Pause(); } catch { }
        current?.TrySetResult();
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
            primed = false; prev = new();
            timer.Start();
            btnStart.Text = "Стоп";
        }
        else
        {
            timer.Stop();
            StopSpeech();
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
            await HandleLines(lines);
        }
        catch (Exception ex) { Log("Ошибка: " + ex.Message); }
        finally { busy = false; }
    }

    async Task HandleLines(List<string> lines)
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
            await Say(l);
        }
        prev = cur;
    }

    async Task Say(string text)
    {
        var line = chkTranslate.Checked ? await Translate(text) : text;
        Log(line != text ? text + "  ->  " + line : line);
        Enqueue(line);
    }

    // Бесплатный неофициальный перевод Google. Если интернета нет - читает как есть.
    static async Task<string> Translate(string text)
    {
        try
        {
            var url = "https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl=ru&dt=t&q=" + Uri.EscapeDataString(text);
            var json = await http.GetStringAsync(url);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var sb = new System.Text.StringBuilder();
            foreach (var part in doc.RootElement[0].EnumerateArray())
                sb.Append(part[0].GetString());
            var r = sb.ToString().Trim();
            return r.Length > 0 ? r : text;
        }
        catch { return text; }
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
