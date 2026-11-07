using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;

internal sealed class TrayFileLoggerProvider : ILoggerProvider
{
    private readonly string _path;
    private readonly object _sync = new();

    public TrayFileLoggerProvider(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    }

    public ILogger CreateLogger(string categoryName) => new TrayFileLogger(_path, categoryName, _sync);

    public void Dispose()
    {
    }
}

internal sealed class TrayFileLogger(string path, string category, object sync) : ILogger
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

        lock (sync)
        {
            using FileStream stream = new(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using StreamWriter writer = new(stream);
            writer.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{logLevel}] {category}: {message}");
        }
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
            _logWindow = new TrayLogWindow(_logPath, $"{_notifyIcon.Text} – Log");

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
    private readonly RichTextBox _textBox = new();
    private readonly CheckBox _blackBackground = new() { Text = "Schwarzer Hintergrund", Checked = true, AutoSize = true };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };
    private readonly string _logPath;
    private string? _lastContent;

    public TrayLogWindow(string logPath, string title)
    {
        _logPath = logPath;
        Text = title;
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

        _blackBackground.CheckedChanged += (_, _) => RefreshLog(force: true);
        _timer.Tick += (_, _) => RefreshLog();
        FormClosed += (_, _) => _timer.Dispose();
        ApplyTheme();
        RefreshLog(force: true);
        _timer.Start();
    }

    private void RefreshLog(bool force = false)
    {
        string content;
        try
        {
            content = File.Exists(_logPath) ? File.ReadAllText(_logPath) : "Noch keine Logeinträge.";
        }
        catch (IOException)
        {
            return;
        }

        if (!force && content == _lastContent)
            return;

        _lastContent = content;
        _textBox.SuspendLayout();
        _textBox.Text = content;
        ApplySyntaxHighlighting(content);
        _textBox.SelectionStart = _textBox.TextLength;
        _textBox.SelectionLength = 0;
        _textBox.ScrollToCaret();
        _textBox.ResumeLayout();
    }

    private void ApplyTheme()
    {
        _textBox.BackColor = _blackBackground.Checked ? Color.Black : Color.White;
        _textBox.ForeColor = _blackBackground.Checked ? Color.Gainsboro : Color.Black;
    }

    private void ApplySyntaxHighlighting(string content)
    {
        ApplyTheme();
        Color normal = _blackBackground.Checked ? Color.Gainsboro : Color.Black;
        Color timestamp = _blackBackground.Checked ? Color.Gray : Color.DimGray;
        Color category = _blackBackground.Checked ? Color.LightSkyBlue : Color.DarkBlue;
        int lineStart = 0;

        while (lineStart < content.Length)
        {
            int lineEnd = content.IndexOf('\n', lineStart);
            if (lineEnd < 0)
                lineEnd = content.Length;
            int lineLength = lineEnd - lineStart;

            ColorRange(lineStart, lineLength, normal);
            int levelStart = lineLength > 0 ? content.IndexOf('[', lineStart, lineLength) : -1;
            int levelEnd = levelStart >= 0 ? content.IndexOf(']', levelStart, lineEnd - levelStart) : -1;
            if (levelStart > lineStart && levelEnd > levelStart)
            {
                ColorRange(lineStart, levelStart - lineStart, timestamp);
                ColorRange(levelStart, levelEnd - levelStart + 1, LevelColor(content.Substring(levelStart + 1, levelEnd - levelStart - 1)));

                int categoryStart = levelEnd + 2;
                int categoryEnd = categoryStart < lineEnd
                    ? content.IndexOf(':', categoryStart, lineEnd - categoryStart)
                    : -1;
                if (categoryEnd > categoryStart)
                    ColorRange(categoryStart, categoryEnd - categoryStart, category);
            }

            lineStart = lineEnd + 1;
        }
    }

    private void ColorRange(int start, int length, Color color)
    {
        if (length <= 0)
            return;

        _textBox.Select(start, length);
        _textBox.SelectionColor = color;
    }

    private Color LevelColor(string level) => level switch
    {
        "Trace" or "Debug" => _blackBackground.Checked ? Color.DarkGray : Color.Gray,
        "Information" => _blackBackground.Checked ? Color.LimeGreen : Color.Green,
        "Warning" => _blackBackground.Checked ? Color.Yellow : Color.DarkGoldenrod,
        "Error" or "Critical" => Color.Red,
        _ => _blackBackground.Checked ? Color.Gainsboro : Color.Black
    };
}
