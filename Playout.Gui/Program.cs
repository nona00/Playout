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

internal sealed class MainForm : Form
{
    private static readonly Color BackgroundColor = Color.FromArgb(17, 20, 28);
    private static readonly Color PanelColor = Color.FromArgb(29, 34, 45);
    private static readonly Color TextColor = Color.FromArgb(235, 238, 245);
    private static readonly Color MutedColor = Color.FromArgb(152, 163, 180);

    public MainForm()
    {
        Text = "Playout";
        MinimumSize = new Size(1000, 650);
        Size = new Size(1440, 900);
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

        lowerLayout.Controls.Add(CreateDeckPanel("AUSSPIELSCHACHT 1"), 0, 0);
        lowerLayout.Controls.Add(CreateAotPanel(), 1, 0);
        lowerLayout.Controls.Add(CreateDeckPanel("AUSSPIELSCHACHT 2"), 2, 0);

        layout.Controls.Add(lowerLayout, 0, 1);
        layout.SetColumnSpan(lowerLayout, 2);

        Controls.Add(layout);
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

    private static Control CreateBrowserPanel()
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

        list.Columns.Add("Titel", 240);
        list.Columns.Add("Interpret", 180);
        list.Columns.Add("Dauer", 80);

        TextBox search = new()
        {
            Dock = DockStyle.Top,
            PlaceholderText = "Musik suchen",
            Margin = new Padding(0, 0, 0, 8)
        };

        panel.Controls.Add(list);
        panel.Controls.Add(search);
        panel.Controls.Add(CreateHeading("MUSIK-RECHERCHE / CUE"));
        return panel;
    }

    private static Control CreateDeckPanel(string title)
    {
        Panel panel = CreatePanel();

        Label status = new()
        {
            Dock = DockStyle.Fill,
            Text = "Kein Titel geladen",
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = MutedColor
        };

        panel.Controls.Add(status);
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