using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

/// <summary>One owner per JSONL stream. Tracking only enqueues
/// bounded strings; disk writes/periodic flushes run on the worker, never under the queue lock.</summary>
public sealed class BoundedDiagnosticJsonlWriter : TextWriter
{
    private readonly Func<TextWriter> openSink;
    private readonly bool includeHealth;
    private readonly Queue<string> pending = new();
    private readonly object gate = new();
    private readonly AutoResetEvent wake = new(false);
    private readonly Thread worker;
    private readonly int capacity;
    private bool closing, flushRequested;
    private long dropped, written, bytes, errors, serializationTicks, serializationCount, captureErrors;
    private string lastError = "";
    public override Encoding Encoding => Encoding.UTF8;
    public long Dropped => Interlocked.Read(ref dropped);
    public long WriteErrors => Interlocked.Read(ref errors);
    public void ReportCaptureError() => Interlocked.Increment(ref captureErrors);

    public BoundedDiagnosticJsonlWriter(TextWriter sink, int capacity = 256)
        : this(sink == null ? throw new ArgumentNullException(nameof(sink)) : () => sink,
            capacity, includeHealth: true)
    {
    }

    public BoundedDiagnosticJsonlWriter(Func<TextWriter> openSink, int capacity = 256, bool includeHealth = true)
    {
        this.openSink = openSink ?? throw new ArgumentNullException(nameof(openSink));
        this.includeHealth = includeHealth;
        this.capacity = Math.Max(1, capacity);
        worker = new Thread(Drain) { IsBackground = true, Name = "DJI JSONL writer" };
        worker.Start();
    }

    public override void WriteLine(string value)
    {
        lock (gate)
        {
            if (closing || pending.Count >= capacity) { Interlocked.Increment(ref dropped); return; }
            pending.Enqueue(value);
            wake.Set();
        }
    }

    public void ObserveSerialization(long ticks)
    {
        Interlocked.Add(ref serializationTicks, ticks);
        Interlocked.Increment(ref serializationCount);
    }

    public override void Flush()
    {
        lock (gate)
        {
            if (closing) return;
            flushRequested = true;
            wake.Set();
        }
    }

    public void RequestClose()
    {
        lock (gate)
        {
            if (closing) return;
            closing = true;
            wake.Set();
        }
    }

    private void Drain()
    {
        try
        {
            using var sink = openSink();
            WritePending(sink);
        }
        catch (Exception) { Interlocked.Increment(ref errors); }
        finally
        {
            lock (gate)
            {
                closing = true;
                Interlocked.Add(ref dropped, pending.Count);
                pending.Clear();
                wake.Dispose();
            }
        }
    }

    private void WritePending(TextWriter sink)
    {
        var lastFlush = DateTime.UtcNow;
        var lastSummary = lastFlush;
        while (true)
        {
            string line = null;
            bool done, flush;
            lock (gate)
            {
                if (pending.Count > 0) line = pending.Dequeue();
                done = closing && line == null;
                flush = flushRequested;
                flushRequested = false;
            }
            try
            {
                if (line != null)
                {
                    sink.WriteLine(line);
                    if (includeHealth) { written++; bytes += Encoding.UTF8.GetByteCount(line) + 1; }
                }
                if (includeHealth && (done || (DateTime.UtcNow - lastSummary).TotalSeconds >= 5))
                {
                    sink.WriteLine(Summary(done));
                    lastSummary = DateTime.UtcNow;
                }
                if (done || flush || (DateTime.UtcNow - lastFlush).TotalSeconds >= 1)
                { sink.Flush(); lastFlush = DateTime.UtcNow; }
            }
            catch (Exception e) { Interlocked.Increment(ref errors); lastError = e.GetType().Name; }
            if (done) break;
            if (line == null) wake.WaitOne(100);
        }
    }

    private string Summary(bool final) =>
        "{\"recordType\":\"OrientationWriterHealth\",\"final\":" + (final ? "true" : "false") +
        ",\"written\":" + written + ",\"utf8Bytes\":" + bytes + ",\"queueCapacity\":" + capacity +
        ",\"queueDrops\":" + Dropped + ",\"fileWriteErrors\":" + WriteErrors +
        ",\"captureErrors\":" + Interlocked.Read(ref captureErrors) +
        ",\"serializationCount\":" + Interlocked.Read(ref serializationCount) +
        ",\"serializationTotalMs\":" + (Interlocked.Read(ref serializationTicks) * 1000d /
            System.Diagnostics.Stopwatch.Frequency).ToString("R", CultureInfo.InvariantCulture) +
        ",\"lastError\":\"" + lastError + "\"}";

    protected override void Dispose(bool disposing)
    {
        if (!disposing) return;
        RequestClose();
        worker.Join(); // Runtime callers join only on the lifecycle worker.
        base.Dispose(disposing);
    }
}
