#!/usr/bin/env dotnet
// read-codex-log.cs — dump a codex session's dispatch log for the flywheel verdict.
//
// Run with the .NET 10 file-based-app runner (no csproj needed):
//
//     dotnet run read-codex-log.cs -- <path-to-logs_2.sqlite> [sinceUnixSeconds] [outFile]
//
// WHY THIS EXISTS. codex records its tool-router outcome — including the
// `Fatal error: tool exec invoked with incompatible payload` dispatch fatal —
// ONLY in logs_2.sqlite. The bridge stays HTTP 200 with function_call on the wire
// while exec is 100% broken, so the flywheel's verdict MUST read this DB, not the
// bridge trace or the client exit code. There is no `sqlite3` binary on this box and
// the DB is a multi-MB binary, so this tiny reader is the one deterministic helper
// the verdict agent invokes from chat.
//
// It reads the DB read-only (never locks a live codex), pulls the recent rows and the
// ERROR / tool-router rows in particular, and writes a readable text digest the agent
// then Reads. It renders NO verdict itself — it surfaces the evidence; the agent
// judges. Exit code is 0 on success regardless of what the log contains (a found
// fatal is data, not a script failure).
//
// NOTE: this repo uses Central Package Management (Directory.Packages.props at the
// repo root), which forbids a Version on a PackageReference. A file-based app under
// the repo would inherit that and fail to build with a pinned #:package version, so
// we opt this app OUT of CPM below — it is a standalone tool, not part of the build.

#:property ManagePackageVersionsCentrally=false
#:package Microsoft.Data.Sqlite@9.0.4

using Microsoft.Data.Sqlite;
using System.Text;

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: dotnet run read-codex-log.cs -- <logs_2.sqlite> [sinceUnixSeconds] [outFile]");
    return 2;
}

var dbPath = args[0];
if (!File.Exists(dbPath))
{
    Console.Error.WriteLine($"logs_2.sqlite not found: {dbPath}");
    return 2;
}

long since = args.Length >= 2 && long.TryParse(args[1], out var s) ? s : 0;
string? outFile = args.Length >= 3 ? args[2] : null;

// Copy the DB to a temp file before opening: a live codex holds a WAL lock, and even
// ReadOnly open can contend. A snapshot copy is always safe to read. CRITICAL: codex
// runs in WAL mode, and recent rows often live ONLY in the `-wal` sidecar, not yet
// checkpointed into the main file. So we MUST copy the -wal/-shm alongside AND open the
// snapshot READ-WRITE (not ReadOnly): a ReadOnly open will not replay the WAL, so those
// rows stay invisible and the digest wrongly reports zero fatals. The snapshot is a
// throwaway copy and codex has already exited by verdict time, so read-write here is
// safe and forces SQLite to fold the WAL in on connect.
var snapshot = Path.Combine(Path.GetTempPath(), $"codexlog-{Guid.NewGuid():N}.sqlite");
File.Copy(dbPath, snapshot, overwrite: true);
// Whether the source has a -wal sidecar, recorded so the digest can say so — a run's
// recent rows usually live in the WAL, so its presence is expected right after a run.
var hadWal = File.Exists(dbPath + "-wal");
foreach (var side in new[] { "-wal", "-shm" })
{
    var extra = dbPath + side;
    if (!File.Exists(extra)) continue;
    // Do NOT swallow this: copying the -wal sidecar is mandatory (recent rows live there
    // only). If it fails we would open a WAL-blind snapshot and print "0 fatals" — the
    // exact false-negative this tool exists to prevent, indistinguishable from a real
    // clean run. Abort loudly instead so the verdict can never be read off a blind copy.
    try
    {
        File.Copy(extra, snapshot + side, overwrite: true);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(
            $"FATAL: could not copy the {side} sidecar of {dbPath} ({ex.Message}). Recent "
            + "WAL-resident rows would be invisible and the digest would falsely report 0 "
            + "fatals — refusing to produce a blind verdict.");
        try { File.Delete(snapshot); } catch { }
        return 3;
    }
}

var sb = new StringBuilder();
void Line(string s = "") => sb.AppendLine(s);

try
{
    var cs = new SqliteConnectionStringBuilder
    {
        DataSource = snapshot,
        // Read-write (not ReadOnly) so SQLite replays the copied -wal into the snapshot
        // on open — otherwise WAL-only rows (the recent ones we care about) are missing.
        Mode = SqliteOpenMode.ReadWrite,
        Pooling = false,
    }.ToString();
    using var conn = new SqliteConnection(cs);
    conn.Open();
    // Force a full checkpoint so every WAL row is materialized before we query.
    using (var wal = conn.CreateCommand())
    {
        wal.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        try { wal.ExecuteNonQuery(); } catch { /* not fatal — a non-WAL DB ignores this */ }
    }

    Line($"# codex dispatch log digest");
    Line($"source: {dbPath}");
    Line($"since:  {(since > 0 ? $"ts >= {since} ({DateTimeOffset.FromUnixTimeSeconds(since):u})" : "(all rows)")}");

    // Total rows scanned in range, so a reader can tell "0 fatals out of N real rows"
    // (a genuine clean run) from "0 fatals out of 0 rows" (nothing logged in the window
    // → INCONCLUSIVE, not clean). Also note whether the source carried a -wal sidecar.
    long rowsInRange;
    using (var cnt = conn.CreateCommand())
    {
        cnt.CommandText = "SELECT count(*) FROM logs WHERE ts >= $since";
        cnt.Parameters.AddWithValue("$since", since);
        rowsInRange = (long)(cnt.ExecuteScalar() ?? 0L);
    }
    Line($"rows in range: {rowsInRange}  (source -wal sidecar present: {(hadWal ? "yes" : "no")})");
    if (rowsInRange == 0)
        Line("WARNING: zero rows in range — this window captured NOTHING, so a 0 fatal "
            + "count is INCONCLUSIVE, not clean. Check the since-window and the stdout canary.");
    Line();

    // 1) The signal that most matters: tool-router / dispatch fatals. A real dispatch
    //    fatal (the "incompatible payload" class) is always logged at ERROR level on a
    //    tools/util target — so gate the whole query on level='ERROR'. The message
    //    signatures are matched WITHIN ERROR rows only: the giant TRACE/DEBUG transport
    //    dumps embed the full conversation history (including any prior error text the
    //    client echoed back), so an unqualified body LIKE would false-positive on every
    //    later transport row of a once-failed session.
    Line("## router / dispatch fatals (the exec-broken signal)");
    var fatalCount = DumpRows(conn, sb, since,
        where: "level = 'ERROR' AND ("
             + "target LIKE 'codex_core::tools::router%' "
             + "OR target LIKE 'codex_core::tools::parallel%' "
             + "OR target LIKE 'codex_core::util%' "
             + "OR feedback_log_body LIKE '%incompatible payload%' "
             + "OR feedback_log_body LIKE '%Missing namespace%' "
             + "OR feedback_log_body LIKE '%Polymorphism_%')",
        limit: 40);
    if (fatalCount == 0) Line("  (none — no tool-router fatal in range)");
    Line();

    // 2) All ERROR-level rows (broader net for any other client-side failure).
    Line("## ERROR-level rows");
    var errCount = DumpRows(conn, sb, since, where: "level = 'ERROR'", limit: 60);
    if (errCount == 0) Line("  (none)");
    Line();

    // 3) A tail of recent rows for context (what the client was doing).
    Line("## recent rows (tail, any level)");
    DumpRows(conn, sb, since, where: "1=1", limit: 40, tail: true);

    Line();
    Line("## summary");
    Line($"router/dispatch-fatal rows: {fatalCount}");
    Line($"ERROR rows:                 {errCount}");
    Line($"verdict hint: a NON-ZERO fatal count means the client could NOT execute what the "
        + "bridge sent — FAIL regardless of the bridge's 200. Zero is necessary but not "
        + "sufficient; also confirm the tool actually ran (output present, not aborted).");
}
finally
{
    try { File.Delete(snapshot); } catch { }
    try { File.Delete(snapshot + "-wal"); } catch { }
    try { File.Delete(snapshot + "-shm"); } catch { }
}

var text = sb.ToString();
if (outFile is not null)
{
    File.WriteAllText(outFile, text);
    Console.WriteLine($"wrote digest → {outFile}");
}
else
{
    Console.WriteLine(text);
}
return 0;

// ---- helpers ----------------------------------------------------------------

static int DumpRows(SqliteConnection conn, StringBuilder sb, long since, string where, int limit, bool tail = false)
{
    using var cmd = conn.CreateCommand();
    // ORDER DESC to get the most-recent `limit` rows; the tail view keeps that order,
    // the targeted views too (newest fatal first is what you want to read).
    cmd.CommandText =
        $"SELECT ts, level, target, feedback_log_body FROM logs "
        + $"WHERE ts >= $since AND ({where}) ORDER BY ts DESC, id DESC LIMIT $limit";
    cmd.Parameters.AddWithValue("$since", since);
    cmd.Parameters.AddWithValue("$limit", limit);

    using var r = cmd.ExecuteReader();
    var n = 0;
    while (r.Read())
    {
        n++;
        var ts = r.GetInt64(0);
        var level = r.GetString(1);
        var target = r.GetString(2);
        var body = r.IsDBNull(3) ? "" : r.GetString(3);
        // codex bodies are `<otel span prefix> … error=<human message>` — the span is
        // a long noisy prefix and the ACTUAL fatal ("Fatal error: tool exec invoked
        // with incompatible payload") sits at the END. Showing only the head would cut
        // off the one line that decides the verdict, so for long bodies show head AND
        // tail; if an `error=` / `message=` marker is present, surface that slice too.
        body = Summarize(body);
        body = body.Replace("\n", "\n      ");
        sb.AppendLine($"  ts={ts} ({DateTimeOffset.FromUnixTimeSeconds(ts):HH:mm:ss}) [{level}] {target}");
        if (body.Length > 0) sb.AppendLine($"      {body}");
    }
    return n;
}

/// <summary>
/// Render a codex log body for reading. Short bodies pass through. Long bodies (huge
/// otel spans) are shown head + tail, because the human-readable fatal message
/// ("...error=Fatal error: tool exec invoked with incompatible payload") is at the
/// END. If an <c>error=</c> / <c>message=</c> marker is present, its slice is pulled
/// to the front so the deciding line is never buried.
/// </summary>
static string Summarize(string body)
{
    if (string.IsNullOrEmpty(body)) return body;

    string? marker = null;
    foreach (var key in new[] { "error=", "message=\"", "message=" })
    {
        var idx = body.LastIndexOf(key, StringComparison.Ordinal);
        if (idx >= 0) { marker = body[idx..]; break; }
    }
    if (marker is not null && marker.Length > 300) marker = marker[..300] + "…";

    string shown;
    if (body.Length <= 500)
        shown = body;
    else
        shown = body[..250] + $" …[+{body.Length - 500}]… " + body[^250..];

    return marker is not null ? $"MSG: {marker}\n      {shown}" : shown;
}
