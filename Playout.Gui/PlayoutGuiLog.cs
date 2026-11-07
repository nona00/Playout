using System.ComponentModel;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace Playout.Gui;

internal static class PlayoutGuiLog
{
    private static readonly object Sync = new();

    public static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Playout", "Logs", "Playout.log");

    public static void Write(string level, string category, string message, Exception? exception = null)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            lock (Sync)
            {
                using FileStream stream = new(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using StreamWriter writer = new(stream);
                writer.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {category}: {message}");
                if (exception is not null)
                    writer.WriteLine(exception);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal sealed class PlayoutLogWindow : Form
{
    private const int MaxLogBytes = 128 * 1024;
    private readonly string _logPath;
    private readonly RichTextBox _textBox = new();
    private readonly CheckBox _blackBackground = new() { Text = "Schwarzer Hintergrund", Checked = true, AutoSize = true };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly BackgroundWorker _reader = new();
    private long _lastLength = -1;
    private DateTime _lastWriteTime;
    private string _content = string.Empty;
    private bool _closing;

    public PlayoutLogWindow(string logPath, Icon applicationIcon)
    {
        _logPath = logPath;
        Text = "Playout – Log";
        Icon = applicationIcon;
        WindowState = FormWindowState.Maximized;
        StartPosition = FormStartPosition.CenterScreen;

        FlowLayoutPanel toolbar = new() { Dock = DockStyle.Top, Height = 34, Padding = new Padding(8, 6, 0, 0) };
        toolbar.Controls.Add(_blackBackground);
        _textBox.Dock = DockStyle.Fill;
        _textBox.ReadOnly = true;
        _textBox.WordWrap = false;
        _textBox.DetectUrls = false;
        _textBox.HideSelection = false;
        _textBox.Font = new Font("Consolas", 10);
        Controls.Add(_textBox);
        Controls.Add(toolbar);

        _blackBackground.CheckedChanged += (_, _) => RenderLog();
        _reader.DoWork += (_, args) => args.Result = ReadSnapshot((ReadState)args.Argument!);
        _reader.RunWorkerCompleted += (_, args) =>
        {
            if (_closing || args.Error is not null || args.Result is not LogSnapshot snapshot)
                return;

            _lastLength = snapshot.Length;
            _lastWriteTime = snapshot.LastWriteTime;
            if (_content != snapshot.Content)
            {
                _content = snapshot.Content;
                RenderLog();
            }
        };
        _timer.Tick += (_, _) => RequestRead();
        FormClosed += (_, _) =>
        {
            _closing = true;
            _timer.Stop();
            _timer.Dispose();
        };

        ApplyTheme();
        RequestRead();
        _timer.Start();
    }

    private void RequestRead()
    {
        if (!_closing && !_reader.IsBusy)
            _reader.RunWorkerAsync(new ReadState(_lastLength, _lastWriteTime));
    }

    private LogSnapshot? ReadSnapshot(ReadState state)
    {
        try
        {
            FileInfo info = new(_logPath);
            if (!info.Exists || (info.Length == state.Length && info.LastWriteTimeUtc == state.LastWriteTime))
                return null;

            using FileStream stream = new(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxLogBytes)
            {
                stream.Position = stream.Length - MaxLogBytes;
                int value;
                while ((value = stream.ReadByte()) >= 0 && value != '\n')
                {
                }
            }

            using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return new LogSnapshot(reader.ReadToEnd(), info.Length, info.LastWriteTimeUtc);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void RenderLog()
    {
        ApplyTheme();
        _textBox.Text = _content.Length == 0 ? "Noch keine Logeinträge." : _content;
        string text = _textBox.Text;
        int lineStart = 0;
        while (lineStart < text.Length)
        {
            int lineEnd = text.IndexOf('\n', lineStart);
            if (lineEnd < 0)
                lineEnd = text.Length;
            int levelStart = lineEnd > lineStart ? text.IndexOf('[', lineStart, lineEnd - lineStart) : -1;
            int levelEnd = levelStart >= 0 ? text.IndexOf(']', levelStart, lineEnd - levelStart) : -1;
            if (levelStart > lineStart && levelEnd > levelStart)
            {
                ColorRange(lineStart, levelStart - lineStart, _blackBackground.Checked ? Color.Gray : Color.DimGray);
                string level = text.Substring(levelStart + 1, levelEnd - levelStart - 1).Trim();
                Color levelColor = level switch
                {
                    "Information" => _blackBackground.Checked ? Color.LimeGreen : Color.Green,
                    "Warning" => _blackBackground.Checked ? Color.Yellow : Color.DarkGoldenrod,
                    "Error" or "Critical" => Color.Red,
                    "Debug" or "Trace" => _blackBackground.Checked ? Color.DarkGray : Color.Gray,
                    _ => _textBox.ForeColor
                };
                ColorRange(levelStart, levelEnd - levelStart + 1, levelColor);
                int categoryStart = levelEnd + 2;
                int categoryEnd = categoryStart < lineEnd ? text.IndexOf(':', categoryStart, lineEnd - categoryStart) : -1;
                if (categoryEnd > categoryStart)
                    ColorRange(categoryStart, categoryEnd - categoryStart, _blackBackground.Checked ? Color.LightSkyBlue : Color.DarkBlue);
            }

            lineStart = lineEnd + 1;
        }

        _textBox.SelectionStart = _textBox.TextLength;
        _textBox.SelectionLength = 0;
        _textBox.ScrollToCaret();
    }

    private void ColorRange(int start, int length, Color color)
    {
        if (length <= 0)
            return;
        _textBox.Select(start, length);
        _textBox.SelectionColor = color;
    }

    private void ApplyTheme()
    {
        _textBox.BackColor = _blackBackground.Checked ? Color.Black : Color.White;
        _textBox.ForeColor = _blackBackground.Checked ? Color.Gainsboro : Color.Black;
    }

    private sealed record ReadState(long Length, DateTime LastWriteTime);
    private sealed record LogSnapshot(string Content, long Length, DateTime LastWriteTime);
}
