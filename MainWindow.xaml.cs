using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace SupportStatus;

// One line (location) in the list. What is shown on screen.
public class Row : INotifyPropertyChanged
{
    bool isExpanded;

    public event PropertyChangedEventHandler PropertyChanged;

    // true = the location is clicked open and shows phone, email and address.
    public bool IsExpanded
    {
        get => isExpanded;
        set { isExpanded = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DetailsVisibility))); }
    }
    public Visibility DetailsVisibility => IsExpanded ? Visibility.Visible : Visibility.Collapsed;

    // Details shown when clicked. Empty fields are hidden.
    public string Phone { get; set; }
    public string Email { get; set; }
    public string Address { get; set; }
    public string PhoneLine => $"Phone: {Phone}";
    public string EmailLine => $"Email: {Email}";
    public string HoursLine { get; set; }
    public Visibility PhoneVisibility => Show(Phone);
    public Visibility EmailVisibility => Show(Email);
    public Visibility AddressVisibility => Show(Address);
    static Visibility Show(string s) => string.IsNullOrWhiteSpace(s) ? Visibility.Collapsed : Visibility.Visible;

    public string Id { get; set; }
    public string Title { get; set; }          // name, with a star if it is the user's own
    public string Name { get; set; }
    public bool IsMine { get; set; }           // the user's own location (star)
    public bool Pinned { get; set; }           // always at the top (e.g. the central desk)
    public string Detail { get; set; }         // line under the name, e.g. "Northtown · Walk-in until 12:00"
    public string StateText { get; set; }      // OPEN / PHONE / CLOSED / FORWARDED
    public SupportState State { get; set; }
    public Brush StateBrush { get; set; }      // green / amber / red / blue
    public Brush RowBackground { get; set; }
    public string SearchText { get; set; }     // what the search box matches against
    public string Info { get; set; }           // tooltip
}

// The main window (the widget) and the tray icon.
public partial class MainWindow : Window
{
    // Colours: green = open, amber = phone only, red = closed, blue = forwarded to the central desk.
    static readonly Brush Green = Frozen("#3DBE6E"), Amber = Frozen("#F0B429"), Red = Frozen("#E5534B"),
                          Blue = Frozen("#4A90D9"), Normal = Frozen("#2A2E36"), Mine = Frozen("#243A2E");

    // Tray icon: a coloured dot with "IT".
    // The colour follows the user's own location. Grey if none is chosen.
    static readonly Drawing.Icon NeutralIcon = DotIcon(Drawing.Color.FromArgb(0x8A, 0x94, 0xA6));
    static readonly Dictionary<SupportState, Drawing.Icon> StateIcons = new()
    {
        [SupportState.Open] = DotIcon(Drawing.Color.FromArgb(0x3D, 0xBE, 0x6E)),
        [SupportState.PhoneOnly] = DotIcon(Drawing.Color.FromArgb(0xF0, 0xB4, 0x29)),
        [SupportState.Closed] = DotIcon(Drawing.Color.FromArgb(0xE5, 0x53, 0x4B)),
        [SupportState.Forwarded] = DotIcon(Drawing.Color.FromArgb(0x4A, 0x90, 0xD9)),
    };

    // Alphabetical order using the PC's language settings.
    static readonly StringComparer NameOrder = StringComparer.Create(System.Globalization.CultureInfo.CurrentCulture, true);

    readonly Settings settings;
    readonly Store store;
    readonly DispatcherTimer timer;
    readonly Forms.NotifyIcon tray;
    readonly Forms.ToolStripMenuItem topmostItem;
    readonly Forms.ToolStripMenuItem forwardItem;  // only for site staff (/admin)
    bool hideTipShown;                          // the "still running" balloon is shown once
    string expandedId;                          // the location clicked open. Kept across refreshes
    List<Row> rows = new();

    public MainWindow(Settings settings, bool adminMode)
    {
        InitializeComponent();
        this.settings = settings;
        StatusLogic.CentralName = string.IsNullOrWhiteSpace(settings.CentralName) ? "Service Desk" : settings.CentralName;
        store = new Store(settings.ResolvedSharePath);
        // The "Set my status" button is only shown with /admin.
        if (adminMode) AdminButton.Visibility = Visibility.Visible;

        // Start in the bottom right corner of the screen.
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 16;
        Top = area.Bottom - Height - 16;

        // Right-click menu on the tray icon.
        topmostItem = new Forms.ToolStripMenuItem("Always on top", null, (_, _) => SetTopmost(!Topmost));
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Show / hide", null, (_, _) => ToggleVisible());
        if (adminMode)
        {
            menu.Items.Add("Set my status", null, (_, _) => OpenAdmin());
            // Quick shortcut when site staff leave: forward their own location (star) with one click.
            forwardItem = new Forms.ToolStripMenuItem($"Forwarded to {StatusLogic.CentralName}", null, (_, _) => ToggleMyForwarding());
            menu.Items.Add(forwardItem);
        }
        menu.Items.Add(topmostItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Close());
        tray = new Forms.NotifyIcon { Icon = NeutralIcon, Text = "Support status", ContextMenuStrip = menu, Visible = true };
        // Left-click on the icon: show or hide the window.
        tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) ToggleVisible(); };
        // Remove the icon when the app closes.
        Closed += (_, _) => tray.Dispose();

        // Minimise (e.g. Win+M) hides the window to the tray.
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) { WindowState = WindowState.Normal; HideToTray(); } };

        // Load now, then every RefreshSeconds.
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(5, settings.RefreshSeconds)) };
        timer.Tick += async (_, _) => await RefreshAsync();
        Loaded += async (_, _) => { await RefreshAsync(); timer.Start(); };
    }

    // Read the shared folder and rebuild the list.
    public async Task RefreshAsync()
    {
        try
        {
            // Read the files in the background so the window does not freeze on a slow network.
            var (locations, overrides, forwarding) = await Task.Run(() => (store.LoadLocations(), store.LoadOverrides(), store.LoadForwarding()));
            var now = DateTime.Now;
            int forwardedCount = locations.Count(l => forwarding.ContainsKey(l.Id));

            // One line per location.
            rows = locations
                .Select(l =>
                {
                    overrides.TryGetValue(l.Id, out var o);
                    forwarding.TryGetValue(l.Id, out var fw);
                    var (state, detail) = StatusLogic.Resolve(l, o, now, fw);
                    // The central desk (Pinned) shows how many locations are forwarded to it.
                    if (l.Pinned && forwardedCount > 0)
                        detail += forwardedCount == 1 ? " · 1 location forwarded here" : $" · {forwardedCount} locations forwarded here";
                    bool mine = l.Id.Equals(settings.MyLocationId, StringComparison.OrdinalIgnoreCase);
                    return new Row
                    {
                        Id = l.Id,
                        Title = mine ? $"★ {l.Name}" : l.Name,
                        Name = l.Name,
                        IsMine = mine,
                        Pinned = l.Pinned,
                        Detail = $"{l.Region} · {detail}",
                        State = state,
                        StateText = StatusLogic.Label(state),
                        StateBrush = state switch
                        {
                            SupportState.Open => Green,
                            SupportState.PhoneOnly => Amber,
                            SupportState.Forwarded => Blue,
                            _ => Red
                        },
                        RowBackground = mine ? Mine : Normal,
                        SearchText = $"{l.Name} {l.Region} {l.Address}".ToLowerInvariant(),
                        Phone = l.PhoneNumber,
                        Email = l.Email,
                        Address = l.Address,
                        HoursLine = StatusLogic.DescribeWeek(l),
                        IsExpanded = l.Id == expandedId,
                        Info = mine
                            ? "Click for phone, email and address\nRight-click to remove the star"
                            : "Click for phone, email and address\nRight-click to make it your location (★)"
                    };
                })
                // Order: pinned (the central desk) first, then the user's own (star),
                // then the rest alphabetically. The status does not change the order.
                .OrderByDescending(r => r.Pinned).ThenByDescending(r => r.IsMine).ThenBy(r => r.Name, NameOrder)
                .ToList();

            ApplyFilter();
            int open = rows.Count(r => r.State == SupportState.Open);
            int phone = rows.Count(r => r.State == SupportState.PhoneOnly);
            var summary = $"{open} open · {phone} phone only";
            if (forwardedCount > 0) summary += $" · {forwardedCount} forwarded";
            FooterText.Text = $"{summary} · updated {now:HH:mm:ss}";
            UpdateTray(summary);
            if (forwardItem != null) forwardItem.Checked = rows.Any(r => r.IsMine && r.State == SupportState.Forwarded);
        }
        catch (Exception ex)
        {
            FooterText.Text = $"Cannot reach the status folder: {ex.Message}";
            UpdateTray("Cannot reach the status folder");
        }
    }

    // Update the colour and text of the tray icon.
    void UpdateTray(string summary)
    {
        var mine = rows.FirstOrDefault(r => r.IsMine);
        tray.Icon = mine != null ? StateIcons[mine.State] : NeutralIcon;
        var text = mine != null ? $"{mine.Name}: {mine.StateText}\n{summary}" : $"Support status\n{summary}";
        tray.Text = text.Length > 127 ? text[..127] : text; // Windows allows max 127 characters
    }

    // Only show the locations that match the search box.
    void ApplyFilter()
    {
        var q = SearchBox.Text.Trim().ToLowerInvariant();
        List.ItemsSource = q.Length == 0 ? rows : rows.Where(r => r.SearchText.Contains(q)).ToList();
    }

    void ToggleVisible()
    {
        if (IsVisible) HideToTray();
        else ShowWidget();
    }

    // Show the window and bring it to the front.
    public void ShowWidget()
    {
        Show();
        Activate();
        // Windows does not always bring the window forward. A brief "topmost" forces it.
        if (!Topmost) { Topmost = true; Topmost = false; }
    }

    // Hide the window. The app keeps running in the tray.
    void HideToTray()
    {
        Hide();
        if (hideTipShown) return;
        hideTipShown = true;
        tray.ShowBalloonTip(4000, "Support status is still running", "Click the icon next to the clock to open it again.", Forms.ToolTipIcon.Info);
    }

    // Toggle "always on top". Keeps the button and the menu in sync.
    void SetTopmost(bool on)
    {
        Topmost = on;
        PinButton.IsChecked = on;
        topmostItem.Checked = on;
    }

    // Tray menu item: forward / un-forward the user's own location (star).
    async void ToggleMyForwarding()
    {
        if (string.IsNullOrWhiteSpace(settings.MyLocationId))
        {
            ShowWidget();
            MessageBox.Show(this, "Choose your location first: right-click it in the list and choose \"★ Make this my location\".",
                            "No location chosen", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        bool turnOn = forwardItem.Checked != true;
        try { store.SetForwarding(settings.MyLocationId, turnOn); }
        catch (Exception ex)
        {
            ShowWidget();
            MessageBox.Show(this, $"Could not save forwarding:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        await RefreshAsync();
        tray.ShowBalloonTip(3000, "Support status",
            turnOn ? $"Forwarded to {StatusLogic.CentralName}. Remember to clear it when you are back." : "Forwarding cleared.",
            Forms.ToolTipIcon.Info);
    }

    // Open the admin window. The list refreshes when it closes.
    async void OpenAdmin()
    {
        ShowWidget();
        new AdminWindow(store, settings.MyLocationId, async () => await RefreshAsync()) { Owner = this }.ShowDialog();
        await RefreshAsync();
    }

    // Buttons and fields in the window.
    void Search_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => ApplyFilter();
    void Header_MouseDown(object sender, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); } // move the window
    void Pin_Click(object sender, RoutedEventArgs e) => SetTopmost(PinButton.IsChecked == true);
    void Hide_Click(object sender, RoutedEventArgs e) => HideToTray();
    void Admin_Click(object sender, RoutedEventArgs e) => OpenAdmin();

    // Find the location a button or menu belongs to.
    static Row RowOf(object sender) => ((FrameworkElement)sender).DataContext as Row;

    // Click a location to open it. Click again to close it. Only one is open at a time.
    void Row_Click(object sender, MouseButtonEventArgs e)
    {
        var row = RowOf(sender);
        if (row == null) return;
        bool open = !row.IsExpanded;
        foreach (var r in rows) r.IsExpanded = false;
        row.IsExpanded = open;
        expandedId = open ? row.Id : null;
    }

    // "Copy" button: copy the text and show "Copied" for 1.5 seconds.
    async void Copy_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        if (!CopyText(button.Tag as string)) return;
        button.Content = "Copied ✓";
        await Task.Delay(1500);
        button.Content = "Copy";
    }

    // Right-click menu on a location.
    void CopyPhone_Click(object sender, RoutedEventArgs e) => CopyText(RowOf(sender)?.Phone);
    void CopyEmail_Click(object sender, RoutedEventArgs e) => CopyText(RowOf(sender)?.Email);
    void CopyAddress_Click(object sender, RoutedEventArgs e) => CopyText(RowOf(sender)?.Address);

    // Put text on the clipboard (Ctrl+V).
    bool CopyText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        try { Clipboard.SetText(text); return true; }
        catch (Exception ex) // the clipboard can be locked by another program for a moment
        {
            MessageBox.Show(this, $"Could not copy:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    // Right-click: "Make this my location" / "Remove star".
    void SetMine_Click(object sender, RoutedEventArgs e) => SaveMine((((FrameworkElement)sender).DataContext as Row)?.Id);
    void ClearMine_Click(object sender, RoutedEventArgs e) => SaveMine("");

    // Save the user's star choice and refresh the list.
    async void SaveMine(string locationId)
    {
        if (locationId == null) return;
        try { settings.SaveMyLocation(locationId); }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not save your location:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        await RefreshAsync();
    }

    // Make a brush from a hex code, e.g. "#3DBE6E".
    static Brush Frozen(string hex)
    {
        var b = (Brush)new BrushConverter().ConvertFromString(hex);
        b.Freeze();
        return b;
    }

    // Draw the tray icon: a coloured dot with a white "IT".
    static Drawing.Icon DotIcon(Drawing.Color color)
    {
        using var bmp = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var brush = new Drawing.SolidBrush(color);
            g.FillEllipse(brush, 0, 0, 31, 31);
            using var font = new Drawing.Font("Segoe UI", 13, Drawing.FontStyle.Bold, Drawing.GraphicsUnit.Pixel);
            using var format = new Drawing.StringFormat { Alignment = Drawing.StringAlignment.Center, LineAlignment = Drawing.StringAlignment.Center };
            g.DrawString("IT", font, Drawing.Brushes.White, new Drawing.RectangleF(0, 1, 32, 32), format);
        }
        return Drawing.Icon.FromHandle(bmp.GetHicon());
    }
}
