using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SupportStatus;

// Possible states: open (walk-in), phone only, closed, and phone forwarded to the central desk.
public enum SupportState { Open, PhoneOnly, Closed, Forwarded }

// A support location. Comes from locations.json in the shared folder.
public class Location
{
    public string Id { get; set; }          // short unique name, e.g. "north". Also used as a file name
    public string Name { get; set; }        // display name
    public string Region { get; set; }      // town / area, shown under the name
    public string PhoneNumber { get; set; }
    public string Email { get; set; }
    public string Address { get; set; }
    public bool Pinned { get; set; }        // true = always at the top of the list (e.g. the central desk)
    public string WalkInHours { get; set; } // walk-in Mon-Fri, e.g. "08:00-12:00". "" = no walk-in
    public string PhoneHours { get; set; }  // phone hours Mon-Fri

    // Hours per weekday, keyed "Monday" ... "Sunday". If present it replaces the two fields above.
    // A missing day = closed that day.
    public Dictionary<string, DayHours> Week { get; set; }

    // Hours for one day. null = closed all day.
    public DayHours HoursFor(DayOfWeek day)
    {
        if (Week != null) return Week.TryGetValue(day.ToString(), out var h) ? h : null;
        // Without a weekly schedule: Mon-Fri use WalkInHours/PhoneHours, weekends are closed.
        if (day is DayOfWeek.Saturday or DayOfWeek.Sunday) return null;
        return new DayHours { WalkIn = WalkInHours ?? "", Phone = PhoneHours ?? "" };
    }

    public override string ToString() => Name; // shown in lists and to screen readers
}

// Hours for one day, e.g. WalkIn "08:00-12:00" and Phone "08:00-16:00". "" = not that day.
// "00:00-24:00" = all day.
public class DayHours
{
    public string WalkIn { get; set; } = "";
    public string Phone { get; set; } = "";
}

// A manual status set by site staff. Stored in overrides\<Id>.json.
public class StatusOverride
{
    public SupportState State { get; set; }
    public string Message { get; set; }     // optional message, e.g. "Out on a job"
    public string SetBy { get; set; }       // Windows user name of whoever set it
    public DateTime SetAt { get; set; }
    public DateTime ExpiresAt { get; set; } // midnight or the time chosen in "Valid until"
}

// The site has forwarded its phone to the central desk. Stored in overrides\forward\<Id>.json.
// File exists = forwarded. It stays (also overnight and at weekends) until the checkbox is cleared.
public class Forwarding
{
    public string By { get; set; }      // Windows user name of whoever set it
    public DateTime Since { get; set; }
}

// Opening hours set by site staff in the admin window. Stored in hours\<Id>.json.
// If the file exists, these hours replace the hours in locations.json.
public class SiteHours
{
    public Dictionary<string, DayHours> Week { get; set; } // hours per weekday. Missing day = closed
    public string ChangedBy { get; set; }
    public DateTime ChangedAt { get; set; }
}

// Settings from settings.json next to the exe.
public class Settings
{
    public string SharePath { get; set; } = "SampleData";    // path to the shared folder
    public string MyLocationId { get; set; } = "";           // default "own" location (star)
    public int RefreshSeconds { get; set; } = 30;            // how often to re-read the folder
    public string CentralName { get; set; } = "Service Desk"; // name of the central desk calls are forwarded to

    // A relative path (e.g. "SampleData") is resolved from the exe folder.
    public string ResolvedSharePath =>
        Path.IsPathRooted(SharePath) ? SharePath : Path.Combine(AppContext.BaseDirectory, SharePath);

    // The user's own star choice is stored in their profile (%APPDATA%),
    // because settings.json usually sits in Program Files where users cannot write.
    static string UserFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SupportStatus", "user.json");

    class UserChoice { public string MyLocationId { get; set; } }

    // Read settings.json. The user's own choice wins if it exists.
    public static Settings Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "settings.json");
        Settings s;
        try { s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Store.Json) ?? new Settings(); }
        catch { s = new Settings(); }

        try
        {
            var choice = JsonSerializer.Deserialize<UserChoice>(File.ReadAllText(UserFile), Store.Json);
            if (choice?.MyLocationId != null) s.MyLocationId = choice.MyLocationId;
        }
        catch { /* no personal choice yet: keep the default from settings.json */ }
        return s;
    }

    // Save the user's star choice. "" = no star.
    public void SaveMyLocation(string locationId)
    {
        MyLocationId = locationId ?? "";
        Directory.CreateDirectory(Path.GetDirectoryName(UserFile));
        File.WriteAllText(UserFile, JsonSerializer.Serialize(new UserChoice { MyLocationId = MyLocationId }, Store.Json));
    }
}

// Reads and writes files in the shared folder.
// Every location has its own files, so sites never overwrite each other.
public class Store(string root)
{
    // Shared JSON options. States are stored as text, e.g. "Open".
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    string OverrideDir => Path.Combine(root, "overrides");
    string HoursDir => Path.Combine(root, "hours");
    // Sub folder of overrides, so the site staff's write access to overrides also covers it.
    string ForwardDir => Path.Combine(root, "overrides", "forward");

    // All locations whose phone is forwarded to the central desk. Key = location Id.
    public Dictionary<string, Forwarding> LoadForwarding()
    {
        var result = new Dictionary<string, Forwarding>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(ForwardDir)) return result;
        foreach (var file in Directory.GetFiles(ForwardDir, "*.json"))
        {
            var f = Read<Forwarding>(file);
            if (f != null) result[Path.GetFileNameWithoutExtension(file)] = f;
        }
        return result;
    }

    // Set or clear forwarding for one location.
    public void SetForwarding(string locationId, bool forwarded)
    {
        var path = Path.Combine(ForwardDir, locationId + ".json");
        if (!forwarded)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        Directory.CreateDirectory(ForwardDir);
        WriteSafe(path, new Forwarding { By = Environment.UserName, Since = DateTime.Now });
    }

    // All locations from locations.json. Empty list if the file is missing or invalid.
    // Hours set by site staff (hours folder) replace the default hours.
    public List<Location> LoadLocations()
    {
        var list = Read<List<Location>>(Path.Combine(root, "locations.json")) ?? new();
        if (!Directory.Exists(HoursDir)) return list;

        var hours = new Dictionary<string, SiteHours>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.GetFiles(HoursDir, "*.json"))
        {
            var h = Read<SiteHours>(file);
            if (h != null) hours[Path.GetFileNameWithoutExtension(file)] = h;
        }
        foreach (var l in list)
        {
            if (hours.TryGetValue(l.Id, out var h) && h.Week != null) l.Week = h.Week;
        }
        return list;
    }

    // Save opening hours for one location (from the admin window).
    public void SaveHours(string locationId, SiteHours h)
    {
        Directory.CreateDirectory(HoursDir);
        WriteSafe(Path.Combine(HoursDir, locationId + ".json"), h);
    }

    // All manual statuses from the overrides folder. Key = location Id (the file name).
    public Dictionary<string, StatusOverride> LoadOverrides()
    {
        var result = new Dictionary<string, StatusOverride>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(OverrideDir)) return result;
        foreach (var file in Directory.GetFiles(OverrideDir, "*.json"))
        {
            var o = Read<StatusOverride>(file);
            if (o != null) result[Path.GetFileNameWithoutExtension(file)] = o;
        }
        return result;
    }

    // Save a manual status.
    public void SaveOverride(string locationId, StatusOverride o)
    {
        Directory.CreateDirectory(OverrideDir);
        WriteSafe(Path.Combine(OverrideDir, locationId + ".json"), o);
    }

    // Write to a .tmp file first and rename it afterwards,
    // so other PCs never read a half-written file.
    static void WriteSafe<T>(string path, T value)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json));
        File.Move(tmp, path, overwrite: true);
    }

    // Delete the manual status, so the opening hours apply again.
    public void ClearOverride(string locationId)
    {
        var path = Path.Combine(OverrideDir, locationId + ".json");
        if (File.Exists(path)) File.Delete(path);
    }

    // Read a JSON file. If it is locked (someone is writing), retry 3 times.
    // An invalid file returns null instead of crashing the app.
    static T Read<T>(string path) where T : class
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return JsonSerializer.Deserialize<T>(fs, Json);
            }
            catch (IOException) { Thread.Sleep(200); }
            catch (JsonException) { return null; }
        }
        return null;
    }
}

// Works out the current status of a location.
public static class StatusLogic
{
    // Name of the central desk, from settings.json ("CentralName").
    public static string CentralName { get; set; } = "Service Desk";

    // Order of precedence:
    // 1. Phone forwarded to the central desk (wins over everything)
    // 2. Manual status from site staff (if not expired)
    // 3. Within today's walk-in hours = open
    // 4. Within today's phone hours = phone only
    // 5. Otherwise closed (with "Opens at" if it opens later today)
    public static (SupportState state, string detail) Resolve(Location l, StatusOverride o, DateTime now, Forwarding fw = null)
    {
        if (fw != null)
            return (SupportState.Forwarded, $"Phone forwarded to {CentralName} since {When(fw.Since, now)}");

        if (o != null && o.ExpiresAt > now)
        {
            var text = string.IsNullOrWhiteSpace(o.Message) ? $"Set by {o.SetBy} at {o.SetAt:HH:mm}" : o.Message;
            // Show the end time if it is not midnight.
            if (o.ExpiresAt.TimeOfDay != TimeSpan.Zero) text += $" (until {o.ExpiresAt:HH:mm})";
            return (o.State, text);
        }

        var h = l.HoursFor(now.DayOfWeek);
        var t = now.TimeOfDay;

        if (TryRange(h?.WalkIn, out var ws, out var we) && t >= ws && t < we)
            return (SupportState.Open, $"Walk-in until {FmtEnd(we)}");

        if (TryRange(h?.Phone, out var ps, out var pe) && t >= ps && t < pe)
            return (SupportState.PhoneOnly, string.IsNullOrWhiteSpace(l.PhoneNumber)
                ? $"Phone only until {FmtEnd(pe)}"
                : $"Call {l.PhoneNumber} until {FmtEnd(pe)}");

        // Does it open later today? Find the first start time after now.
        var next = new[] { h?.WalkIn, h?.Phone }
            .Select(r => TryRange(r, out var s, out _) ? s : (TimeSpan?)null)
            .Where(s => s > t)
            .Min();
        if (next != null) return (SupportState.Closed, $"Opens at {Fmt(next.Value)}");

        bool weekend = now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        return (SupportState.Closed, weekend && h == null ? "Closed for the weekend" : "Closed for today");
    }

    // The week's hours as short lines, e.g. "Mon-Fri: phone 08:00-16:00". Equal days in a row are grouped.
    static readonly DayOfWeek[] WeekDays =
        { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday };
    static readonly string[] DayNames = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };

    public static string DescribeWeek(Location l)
    {
        static string Text(DayHours h)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(h?.WalkIn)) parts.Add($"walk-in {h.WalkIn}");
            if (!string.IsNullOrWhiteSpace(h?.Phone)) parts.Add($"phone {h.Phone}");
            return parts.Count == 0 ? "closed" : string.Join(", ", parts);
        }

        var lines = new List<string>();
        for (int i = 0; i < 7;)
        {
            var text = Text(l.HoursFor(WeekDays[i]));
            int j = i;
            while (j + 1 < 7 && Text(l.HoursFor(WeekDays[j + 1])) == text) j++;
            var days = i == j ? DayNames[i] : $"{DayNames[i]}-{DayNames[j]}";
            lines.Add($"{days}: {text}");
            i = j + 1;
        }
        return string.Join("\n", lines);
    }

    // "08:15" if today, otherwise e.g. "Fri 15:02" or "12 Sep 15:02".
    static string When(DateTime t, DateTime now)
    {
        if (t.Date == now.Date) return $"{t:HH:mm}";
        if (t.Date > now.Date.AddDays(-7)) return $"{DayNames[((int)t.DayOfWeek + 6) % 7]} {t:HH:mm}";
        return t.ToString("d MMM HH:mm", CultureInfo.InvariantCulture);
    }

    // Show a time as "08:00". 24 hours is shown as "24:00".
    public static string Fmt(TimeSpan t) => t >= TimeSpan.FromHours(24) ? "24:00" : t.ToString(@"hh\:mm");

    // An end time in a sentence: "16:00", or "midnight" for 24:00.
    static string FmtEnd(TimeSpan t) => t >= TimeSpan.FromHours(24) ? "midnight" : Fmt(t);

    // The label shown on the right of each location.
    public static string Label(SupportState state) => state switch
    {
        SupportState.Open => "OPEN",
        SupportState.PhoneOnly => "PHONE",
        SupportState.Forwarded => "FORWARDED",
        _ => "CLOSED"
    };

    // Turns "08:00-12:00" into start and end. false if the text is empty or invalid.
    public static bool TryRange(string s, out TimeSpan start, out TimeSpan end)
    {
        end = default;
        var parts = (s ?? "").Split('-');
        return TryTime(parts[0], out start) && parts.Length == 2 && TryTime(parts[1], out end) && start < end;
    }

    // Turns "08:00" into a time of day. "24:00" = end of the day.
    static bool TryTime(string s, out TimeSpan t)
    {
        s = s.Trim();
        if (s == "24:00") { t = TimeSpan.FromHours(24); return true; }
        return TimeSpan.TryParse(s, CultureInfo.InvariantCulture, out t) && t < TimeSpan.FromHours(24);
    }
}
