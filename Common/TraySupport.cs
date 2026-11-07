using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using Playout;

internal sealed class TrayFileLoggerProvider : ILoggerProvider
{
    private readonly string _path;

    public TrayFileLoggerProvider(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    }

    public ILogger CreateLogger(string categoryName) => new TrayFileLogger(_path, categoryName);

    public void Dispose()
    {
    }
}

internal sealed class TrayFileLogger(string path, string category) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        string message = formatter(state, exception);
        if (exception is not null)
            message += Environment.NewLine + exception;

        PlayoutLog.Write(path, logLevel.ToString(), category, message, exception);
    }
}

internal sealed class TrayHost : IDisposable
{
    private readonly ManualResetEventSlim _ready = new();
    private readonly Thread _thread;
    private TrayContext? _context;
    private Exception? _startupException;

    public TrayHost(string label, string applicationName, string logPath, Action stopApplication, string? doubleClickUrl = null)
    {
        _thread = new Thread(() => Run(label, applicationName, logPath, stopApplication, doubleClickUrl))
        {
            IsBackground = true,
            Name = $"{applicationName} tray"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();

        if (_startupException is not null)
            throw new InvalidOperationException($"Das Tray für {applicationName} konnte nicht gestartet werden.", _startupException);
    }

    public void Dispose()
    {
        _context?.RequestExit();
        if (_thread.IsAlive && Thread.CurrentThread != _thread)
            _thread.Join(TimeSpan.FromSeconds(5));
        _ready.Dispose();
    }

    private void Run(string label, string applicationName, string logPath, Action stopApplication, string? doubleClickUrl)
    {
        try
        {
            _context = new TrayContext(label, applicationName, logPath, stopApplication, doubleClickUrl);
            _ready.Set();
            Application.Run(_context);
        }
        catch (Exception exception)
        {
            _startupException = exception;
            _ready.Set();
        }
    }
}

internal sealed class TrayContext : ApplicationContext
{
    private readonly Form _dispatcher = new();
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly string _logPath;
    private readonly string? _doubleClickUrl;
    private readonly System.Windows.Forms.Timer _singleClickTimer = new() { Interval = 350 };
    private TrayLogWindow? _logWindow;

    public TrayContext(string label, string applicationName, string logPath, Action stopApplication, string? doubleClickUrl)
    {
        _logPath = logPath;
        _doubleClickUrl = doubleClickUrl;
        _dispatcher.ShowInTaskbar = false;
        _dispatcher.FormBorderStyle = FormBorderStyle.FixedToolWindow;
        _dispatcher.CreateControl();
        _ = _dispatcher.Handle;

        _menu = new ContextMenuStrip();
        _menu.Items.Add("Log anzeigen", null, (_, _) => ShowLog());
        if (_doubleClickUrl is not null)
            _menu.Items.Add("Webinterface öffnen", null, (_, _) => OpenWebInterface());
        _menu.Items.Add("Beenden", null, (_, _) => stopApplication());

        _icon = TrayIcon.Create(label[0], label == "E" ? Color.FromArgb(35, 91, 145) : Color.FromArgb(54, 126, 78));
        _notifyIcon = new NotifyIcon
        {
            Icon = _icon,
            Text = applicationName,
            ContextMenuStrip = _menu,
            Visible = true
        };
        _notifyIcon.MouseClick += (_, args) =>
        {
            if (args.Button != MouseButtons.Left)
                return;

            if (_doubleClickUrl is null)
                ShowLog();
            else
                _singleClickTimer.Start();
        };
        _notifyIcon.DoubleClick += (_, _) =>
        {
            if (_doubleClickUrl is null)
            {
                ShowLog();
                return;
            }

            _singleClickTimer.Stop();
            OpenWebInterface();
        };
        _singleClickTimer.Tick += (_, _) =>
        {
            _singleClickTimer.Stop();
            ShowLog();
        };
    }

    public void RequestExit()
    {
        if (!_dispatcher.IsDisposed && _dispatcher.IsHandleCreated)
            _dispatcher.BeginInvoke(new Action(ExitThread));
    }

    protected override void ExitThreadCore()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _icon.Dispose();
        _menu.Dispose();
        _singleClickTimer.Dispose();
        _logWindow?.Close();
        _dispatcher.Dispose();
        base.ExitThreadCore();
    }

    private void ShowLog()
    {
        if (_logWindow is null || _logWindow.IsDisposed)
            _logWindow = new TrayLogWindow(_logPath, $"{_notifyIcon.Text} – Log", _icon);

        if (!_logWindow.Visible)
            _logWindow.Show();
        _logWindow.WindowState = FormWindowState.Maximized;
        _logWindow.BringToFront();
        _logWindow.Activate();
    }

    private void OpenWebInterface()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_doubleClickUrl!) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            try
            {
                File.AppendAllText(_logPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [Error] TrayContext: Webinterface konnte nicht geöffnet werden: {exception}{Environment.NewLine}");
            }
            catch (IOException)
            {
            }
        }
    }
}

internal sealed class TrayLogWindow : Form
{
    private const int InitialTailBytes = 128 * 1024;
    private const int MaxBytesPerRead = 16 * 1024;
    private const int MaxVisibleCharacters = 400_000;
    private readonly RichTextBox _textBox = new();
    private readonly CheckBox _blackBackground = new() { Text = "Schwarzer Hintergrund", Checked = true, AutoSize = true };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };
    private readonly BackgroundWorker _reader = new();
    private readonly LogReaderState _readerState;
    private readonly List<HighlightSpan> _highlightSpans = [];
    private bool _closing;

    public TrayLogWindow(string logPath, string title, Icon? applicationIcon = null)
    {
        _readerState = new LogReaderState(logPath);
        Text = title;
        Icon = applicationIcon;
        WindowState = FormWindowState.Maximized;
        StartPosition = FormStartPosition.CenterScreen;

        FlowLayoutPanel toolbar = new()
        {
            Dock = DockStyle.Top,
            Height = 34,
            Padding = new Padding(8, 6, 0, 0)
        };
        toolbar.Controls.Add(_blackBackground);
        _textBox.Dock = DockStyle.Fill;
        _textBox.ReadOnly = true;
        _textBox.WordWrap = false;
        _textBox.DetectUrls = false;
        _textBox.HideSelection = false;
        _textBox.Font = new Font("Consolas", 10);
        Controls.Add(_textBox);
        Controls.Add(toolbar);

        _blackBackground.CheckedChanged += (_, _) =>
        {
            ApplyTheme();
            RecolorVisibleText();
        };
        _reader.DoWork += (_, args) => args.Result = ReadLogBatch((LogReaderState)args.Argument!);
        _reader.RunWorkerCompleted += (_, args) =>
        {
            if (!_closing && args.Error is null && args.Result is string text)
                AppendLogText(text);
        };
        _timer.Tick += (_, _) => RequestLogRead();
        FormClosed += (_, _) =>
        {
            _closing = true;
            _timer.Stop();
            _timer.Dispose();
        };
        ApplyTheme();
        RequestLogRead();
        _timer.Start();
    }

    private void RequestLogRead()
    {
        if (!_closing && !_reader.IsBusy)
            _reader.RunWorkerAsync(_readerState);
    }

    private static string ReadLogBatch(LogReaderState state)
    {
        try
        {
            if (!File.Exists(state.Path))
                return string.Empty;

            using FileStream stream = new(state.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long fileLength = stream.Length;
            if (!state.Initialized || fileLength < state.Offset)
            {
                state.Reset();
                if (fileLength > InitialTailBytes)
                {
                    stream.Position = fileLength - InitialTailBytes;
                    SkipPartialLine(stream);
                }

                state.Initialized = true;
                state.Offset = stream.Position;
            }
            else
            {
                stream.Position = state.Offset;
            }

            int byteCount = (int)Math.Min(Math.Max(0, stream.Length - stream.Position), MaxBytesPerRead);
            if (byteCount == 0)
                return string.Empty;

            byte[] bytes = new byte[byteCount];
            int bytesRead = stream.Read(bytes, 0, bytes.Length);
            state.Offset = stream.Position;
            char[] chars = new char[Encoding.UTF8.GetMaxCharCount(bytesRead)];
            int charsRead = state.Decoder.GetChars(bytes, 0, bytesRead, chars, 0, flush: false);
            string combined = state.PendingText + new string(chars, 0, charsRead);
            int lastNewLine = combined.LastIndexOf('\n');
            if (lastNewLine < 0)
            {
                state.PendingText = combined;
                return string.Empty;
            }

            state.PendingText = combined[(lastNewLine + 1)..];
            return combined[..(lastNewLine + 1)];
        }
        catch (IOException)
        {
            return string.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static void SkipPartialLine(Stream stream)
    {
        int value;
        while ((value = stream.ReadByte()) >= 0 && value != '\n')
        {
        }
    }

    private void AppendLogText(string text)
    {
        if (text.Length == 0)
            return;

        TrimVisibleText(text.Length);
        string currentText = _textBox.Text;
        int start = currentText.Length;
        _textBox.SuspendLayout();
        _textBox.SelectionStart = _textBox.TextLength;
        _textBox.SelectionLength = 0;
        _textBox.SelectionColor = GetColor(HighlightKind.Normal);
        _textBox.AppendText(text);
        string renderedText = _textBox.Text;
        AddHighlightSpans(renderedText[start..], start);
        _textBox.SelectionStart = _textBox.TextLength;
        _textBox.SelectionLength = 0;
        _textBox.SelectionColor = GetColor(HighlightKind.Normal);
        _textBox.ScrollToCaret();
        _textBox.ResumeLayout();
    }

    private void TrimVisibleText(int incomingLength)
    {
        int removeAtLeast = _textBox.TextLength + incomingLength - MaxVisibleCharacters;
        if (removeAtLeast <= 0)
            return;

        int newline = _textBox.Text.IndexOf('\n', removeAtLeast);
        int removeCount = newline >= 0 ? newline + 1 : removeAtLeast;
        _textBox.Select(0, removeCount);
        _textBox.SelectedText = string.Empty;

        for (int i = _highlightSpans.Count - 1; i >= 0; i--)
        {
            HighlightSpan span = _highlightSpans[i];
            int end = span.Start + span.Length;
            if (end <= removeCount)
                _highlightSpans.RemoveAt(i);
            else if (span.Start < removeCount)
                _highlightSpans[i] = span with { Start = 0, Length = end - removeCount };
            else
                _highlightSpans[i] = span with { Start = span.Start - removeCount };
        }
    }

    private void ApplyTheme()
    {
        _textBox.BackColor = _blackBackground.Checked ? Color.Black : Color.White;
        _textBox.ForeColor = _blackBackground.Checked ? Color.Gainsboro : Color.Black;
    }

    private void AddHighlightSpans(string text, int offset)
    {
        int firstNewSpan = _highlightSpans.Count;
        _highlightSpans.Add(new HighlightSpan(offset, text.Length, HighlightKind.Normal));
        int lineStart = 0;

        while (lineStart < text.Length)
        {
            int lineEnd = text.IndexOf('\n', lineStart);
            if (lineEnd < 0)
                lineEnd = text.Length;
            int levelStart = lineEnd > lineStart ? text.IndexOf('[', lineStart, lineEnd - lineStart) : -1;
            int levelEnd = levelStart >= 0 ? text.IndexOf(']', levelStart, lineEnd - levelStart) : -1;
            if (levelStart > lineStart && levelEnd > levelStart &&
                TryGetLevelKind(text.Substring(levelStart + 1, levelEnd - levelStart - 1), out HighlightKind levelKind))
            {
                _highlightSpans.Add(new HighlightSpan(offset + lineStart, levelStart - lineStart, HighlightKind.Timestamp));
                _highlightSpans.Add(new HighlightSpan(offset + levelStart, levelEnd - levelStart + 1, levelKind));

                int categoryStart = levelEnd + 2;
                int categoryEnd = categoryStart < lineEnd
                    ? text.IndexOf(':', categoryStart, lineEnd - categoryStart)
                    : -1;
                if (categoryEnd > categoryStart)
                    _highlightSpans.Add(new HighlightSpan(offset + categoryStart, categoryEnd - categoryStart, HighlightKind.Category));
            }

            lineStart = lineEnd + 1;
        }

        RecolorSpans(firstNewSpan);
    }

    private void RecolorVisibleText()
    {
        RecolorSpans(0);
        _textBox.SelectionStart = _textBox.TextLength;
        _textBox.SelectionLength = 0;
        _textBox.SelectionColor = GetColor(HighlightKind.Normal);
    }

    private void RecolorSpans(int startIndex)
    {
        for (int i = startIndex; i < _highlightSpans.Count; i++)
        {
            HighlightSpan span = _highlightSpans[i];
            if (span.Length <= 0 || span.Start + span.Length > _textBox.TextLength)
                continue;

            _textBox.Select(span.Start, span.Length);
            _textBox.SelectionColor = GetColor(span.Kind);
        }
    }

    private Color GetColor(HighlightKind kind) => kind switch
    {
        HighlightKind.Timestamp => _blackBackground.Checked ? Color.Gray : Color.DimGray,
        HighlightKind.Category => _blackBackground.Checked ? Color.LightSkyBlue : Color.DarkBlue,
        HighlightKind.Trace or HighlightKind.Debug => _blackBackground.Checked ? Color.DarkGray : Color.Gray,
        HighlightKind.Information => _blackBackground.Checked ? Color.LimeGreen : Color.Green,
        HighlightKind.Warning => _blackBackground.Checked ? Color.Yellow : Color.DarkGoldenrod,
        HighlightKind.Error or HighlightKind.Critical => Color.Red,
        _ => _blackBackground.Checked ? Color.Gainsboro : Color.Black
    };

    private static bool TryGetLevelKind(string level, out HighlightKind kind)
    {
        kind = level.Trim() switch
        {
            "Trace" => HighlightKind.Trace,
            "Debug" => HighlightKind.Debug,
            "Information" => HighlightKind.Information,
            "Warning" => HighlightKind.Warning,
            "Error" => HighlightKind.Error,
            "Critical" => HighlightKind.Critical,
            _ => HighlightKind.Normal
        };
        return kind != HighlightKind.Normal;
    }

    private sealed class LogReaderState
    {
        public string Path => _path;
        public bool Initialized { get; set; }
        public long Offset { get; set; }
        public string PendingText { get; set; } = string.Empty;
        public Decoder Decoder { get; private set; } = Encoding.UTF8.GetDecoder();
        private readonly string _path;

        public LogReaderState(string path)
        {
            _path = path;
        }

        public void Reset()
        {
            Offset = 0;
            PendingText = string.Empty;
            Decoder = Encoding.UTF8.GetDecoder();
        }
    }

    private enum HighlightKind
    {
        Normal,
        Timestamp,
        Category,
        Trace,
        Debug,
        Information,
        Warning,
        Error,
        Critical
    }

    private readonly record struct HighlightSpan(int Start, int Length, HighlightKind Kind);
}
