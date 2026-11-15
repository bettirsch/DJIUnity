using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

/// <summary>One owner for the existing visual-latency stream. Tracking only enqueues
/// bounded strings; disk writes/periodic flushes run on the worker, never under the queue lock.</summary>
public sealed class BoundedDiagnosticJsonlWriter : TextWriter
{
    private readonly TextWriter sink;
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
    {
        this.sink = sink ?? throw new ArgumentNullException(nameof(sink));
        this.capacity = Math.Max(1, capacity);
        worker = new Thread(Drain) { IsBackground = true, Name = "DJI visual JSONL" };
        worker.Start();
    }

    public override void WriteLine(string value)
    {
        lock (gate)
        {
            if (closing || pending.Count >= capacity) { Interlocked.Increment(ref dropped); return; }
            pending.Enqueue(value);
        }
        wake.Set();
    }

    public void ObserveSerialization(long ticks)
    {
        Interlocked.Add(ref serializationTicks, ticks);
        Interlocked.Increment(ref serializationCount);
    }

    public override void Flush() { lock (gate) flushRequested = true; wake.Set(); }

    private void Drain()
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
                if (line != null) { sink.WriteLine(line); written++; bytes += Encoding.UTF8.GetByteCount(line) + 1; }
                if (done || (DateTime.UtcNow - lastSummary).TotalSeconds >= 5)
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
        try { sink.Dispose(); } catch (Exception) { Interlocked.Increment(ref errors); }
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
        lock (gate) { if (closing) return; closing = true; }
        wake.Set();
        worker.Join(); // Only session finalization waits for outstanding I/O.
        wake.Dispose();
        base.Dispose(disposing);
    }
}
