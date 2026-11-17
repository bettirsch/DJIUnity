using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

public enum DjiLoggingMode { Normal, Diagnostic }

public static class DjiLogRetention
{
    public const long BudgetBytes = 100_000_000L;
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);
    public const string SessionFormat = "yyyyMMdd'T'HHmmssfff'Z'";
    private static readonly string[] Prefixes =
    {
        "dji-session-events-", "dji-telemetry-validation-", "dji-visual-latency-",
        "dji-outage-events-", "dji-position-callbacks-", "dji-velocity-callbacks-"
    };

    public static void Prune(string directory, DateTime utcNow, Func<string, bool> isActive,
        long budgetBytes = BudgetBytes, Action<Exception> onError = null)
    {
        if (!Directory.Exists(directory) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            return;

        var sessions = new Dictionary<string, List<FileInfo>>(StringComparer.Ordinal);
        long total = 0;
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("dji-*.jsonl"))
        {
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0 || !TrySessionId(file.Name, out var id))
                continue;
            try
            {
                total += file.Length;
                if (!sessions.TryGetValue(id, out var files)) sessions[id] = files = new List<FileInfo>();
                files.Add(file);
            }
            catch (IOException e) { onError?.Invoke(e); }
            catch (UnauthorizedAccessException e) { onError?.Invoke(e); }
        }

        var cutoff = utcNow.Subtract(MaxAge).ToString(SessionFormat, CultureInfo.InvariantCulture);
        foreach (var session in sessions.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (StringComparer.Ordinal.Compare(session.Key, cutoff) >= 0 && total <= budgetBytes) break;
            if (isActive(session.Key)) continue;
            foreach (var file in session.Value)
            {
                try
                {
                    var size = file.Length;
                    file.Delete();
                    total -= size;
                }
                catch (IOException e) { onError?.Invoke(e); }
                catch (UnauthorizedAccessException e) { onError?.Invoke(e); }
            }
        }
    }

    private static bool TrySessionId(string name, out string id)
    {
        id = null;
        const string extension = ".jsonl";
        if (!name.EndsWith(extension, StringComparison.Ordinal)) return false;
        foreach (var prefix in Prefixes)
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
            id = name.Substring(prefix.Length, name.Length - prefix.Length - extension.Length);
            return id.Length == 19 && DateTime.TryParseExact(id, SessionFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out _);
        }
        return false;
    }
}

// Session IDs are registered before any writer opens a file. Only the backing workers
// touch the filesystem; the lock never covers I/O or a JNI call.
internal static class DjiLogMaintenance
{
    private static readonly object Gate = new();
    private static readonly HashSet<string> Active = new();
    private static readonly Queue<Action> Jobs = new();
    private static bool running;
    private static DateTime previousUtc;

    public static string OpenSession()
    {
        lock (Gate)
        {
            var utc = DateTime.UtcNow;
            utc = utc.AddTicks(-(utc.Ticks % TimeSpan.TicksPerMillisecond));
            if (utc <= previousUtc) utc = previousUtc.AddMilliseconds(1);
            previousUtc = utc;
            var id = utc.ToString(DjiLogRetention.SessionFormat, CultureInfo.InvariantCulture);
            Active.Add(id);
            return id;
        }
    }

    public static void Start(string root, string id, bool diagnostic) => Enqueue(() =>
    {
        if (diagnostic && IsActive(id)) RunJni(() => DJIPoseProvider.StartRawTelemetryCallbackLogging(id));
        Prune(root);
    });

    public static void Finish(string root, string id, bool diagnostic, params BoundedDiagnosticJsonlWriter[] writers)
    {
        foreach (var writer in writers) writer?.RequestClose();
        Enqueue(() =>
        {
            foreach (var writer in writers) writer?.Dispose();
            bool rawClosed = true;
            if (diagnostic) RunJni(() => rawClosed = DJIPoseProvider.StopRawTelemetryCallbackLogging());
            if (rawClosed)
                lock (Gate) Active.Remove(id);
            Prune(root);
            foreach (var writer in writers)
                if (writer != null && (writer.Dropped > 0 || writer.WriteErrors > 0))
                    UnityEngine.Debug.LogWarning($"DJI_JSONL_INCOMPLETE sessionId={id} queueDrops={writer.Dropped} fileWriteErrors={writer.WriteErrors}");
        });
    }

    private static bool IsActive(string id) { lock (Gate) return Active.Contains(id); }

    private static void Prune(string root) =>
        DjiLogRetention.Prune(root, DateTime.UtcNow, IsActive, onError: ReportError);

    private static void Enqueue(Action job)
    {
        lock (Gate)
        {
            Jobs.Enqueue(job);
            if (running) return;
            running = true;
        }
        ThreadPool.QueueUserWorkItem(_ =>
        {
            while (true)
            {
                Action next;
                lock (Gate)
                {
                    if (Jobs.Count == 0) { running = false; return; }
                    next = Jobs.Dequeue();
                }
                try { next(); }
                catch (Exception e) { ReportError(e); }
            }
        });
    }

    private static void RunJni(Action action)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (UnityEngine.AndroidJNI.AttachCurrentThread() != 0)
            throw new InvalidOperationException("Could not attach the DJI logging worker to JNI.");
        try { action(); }
        finally { UnityEngine.AndroidJNI.DetachCurrentThread(); }
#endif
    }

    private static void ReportError(Exception e) =>
        UnityEngine.Debug.LogWarning($"DJI_LOG_MAINTENANCE_ERROR type={e.GetType().Name} message={e.Message}");
}
