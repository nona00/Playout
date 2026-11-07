using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
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

        if (logLevel == LogLevel.Information && category.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal))
            logLevel = LogLevel.Debug;

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
    private const int WmSetRedraw = 0x000B;
    private const int EmGetFirstVisibleLine = 0x00CE;
    private const int EmLineScroll = 0x00B6;
    private const int InitialTailBytes = 128 * 1024;
    private const int MaxBytesPerRead = 16 * 1024;
    private const int MaxVisibleCharacters = 400_000;
    private readonly RichTextBox _textBox = new();
    private readonly CheckBox _blackBackground = new() { Text = "Schwarzer Hintergrund", Checked = true, AutoSize = true };
    private readonly CheckBox _autoScroll = new()
    {
        Text = "Auto-Scroll: Ein",
        Checked = true,
        AutoSize = true,
        Appearance = Appearance.Button
    };
    private readonly ComboBox _levelFilter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 135 };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };
    private readonly BackgroundWorker _reader = new();
    private readonly LogReaderState _readerState;
    private readonly List<HighlightSpan> _highlightSpans = [];
    private readonly Queue<LogLine> _logLines = new();
    private int _firstVisibleHighlightSpan;
    private int _trimmedTextLength;
    private int _bufferedLogLength;
    private HighlightKind _lastLogKind = HighlightKind.Normal;
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
        toolbar.Controls.Add(_autoScroll);
        toolbar.Controls.Add(new Label { Text = "Typ:", AutoSize = true, Margin = new Padding(8, 8, 0, 0) });
        _levelFilter.Items.AddRange(["Alle", "Trace", "Debug", "Information", "Warning", "Error", "Critical"]);
        _levelFilter.SelectedIndex = 0;
        toolbar.Controls.Add(_levelFilter);
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
        _autoScroll.CheckedChanged += (_, _) =>
        {
            _autoScroll.Text = _autoScroll.Checked ? "Auto-Scroll: Ein" : "Auto-Scroll: Aus";
            if (_autoScroll.Checked)
                ScrollToEnd();
        };
        _levelFilter.SelectedIndexChanged += (_, _) => RebuildVisibleText();
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

        string displayText = text.Replace("\r\n", "\n").Replace("\n", "\r\n");
        StringBuilder visibleText = new();
        int lineStart = 0;
        while (lineStart < displayText.Length)
        {
            int lineEnd = displayText.IndexOf('\n', lineStart);
            int lineAfter = lineEnd >= 0 ? lineEnd + 1 : displayText.Length;
            int contentEnd = lineEnd >= 0 ? lineEnd : displayText.Length;
            int levelStart = displayText.IndexOf('[', lineStart, contentEnd - lineStart);
            int levelEnd = levelStart >= 0 ? displayText.IndexOf(']', levelStart, contentEnd - levelStart) : -1;

            if (levelStart > lineStart && levelEnd > levelStart &&
                TryGetLevelKind(displayText.Substring(levelStart + 1, levelEnd - levelStart - 1), out HighlightKind levelKind))
                _lastLogKind = levelKind;

            string lineText = displayText[lineStart..lineAfter];
            _logLines.Enqueue(new LogLine(lineText, _lastLogKind));
            _bufferedLogLength += lineText.Length;
            if (IsVisibleLogKind(_lastLogKind))
                visibleText.Append(lineText);

            lineStart = lineAfter;
        }

        int removedVisibleLines = 0;
        while (_bufferedLogLength > MaxVisibleCharacters && _logLines.Count > 0)
        {
            LogLine removedLine = _logLines.Dequeue();
            _bufferedLogLength -= removedLine.Text.Length;
            if (IsVisibleLogKind(removedLine.Kind))
                removedVisibleLines++;
        }

        RemoveVisibleLines(removedVisibleLines);
        if (visibleText.Length > 0)
            AppendVisibleText(visibleText.ToString());
    }

    private bool IsVisibleLogKind(HighlightKind kind)
    {
        return _levelFilter.SelectedIndex == 0 ||
            Enum.TryParse((string?)_levelFilter.SelectedItem, out HighlightKind selectedKind) && selectedKind == kind;
    }

    private void RebuildVisibleText()
    {
        StringBuilder visibleText = new();
        foreach (LogLine line in _logLines)
        {
            if (IsVisibleLogKind(line.Kind))
                visibleText.Append(line.Text);
        }

        _textBox.Clear();
        _highlightSpans.Clear();
        _firstVisibleHighlightSpan = 0;
        _trimmedTextLength = 0;
        if (visibleText.Length > 0)
            AppendVisibleText(visibleText.ToString());
    }

    private void RemoveVisibleLines(int lineCount)
    {
        if (lineCount <= 0 || _textBox.TextLength == 0)
            return;

        int removeCount = _textBox.GetFirstCharIndexFromLine(lineCount);
        if (removeCount < 0 || removeCount > _textBox.TextLength)
            removeCount = _textBox.TextLength;
        if (removeCount <= 0)
            return;

        _textBox.Select(0, removeCount);
        _textBox.SelectedText = string.Empty;
        _trimmedTextLength += removeCount;
        TrimHighlightSpans();
    }

    private void AppendVisibleText(string displayText)
    {
        int firstNewLine = _textBox.GetLineFromCharIndex(_textBox.TextLength);
        IntPtr handle = _textBox.Handle;
        int firstVisibleLine = SendMessage(handle, EmGetFirstVisibleLine, IntPtr.Zero, IntPtr.Zero).ToInt32();
        int selectionStart = _textBox.SelectionStart;
        int selectionLength = _textBox.SelectionLength;
        SendMessage(handle, WmSetRedraw, IntPtr.Zero, IntPtr.Zero);
        _textBox.SuspendLayout();

        try
        {
            _textBox.SelectionStart = _textBox.TextLength;
            _textBox.SelectionLength = 0;
            _textBox.SelectionColor = GetColor(HighlightKind.Normal);
            _textBox.AppendText(displayText);
            AddHighlightSpans(displayText, firstNewLine);
            if (_autoScroll.Checked)
            {
                ScrollToEnd();
            }
            else
            {
                _textBox.Select(Math.Min(selectionStart, _textBox.TextLength), selectionLength);
                int currentFirstVisibleLine = SendMessage(handle, EmGetFirstVisibleLine, IntPtr.Zero, IntPtr.Zero).ToInt32();
                SendMessage(handle, EmLineScroll, IntPtr.Zero, new IntPtr(firstVisibleLine - currentFirstVisibleLine));
            }
        }
        finally
        {
            _textBox.ResumeLayout();
            SendMessage(handle, WmSetRedraw, new IntPtr(1), IntPtr.Zero);
            _textBox.Invalidate();
        }
    }

    private void ScrollToEnd()
    {
        _textBox.SelectionStart = _textBox.TextLength;
        _textBox.SelectionLength = 0;
        _textBox.ScrollToCaret();
    }

    private void TrimHighlightSpans()
    {
        while (_firstVisibleHighlightSpan < _highlightSpans.Count)
        {
            HighlightSpan span = _highlightSpans[_firstVisibleHighlightSpan];
            int end = span.Start + span.Length;
            if (end <= _trimmedTextLength)
                _firstVisibleHighlightSpan++;
            else if (span.Start < _trimmedTextLength)
            {
                _highlightSpans[_firstVisibleHighlightSpan] = span with
                {
                    Start = _trimmedTextLength,
                    Length = end - _trimmedTextLength
                };
                break;
            }
            else
                break;
        }

        if (_firstVisibleHighlightSpan >= 1024 && _firstVisibleHighlightSpan * 2 >= _highlightSpans.Count)
        {
            _highlightSpans.RemoveRange(0, _firstVisibleHighlightSpan);
            _firstVisibleHighlightSpan = 0;
        }
    }

    private void ApplyTheme()
    {
        _textBox.BackColor = _blackBackground.Checked ? Color.Black : Color.White;
        _textBox.ForeColor = _blackBackground.Checked ? Color.Gainsboro : Color.Black;
    }

    private void AddHighlightSpans(string text, int firstLine)
    {
        int firstNewSpan = _highlightSpans.Count;
        int lineStart = 0;
        int lineNumber = firstLine;

        while (lineStart < text.Length)
        {
            int richLineStart = _textBox.GetFirstCharIndexFromLine(lineNumber++);
            int lineEnd = text.IndexOf('\n', lineStart);
            if (lineEnd < 0)
                lineEnd = text.Length;
            int levelStart = lineEnd > lineStart ? text.IndexOf('[', lineStart, lineEnd - lineStart) : -1;
            int levelEnd = levelStart >= 0 ? text.IndexOf(']', levelStart, lineEnd - levelStart) : -1;
            if (richLineStart >= 0 && levelStart > lineStart && levelEnd > levelStart &&
                TryGetLevelKind(text.Substring(levelStart + 1, levelEnd - levelStart - 1), out HighlightKind levelKind))
            {
                int absoluteLineStart = _trimmedTextLength + richLineStart;
                int absoluteLevelStart = absoluteLineStart + levelStart - lineStart;
                _highlightSpans.Add(new HighlightSpan(absoluteLineStart, levelStart - lineStart, HighlightKind.Timestamp));
                _highlightSpans.Add(new HighlightSpan(absoluteLevelStart, levelEnd - levelStart + 1, levelKind));

                int categoryStart = levelEnd + 2;
                int categoryEnd = categoryStart < lineEnd
                    ? text.IndexOf(':', categoryStart, lineEnd - categoryStart)
                    : -1;
                if (categoryEnd > categoryStart)
                    _highlightSpans.Add(new HighlightSpan(
                        absoluteLineStart + categoryStart - lineStart,
                        categoryEnd - categoryStart,
                        HighlightKind.Category));
            }

            lineStart = lineEnd + 1;
        }

        RecolorSpans(firstNewSpan);
    }

    private void RecolorVisibleText()
    {
        _textBox.Select(0, _textBox.TextLength);
        _textBox.SelectionColor = GetColor(HighlightKind.Normal);
        RecolorSpans(0);
        _textBox.SelectionStart = _textBox.TextLength;
        _textBox.SelectionLength = 0;
        _textBox.SelectionColor = GetColor(HighlightKind.Normal);
    }

    private void RecolorSpans(int startIndex)
    {
        for (int i = Math.Max(startIndex, _firstVisibleHighlightSpan); i < _highlightSpans.Count; i++)
        {
            HighlightSpan span = _highlightSpans[i];
            int selectionStart = span.Start - _trimmedTextLength;
            if (span.Length <= 0 || selectionStart < 0 || selectionStart + span.Length > _textBox.TextLength)
                continue;

            _textBox.Select(selectionStart, span.Length);
            _textBox.SelectionColor = GetColor(span.Kind);
        }
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int message, IntPtr wParam, IntPtr lParam);

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

    private readonly record struct LogLine(string Text, HighlightKind Kind);
    private readonly record struct HighlightSpan(int Start, int Length, HighlightKind Kind);
}
