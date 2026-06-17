// Genetec SDK performance test.
// Each test = fresh LoginManager.LogOn -> ONE operation -> LoginManager.LogOff.
// All credentials/GUIDs hardcoded below. Build via run.bat.
// All sensative data has been removed
// Username and password and GUIDs all invented

using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Genetec.Sdk;
using Genetec.Sdk.Entities;
using Genetec.Sdk.Queries;

namespace GenetecPerformanceTest
{
    public static class Program
    {
        const string SERVER             = "10.99.99.11";
        const string USERNAME           = "MrSuperPotatoRocketMan";
        const string PASSWORD           = "AllPotatoesGoToHeaven";
        const string CLIENT_CERTIFICATE = "314u0dlfkajsdlfkjasdlkfj/asd;lfkjaslkdjf83u40823u";
        const int    LOGON_TIMEOUT_SEC  = 180;
        const int    RESULT_CAP         = 50;

        // Sustained-load limits (we don't want a test to literally run for an hour).
        // Most problems WINK sees are related to long run
        const int    SUSTAINED_MAX_ATTEMPTS   = 500;
        const int    SUSTAINED_MAX_CONSECUTIVE_FAILS = 20;
        const int    SUSTAINED_MAX_MS         = 10 * 60 * 1000;  // 10 min

        static readonly Guid REPEAT_GUID = new Guid("01000000-0001-babe-00c8-a41894114257");

        public static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.WriteLine("Genetec SDK performance test");
            Console.WriteLine($"Server: {SERVER}   User: {USERNAME}");
            Console.WriteLine($"Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine();

            // "show the hangup" tests and the most important data.
            Test_SustainedGetEntity(EntityType.AnalogMonitor);
            Test_SustainedGetEntity(EntityType.Camera);
            Test_SustainedForgeWorkflow();

            // some preop stuff
            const int RUNS = 3;
            Repeat(RUNS, Test_LoginBaseline);
            Repeat(RUNS, () => Test_BulkQuery(EntityType.AnalogMonitor));
            Repeat(RUNS, () => Test_BulkQuery(EntityType.Camera));
            Repeat(RUNS, () => Test_BulkQuery(EntityType.VideoUnit));
            Repeat(RUNS, () => Test_PerEntity(EntityType.AnalogMonitor, RESULT_CAP));
            Repeat(RUNS, () => Test_PerEntity(EntityType.Camera, RESULT_CAP));
            Repeat(RUNS, () => Test_PerEntity(EntityType.VideoUnit, RESULT_CAP));
            Repeat(RUNS, Test_RepeatGetEntity);
            Repeat(RUNS, () => Test_ForgeWorkflow(RESULT_CAP));

            Console.WriteLine();
            Console.WriteLine("DONE");
            return 0;
        }

        static void Test_LoginBaseline()
        {
            Section("Login + Logoff");
            RunInSession(_ => { });
        }

        static void Test_BulkQuery(EntityType type)
        {
            Section($"EntityConfigurationQuery: {type} (DataTable only)");
            RunInSession(engine =>
            {
                var q = engine.ReportManager.CreateReportQuery(ReportType.EntityConfiguration) as EntityConfigurationQuery;
                q.EntityTypeFilter.Add(type);
                var sw = Stopwatch.StartNew();
                var r = q.Query();
                sw.Stop();
                int rows = r.Data?.Rows?.Count ?? 0;
                Console.WriteLine($"  query: {sw.ElapsedMilliseconds} ms   rows: {rows}   success: {r.Success}");
            });
        }

        static void Test_PerEntity(EntityType type, int cap)
        {
            Section($"GetEntity per row: {type}, first {cap}");
            RunInSession(engine =>
            {
                var q = engine.ReportManager.CreateReportQuery(ReportType.EntityConfiguration) as EntityConfigurationQuery;
                q.EntityTypeFilter.Add(type);
                var bulk = q.Query();
                int rows = bulk.Data?.Rows?.Count ?? 0;
                Console.WriteLine($"  bulk query: {rows} rows");
                if (rows == 0) return;

                var times = new List<long>();
                int hydrated = 0, nulls = 0;
                var swTotal = Stopwatch.StartNew();
                foreach (DataRow row in bulk.Data.Rows)
                {
                    if (hydrated >= cap) break;
                    Guid g;
                    try { g = (Guid)row[0]; } catch { continue; }
                    var sw = Stopwatch.StartNew();
                    Entity ent = null;
                    try { ent = engine.GetEntity(g); } catch { }
                    sw.Stop();
                    if (ent == null) nulls++;
                    times.Add(sw.ElapsedMilliseconds);
                    hydrated++;
                }
                swTotal.Stop();
                Console.WriteLine($"  {hydrated} GetEntity calls: {swTotal.ElapsedMilliseconds} ms   nulls: {nulls}   avg: {times.Average():F0} ms   min: {times.Min()} ms   max: {times.Max()} ms");
            });
        }

        static void Test_RepeatGetEntity()
        {
            Section($"GetEntity({REPEAT_GUID}) x5 (same session)");
            RunInSession(engine =>
            {
                for (int i = 1; i <= 5; i++)
                {
                    var sw = Stopwatch.StartNew();
                    var ent = engine.GetEntity(REPEAT_GUID);
                    sw.Stop();
                    Console.WriteLine($"  iter {i}: {sw.ElapsedMilliseconds} ms   {(ent != null ? "ok" : "null")}");
                }
            });
        }

        // this is basically what we do, get monitors + connectedentites
        static void Test_ForgeWorkflow(int cap)
        {
            Section($"Forge workflow: {cap} monitors (monitor + connected camera + camera config each)");
            RunInSession(engine =>
            {
                var q = engine.ReportManager.CreateReportQuery(ReportType.EntityConfiguration) as EntityConfigurationQuery;
                q.EntityTypeFilter.Add(EntityType.AnalogMonitor);
                var bulk = q.Query();
                int totalRows = bulk.Data?.Rows?.Count ?? 0;
                Console.WriteLine($"  bulk AnalogMonitor list: {totalRows} rows");
                if (totalRows == 0) return;

                int processed = 0, monNulls = 0, camNulls = 0, noConnected = 0, cfgPopulated = 0;
                var swTotal = Stopwatch.StartNew();
                foreach (DataRow row in bulk.Data.Rows)
                {
                    if (processed >= cap) break;
                    Guid mg;
                    try { mg = (Guid)row[0]; } catch { continue; }
                    processed++;

                    AnalogMonitor mon = null;
                    try { mon = engine.GetEntity(mg) as AnalogMonitor; } catch { }
                    if (mon == null) { monNulls++; continue; }

                    Guid connected = Guid.Empty;
                    try { connected = mon.ConnectedEntity; } catch { }
                    if (connected == Guid.Empty) { noConnected++; continue; }

                    Genetec.Sdk.Entities.Camera cam = null;
                    try { cam = engine.GetEntity(connected) as Genetec.Sdk.Entities.Camera; } catch { }
                    if (cam == null) { camNulls++; continue; }

                    try
                    {
                        var ccq = engine.ReportManager.CreateReportQuery(ReportType.CameraConfiguration) as CameraConfigurationQuery;
                        ccq.Cameras.Add(cam.Guid);
                        var cfg = ccq.Query();
                        if (cfg.Success && cfg.Data != null && cfg.Data.Rows.Count > 0) cfgPopulated++;
                    }
                    catch { }
                }
                swTotal.Stop();

                double secPerMon = swTotal.ElapsedMilliseconds / Math.Max(1.0, processed) / 1000.0;
                double estTotalMin = (secPerMon * totalRows) / 60.0;
                Console.WriteLine($"  {processed} monitors processed in {swTotal.ElapsedMilliseconds} ms");
                Console.WriteLine($"    monitor nulls: {monNulls}   no-connected-entity: {noConnected}   camera nulls: {camNulls}   camera config rows: {cfgPopulated}");
                Console.WriteLine($"  per-monitor: {secPerMon:F2} sec");
                Console.WriteLine($"  extrapolated to {totalRows} monitors: {estTotalMin:F1} minutes");
            });
        }

        //  exactly where and how it hangs up.
        //   caps at 500 attempts, 20 consecutive fails, 10 min.
        // way fancy for test app, but we trying to show that its not 1 or 2 quries
        static void Test_SustainedGetEntity(EntityType type)
        {
            Section($"SUSTAINED GetEntity: {type}, no reconnect, single session");
            RunInSession(engine =>
            {
                var q = engine.ReportManager.CreateReportQuery(ReportType.EntityConfiguration) as EntityConfigurationQuery;
                q.EntityTypeFilter.Add(type);
                var bulk = q.Query();
                int totalRows = bulk.Data?.Rows?.Count ?? 0;
                Console.WriteLine($"  bulk list: {totalRows} rows");
                if (totalRows == 0) return;

                int attempted = 0, successful = 0, nullCount = 0, exceptions = 0;
                int firstNullIdx = -1, firstExceptionIdx = -1;
                long firstNullMs = -1, firstExceptionMs = -1;
                string firstExceptionMessage = null;
                int consecutiveFails = 0;
                long totalGetEntityMs = 0;
                var swWall = Stopwatch.StartNew();

                foreach (DataRow row in bulk.Data.Rows)
                {
                    if (attempted >= SUSTAINED_MAX_ATTEMPTS) break;
                    if (consecutiveFails >= SUSTAINED_MAX_CONSECUTIVE_FAILS) break;
                    if (swWall.ElapsedMilliseconds >= SUSTAINED_MAX_MS) break;

                    Guid g;
                    try { g = (Guid)row[0]; } catch { continue; }
                    attempted++;
                    var sw = Stopwatch.StartNew();
                    Entity ent = null;
                    Exception exc = null;
                    try { ent = engine.GetEntity(g); }
                    catch (Exception ex) { exc = ex; }
                    sw.Stop();
                    totalGetEntityMs += sw.ElapsedMilliseconds;

                    if (exc != null)
                    {
                        exceptions++;
                        consecutiveFails++;
                        if (firstExceptionIdx < 0) { firstExceptionIdx = attempted; firstExceptionMs = swWall.ElapsedMilliseconds; firstExceptionMessage = exc.GetType().Name + ": " + exc.Message; }
                    }
                    else if (ent == null)
                    {
                        nullCount++;
                        consecutiveFails++;
                        if (firstNullIdx < 0) { firstNullIdx = attempted; firstNullMs = swWall.ElapsedMilliseconds; }
                    }
                    else
                    {
                        successful++;
                        consecutiveFails = 0;
                    }

                    if (attempted % 25 == 0)
                    {
                        Console.WriteLine($"  ...attempt {attempted}: successful={successful}, nulls={nullCount}, exceptions={exceptions}, consecFails={consecutiveFails}, wallClock={swWall.ElapsedMilliseconds}ms");
                    }
                }
                swWall.Stop();

                string stopReason =
                    consecutiveFails >= SUSTAINED_MAX_CONSECUTIVE_FAILS ? $"{consecutiveFails} consecutive failures (session collapse)"
                    : attempted >= SUSTAINED_MAX_ATTEMPTS ? $"attempt cap ({SUSTAINED_MAX_ATTEMPTS}) reached"
                    : swWall.ElapsedMilliseconds >= SUSTAINED_MAX_MS ? $"time cap ({SUSTAINED_MAX_MS / 1000}s) reached"
                    : "all rows in bulk list processed";

                Console.WriteLine($"  STOPPED: {stopReason}");
                Console.WriteLine($"  attempted: {attempted}   successful: {successful}   nulls: {nullCount}   exceptions: {exceptions}");
                if (firstNullIdx > 0) Console.WriteLine($"  first NULL at entity #{firstNullIdx} (wall clock {firstNullMs} ms)");
                if (firstExceptionIdx > 0) Console.WriteLine($"  first EXCEPTION at entity #{firstExceptionIdx} (wall clock {firstExceptionMs} ms): {firstExceptionMessage}");
                Console.WriteLine($"  total wall clock: {swWall.ElapsedMilliseconds} ms   total GetEntity time: {totalGetEntityMs} ms");
                if (successful > 0)
                    Console.WriteLine($"  successful entities: avg {(double)totalGetEntityMs / attempted:F0} ms/call   throughput {successful * 1000.0 / swWall.ElapsedMilliseconds:F1} entities/sec");
            });
        }

        // Same idea but the realistic Forge workflow per iteration: monitor + connected camera + camera config.
        static void Test_SustainedForgeWorkflow()
        {
            Section("SUSTAINED Forge workflow: monitor + connected camera + camera config, no reconnect");
            RunInSession(engine =>
            {
                var q = engine.ReportManager.CreateReportQuery(ReportType.EntityConfiguration) as EntityConfigurationQuery;
                q.EntityTypeFilter.Add(EntityType.AnalogMonitor);
                var bulk = q.Query();
                int totalRows = bulk.Data?.Rows?.Count ?? 0;
                Console.WriteLine($"  bulk AnalogMonitor list: {totalRows} rows");
                if (totalRows == 0) return;

                int attempted = 0, fullyCompleted = 0;
                int monNulls = 0, monExceptions = 0, camNulls = 0, camExceptions = 0, cfgZeroRows = 0;
                int firstFailIdx = -1; long firstFailMs = -1; string firstFailReason = null;
                int consecutiveFails = 0;
                var swWall = Stopwatch.StartNew();

                foreach (DataRow row in bulk.Data.Rows)
                {
                    if (attempted >= SUSTAINED_MAX_ATTEMPTS) break;
                    if (consecutiveFails >= SUSTAINED_MAX_CONSECUTIVE_FAILS) break;
                    if (swWall.ElapsedMilliseconds >= SUSTAINED_MAX_MS) break;

                    Guid mg;
                    try { mg = (Guid)row[0]; } catch { continue; }
                    attempted++;
                    bool failedThisRow = false;

                    AnalogMonitor mon = null;
                    try { mon = engine.GetEntity(mg) as AnalogMonitor; }
                    catch (Exception ex) {
                        monExceptions++; failedThisRow = true;
                        if (firstFailIdx < 0) { firstFailIdx = attempted; firstFailMs = swWall.ElapsedMilliseconds; firstFailReason = $"monitor GetEntity threw {ex.GetType().Name}: {ex.Message}"; }
                    }
                    if (!failedThisRow && mon == null)
                    {
                        monNulls++; failedThisRow = true;
                        if (firstFailIdx < 0) { firstFailIdx = attempted; firstFailMs = swWall.ElapsedMilliseconds; firstFailReason = "monitor GetEntity returned null"; }
                    }

                    Guid connected = Guid.Empty;
                    if (!failedThisRow)
                    {
                        try { connected = mon.ConnectedEntity; } catch { }
                    }
                    if (!failedThisRow && connected != Guid.Empty)
                    {
                        Genetec.Sdk.Entities.Camera cam = null;
                        try { cam = engine.GetEntity(connected) as Genetec.Sdk.Entities.Camera; }
                        catch (Exception ex) {
                            camExceptions++; failedThisRow = true;
                            if (firstFailIdx < 0) { firstFailIdx = attempted; firstFailMs = swWall.ElapsedMilliseconds; firstFailReason = $"camera GetEntity threw {ex.GetType().Name}: {ex.Message}"; }
                        }
                        if (!failedThisRow && cam == null)
                        {
                            camNulls++; failedThisRow = true;
                            if (firstFailIdx < 0) { firstFailIdx = attempted; firstFailMs = swWall.ElapsedMilliseconds; firstFailReason = "camera GetEntity returned null"; }
                        }
                        if (!failedThisRow)
                        {
                            try
                            {
                                var ccq = engine.ReportManager.CreateReportQuery(ReportType.CameraConfiguration) as CameraConfigurationQuery;
                                ccq.Cameras.Add(cam.Guid);
                                var cfg = ccq.Query();
                                if (!(cfg.Success && cfg.Data != null && cfg.Data.Rows.Count > 0))
                                {
                                    cfgZeroRows++;
                                    failedThisRow = true;
                                    if (firstFailIdx < 0) { firstFailIdx = attempted; firstFailMs = swWall.ElapsedMilliseconds; firstFailReason = "CameraConfigurationQuery returned 0 rows"; }
                                }
                            }
                            catch (Exception ex)
                            {
                                failedThisRow = true;
                                if (firstFailIdx < 0) { firstFailIdx = attempted; firstFailMs = swWall.ElapsedMilliseconds; firstFailReason = $"CameraConfigurationQuery threw {ex.GetType().Name}: {ex.Message}"; }
                            }
                        }
                    }

                    if (failedThisRow) consecutiveFails++;
                    else { fullyCompleted++; consecutiveFails = 0; }

                    if (attempted % 25 == 0)
                    {
                        Console.WriteLine($"  ...attempt {attempted}: fullyCompleted={fullyCompleted}, monNulls={monNulls}, camNulls={camNulls}, consecFails={consecutiveFails}, wallClock={swWall.ElapsedMilliseconds}ms");
                    }
                }
                swWall.Stop();

                string stopReason =
                    consecutiveFails >= SUSTAINED_MAX_CONSECUTIVE_FAILS ? $"{consecutiveFails} consecutive failures (session collapse)"
                    : attempted >= SUSTAINED_MAX_ATTEMPTS ? $"attempt cap ({SUSTAINED_MAX_ATTEMPTS}) reached"
                    : swWall.ElapsedMilliseconds >= SUSTAINED_MAX_MS ? $"time cap ({SUSTAINED_MAX_MS / 1000}s) reached"
                    : "all rows in bulk list processed";

                Console.WriteLine($"  STOPPED: {stopReason}");
                Console.WriteLine($"  attempted: {attempted}   fullyCompleted: {fullyCompleted}   ({(fullyCompleted * 100.0 / Math.Max(1, attempted)):F1}%)");
                Console.WriteLine($"  monitor nulls: {monNulls}   monitor exceptions: {monExceptions}");
                Console.WriteLine($"  camera nulls: {camNulls}    camera exceptions: {camExceptions}");
                Console.WriteLine($"  cfg-query 0-rows: {cfgZeroRows}");
                if (firstFailIdx > 0) Console.WriteLine($"  first FAILURE at attempt #{firstFailIdx} (wall clock {firstFailMs} ms): {firstFailReason}");
                Console.WriteLine($"  total wall clock: {swWall.ElapsedMilliseconds} ms");
                if (fullyCompleted > 0)
                    Console.WriteLine($"  per fully-completed monitor: {swWall.ElapsedMilliseconds / (double)fullyCompleted:F0} ms   theoretical full-run for {totalRows}: {(swWall.ElapsedMilliseconds / (double)fullyCompleted * totalRows) / 60000.0:F1} min");
            });
        }

        // ── infrastructure ───────────────────────────────────────────────
        static int s_run = 0;
        static int s_runTotal = 0;
        static void Repeat(int n, Action body)
        {
            s_runTotal = n;
            for (int i = 1; i <= n; i++)
            {
                s_run = i;
                body();
            }
            // Reset so subsequent un-Repeated calls don't inherit "[run X/N]" suffix.
            s_runTotal = 0;
            s_run = 0;
        }

        static void Section(string title)
        {
            Console.WriteLine();
            string suffix = s_runTotal > 1 ? $" [run {s_run}/{s_runTotal}]" : "";
            Console.WriteLine("--- " + title + suffix);
        }

        static void RunInSession(Action<Engine> body)
        {
            using (var engine = new Engine())
            {
                bool loggedOn = false;
                string err = null;
                engine.LoginManager.LogonStatusChanged += (s, e) =>
                {
                    if (e.Status == ConnectionStateCode.Success || e.Status == ConnectionStateCode.ConnectionEstablished)
                        loggedOn = true;
                    if (e.Status == ConnectionStateCode.InvalidCredential || e.Status == ConnectionStateCode.Failed)
                        err = e.Status.ToString();
                };
                var swLogin = Stopwatch.StartNew();
                engine.ClientCertificate = CLIENT_CERTIFICATE;
                engine.LoginManager.LogOn(SERVER, USERNAME, PASSWORD);
                int waited = 0;
                while (waited < LOGON_TIMEOUT_SEC * 1000)
                {
                    Thread.Sleep(200); waited += 200;
                    if (loggedOn && (engine.LoginManager?.IsConnected ?? false)) break;
                    if (err != null) break;
                }
                swLogin.Stop();
                if (engine.LoginManager?.IsConnected != true)
                {
                    Console.WriteLine($"  login FAILED: {swLogin.ElapsedMilliseconds} ms   error: {err ?? "timeout"}");
                    return;
                }
                Console.WriteLine($"  login: {swLogin.ElapsedMilliseconds} ms");

                try { body(engine); }
                catch (Exception ex) { Console.WriteLine($"  exception: {ex.Message}"); }

                var swLogoff = Stopwatch.StartNew();
                try { engine.LoginManager.LogOff(); } catch { }
                swLogoff.Stop();
                Console.WriteLine($"  logoff: {swLogoff.ElapsedMilliseconds} ms");
            }
        }
    }
}
