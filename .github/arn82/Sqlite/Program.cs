using Microsoft.Data.Sqlite;

var mode = args[1];
var builder = new SqliteConnectionStringBuilder
{
    DataSource = args[0],
    Mode = mode is "count" or "duplicate" or "history" or "overlap" ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
    DefaultTimeout = 2,
    Pooling = false
};
using var db = new SqliteConnection(builder.ToString());
db.Open();
using var cmd = db.CreateCommand();
if (mode == "hold")
{
    using var transaction = db.BeginTransaction(deferred: false);
    File.WriteAllText(args[2], "held");
    var deadline = DateTime.UtcNow.AddSeconds(28);
    while (!File.Exists(args[3]) && DateTime.UtcNow < deadline) Thread.Sleep(40);
    transaction.Rollback();
    return;
}
cmd.CommandText = mode switch
{
    "count" => "SELECT COUNT(*) FROM app_usage WHERE lower(process_name) LIKE 'screenyaudit%';",
    "history" => "SELECT id || '/' || lower(process_name) || '/' || start_time || '/' || end_time || '/' || duration FROM app_usage WHERE lower(process_name) IN ('screenyauditalpha','screenyauditactiveone','screenyauditactivetwo','screenyauditpausedone','screenyauditpausedtwo') ORDER BY id;",
    "overlap" => "SELECT COUNT(*) FROM app_usage a JOIN app_usage b ON a.id < b.id WHERE lower(a.process_name) IN ('screenyauditalpha','screenyauditactiveone','screenyauditactivetwo','screenyauditpausedone','screenyauditpausedtwo') AND lower(b.process_name) IN ('screenyauditalpha','screenyauditactiveone','screenyauditactivetwo','screenyauditpausedone','screenyauditpausedtwo') AND a.start_time < b.end_time AND b.start_time < a.end_time;",
    "duplicate" => "SELECT COUNT(*) || '/' || COALESCE(MAX(CAST((julianday(MIN(a.end_time,b.end_time))-julianday(MAX(a.start_time,b.start_time)))*86400000 AS INTEGER)),0) FROM app_usage a JOIN app_usage b ON a.id < b.id WHERE lower(a.process_name) LIKE 'screenyauditalpha%' AND lower(b.process_name) LIKE 'screenyauditalpha%' AND a.start_time < b.end_time AND b.start_time < a.end_time;",
    "abort-delete" => "CREATE TRIGGER arn82_abort BEFORE DELETE ON app_usage BEGIN SELECT RAISE(ABORT, 'synthetic deletion failure'); END;",
    "clear-abort" => "DROP TRIGGER arn82_abort;",
    _ => throw new ArgumentOutOfRangeException(nameof(args))
};
if (mode == "history")
{
    using var reader = cmd.ExecuteReader();
    while (reader.Read()) Console.WriteLine(reader.GetString(0));
}
else if (mode is "count" or "duplicate" or "overlap") Console.WriteLine(cmd.ExecuteScalar());
else cmd.ExecuteNonQuery();
