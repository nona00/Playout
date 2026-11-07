using System.Drawing;
using System.Windows.Forms;

namespace Playout.Gui;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

internal sealed partial class MainForm : Form
{
    private static readonly Color BackgroundColor = Color.FromArgb(17, 20, 28);
    private static readonly Color PanelColor = Color.FromArgb(29, 34, 45);
    private static readonly Color TextColor = Color.FromArgb(235, 238, 245);
    private static readonly Color MutedColor = Color.FromArgb(152, 163, 180);
    private readonly Icon windowIcon;
    private readonly Icon trayIconImage;
    private readonly ContextMenuStrip trayMenu;
    private readonly NotifyIcon notifyIcon;

    public MainForm()
    {
        Text = "Playout";
        windowIcon = TrayIcon.Create('P', Color.FromArgb(119, 78, 160));
        Icon = windowIcon;
        trayIconImage = (Icon)windowIcon.Clone();
        trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Playout anzeigen", null, (_, _) => RestoreFromTray());
        trayMenu.Items.Add("Beenden", null, (_, _) => Close());
        notifyIcon = new NotifyIcon
        {
            Icon = trayIconImage,
            Text = "Playout",
            ContextMenuStrip = trayMenu,
            Visible = true
        };
        notifyIcon.MouseClick += (_, args) =>
        {
            if (args.Button == MouseButtons.Left)
                RestoreFromTray();
        };
        notifyIcon.DoubleClick += (_, _) => RestoreFromTray();
        MinimumSize = new Size(1000, 650);
        Size = new Size(1440, 900);
        WindowState = FormWindowState.Maximized;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = BackgroundColor;
        ForeColor = TextColor;
        Font = new Font("Segoe UI", 10);

        TableLayoutPanel layout = new()
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 2,
            RowCount = 2
        };

        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 65));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 35));

        layout.Controls.Add(CreatePlaylistPanel(), 0, 0);
        layout.Controls.Add(CreateBrowserPanel(), 1, 0);

        TableLayoutPanel lowerLayout = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Margin = Padding.Empty
        };

        lowerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        lowerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
        lowerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));

        lowerLayout.Controls.Add(CreateDeckPanel("AUSSPIELSCHACHT 1", "Deck 1", deck1Status), 0, 0);
        lowerLayout.Controls.Add(CreateAotPanel(), 1, 0);
        lowerLayout.Controls.Add(CreateDeckPanel("AUSSPIELSCHACHT 2", "Deck 2", deck2Status), 2, 0);

        layout.Controls.Add(lowerLayout, 0, 1);
        layout.SetColumnSpan(lowerLayout, 2);

        Controls.Add(layout);
        Controls.Add(CreateEngineToolbar());
        Shown += MainFormShown;
        FormClosed += MainFormClosed;
    }

    private void RestoreFromTray()
    {
        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;
        Show();
        BringToFront();
        Activate();
    }

    private static Control CreatePlaylistPanel()
    {
        Panel panel = CreatePanel();

        ListView list = new()
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            BackColor = PanelColor,
            ForeColor = TextColor,
            BorderStyle = BorderStyle.None
        };

        list.Columns.Add("Zeit", 80);
        list.Columns.Add("Titel", 240);
        list.Columns.Add("Interpret", 180);
        list.Columns.Add("AOT", 80);

        panel.Controls.Add(list);
        panel.Controls.Add(CreateHeading("SPIELLISTE"));
        return panel;
    }

    private Control CreateBrowserPanel()
    {
        Panel panel = CreatePanel();
        TableLayoutPanel browser = new() { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
        browser.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        browser.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        browser.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        browser.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        browser.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        browser.Controls.Add(CreateHeading("MUSIKQUELLEN / RECHERCHE"), 0, 0);

        musicSearch.Dock = DockStyle.Fill;
        musicSearch.PlaceholderText = "Dateiname oder Pfad suchen";
        musicSearch.TextChanged += (_, _) => RenderTracks();
        browser.Controls.Add(musicSearch, 0, 1);

        FlowLayoutPanel pfl = new() { Dock = DockStyle.Fill, WrapContents = false };
        pfl.Controls.Add(new Label { Text = "PFL", AutoSize = true, Padding = new Padding(0, 8, 8, 0), ForeColor = MutedColor });
        pfl.Controls.Add(pflStatus);
        pfl.Controls.Add(CreateActionButton("Stopp", async () => await RunEngineActionAsync(() => engine.StopAsync("PFL"))));
        browser.Controls.Add(pfl, 0, 2);

        musicList.Dock = DockStyle.Fill;
        musicList.View = View.Details;
        musicList.FullRowSelect = true;
        musicList.MultiSelect = false;
        musicList.BackColor = PanelColor;
        musicList.ForeColor = TextColor;
        musicList.BorderStyle = BorderStyle.None;
        musicList.Columns.Add("Titel (Dateiname)", 220);
        musicList.Columns.Add("Dateipfad", 420);
        ContextMenuStrip musicMenu = new();
        musicMenu.Items.Add("Über PFL vorhören", null, async (_, _) => await PreviewSelectedAsync());
        musicList.ContextMenuStrip = musicMenu;
        musicList.MouseDown += (_, eventArgs) =>
        {
            if (eventArgs.Button != MouseButtons.Right) return;
            ListViewItem? item = musicList.GetItemAt(eventArgs.X, eventArgs.Y);
            if (item is not null) item.Selected = true;
        };
        musicList.DoubleClick += async (_, _) => await LoadAndPlayInFreeDeckAsync();
        browser.Controls.Add(musicList, 0, 3);

        FlowLayoutPanel loadButtons = new() { Dock = DockStyle.Fill, WrapContents = false };
        loadButtons.Controls.Add(CreateActionButton("In Schacht 1 laden", async () => await LoadSelectedAsync("Deck 1")));
        loadButtons.Controls.Add(CreateActionButton("In Schacht 2 laden", async () => await LoadSelectedAsync("Deck 2")));
        loadButtons.Controls.Add(libraryStatus);
        browser.Controls.Add(loadButtons, 0, 4);
        panel.Controls.Add(browser);
        return panel;
    }

    private Control CreateDeckPanel(string title, string deckId, Label status)
    {
        Panel panel = CreatePanel();
        status.Dock = DockStyle.Fill;
        status.Text = "Engine nicht verbunden";
        status.TextAlign = ContentAlignment.MiddleCenter;
        status.ForeColor = MutedColor;
        FlowLayoutPanel buttons = new() { Dock = DockStyle.Bottom, Height = 42, WrapContents = false };
        buttons.Controls.Add(CreateActionButton("Start", async () => await RunEngineActionAsync(() => engine.PlayAsync(deckId))));
        buttons.Controls.Add(CreateActionButton("Pause", async () => await RunEngineActionAsync(() => engine.PauseAsync(deckId))));
        buttons.Controls.Add(CreateActionButton("Stopp", async () => await RunEngineActionAsync(() => engine.StopAsync(deckId))));
        panel.Controls.Add(status);
        panel.Controls.Add(buttons);
        panel.Controls.Add(CreateHeading(title));
        return panel;
    }

    private static Control CreateAotPanel()
    {
        Panel panel = CreatePanel();

        Label remaining = new()
        {
            Dock = DockStyle.Fill,
            Text = "--:--:--",
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 24, FontStyle.Bold),
            ForeColor = TextColor
        };

        panel.Controls.Add(remaining);
        panel.Controls.Add(CreateHeading("AOT"));
        return panel;
    }

    private static Panel CreatePanel()
    {
        return new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = PanelColor,
            Padding = new Padding(16),
            Margin = new Padding(6)
        };
    }

    private static Label CreateHeading(string text)
    {
        return new Label
        {
            Dock = DockStyle.Top,
            Height = 36,
            Text = text,
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            ForeColor = MutedColor
        };
    }
}