using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using ScreenTimeTracker.Models;
using ScreenTimeTracker.Services;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
var broad = new DateTime(2000, 1, 1);
var day = new DateTime(2024, 2, 29);
const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

if (args.FirstOrDefault() == "--native-rows")
{
    using var conn = new SqliteConnection(new SqliteConnectionStringBuilder {
        DataSource=args[1], Mode=SqliteOpenMode.ReadOnly, DefaultTimeout=2, Pooling=false }.ToString());
    conn.Open();
    using var command = conn.CreateCommand();
    command.CommandText = """
        SELECT lower(process_name),start_time,end_time,duration FROM app_usage
        WHERE end_time IS NOT NULL AND lower(process_name) IN
          ('screenytimelinealpha','screenytimelinebravo','idle / away')
        ORDER BY start_time
        """;
    using var rows = command.ExecuteReader();
    while (rows.Read())
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new {
            process=rows.GetString(0), start=rows.GetString(1), end=rows.GetString(2), duration=rows.GetInt64(3)
        }));
    return 0;
}
if (args.FirstOrDefault() == "--child")
{
    var kind = args[1];
    var path = args[2];
    using var db = new DatabaseService(path);
    Need(db.IsDatabaseInitialized(), "child database initialized");
    if (kind == "tx")
    {
        var seedTime = new DateTime(2024,2,29,1,0,0);
        Need(db.SaveSlice(Slice("Committed",seedTime,seedTime.AddSeconds(10))!), "commit control before forced exit");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource=path, Cache=SqliteCacheMode.Private, Pooling=false }.ToString());
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var q = connection.CreateCommand();
        q.Transaction = transaction;
        q.CommandText = "INSERT INTO app_usage(date,process_name,start_time,end_time,duration) VALUES ('2024-02-29','Uncommitted','2024-02-29T10:00:00','2024-02-29T10:00:10',10000);";
        Need(q.ExecuteNonQuery() == 1 && File.Exists(path + "-wal"), "WAL and uncommitted row present");
        Console.WriteLine("UNCOMMITTED");
        Thread.Sleep(Timeout.Infinite);
    }
    var writer = new UsageSliceWriter(db, (health, _) => {
        if (health == PersistenceHealthStatus.RetryableIssue && kind == "pending")
            Console.WriteLine("PENDING");
    });
    using var tracker = new WindowTrackingService();
    tracker.UsageSliceFinalized += (_, s) => writer.Enqueue(s);
    Seed(tracker, "ChildVisit", DateTime.Now.AddSeconds(-12));
    if (kind == "open")
    {
        Console.WriteLine(tracker.CurrentRecord?.ProcessName == "ChildVisit" ? "OPEN" : "BAD");
        Thread.Sleep(Timeout.Infinite);
    }
    if (kind == "pending") { Console.WriteLine("READY"); Need(Console.ReadLine() == "LOCKED", "parent lock handshake"); }
    tracker.StopTracking();
    if (kind == "pending") Thread.Sleep(Timeout.Infinite);
    Need(await writer.WaitForDrainAsync() && writer.Stop() == (0, false), "clean drain");
    Console.WriteLine("COMMITTED");
    return 0;
}

using (var c = new SqliteConnection("Data Source=:memory:"))
{
    c.Open();
    var native = Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
        .Select(m => m.FileName).FirstOrDefault(p => Path.GetFileName(p).StartsWith("e_sqlite3")
            || Path.GetFileName(p).StartsWith("libe_sqlite3"));
    var sqlite = Sql(c, "SELECT sqlite_version()")?.ToString();
    Console.WriteLine($"Platform={RuntimeInformation.OSDescription}, arch={RuntimeInformation.ProcessArchitecture}, .NET={Environment.Version}, SQLite={sqlite}, provider={typeof(SqliteConnection).Assembly.GetName().Version}, native={Path.GetFileName(native)}, sha256={(native == null ? null : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(native))))}");
    if (OperatingSystem.IsWindows()) Need(sqlite == "3.54.1" && native != null &&
        RuntimeInformation.ProcessArchitecture == Architecture.X64, "suite must load Windows x64 SQLite 3.54.1");
}

var tests = new List<(string Name, Action<DbCase> Probe)>();
void Case(string name, Action<DbCase> test) => tests.Add((name, test));
Case("T01-rollover", f => {
    foreach (var (name, midnight) in new[] {
        ("ordinary", new DateTime(2024,2,15)), ("leap-in", new DateTime(2024,2,29)),
        ("leap-out", new DateTime(2024,3,1)), ("week", new DateTime(2024,3,4)), ("year", new DateTime(2025,1,1)) })
    {
        using var t = new WindowTrackingService();
        var slices = Observe(t, f);
        var process = "Visit" + name;
        var start = midnight.AddSeconds(-5);
        Seed(t, process, start);
        t.UpdateFocusedRecordForTest(midnight.AddTicks(-1));
        Need(slices.Count == 0 && t.CurrentRecord?.StartTime == start, "before midnight: live only");
        t.UpdateFocusedRecordForTest(name == "ordinary" ? midnight : midnight.AddSeconds(3));
        t.UpdateFocusedRecordForTest(midnight.AddSeconds(20));
        Need(t.GetRecords().Single() is { } open && open.ProcessName == process &&
             open.StartTime == midnight && open.Date == midnight && open.EndTime == null &&
             open.WindowHandle == (nint)1 && open.ProcessId == 1 && open.IsFocused, "same eligible HWND continued at midnight");
        Need(slices.Count == 1 && f.Db.Total(midnight, midnight, process) == 0, "continuation stays live until stop");
        t.ClockForTest = () => midnight.AddSeconds(21);
        t.StopTracking();
        t.StopTracking();
        ExactRows(f, slices, (process, start, midnight), (process, midnight, midnight.AddSeconds(21)));
        Need(f.Db.Total(midnight.AddDays(-1), midnight.AddDays(-1), process) == 5000 &&
             f.Db.Total(midnight, midnight, process) == 21000, "per-day stored totals");
        Console.WriteLine($"T01 {name}: previous=5000ms next=21000ms, unchanged HWND continued at 00:00:00.000; continued record was not written before stop");
    }
});
Case("T02-crossing-before-tick", f => {
    using var t = new WindowTrackingService();
    var rows = Observe(t, f);
    var midnight = day.AddDays(1);
    var start = midnight.AddSeconds(-5);
    var idle = midnight.AddSeconds(3);
    var resume = midnight.AddSeconds(11);
    Seed(t, "Alpha", start);
    t.ApplyIdleStateForTest(true, idle);
    Need(t.GetRecords().Single().ProcessName == "Idle / Away", "idle replaces foreground");
    t.ApplyIdleStateForTest(false, resume);
    ExactRows(f, rows, ("Alpha", start, midnight), ("Alpha", midnight, idle), ("Idle / Away", idle, resume));
    Need(f.Db.Total(day, day, "Alpha") == 5000 && f.Db.Total(midnight, midnight, "Alpha") == 3000 &&
         f.Db.Total(day, midnight, "Alpha") == 8000 && f.Db.Total(midnight, midnight, "Idle / Away") == 8000, "exact range and date totals");
    Console.WriteLine("T02 idle edge before tick: Alpha=5000+3000ms, idle=8000ms; adjacent, no overlap");
});
Case("T03-idle-crossing", f => {
    using var t = new WindowTrackingService();
    var rows = Observe(t, f);
    t.SetOpenRecordsForTest(null, null);
    var midnight = day.AddDays(1);
    var idle = midnight.AddSeconds(-10);
    var resume = midnight.AddSeconds(120);
    t.ApplyIdleStateForTest(true, idle);
    t.ApplyIdleStateForTest(true, midnight.AddSeconds(110));
    t.ApplyIdleStateForTest(true, midnight.AddSeconds(115));
    Need(t.GetRecords().Single().StartTime == midnight && rows.Count == 1 &&
         f.Db.Total(midnight, midnight, "Idle / Away") == 0, "idle continuation waits for resume before saving");
    Need(t.ApplyIdleStateForTest(false, resume), "fresh foreground requested");
    Need(!t.ApplyIdleStateForTest(false, resume) && !t.GetRecords().Any(), "duplicate resume emits nothing");
    ExactRows(f, rows, ("Idle / Away", idle, midnight), ("Idle / Away", midnight, resume));
    Need(f.Db.Total(day, day, "Idle / Away") == 10000 && f.Db.Total(midnight, midnight, "Idle / Away") == 120000, "idle per-day totals");
    Console.WriteLine("T03 idle=10000+120000ms; open continuation never persisted; repeat tick/resume adds no rows");
});
Case("T04-wall-clock-gap-fold-rollback", f => {
    var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    void Dst(string name, DateTimeOffset start, DateTimeOffset end, int expectedWallMins)
    {
        var a = TimeZoneInfo.ConvertTime(start, zone);
        var b = TimeZoneInfo.ConvertTime(end, zone);
        var slice = Slice(name, a.DateTime, b.DateTime);
        var actual = (int)(slice?.Duration.TotalMinutes ?? -1);
        Console.WriteLine($"T04 {name} {a:HH:mm zzz} -> {b:HH:mm zzz} expected elapsed={(end-start).TotalMinutes}min; actual={actual}min (-1=no slice)");
        Need(actual == expectedWallMins, name);
        if (slice != null) Need(f.Db.SaveSlice(slice) && f.Db.Total(a.Date, a.Date, name) == actual * 60_000, "round trip");
    }
    var spring = DateTimeOffset.Parse("2024-03-10T06:50:00Z");
    var fall = DateTimeOffset.Parse("2024-11-03T05:50:00Z");
    Dst("Spring", spring, spring.AddMinutes(20), 80);
    Dst("FoldDrop", fall, fall.AddMinutes(20), -1);
    Dst("FoldUndercount", fall.AddMinutes(-10), fall.AddMinutes(90), 40);
    Dst("Control", spring.AddDays(1), spring.AddDays(1).AddMinutes(20), 20);
    Need(Slice("ClockRollback", day.AddHours(10), day.AddHours(9).AddMinutes(59)) == null, "rollback no slice");
    Need(Slice("ClockForward", day.AddHours(10), day.AddHours(11).AddMinutes(1))?.Duration.TotalMinutes == 61, "forward inflated");
    Console.WriteLine("T04 manual clock inputs: one minute elapsed with clock set back two minutes => no slice; with clock set ahead one hour => 61 minutes");
});
Case("T05-offset-read-and-culture", f => {
    f.Db.Init();
    var zone = TimeZoneInfo.Local;
    var off = zone.GetUtcOffset(day.AddHours(23)) == TimeSpan.FromHours(-8) ? "+09:00" : "-08:00";
    var start = day.ToString("yyyy-MM-dd") + "T23:59:00.0000000" + off;
    var end = day.ToString("yyyy-MM-dd") + "T23:59:10.0000000" + off;
    f.Exec("INSERT INTO app_usage(date,process_name,start_time,end_time,duration) VALUES ('2024-02-29','Travel',@s,@e,10000);", ("@s",start), ("@e",end));
    var travel = f.Db.GetRawRecordsForDateRange(day, day).Single();
    Need(travel.Date == day && travel.StartTime == DateTimeOffset.Parse(start).LocalDateTime && f.Db.Total(day, day, "Travel") == 10000, "travel projection");
    Console.WriteLine($"T05 captured {start}, current zone {zone.Id}; read Date={travel.Date:yyyy-MM-dd}, StartTime={travel.StartTime:o}, IsFromDate(capture)={travel.IsFromDate(day)}");
    var a = Slice("Gregorian", day, day.AddSeconds(10))!;
    var b = a with { ProcessName = "Buddhist" };
    Need(f.Db.SaveSlice(a), "culture control save");
    try { CultureInfo.CurrentCulture = new CultureInfo("th-TH"); Need(f.Db.SaveSlice(b), "Thai save"); }
    finally { CultureInfo.CurrentCulture = CultureInfo.InvariantCulture; }
    var keys = f.Cells("SELECT date FROM app_usage ORDER BY id;");
    var visible = f.Db.GetRawRecordsForDateRange(day, day).Select(r => r.ProcessName).ToArray();
    Console.WriteLine($"T05 expected both saves visible after language change. keys={string.Join(",",keys)}; read={string.Join(",",visible)}");
    Need(keys.SequenceEqual(new[]{"2024-02-29","2024-02-29","2567-02-29"}) && !visible.Contains("Buddhist"), "culture behavior changed");
});
Case("T06-fold-read-in-fixed-offset", f => {
    if (TimeZoneInfo.Local.GetUtcOffset(day) != TimeSpan.Zero)
    {
        Console.WriteLine("T06 skip: default UTC fixture zone required; T04 covers construction regardless of runner zone");
        return;
    }
    f.Db.Init();
    var a = day.AddHours(14);
    Need(f.Db.SaveSlice(Slice("Control", a, a.AddMinutes(40))!), "control");
    f.Exec("""
        INSERT INTO app_usage(date,process_name,start_time,end_time,duration)
        VALUES ('2024-02-29','Offset','2024-02-29T10:00:00-04:00','2024-02-29T10:40:00-05:00',2400000)
        """);
    var r = f.Db.GetRawRecordsForDateRange(day, day).Single(r => r.ProcessName == "Offset");
    Need(r.Duration.TotalMinutes == 40 && (r.EndTime!.Value-r.StartTime).TotalMinutes == 100, "stored hours disagree with endpoints");
    Console.WriteLine("T06 representative offset strings: stored duration 40min, UTC-projected interval 100min; fixed-offset no-change control 40min");
});
Case("L01-switch-and-duplicate", f => {
    using var t = new WindowTrackingService();
    var rows = Observe(t, f);
    t.SetOpenRecordsForTest(null, null);
    Focus(t, 11, 100, "Alpha", "Page 1");
    Thread.Sleep(15);
    Focus(t, 11, 100, "Alpha", "Page 1");
    Need(rows.Count == 0, "identical sample emitted a second slice");
    Focus(t, 22, 200, "Bravo", "Page B");
    Thread.Sleep(15);
    Focus(t, 11, 100, "Alpha", "Page 1");
    Thread.Sleep(15);
    Focus(t, 11, 100, "Alpha", "Page 2");
    Thread.Sleep(15);
    Focus(t, 33, 100, "Alpha", "Second window");
    Thread.Sleep(15);
    t.StopTracking();
    t.StopTracking();
    Need(rows.Select(r => r.ProcessName).SequenceEqual(new[]{"Alpha","Bravo","Alpha","Alpha","Alpha"})
        && rows.Zip(rows.Skip(1)).All(p => p.First.EndTime <= p.Second.StartTime)
        && rows.All(r => r.Duration.TotalMilliseconds > 0) && t.GetRecords().Count() == 0, "event order or process identity");
    var persisted = f.TodayRows();
    Need(persisted.Count == 5 && persisted.Count(r => r.ProcessName == "Alpha") == 4, "all shortened visits retained under stable process");
    Console.WriteLine("L01 ordered A→B→A, repeated event, same-HWND title sample, second HWND same PID: observed 5 ordered slices, 2 process identities, no overlap");
});
Case("L02-null-and-filtered-foreground", f => {
    using var t = new WindowTrackingService();
    var rows = Observe(t, f);
    Seed(t, "Alpha", DateTime.Now.AddSeconds(-10));
    typeof(WindowTrackingService).GetMethod("CheckActiveWindow", Private)!.Invoke(t, new object[]{Gen(t), IntPtr.Zero, null!});
    Need(t.CurrentRecord?.ProcessName == "Alpha" && rows.Count == 0, "null foreground behavior");
    Focus(t, 22, 0, "explorer", "shell");
    Thread.Sleep(15);
    t.StopTracking();
    Need(rows.Select(s => s.ProcessName).SequenceEqual(new[]{"Alpha","explorer"}) && f.TodayRows().Count == 2 &&
        ProcessFilter.ShouldIgnoreProcess("explorer") && !ProcessFilter.ShouldIgnoreProcess("Alpha"), "filter behavior");
    Console.WriteLine("L02 null sample leaves Alpha open. Filtered sample closes Alpha and a later stop still stores explorer; read-layer process filters are separate");
});
Case("L03-idle-overlap", f => {
    using var t = new WindowTrackingService();
    var rows = Observe(t, f);
    var midnight = day.AddDays(1);
    var now = midnight.AddSeconds(-12);
    var start = now;
    t.ClockForTest = () => now;
    Seed(t, "Alpha", now);
    int beforeIdle = Gen(t);
    now = midnight.AddSeconds(-10);
    Need(!t.ApplyIdleStateForTest(true, now) && t.CurrentRecord == null, "idle-entry control");
    int duringIdle = Gen(t);
    var queuedDuringIdle = Order(t);
    now = midnight.AddSeconds(-5);
    Focus(t, 22, 222, "Bravo", "focus changed without input");
    Need(t.GetRecords().Single().ProcessName == "Idle / Away", "foreground event must not become live while idle");
    now = midnight.AddSeconds(5);
    Need(t.ApplyIdleStateForTest(false, now), "resume requests fresh foreground");
    var resumed = now;
    Apply(t, "PreIdleCallback", beforeIdle, Order(t));
    Apply(t, "QueuedIdleCallback", duringIdle, queuedDuringIdle);
    Need(!t.GetRecords().Any(), "old foreground work may not open after resume");
    Focus(t, 22, 222, "Bravo", "focus changed without input");
    Focus(t, 22, 222, "Bravo", "focus changed without input");
    now = midnight.AddSeconds(10);
    t.StopTracking();
    ExactRows(f, rows, ("Alpha", start, midnight.AddSeconds(-10)),
        ("Idle / Away", midnight.AddSeconds(-10), midnight), ("Idle / Away", midnight, resumed), ("Bravo", resumed, now));
    Console.WriteLine("L03 app/idle/app nonoverlap; pre-idle and queued-while-idle callbacks rejected; Bravo starts on actual resume");
});
Case("L04-generation-and-reverse-completion", f => {
    using var t = new WindowTrackingService();
    var rows = Observe(t, f);
    Seed(t, "Old", DateTime.Now.AddSeconds(-20));
    var cancelled = Gen(t);
    t.StopTracking();
    Apply(t, "RejectedWhileStopped", cancelled, Order(t));
    t.SetOpenRecordsForTest(null, null);
    Apply(t, "RejectedAfterRestart", cancelled, Order(t));
    Need(t.CurrentRecord == null && rows.Count == 1, "old queued focus after stop/restart");
    var active = Gen(t);
    var delayed = Order(t);
    Apply(t, "NewestA", active, Order(t));
    Apply(t, "DelayedB", active, delayed);
    Need(t.CurrentRecord?.ProcessName == "NewestA" && rows.Count == 1, "reverse completion changed foreground or finalized prematurely");
    Console.WriteLine("L04 foreground from an old generation or an older completed observation did not supersede the current app");
});
Case("L05-stale-idle-jitter", f => {
    using var t = new WindowTrackingService();
    var rows = Observe(t, f);
    var now = DateTime.Now;
    Seed(t, "Alpha", now.AddSeconds(-30));
    t.ApplyIdleStateForTest(true, now.AddSeconds(-2));
    t.ApplyIdleStateForTest(true, now.AddSeconds(-1));
    Need(rows.Count == 1 && t.GetRecords().Count() == 1, "repeated idle should not duplicate");
    var idleGeneration = Gen(t);
    long preResume = Order(t);
    t.ApplyIdleStateForTest(false, now);
    var active = Gen(t);
    long delayed = Order(t);
    t.ApplyIdleStateForTest(false, now.AddMilliseconds(100));
    IdleObservation(t, true, now.AddMilliseconds(200), active, delayed);
    IdleObservation(t, true, now.AddMilliseconds(200), idleGeneration, preResume);
    Need(rows.Count == 2 && !t.GetRecords().Any(), "stale idle work did not reopen idle");
    Console.WriteLine("L05 repeated idle/resume emits one idle slice; late idle observations in the same and previous generation are rejected");
});
Case("T07-switch-at-and-after-midnight", f => {
    using var t = new WindowTrackingService();
    var rows = Observe(t, f);
    var midnight = day.AddDays(1);
    var now = midnight;
    t.ClockForTest = () => now;
    Seed(t, "Before", midnight.AddSeconds(-5));
    Focus(t, 22, 222, "At", "Midnight");
    t.UpdateFocusedRecordForTest(now);
    now = midnight.AddSeconds(5);
    Focus(t, 33, 333, "BeforeSecond", "Second midnight approaches");
    var dayTwo = midnight.AddDays(1);
    now = dayTwo.AddSeconds(-5);
    Focus(t, 55, 555, "Crossing", "Crossing before a tick");
    now = dayTwo.AddSeconds(3);
    Focus(t, 66, 666, "After", "After midnight");
    now = dayTwo.AddSeconds(10);
    t.StopTracking();
    ExactRows(f, rows, ("Before", midnight.AddSeconds(-5), midnight),
        ("At", midnight, midnight.AddSeconds(5)), ("BeforeSecond", midnight.AddSeconds(5), dayTwo.AddSeconds(-5)),
        ("Crossing", dayTwo.AddSeconds(-5), dayTwo), ("Crossing", dayTwo, dayTwo.AddSeconds(3)), ("After", dayTwo.AddSeconds(3), now));
    Console.WriteLine("T07 switch at boundary emits no empty segment; delayed-tick crossing uses shared split and old/new visit bounds touch");
});
Case("T08-pause-and-power-crossing", f => {
    var midnight = day.AddDays(1);
    var now = midnight.AddSeconds(-5);
    var host = new WindowHost(f.Db);
    var t = host._trackingService;
    t.ClockForTest = () => now;
    var rows = new List<UsageSlice>();
    t.UsageSliceFinalized += (_, slice) => rows.Add(slice);
    Seed(t, "Power", now);
    host.SetTrackingRequested(true);
    var old = Gen(t);
    now = midnight.AddSeconds(3);
    host.Suspend();
    host.Suspend();
    host.Display(0);
    Apply(t, "Stopped", old, Order(t));
    Need(!t.IsTracking && !t.GetRecords().Any(), "power gate closes once");
    now = midnight.AddHours(3);
    host.SetTrackingRequested(false);
    host.Display(1);
    host.Resume();
    Need(!t.IsTracking && !t.GetRecords().Any(), "power clear cannot cancel manual pause");
    host.SetTrackingRequested(true);
    Apply(t, "OldGeneration", old, Order(t));
    Need(t.IsTracking, "explicit resume only");
    t.SetOpenRecordsForTest(null, null);
    Focus(t, 79, 797, "Resumed", "After the gap");
    var next = midnight.AddDays(1);
    now = next;
    host.SetTrackingRequested(false);
    host.Dispose();
    host.Dispose();
    ExactRows(f, rows, ("Power", midnight.AddSeconds(-5), midnight),
        ("Power", midnight, midnight.AddSeconds(3)), ("Resumed", midnight.AddHours(3), next));
    Need(f.Db.Total(midnight, midnight, "Power") == 3000 && f.Db.Total(next,next,"Resumed") == 0 &&
         f.Db.Total(midnight,midnight,"Resumed") == 21 * 60 * 60 * 1000, "exact pause/offline totals");
    Console.WriteLine("T08 power and manual-pause boundaries: 5000+3000ms before suspend, no 3h gap, 21h after explicit resume, no empty next-day row");
});
Case("T09-submillisecond-boundary", f => {
    using var t = new WindowTrackingService();
    var rows = Observe(t, f);
    var midnight = day.AddDays(1);
    Seed(t, "Fraction", midnight.AddTicks(-1));
    t.UpdateFocusedRecordForTest(midnight);
    t.ClockForTest = () => midnight.AddMilliseconds(1);
    t.StopTracking();
    ExactRows(f, rows, ("Fraction", midnight, midnight.AddMilliseconds(1)));
    Console.WriteLine("T09 an interval below the SQLite ledger's 1ms resolution does not generate a zero-duration row");
});
Case("L06-power-reasons", f => {
    int k = 0;
    foreach (var protect in new[] { false, true })
    foreach (var order in new[]{"SDA","SAD","DSA","DAS","ADS","ASD"})
    {
        var host = new WindowHost(f.Db);
        host.SetTrackingRequested(true);
        Need(host._trackingService.IsTracking, "ordered sequence control tracking requested");
        Seed(host._trackingService, "Power" + k++, DateTime.Now.AddSeconds(-10));
        void Edge(char c, bool on)
        {
            if (c == 'S') { if (on) host.Suspend(); else host.Resume(); }
            else if (c == 'D') host.Display(on ? (byte)0 : (byte)1);
            else host.Away(on);
        }
        foreach (var c in "SDA") { Edge(c,true); Edge(c,true); }
        if (protect) host.SetTrackingRequested(false);
        for (int i = 0; i < order.Length; i++)
        {
            Edge(order[i],false);
            Edge(order[i],false);
            Need(host._trackingService.IsTracking == (!protect && i == 2), $"overlapping pause reasons: order={order}, manual={protect}, step={i}");
        }
        host.SetTrackingRequested(false);
        Need(host.Writer.WaitForDrainAsync().GetAwaiter().GetResult(), "power drain");
        Need(f.TodayRows().Count(r => r.ProcessName.StartsWith("Power")) == k, "each explicit pre-pause slice exactly once");
        host._trackingService.Dispose();
        host.Writer.Stop();
    }
    var dim = new WindowHost(f.Db);
    dim.SetTrackingRequested(true);
    dim.Display(0);
    dim.Display(2);
    Need(!dim._trackingService.IsTracking, "dim after off no longer retains off bit");
    dim.Display(1);
    Need(dim._trackingService.IsTracking, "display-on control");
    dim.SetTrackingRequested(false);
    dim._trackingService.Dispose();
    dim.Writer.WaitForDrainAsync().GetAwaiter().GetResult();
    dim.Writer.Stop();
    Console.WriteLine("L06 12 resume-order permutations with duplicate edges pass manual/automatic pause invariants; off→dim(2) kept tracking paused until on(1)");
});
Case("L07-close-drain-and-reopen", f => {
    f.Db.Init();
    var host = new WindowHost(f.Db);
    Seed(host._trackingService, "Closing", DateTime.Now.AddSeconds(-10));
    host.Dispose();
    host.Dispose();
    using var reopen = new DatabaseService(f.Path);
    Need(reopen.IsDatabaseInitialized() && reopen.GetRawRecordsForDateRange(DateTime.Today, DateTime.Today).Count(r => r.ProcessName == "Closing") == 1, "clean close with open slice");
    Console.WriteLine("L07 real disposal method body in fake window host: repeated Dispose persisted one open visit; reopened database read one row");
});

Case("D01-replay-and-contention", f => {
    var a = At("A",0);
    Need(f.Db.SaveSliceWithResult(a) == PersistenceResult.Saved && f.Db.SaveSliceWithResult(a) == PersistenceResult.DuplicateIgnored, "exact replay");
    var b = a with { EndTime = a.EndTime.AddSeconds(1), Duration = a.Duration.Add(TimeSpan.FromSeconds(1)) };
    Need(f.Db.SaveSliceWithResult(b) == PersistenceResult.Saved && f.Db.Total(day,day,"A") == 21000, "near-duplicate control");
    var notes = new ConcurrentQueue<PersistenceHealthStatus>();
    var writer = new UsageSliceWriter(f.Db, (s,_) => notes.Enqueue(s));
    using (var c = f.Open())
    using (var tx = c.BeginTransaction())
    {
        writer.Enqueue(At("B",1));
        writer.Enqueue(At("C",2));
        Until(() => notes.Contains(PersistenceHealthStatus.RetryableIssue));
        Need(f.Names().SequenceEqual(new[]{"A","A"}), "WAL reader sees old committed rows under private write lock");
        tx.Rollback();
    }
    Need(writer.WaitForDrainAsync().GetAwaiter().GetResult() && writer.Stop() == (0,false) &&
         f.Names().SequenceEqual(new[]{"A","A","B","C"}) && notes.Last() == PersistenceHealthStatus.Healthy, "FIFO recovery");
    Console.WriteLine("D01 write lock delays two accepted rows, concurrent WAL read preserves old snapshot, unlock commits FIFO, exact replay dedupes");
});
Case("D02-overflow-and-reset-status", f => {
    var notes = new ConcurrentQueue<PersistenceHealthStatus>();
    var db = f.Db;
    var writer = new UsageSliceWriter(db, (s,_) => notes.Enqueue(s));
    using (var c = f.Open())
    using (var tx = c.BeginTransaction())
    {
        writer.Enqueue(At("Head",0));
        Until(() => notes.Contains(PersistenceHealthStatus.RetryableIssue));
        for (int i = 1; i < 1024; i++) writer.Enqueue(At("Accepted" + i.ToString("D4"), i));
        writer.Enqueue(At("Overflow",1024));
        Need(f.Names().Length == 0 && notes.Last() == PersistenceHealthStatus.HistoryIncomplete, "overflow reported before unlock");
        tx.Rollback();
    }
    Until(() => f.Names().Length == 1024, 20);
    Need(writer.WaitForDrainAsync().GetAwaiter().GetResult(), "drain result after overflow");
    Need(writer.Stop() == (0,true) && notes.Last() == PersistenceHealthStatus.HistoryIncomplete && !f.Names().Contains("Overflow"), "loss sticky after drain");
    Need(writer.Reset() && f.Names().Length == 0 && notes.Last() == PersistenceHealthStatus.Healthy, "successful reset clears sticky loss");
    Console.WriteLine("D02 1025 offers under lock → 1024 durable; queue empty=true, Lost=true, HistoryIncomplete; reset clears it");
});
Case("D03-failed-reset-with-lock-and-reopen", f => {
    var db = f.Db;
    db.Init();
    Need(db.SaveSlice(At("Seed",0)), "preexisting usage");
    var host = new WindowHost(db);
    host.SetTrackingRequested(true);
    host._trackingService.SetOpenRecordsForTest(null, null);
    host.SetTrackingRequested(false);
    ContentDialog.OnShow = title => title == "Erase all screen-time data?" ? ContentDialogResult.Primary : ContentDialogResult.None;
    using (var c = f.Open())
    using (var tx = c.BeginTransaction())
    {
        host.Writer.Enqueue(At("Pending",1));
        Until(() => { host.DispatcherQueue.Pump(); return host._viewModel.PersistenceHealth == PersistenceHealthStatus.RetryableIssue; });
        var elapsed = Stopwatch.StartNew();
        host.OnResetDataRequested(null, EventArgs.Empty).GetAwaiter().GetResult();
        host.DispatcherQueue.Pump();
        var names = f.Names();
        Console.WriteLine($"D03 lock-held reset returned after {elapsed.Elapsed.TotalSeconds:F1}s; rows={string.Join(",", names)}; tracking={host._trackingService.IsTracking}; health={host._viewModel.PersistenceHealth}");
        Need(names.SequenceEqual(new[]{"Seed"}) && !host._trackingService.IsTracking &&
            host._viewModel.PersistenceHealth == PersistenceHealthStatus.RetryableIssue, "failed locked reset preserves committed data and pause");
        tx.Rollback();
    }
    Need(host.Writer.WaitForDrainAsync().GetAwaiter().GetResult(), "queued row retries after reset failure");
    host.Dispose();
    using var reopened = new DatabaseService(f.Path);
    Need(reopened.GetRawRecordsForDateRange(day,day).Select(r => r.ProcessName).SequenceEqual(new[]{"Seed","Pending"}), "relaunch after failed reset");
});
Case("D04-successful-reset-cuts-pending", f => {
    var notes = new ConcurrentQueue<PersistenceHealthStatus>();
    var writer = new UsageSliceWriter(f.Db, (s,_) => notes.Enqueue(s));
    Need(f.Db.SaveSlice(At("BeforeReset",0)), "seed prior to reset");
    using var c = f.Open();
    using var tx = c.BeginTransaction();
    writer.Enqueue(At("BeforePending",1));
    Until(() => notes.Contains(PersistenceHealthStatus.RetryableIssue));
    var release = Task.Run(async () => { await Task.Delay(1750); tx.Rollback(); });
    Need(writer.Reset(), "reset succeeded once lock released");
    release.GetAwaiter().GetResult();
    writer.Enqueue(At("AfterReset",2));
    Need(writer.WaitForDrainAsync().GetAwaiter().GetResult() && writer.Stop() == (0,false) &&
        f.Names().SequenceEqual(new[]{"AfterReset"}), "no replay of old queue");
    using var reopened = new DatabaseService(f.Path);
    Need(reopened.Total(day,day,"AfterReset") == 10000 &&
        reopened.GetRawRecordsForDateRange(day,day).Count == 1, "reopened reset boundary");
    Console.WriteLine("D04 only post-reset data survives, including across database reopen");
});
Case("D05-write-abort-and-fatal-sticky", f => {
    var notes = new ConcurrentQueue<PersistenceHealthStatus>();
    var writer = new UsageSliceWriter(f.Db, (s,_) => notes.Enqueue(s));
    f.Db.Init();
    f.Exec("""
        CREATE TRIGGER reject_after AFTER INSERT ON app_usage WHEN NEW.process_name='Reject'
        BEGIN SELECT RAISE(ABORT,'synthetic statement failure'); END;
        """);
    writer.Enqueue(At("First",0));
    writer.Enqueue(At("Reject",1));
    writer.Enqueue(At("Later",2));
    Need(writer.WaitForDrainAsync().GetAwaiter().GetResult() && writer.Stop() == (0,true) &&
        f.Names().SequenceEqual(new[]{"First","Later"}) && notes.Last() == PersistenceHealthStatus.HistoryIncomplete, "ABORT atomic; sticky status");
    using var reopened = new DatabaseService(f.Path);
    var freshNotes = new ConcurrentQueue<PersistenceHealthStatus>();
    var fresh = new UsageSliceWriter(reopened, (s,_) => freshNotes.Enqueue(s));
    fresh.Enqueue(At("AfterRestart",3));
    Need(fresh.WaitForDrainAsync().GetAwaiter().GetResult() && freshNotes.Last() == PersistenceHealthStatus.Healthy &&
        fresh.Stop() == (0,false) && !f.Names().Contains("Reject"), "restart loses sticky loss indicator");
    Console.WriteLine("D05 rejected insert after row work rolled back; later row saved, WaitForDrain=true despite loss; after restart status is Healthy and missing row stays missing");
});
Case("D06-child-clean-exit", f => {
    f.Layout();
    using var child = Child("clean", f.Path);
    Need(child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult() == "COMMITTED", "clean child checkpoint (stderr: " + (child.HasExited ? child.StandardError.ReadToEnd() : "") + ")");
    Need(child.WaitForExit(5000) && child.ExitCode == 0, "clean exit");
    Need(f.Db.IsDatabaseInitialized() && f.TodayRows().Count(r => r.ProcessName=="ChildVisit") == 1, "fresh parent service reads child visit");
    Console.WriteLine("D06 separate clean process finalized and drained its visit; restarted service sees one durable row");
});
Case("D07-child-kill-open-and-pending", f => {
    Need(f.Db.SaveSlice(At("DurableBeforeKill",0)), "preexisting data");
    using (var child = Child("open", f.Path))
    {
        try { Need(child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult() == "OPEN", "in-memory open live record"); }
        finally { Kill(child); }
    }
    Need(f.TodayRows().All(r => r.ProcessName != "ChildVisit"), "open visit not durable");
    using (var child = Child("pending", f.Path))
    {
        try
        {
            Need(child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult() == "READY", "child initialized before lock");
            using var c = f.Open();
            using var tx = c.BeginTransaction();
            child.StandardInput.WriteLine("LOCKED");
            child.StandardInput.Flush();
            Need(child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult() == "PENDING", "actual SQLite retry signaled by child");
            Kill(child);
            tx.Rollback();
        }
        finally { if (!child.HasExited) Kill(child); }
    }
    using var reopened = new DatabaseService(f.Path);
    Need(reopened.IsDatabaseInitialized() && reopened.Total(day,day,"DurableBeforeKill") == 10000 &&
        reopened.GetRawRecordsForDateRange(broad,DateTime.Today).Count == 1, "committed rows survive; in-memory queue does not");
    Console.WriteLine("D07 killed only test children after observed OPEN and PENDING signals; restart preserved seed, neither unsaved visit, and has no persistent loss indication");
});
Case("D08-migrations-valid", f => {
    f.Layout();
    f.Exec("""
        CREATE TABLE app_usage(id INTEGER PRIMARY KEY,process_name TEXT NOT NULL,
          app_name TEXT,start_time TEXT NOT NULL,end_time TEXT,duration INTEGER);
        INSERT INTO app_usage VALUES (1,'Legacy','Legacy','2024-02-29 11:59:50','2024-02-29 12:00:00',10000);
        INSERT INTO app_usage VALUES (2,'Legacy','Legacy','2024-03-01 00:00:00','2024-03-01 00:00:20',20000);
        """);
    Need(f.Db.SchemaVersion == 2 && f.Db.CurrentJournalMode == "wal" &&
        f.Db.Total(day,day,"Legacy") == 10000 && f.Db.Total(day.AddDays(1),day.AddDays(1),"Legacy") == 20000, "v0 migration");
    Need(f.Db.SaveSlice(At("New",0)), "v2 insert control");
    using var reopened = new DatabaseService(f.Path);
    Need(reopened.IsDatabaseInitialized() && reopened.Total(day,day.AddDays(1),"Legacy") == 30000 && reopened.Total(day,day,"New") == 10000, "repeat migration idempotent");
    Console.WriteLine("D08 pre-date v0 migration kept Feb 29 and Mar 1 totals; restart v2 preserved old and new rows");
});
Case("D09-future-schema", f => {
    f.Layout();
    f.Exec("""
        CREATE TABLE app_usage(id INTEGER PRIMARY KEY,date TEXT NOT NULL,process_name TEXT NOT NULL,
          app_name TEXT,start_time TEXT NOT NULL,end_time TEXT,duration INTEGER,is_focused INTEGER,last_updated TEXT);
        INSERT INTO app_usage VALUES (1,'2024-02-29','Future','Future','2024-02-29T10:00:00','2024-02-29T10:00:10',10000,0,NULL);
        PRAGMA user_version=99;
        """);
    Need(f.Db.IsDatabaseInitialized() && f.Db.SchemaVersion == 2 && f.Db.Total(day,day,"Future") == 10000, "future version rewrite");
    Console.WriteLine("D09 expected unknown v99 rejected read-only and unchanged; actual initialized true, user_version changed to 2");
});
Case("D10-partial-schema", f => {
    f.Layout();
    f.Exec("""
        CREATE TABLE app_usage(id INTEGER PRIMARY KEY,date TEXT NOT NULL,process_name TEXT NOT NULL,
           app_name TEXT,start_time TEXT NOT NULL,end_time TEXT,is_focused INTEGER,last_updated TEXT);
        INSERT INTO app_usage VALUES (1,'2024-02-29','Partial','Partial','2024-02-29T10:00:00','2024-02-29T10:00:10',0,NULL);
        PRAGMA user_version=2;
        """);
    bool listingThrows = false;
    try { f.Db.GetUsageReportForDateRange(day,day); }
    catch (SqliteException) { listingThrows = true; }
    Need(f.Db.IsDatabaseInitialized() && f.Db.GetRawRecordsForDateRange(day,day).Count == 0 &&
        f.Db.SaveSliceWithResult(At("Test",0)) == PersistenceResult.FatalFailure && listingThrows &&
        f.Names().SequenceEqual(new[]{"Partial"}), "partial schema false health");
    Console.WriteLine("D10 valid on-disk SQLite, v2 missing duration: initialization true; raw history empty; range throws; next insert fatal; original row survives");
});
Case("D11-partial-date-migration", f => {
    f.Layout();
    f.Exec("""
        CREATE TABLE app_usage(id INTEGER PRIMARY KEY,date TEXT,process_name TEXT NOT NULL,
           app_name TEXT,start_time TEXT NOT NULL,end_time TEXT,duration INTEGER);
        INSERT INTO app_usage VALUES (1,NULL,'Undated','Undated','2024-02-29T10:00:00','2024-02-29T10:00:10',10000),
                                     (2,'2024-02-29','Valid','Valid','2024-02-29T10:00:10','2024-02-29T10:00:20',10000);
        PRAGMA user_version=1;
        """);
    Need(f.Db.IsDatabaseInitialized() && f.Db.SchemaVersion == 2 && f.Db.Total(day,day,"Valid") == 10000 &&
        f.Db.Total(day,day,"Undated") == 0 && f.Names().Length == 2, "partial migration invisibility");
    Console.WriteLine("D11 nullable legacy date layout: two rows on disk, v2 reports initialized, one row still missing from every date query");
});
Case("D12-legacy-exact-duplicate-conflict", f => {
    f.Layout();
    f.Exec("""
        CREATE TABLE app_usage(id INTEGER PRIMARY KEY,date TEXT NOT NULL,process_name TEXT NOT NULL,
          app_name TEXT,start_time TEXT NOT NULL,end_time TEXT,duration INTEGER,is_focused INTEGER,last_updated TEXT);
        INSERT INTO app_usage VALUES
          (1,'2024-02-29','Legacy','Legacy','2024-02-29T10:00:00','2024-02-29T10:00:20',0,0,NULL),
          (2,'2024-02-29','Legacy','Legacy','2024-02-29T10:00:00','2024-02-29T10:00:20',20000,0,NULL),
          (3,'2024-02-29','Separate','Separate','2024-02-29T11:00:00','2024-02-29T11:00:20',20000,0,NULL);
        PRAGMA user_version=2;
        """);
    Need(f.Db.IsDatabaseInitialized() && f.Names().SequenceEqual(new[]{"Legacy","Separate"}) &&
        f.Db.Total(day,day,"Legacy") == 0 && f.Db.Total(day,day,"Separate") == 20000, "payload conflict");
    Console.WriteLine("D12 duplicate keys but unequal payload: startup deleted valid 20-second row and left earliest zero-duration row; independent 20-second row intact");
});
Case("D13-corruption-backup-and-sidecars", f => {
    f.Layout();
    var junk = System.Text.Encoding.ASCII.GetBytes(new string('Z', 4096));
    File.WriteAllBytes(f.Path, junk);
    var wal = f.Path + "-wal";
    File.WriteAllBytes(wal, junk);
    using (var first = new DatabaseService(f.Path))
        Need(!first.IsDatabaseInitialized() && File.ReadAllBytes(f.Path).SequenceEqual(junk)
             && File.Exists(wal) && !Directory.EnumerateFiles(f.Root,"*.corrupt.*").Any(), "sidecars protect main file");
    Console.WriteLine($"D13 protected path files after check: {string.Join(",",Directory.EnumerateFiles(f.Root).Select(System.IO.Path.GetFileName))}");
    var noSidecar = System.IO.Path.Combine(f.Root,"standalone.sqlite");
    File.WriteAllBytes(noSidecar,junk);
    using var backup = new DatabaseService(noSidecar);
    var files = Directory.EnumerateFiles(f.Root).Select(System.IO.Path.GetFileName).ToArray();
    Console.WriteLine($"D13 no-sidecar path initialized={backup.IsDatabaseInitialized()}, files={string.Join(",",files)}");
    Need(backup.IsDatabaseInitialized() && Directory.EnumerateFiles(f.Root,"standalone.sqlite.corrupt.*").Count() == 1 &&
        File.ReadAllBytes(Directory.EnumerateFiles(f.Root,"standalone.sqlite.corrupt.*").Single()).SequenceEqual(junk) &&
        backup.SaveSlice(At("HealthyReplacement",0)), "confirmed NOTADB with no sidecars archives and starts fresh");
    Console.WriteLine("D13 corrupt source with sidecar refused replacement. Independent sidecar-free NOTADB archived exact bytes and created writable replacement.");
});
Case("D14-inaccessible-startup-latch", f => {
    f.Layout();
    var path = System.IO.Path.Combine(f.Root, "new", "db.sqlite");
    var block = System.IO.Path.GetDirectoryName(path)!;
    File.WriteAllText(block, "this is a file");
    using var wedged = new DatabaseService(path);
    Need(!wedged.IsDatabaseInitialized() && wedged.SaveSliceWithResult(At("Unavailable",0)) == PersistenceResult.FatalFailure &&
        File.ReadAllText(block) == "this is a file", "unavailable path safe refusal");
    File.Delete(block);
    Need(!wedged.IsDatabaseInitialized() && wedged.SaveSliceWithResult(At("AfterRepair",1)) == PersistenceResult.FatalFailure &&
        !File.Exists(path), "same instance cannot recover");
    using var newInstance = new DatabaseService(path);
    Need(newInstance.IsDatabaseInitialized() && newInstance.SaveSlice(At("AfterRestart",2)), "new instance recovers");
    Console.WriteLine("D14 inaccessible storage preserved obstruction. Same database service remained fatal after access was restored; restart recovered.");
});
Case("D15-native-windows-share-denied", f => {
    if (!OperatingSystem.IsWindows()) { Console.WriteLine("D15 skip: Windows file-sharing behavior only"); return; }
    f.Layout();
    using (var db = new DatabaseService(f.Path))
        Need(db.SaveSlice(At("Original",0)), "seed");
    SqliteConnection.ClearAllPools();
    var original = File.ReadAllBytes(f.Path);
    using (var access = new FileStream(f.Path, FileMode.Open, FileAccess.Read, FileShare.None))
    using (var blocked = new DatabaseService(f.Path))
        Need(!blocked.IsDatabaseInitialized() && !Directory.EnumerateFiles(f.Root,"*.corrupt.*").Any(), "sharing denied must not quarantine");
    Need(File.ReadAllBytes(f.Path).SequenceEqual(original), "byte identity");
    using var reopen = new DatabaseService(f.Path);
    Need(reopen.Total(day,day,"Original") == 10000, "reopen after sharing access release");
    Console.WriteLine("D15 Win32 FileShare.None blocks startup without archive/replacement; release and reopen preserves exact bytes and usage");
});
Case("D16-retention-boundaries", f => {
    var db = f.Db;
    var limit = DateTime.Today.AddDays(-7);
    foreach (var (i,n) in new[]{(-1,"Expired"),(0,"Cutoff"),(1,"Kept"),(7,"Today")})
    {
        var a = limit.AddDays(i).AddHours(1);
        Need(db.SaveSlice(Slice(n,a,a.AddSeconds(10))!), "retention seed");
    }
    db.CleanupExpiredRecords(7);
    db.CleanupExpiredRecords(7);
    Need(f.Names().SequenceEqual(new[]{"Cutoff","Kept","Today"}) && db.Total(limit,DateTime.Today,"Cutoff") == 10000, "retention cutoff and replay");
    Console.WriteLine("D16 explicit unused retention operation removed strictly older day, kept cutoff and Today; repeat unchanged");
});
Case("D17-raw-versus-sql-legacy-row", f => {
    f.Db.Init();
    f.Exec("""
        INSERT INTO app_usage(date,process_name,start_time,end_time,duration)
        VALUES ('2024-02-29','Invalid','not a datetime','bad',65000),
               ('2024-02-29','Open','2024-02-29T10:00:00',NULL,77000),
               ('2024-02-29','Valid','2024-02-29T11:00:00','2024-02-29T11:00:10',10000);
        """);
    var raw = f.Db.GetRawRecordsForDateRange(day,day);
    Need(raw.Select(r => r.ProcessName).SequenceEqual(new[]{"Open","Valid"}) &&
        f.Db.Total(day,day,"Invalid") == 65000 && f.Db.Total(day,day,"Open") == 77000 && f.Db.Total(day,day,"Valid") == 10000, "range sum includes unparseable and unfinished rows");
    Console.WriteLine("D17 range sums 65s malformed + 77s unfinished + 10s healthy, but raw query drops malformed row and leaves unfinished open");
});

Case("D18-startup-busy-latch", f => {
    Need(f.Db.SaveSlice(At("Seed",0)), "healthy DB before lock");
    DatabaseService blocked;
    var elapsed = Stopwatch.StartNew();
    using (var c = f.Open())
    using (var tx = c.BeginTransaction())
    {
        blocked = new DatabaseService(f.Path);
        Need(!blocked.IsDatabaseInitialized() && f.Names().SequenceEqual(new[]{"Seed"}) &&
            !Directory.EnumerateFiles(f.Root,"*.corrupt.*").Any(), "busy startup must not replace DB");
        tx.Rollback();
    }
    using (blocked)
    {
        Need(!blocked.IsDatabaseInitialized() &&
            blocked.SaveSliceWithResult(At("AfterUnlock",1)) == PersistenceResult.FatalFailure,
            "latch still rejects after transient lock released");
    }
    using var second = new DatabaseService(f.Path);
    Need(second.IsDatabaseInitialized() && second.Total(day,day,"Seed") == 10000 &&
        second.SaveSlice(At("AfterRestart",2)), "new instance can read and write same history");
    Console.WriteLine($"D18 under startup lock: {elapsed.Elapsed.TotalSeconds:F1}s elapsed; failed/latches after unlock; no corruption backup; new instance recovered");
});
Case("D19-abrupt-wal-transaction-reopen", f => {
    using (var child = Child("tx",f.Path))
    {
        try
        {
            Need(child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult() == "UNCOMMITTED", "actual child transaction checkpoint");
            Need(File.Exists(f.Path+"-wal"), "WAL present while killed child holds write");
        }
        finally { Kill(child); }
    }
    var walAfterKill = File.Exists(f.Path+"-wal");
    Need(f.Db.IsDatabaseInitialized() && f.Names().SequenceEqual(new[]{"Committed"}) &&
        f.Db.Total(day,day,"Committed") == 10000 && f.Db.SaveSlice(At("AfterRecovery",1)), "recovery atomic across abrupt OS process exit");
    Console.WriteLine($"D19 child killed with an uncommitted transaction. WAL after kill={walAfterKill}; committed row survived, uncommitted row did not, and new writes work.");
});
Case("D20-failed-migration-backfill-rolls-back", f => {
    f.Exec("""
        CREATE TABLE app_usage(id INTEGER PRIMARY KEY,process_name TEXT NOT NULL,
          app_name TEXT,start_time TEXT NOT NULL,end_time TEXT,duration INTEGER);
        INSERT INTO app_usage VALUES (1,'Seed','Seed','2024-02-29T10:00:00','2024-02-29T10:00:10',10000);
        CREATE TRIGGER fail_backfill BEFORE UPDATE ON app_usage BEGIN SELECT RAISE(ABORT,'synthetic migration failure'); END;
        """);
    Need(!f.Db.IsDatabaseInitialized() && !f.Cells("SELECT name FROM pragma_table_info('app_usage');").Contains("date") &&
        f.Names().SequenceEqual(new[]{"Seed"}) && !Directory.EnumerateFiles(f.Root,"*.corrupt.*").Any(), "backfill failure retained version-zero table and row");
    var marker = f.Cells("PRAGMA user_version;").Single();
    Need(marker == "2", "failed V1 still publishes V2 marker");
    f.Exec("DROP TRIGGER fail_backfill;");
    using (var retry = new DatabaseService(f.Path))
        Need(!retry.IsDatabaseInitialized() && f.Names().SequenceEqual(new[]{"Seed"}), "restart skips V1 despite restored environment");
    f.Exec("PRAGMA user_version=0;");
    using var repaired = new DatabaseService(f.Path);
    Need(repaired.IsDatabaseInitialized() && repaired.Total(day,day,"Seed")==10000, "migration control after rewinding disposable marker");
    Console.WriteLine("D20 failed V1 backfill rolled back date, retained data, but V2 stamped version=2. Removing fault did not recover on restart. Rewinding only the synthetic version marker allowed re-migration.");
});

var selected = args.Length == 0 ? tests : tests.Where(t => args.Contains(t.Name)).ToList();
if (selected.Count == 0) throw new ArgumentException("No selected cases");
var errors = 0;
foreach (var (name, probe) in selected)
{
    var sw = Stopwatch.StartNew();
    try { using var f = new DbCase(); probe(f); Console.WriteLine($"OBSERVED {name} {sw.Elapsed.TotalSeconds:F2}s"); }
    catch (Exception ex) { Console.WriteLine($"FAILED {name}: {ex}"); errors++; }
}
Console.WriteLine($"Fixture expectation mismatches={errors}, executed={selected.Count}. OBSERVED includes intentionally reproduced defects.");
return errors == 0 ? 0 : 1;

UsageSlice At(string process, int i) => Slice(process, day.AddMinutes(i), day.AddMinutes(i).AddSeconds(10))!;
AppUsageRecord OpenRecord(string process, DateTime dt) => new() {
    ProcessName=process, ApplicationName=process, StartTime=dt, Date=dt.Date, IsFocused=true,
    ProcessId=1, WindowHandle=(nint)1
};
void Seed(WindowTrackingService t, string process, DateTime dt) => t.SetOpenRecordsForTest(OpenRecord(process,dt),null);
List<UsageSlice> Observe(WindowTrackingService t, DbCase f)
{
    var list = new List<UsageSlice>();
    t.UsageSliceFinalized += (_,s) => { list.Add(s); Need(f.Db.SaveSlice(s), "test observer save"); };
    return list;
}
int Gen(WindowTrackingService t) => (int)typeof(WindowTrackingService).GetMethod("GetTrackingGeneration", Private)!.Invoke(t,null)!;
long Order(WindowTrackingService t) => (long)typeof(WindowTrackingService).GetMethod("NextObservation", Private)!.Invoke(t,null)!;
void Apply(WindowTrackingService t, string name, int generation, long order)
    => typeof(WindowTrackingService).GetMethod("ProcessWindowChange", Private)!.Invoke(t, new object[]{ (nint)name.Length, 0, name, name, generation, order });
void IdleObservation(WindowTrackingService t, bool idle, DateTime now, int generation, long order)
    => typeof(WindowTrackingService).GetMethod("ApplyIdleObservation", Private)!.Invoke(t, new object[]{ idle, now, generation, order });
void ExactRows(DbCase f, List<UsageSlice> slices, params (string Process, DateTime Start, DateTime End)[] expected)
{
    var names = expected.Select(x => x.Process).ToHashSet();
    Need(slices.Where(x => names.Contains(x.ProcessName)).Select(x => (x.ProcessName, x.StartTime, x.EndTime)).SequenceEqual(expected), "unexpected finalized event order/endpoints");
    using var db = f.Open();
    using var q = db.CreateCommand();
    q.CommandText = "SELECT date,process_name,start_time,end_time,duration,is_focused FROM app_usage ORDER BY id";
    using var r = q.ExecuteReader();
    var persisted = new List<(string Process, DateTime Start, DateTime End)>();
    while (r.Read())
    {
        if (!names.Contains(r.GetString(1))) continue;
        Need(!r.IsDBNull(3) && r.GetInt64(4) > 0 && r.GetInt64(5) == 0, "only positive finalized rows");
        var start = DateTime.Parse(r.GetString(2), null, DateTimeStyles.RoundtripKind);
        var end = DateTime.Parse(r.GetString(3), null, DateTimeStyles.RoundtripKind);
        Need(r.GetString(0) == start.Date.ToString("yyyy-MM-dd") && end > start &&
             end <= start.Date.AddDays(1) && r.GetInt64(4) == (long)(end-start).TotalMilliseconds,
             "bucket, duration, or exact timestamp");
        persisted.Add((r.GetString(1), start, end));
    }
    Need(persisted.SequenceEqual(expected) && persisted.Zip(persisted.Skip(1)).All(p => p.First.End <= p.Second.Start),
         "stored endpoints/order, nonoverlap, or duplicate");
}
static void Focus(WindowTrackingService t, int hwnd, int pid, string name, string title) => t.ProcessWindowChangeForTest((nint)hwnd,pid,name,title);
static UsageSlice? Slice(string name, DateTime a, DateTime b)
    => UsageSlice.TryCreate(name,name,"synthetic",a,b,out var s) ? s : null;
static object? Sql(SqliteConnection c, string sql)
{
    using var q = c.CreateCommand();
    q.CommandText = sql;
    return q.ExecuteScalar();
}
static void Need(bool ok, string label)
{
    if (!ok) throw new InvalidOperationException(label);
}
static void Until(Func<bool> ready, int seconds=10)
{
    var deadline = Stopwatch.StartNew();
    while (!ready())
    {
        if (deadline.Elapsed.TotalSeconds >= seconds) throw new TimeoutException("observation timed out");
        Thread.Sleep(20);
    }
}
static Process Child(string action, string path)
{
    var info = new ProcessStartInfo(Environment.ProcessPath!) {
        RedirectStandardOutput=true, RedirectStandardError=true, RedirectStandardInput=true, UseShellExecute=false,
    };
    if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    foreach (var arg in new[]{"--child",action,path}) info.ArgumentList.Add(arg);
    return Process.Start(info)!;
}
static void Kill(Process child)
{
    if (!child.HasExited) child.Kill();
    Need(child.WaitForExit(5000) && child.ExitCode != 0, "abrupt test-child termination");
}

sealed class DbCase : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("screeny-arn85-").FullName;
    public string Path => System.IO.Path.Combine(Root,"fixture.sqlite");
    private DatabaseService? _db;
    public DatabaseService Db => _db ??= new DatabaseService(Path);
    public void Layout() => Directory.CreateDirectory(Root);
    public SqliteConnection Open()
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=Path, Cache=SqliteCacheMode.Private, Pooling=false }.ToString());
        c.Open();
        return c;
    }
    public void Exec(string sql, params (string Name, object Value)[] values)
    {
        using var c = Open();
        using var q = c.CreateCommand();
        q.CommandText = sql;
        foreach (var (n,v) in values) q.Parameters.AddWithValue(n,v);
        q.ExecuteNonQuery();
    }
    public string[] Names() => Cells("SELECT process_name FROM app_usage ORDER BY id;");
    public string[] Cells(string sql)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        var cells = new List<string>();
        while (r.Read()) cells.Add(r.IsDBNull(0) ? "<null>" : r.GetString(0));
        return cells.ToArray();
    }
    public List<AppUsageRecord> TodayRows() => Db.GetRawRecordsForDateRange(DateTime.Today,DateTime.Today);
    public void Dispose()
    {
        _db?.Dispose();
        for (var retry = 0; ; retry++)
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(Root,true); break; }
            catch (IOException) when (retry < 10) { Thread.Sleep(100); }
        }
    }
}
static class DbExtensions
{
    public static long Total(this DatabaseService db, DateTime from, DateTime to, string process)
        => (long)db.GetUsageReportForDateRange(from,to).SingleOrDefault(r=>r.ProcessName==process).TotalDuration.TotalMilliseconds;
    public static void Init(this DatabaseService db)
    {
        if (!db.IsDatabaseInitialized()) throw new InvalidOperationException("SQLite fixture did not initialize");
    }
}
