using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;
#if GOOSE_SENTRY
using Sentry;
using USentry = Sentry.Unity.SentrySdk;
#endif

namespace GooseBrawl
{
    /// <summary>
    /// The one place the game talks to Sentry. Compiles to no-ops when the Sentry package is absent
    /// (the GOOSE_SENTRY define is added by GooseBrawlSetup only when io.sentry.unity is resolved), so the
    /// project always builds. Transactions carry the round's frame measurements, spans wrap the brain, voice
    /// and AR work, structured logs carry the spike / yell / fallback context, and outgoing requests get the
    /// sentry-trace + baggage headers so one trace runs phone -> Worker -> OpenAI / ElevenLabs.
    /// </summary>
    public static class GooseTelemetry
    {
        public enum Level { Debug, Info, Warning, Error }

        /// <summary>A span or transaction handle. Disposing finishes it.</summary>
        public sealed class Span : IDisposable
        {
#if GOOSE_SENTRY
            internal ISpan Inner;
            internal ITransactionTracer Transaction;
#endif
            internal Span Parent;
            internal string Op, Name;
            internal float StartedAt;
            bool m_Finished;

            public float ElapsedSeconds => Time.realtimeSinceStartup - StartedAt;

            public void SetData(string key, object value)
            {
#if GOOSE_SENTRY
                try { Inner?.SetData(key, value); } catch { }
#endif
            }

            public void SetTag(string key, string value)
            {
#if GOOSE_SENTRY
                try { Inner?.SetTag(key, value); } catch { }
#endif
            }

            /// <summary>Measurements only apply to transactions (the Sentry data model), spans keep them as data.</summary>
            public void SetMeasurement(string name, double value, string unit)
            {
#if GOOSE_SENTRY
                try
                {
                    if (Transaction != null) Transaction.SetMeasurement(name, value, ToUnit(unit));
                    else Inner?.SetData(name, value);
                }
                catch { }
#endif
            }

            public void Finish(bool ok = true)
            {
                if (m_Finished) return;
                m_Finished = true;
#if GOOSE_SENTRY
                try { Inner?.Finish(ok ? SpanStatus.Ok : SpanStatus.InternalError); } catch { }
#endif
                if (s_Current == this) s_Current = Parent;
            }

            public void Dispose() => Finish();
        }

        static Span s_Current;
        static readonly List<string> s_Sink = new List<string>();

        /// <summary>True when the SDK is compiled in and initialised (a DSN is configured).</summary>
        public static bool Enabled
        {
            get
            {
#if GOOSE_SENTRY
                try { return USentry.IsEnabled; } catch { return false; }
#else
                return false;
#endif
            }
        }

        public static bool Compiled =>
#if GOOSE_SENTRY
            true;
#else
            false;
#endif

        /// <summary>The innermost open span (or transaction), if any.</summary>
        public static Span Current => s_Current;

        public static Span StartTransaction(string name, string op, IDictionary<string, string> tags = null)
        {
            var span = new Span { Op = op, Name = name, StartedAt = Time.realtimeSinceStartup, Parent = null };
#if GOOSE_SENTRY
            try
            {
                if (Enabled)
                {
                    var tx = USentry.StartTransaction(name, op);
                    if (tags != null) foreach (var kv in tags) tx.SetTag(kv.Key, kv.Value);
                    USentry.ConfigureScope(scope => scope.Transaction = tx);
                    span.Inner = tx;
                    span.Transaction = tx;
                }
            }
            catch (Exception e) { GooseLog.Warn("Telemetry transaction failed: " + e.Message); }
#endif
            s_Current = span;
            return span;
        }

        /// <summary>Child span of the innermost open span; a harmless no-op handle when nothing is open.</summary>
        public static Span StartSpan(string op, string description)
        {
            var parent = s_Current;
            var span = new Span { Op = op, Name = description, StartedAt = Time.realtimeSinceStartup, Parent = parent };
#if GOOSE_SENTRY
            try
            {
                if (parent != null && parent.Inner != null) span.Inner = parent.Inner.StartChild(op, description);
            }
            catch { }
#endif
            s_Current = span;
            return span;
        }

        public static void SetMeasurement(string name, double value, string unit)
        {
            var t = s_Current;
            while (t != null && t.Parent != null) t = t.Parent;
            t?.SetMeasurement(name, value, unit);
        }

        public static void SetTag(string key, string value)
        {
#if GOOSE_SENTRY
            try { if (Enabled) USentry.ConfigureScope(scope => scope.SetTag(key, value)); } catch { }
#endif
        }

        /// <summary>Structured log with attributes. Also mirrored to the Unity console in the Editor so the smoke run shows them.</summary>
        public static void Log(Level level, string message, params (string key, object value)[] attrs)
        {
#if GOOSE_SENTRY
            try
            {
                if (Enabled)
                {
                    Action<SentryLog> configure = log =>
                    {
                        if (attrs == null) return;
                        foreach (var a in attrs)
                        {
                            if (a.key == null) continue;
                            switch (a.value)
                            {
                                case null: break;
                                case bool b: log.SetAttribute(a.key, b); break;
                                case int i: log.SetAttribute(a.key, i); break;
                                case long l: log.SetAttribute(a.key, l); break;
                                case float f: log.SetAttribute(a.key, (double)f); break;
                                case double d: log.SetAttribute(a.key, d); break;
                                default: log.SetAttribute(a.key, a.value.ToString()); break;
                            }
                        }
                    };
                    switch (level)
                    {
                        case Level.Debug: USentry.Logger.LogDebug(configure, message); break;
                        case Level.Info: USentry.Logger.LogInfo(configure, message); break;
                        case Level.Warning: USentry.Logger.LogWarning(configure, message); break;
                        default: USentry.Logger.LogError(configure, message); break;
                    }
                }
            }
            catch { }
#endif
            if (Application.isEditor || level >= Level.Warning)
            {
                var sb = new System.Text.StringBuilder("[Telemetry] ").Append(message);
                if (attrs != null)
                    foreach (var a in attrs) sb.Append(' ').Append(a.key).Append('=').Append(a.value);
                string line = sb.ToString();
                if (level == Level.Error) Debug.LogError(line);
                else if (level == Level.Warning) Debug.LogWarning(line);
                else GooseLog.Info(line);
                lock (s_Sink)
                {
                    s_Sink.Add(line);
                    if (s_Sink.Count > 400) s_Sink.RemoveAt(0);
                }
            }
        }

        public static void CaptureException(Exception e, string context)
        {
#if GOOSE_SENTRY
            try
            {
                if (Enabled)
                {
                    USentry.ConfigureScope(scope => scope.SetTag("context", context ?? "none"));
                    USentry.CaptureException(e);
                }
            }
            catch { }
#endif
            Debug.LogWarning("[Telemetry] exception in " + context + ": " + e.Message);
        }

        /// <summary>Attach sentry-trace / baggage so the Worker continues this trace.</summary>
        public static void AddTraceHeaders(UnityWebRequest request)
        {
            if (request == null) return;
#if GOOSE_SENTRY
            try
            {
                if (!Enabled) return;
                var trace = USentry.GetTraceHeader();
                if (trace != null) request.SetRequestHeader("sentry-trace", trace.ToString());
                var baggage = USentry.GetBaggage();
                if (baggage != null) request.SetRequestHeader("baggage", baggage.ToString());
            }
            catch { }
#endif
        }

        // ------------------------------------------------------------------ metrics (Sentry Application Metrics)
#if GOOSE_SENTRY
        static List<KeyValuePair<string, object>> Attrs((string key, string value)[] tags)
        {
            var list = new List<KeyValuePair<string, object>>(tags != null ? tags.Length + 1 : 1);
            if (tags != null) foreach (var t in tags) if (t.key != null && t.value != null) list.Add(new KeyValuePair<string, object>(t.key, t.value));
            return list;
        }
#endif

        /// <summary>A distribution sample (e.g. a round's p95 frame time in ms).</summary>
        public static void Distribution(string name, double value, string unit, params (string key, string value)[] tags)
        {
#if GOOSE_SENTRY
            try { if (Enabled) USentry.Metrics.EmitDistribution(name, value, ToUnit(unit), Attrs(tags), null); } catch { }
#endif
        }

        public static void Increment(string name, double value = 1, params (string key, string value)[] tags)
        {
#if GOOSE_SENTRY
            try { if (Enabled) USentry.Metrics.EmitCounter(name, value, Attrs(tags), null); } catch { }
#endif
        }

        public static void Gauge(string name, double value, string unit, params (string key, string value)[] tags)
        {
#if GOOSE_SENTRY
            try { if (Enabled) USentry.Metrics.EmitGauge(name, value, ToUnit(unit), Attrs(tags), null); } catch { }
#endif
        }

        /// <summary>Recent mirrored log lines (Editor / smoke test introspection).</summary>
        public static List<string> RecentLogs()
        {
            lock (s_Sink) return new List<string>(s_Sink);
        }

#if GOOSE_SENTRY
        static MeasurementUnit ToUnit(string unit)
        {
            switch (unit)
            {
                case "ms": return MeasurementUnit.Duration.Millisecond;
                case "s": return MeasurementUnit.Duration.Second;
                case "mb": return MeasurementUnit.Information.Megabyte;
                case "kb": return MeasurementUnit.Information.Kilobyte;
                case "byte": return MeasurementUnit.Information.Byte;
                case "ratio": return MeasurementUnit.Fraction.Ratio;
                case "percent": return MeasurementUnit.Fraction.Percent;
                case null:
                case "": return MeasurementUnit.None;
                default: return MeasurementUnit.Custom(unit);
            }
        }
#endif
    }
}
