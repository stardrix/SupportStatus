using System.Windows;
using System.Windows.Controls;

namespace SupportStatus;

// The "Set location status" window. Only available when the app is started with /admin.
public partial class AdminWindow : Window
{
    readonly Store store;
    readonly Action changed; // called after every change so the widget refreshes at once

    public AdminWindow(Store store, string myLocationId, Action changed = null)
    {
        InitializeComponent();
        this.store = store;
        this.changed = changed;
        ForwardText.Text = $"The phone is forwarded to {StatusLogic.CentralName}";
        AutomationName(ForwardCheck, ForwardText.Text);
        BuildHoursGrid();
        // Fill the location list. The user's own (star) is selected up front.
        // Same order as the widget: pinned first, then alphabetical.
        var order = StringComparer.Create(System.Globalization.CultureInfo.CurrentCulture, true);
        var locations = store.LoadLocations().OrderByDescending(l => l.Pinned).ThenBy(l => l.Name, order).ToList();
        LocationBox.ItemsSource = locations;
        LocationBox.SelectedItem = locations.FirstOrDefault(l => l.Id.Equals(myLocationId, StringComparison.OrdinalIgnoreCase))
                                   ?? locations.FirstOrDefault();
    }

    Location Selected => LocationBox.SelectedItem as Location;

    // A new location was chosen: show its status, message, forwarding and opening hours.
    void Location_Changed(object sender, SelectionChangedEventArgs e)
    {
        var active = ActiveOverride();
        MessageInput.Text = active?.Message ?? "";
        FillUntil(active?.ExpiresAt);
        loadingForward = true;
        ForwardCheck.IsChecked = ActiveForwarding() != null;
        loadingForward = false;
        ShowCurrent();
        if (Selected != null) FillHours(Selected);
    }

    // ---------- Forwarding ----------

    bool loadingForward; // true while the checkbox is set from the file, so it is not saved again

    Forwarding ActiveForwarding()
    {
        if (Selected == null) return null;
        store.LoadForwarding().TryGetValue(Selected.Id, out var f);
        return f;
    }

    // Checkbox on/off: save at once.
    void Forward_Changed(object sender, RoutedEventArgs e)
    {
        if (loadingForward || Selected == null) return;
        try
        {
            store.SetForwarding(Selected.Id, ForwardCheck.IsChecked == true);
            ShowCurrent();
            changed?.Invoke();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not save forwarding:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            loadingForward = true;
            ForwardCheck.IsChecked = ActiveForwarding() != null; // show what is actually saved
            loadingForward = false;
        }
    }

    // ---------- Opening hours ----------

    const string HoursHelp = "The hours apply until you change them again. Everyone sees them at once.";

    // Choices in the lists: 00:00 to 24:00 in 15-minute steps.
    static readonly List<string> Times = Enumerable.Range(0, 24 * 4 + 1)
        .Select(i => StatusLogic.Fmt(TimeSpan.FromMinutes(i * 15))).ToList();

    static readonly DayOfWeek[] WeekDays =
        { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday };

    // One row in the schedule: a day with checkboxes and from/to for walk-in and phone.
    class DayRow
    {
        public DayOfWeek Day;
        public string Name;
        public CheckBox Walk, Phone;
        public ComboBox WalkFrom, WalkTo, PhoneFrom, PhoneTo;
    }
    readonly List<DayRow> dayRows = new();

    // Build the 7-day schedule. Columns: day | walk-in from - to | phone from - to
    void BuildHoursGrid()
    {
        foreach (var w in new[] { 80.0, 0, 64, 0, 64, 14, 0, 64, 0, 64 })
            HoursGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = w == 0 ? GridLength.Auto : new GridLength(w) });

        HoursGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddCell(new TextBlock { Text = "Walk-in", FontWeight = FontWeights.SemiBold }, 0, 1, 4);
        AddCell(new TextBlock { Text = "Phone", FontWeight = FontWeights.SemiBold }, 0, 6, 4);

        for (int i = 0; i < 7; i++)
        {
            int row = i + 1;
            HoursGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var r = new DayRow
            {
                Day = WeekDays[i],
                Name = WeekDays[i].ToString(),
                Walk = new CheckBox { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) },
                Phone = new CheckBox { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) },
                WalkFrom = new ComboBox(), WalkTo = new ComboBox(), PhoneFrom = new ComboBox(), PhoneTo = new ComboBox()
            };
            // Names for screen readers and UI tests, e.g. "Monday walk-in from".
            AutomationName(r.Walk, $"{r.Name} walk-in");
            AutomationName(r.Phone, $"{r.Name} phone");
            AutomationName(r.WalkFrom, $"{r.Name} walk-in from");
            AutomationName(r.WalkTo, $"{r.Name} walk-in to");
            AutomationName(r.PhoneFrom, $"{r.Name} phone from");
            AutomationName(r.PhoneTo, $"{r.Name} phone to");

            // Ticking a checkbox enables its from/to lists.
            r.Walk.Checked += (_, _) => UpdateEnabled(r);
            r.Walk.Unchecked += (_, _) => UpdateEnabled(r);
            r.Phone.Checked += (_, _) => UpdateEnabled(r);
            r.Phone.Unchecked += (_, _) => UpdateEnabled(r);

            AddCell(new TextBlock { Text = r.Name, VerticalAlignment = VerticalAlignment.Center }, row, 0);
            AddCell(r.Walk, row, 1);
            AddCell(r.WalkFrom, row, 2);
            AddCell(new TextBlock { Text = "-", Margin = new Thickness(3, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center }, row, 3);
            AddCell(r.WalkTo, row, 4);
            AddCell(r.Phone, row, 6);
            AddCell(r.PhoneFrom, row, 7);
            AddCell(new TextBlock { Text = "-", Margin = new Thickness(3, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center }, row, 8);
            AddCell(r.PhoneTo, row, 9);
            dayRows.Add(r);
        }
    }

    // Place a control in the schedule at a given row and column.
    void AddCell(UIElement e, int row, int col, int span = 1)
    {
        if (e is FrameworkElement fe) fe.Margin = new Thickness(fe.Margin.Left, fe.Margin.Top + 3, fe.Margin.Right, fe.Margin.Bottom);
        Grid.SetRow(e, row);
        Grid.SetColumn(e, col);
        Grid.SetColumnSpan(e, span);
        HoursGrid.Children.Add(e);
    }

    static void AutomationName(DependencyObject e, string name) => System.Windows.Automation.AutomationProperties.SetName(e, name);

    static void UpdateEnabled(DayRow r)
    {
        r.WalkFrom.IsEnabled = r.WalkTo.IsEnabled = r.Walk.IsChecked == true;
        r.PhoneFrom.IsEnabled = r.PhoneTo.IsEnabled = r.Phone.IsChecked == true;
    }

    // Show the location's current hours in the schedule.
    void FillHours(Location l)
    {
        foreach (var r in dayRows)
        {
            var h = l.HoursFor(r.Day);
            SetRange(r.Walk, r.WalkFrom, r.WalkTo, h?.WalkIn, "08:00", "12:00");
            SetRange(r.Phone, r.PhoneFrom, r.PhoneTo, h?.Phone, "08:00", "15:00");
            UpdateEnabled(r);
        }
        HoursText.Text = HoursHelp;
    }

    // Set checkbox and from/to from e.g. "08:00-12:00". Empty = unticked, default times in the lists.
    static void SetRange(CheckBox check, ComboBox from, ComboBox to, string range, string defaultFrom, string defaultTo)
    {
        bool has = StatusLogic.TryRange(range, out var s, out var e);
        SetTime(from, has ? StatusLogic.Fmt(s) : defaultFrom);
        SetTime(to, has ? StatusLogic.Fmt(e) : defaultTo);
        check.IsChecked = has;
    }

    // Select a time in a list. If it is not there (e.g. 07:50) it is added.
    static void SetTime(ComboBox box, string time)
    {
        var items = new List<string>(Times);
        if (!items.Contains(time)) { items.Add(time); items.Sort(StringComparer.Ordinal); }
        box.ItemsSource = items;
        box.SelectedItem = time;
    }

    // "Copy Monday to Tue-Fri" button.
    void CopyMonday_Click(object sender, RoutedEventArgs e)
    {
        var mon = dayRows[0];
        foreach (var r in dayRows.Skip(1).Take(4))
        {
            r.Walk.IsChecked = mon.Walk.IsChecked;
            r.Phone.IsChecked = mon.Phone.IsChecked;
            SetTime(r.WalkFrom, (string)mon.WalkFrom.SelectedItem);
            SetTime(r.WalkTo, (string)mon.WalkTo.SelectedItem);
            SetTime(r.PhoneFrom, (string)mon.PhoneFrom.SelectedItem);
            SetTime(r.PhoneTo, (string)mon.PhoneTo.SelectedItem);
            UpdateEnabled(r);
        }
    }

    // "Save opening hours" button. Checks that "from" is before "to" on every ticked day.
    void SaveHours_Click(object sender, RoutedEventArgs e)
    {
        if (Selected == null) return;

        var week = new Dictionary<string, DayHours>();
        foreach (var r in dayRows)
        {
            string walk = "", phone = "";
            if (r.Walk.IsChecked == true && !TryRangeText(r.WalkFrom, r.WalkTo, out walk))
            {
                MessageBox.Show(this, $"{r.Name}, walk-in: \"from\" must be before \"to\".", "Invalid time", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (r.Phone.IsChecked == true && !TryRangeText(r.PhoneFrom, r.PhoneTo, out phone))
            {
                MessageBox.Show(this, $"{r.Name}, phone: \"from\" must be before \"to\".", "Invalid time", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            // Days without walk-in and phone are not saved = closed.
            if (walk != "" || phone != "") week[r.Day.ToString()] = new DayHours { WalkIn = walk, Phone = phone };
        }

        try
        {
            store.SaveHours(Selected.Id, new SiteHours { Week = week, ChangedBy = Environment.UserName, ChangedAt = DateTime.Now });
            Selected.Week = week;
            ShowCurrent();
            HoursText.Text = $"Saved at {DateTime.Now:HH:mm}. " + HoursHelp;
            changed?.Invoke();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not save opening hours:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // Build "08:00-12:00" from two lists. false if "from" is not before "to".
    static bool TryRangeText(ComboBox from, ComboBox to, out string range)
    {
        range = "";
        if (from.SelectedItem is not string f || to.SelectedItem is not string t) return false;
        if (string.CompareOrdinal(f, t) >= 0) return false;
        range = $"{f}-{t}";
        return true;
    }

    // ---------- Manual status ----------

    // The manual status that applies right now for the selected location. null = none.
    StatusOverride ActiveOverride()
    {
        if (Selected == null) return null;
        store.LoadOverrides().TryGetValue(Selected.Id, out var o);
        return o != null && o.ExpiresAt > DateTime.Now ? o : null;
    }

    // Show the current status for the selected location.
    // "Save message" can only be used when a manual status is set.
    void ShowCurrent()
    {
        if (Selected == null) return;
        var o = ActiveOverride();
        var (state, detail) = StatusLogic.Resolve(Selected, o, DateTime.Now, ActiveForwarding());
        CurrentText.Text = $"Right now: {StatusLogic.Label(state)} - {detail}";
        SaveMessageButton.IsEnabled = o != null;
        SaveMessageButton.ToolTip = o != null ? "Save the message on the status that is set" : "Press Open, Phone only or Closed first";
    }

    // "Save message" button: update the message (and "Valid until"). The status stays the same.
    void SaveMessage_Click(object sender, RoutedEventArgs e) => SaveMessage();

    // Enter in the message box = Save message.
    void Message_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) SaveMessage();
    }

    void SaveMessage()
    {
        var o = ActiveOverride();
        if (o == null)
        {
            MessageBox.Show(this, "Press Open, Phone only or Closed first. The message is saved together with the status.",
                            "No status set", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Set(o.State);
    }

    // ---------- Valid until ----------

    const string Midnight = "Midnight (rest of the day)";

    // Fill the "Valid until" list: midnight, or a time later today in 15-minute steps.
    // current = end time of a status that is already set (selected up front).
    void FillUntil(DateTime? current)
    {
        var now = DateTime.Now;
        var times = new List<string>();
        var t = now.Date.AddMinutes(Math.Floor(now.TimeOfDay.TotalMinutes / 15) * 15 + 15);
        for (; t < now.Date.AddDays(1); t = t.AddMinutes(15)) times.Add($"{t:HH:mm}");

        string selected = Midnight;
        if (current is DateTime c && c > now && c.TimeOfDay != TimeSpan.Zero)
        {
            selected = $"{c:HH:mm}";
            if (!times.Contains(selected)) { times.Add(selected); times.Sort(StringComparer.Ordinal); }
        }
        UntilBox.ItemsSource = new[] { Midnight }.Concat(times).ToList();
        UntilBox.SelectedItem = selected;
    }

    // The chosen end time. Midnight = start of tomorrow.
    DateTime ChosenUntil()
    {
        if (UntilBox.SelectedItem is not string s || s == Midnight) return DateTime.Today.AddDays(1);
        return DateTime.Today + TimeSpan.Parse(s, System.Globalization.CultureInfo.InvariantCulture); // "13:00"
    }

    // Save a manual status. It lasts until midnight or the chosen time.
    // Needs write access to the overrides folder, otherwise an error is shown.
    void Set(SupportState state)
    {
        if (Selected == null) return;
        var until = ChosenUntil();
        if (until <= DateTime.Now)
        {
            MessageBox.Show(this, "The time in \"Valid until\" has already passed. Choose a later time.",
                            "Invalid time", MessageBoxButton.OK, MessageBoxImage.Warning);
            FillUntil(null);
            return;
        }
        try
        {
            store.SaveOverride(Selected.Id, new StatusOverride
            {
                State = state,
                Message = MessageInput.Text.Trim(),
                SetBy = Environment.UserName,
                SetAt = DateTime.Now,
                ExpiresAt = until
            });
            ShowCurrent();
            changed?.Invoke();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not save status:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // The Open / Phone only / Closed buttons.
    void Open_Click(object sender, RoutedEventArgs e) => Set(SupportState.Open);
    void Phone_Click(object sender, RoutedEventArgs e) => Set(SupportState.PhoneOnly);
    void Closed_Click(object sender, RoutedEventArgs e) => Set(SupportState.Closed);

    // "Back to normal opening hours" button: deletes the manual status.
    void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (Selected == null) return;
        try { store.ClearOverride(Selected.Id); MessageInput.Text = ""; FillUntil(null); ShowCurrent(); changed?.Invoke(); }
        catch (Exception ex) { MessageBox.Show(this, $"Could not reset status: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}
