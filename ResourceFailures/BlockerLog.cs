using System;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Logging;

namespace DIYLevelFastInit.ResourceFailures
{
    internal static class BlockerLog
    {
        private static readonly object Gate = new object();
        private static ManualLogSource source;
        private static StreamWriter writer;
        private static string tag;
        private static bool closed;

        internal static void Initialize(string pluginGuid, string stableTag)
        {
            tag = stableTag;
            closed = false;
            source = Logger.CreateLogSource(stableTag);
            try
            {
                string directory = Path.Combine(Path.Combine(Paths.BepInExRootPath, "logs"), pluginGuid);
                Directory.CreateDirectory(directory);
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                string path = Path.Combine(directory, stableTag + "-" + stamp + ".log");
                int suffix = 0;
                while (File.Exists(path))
                {
                    suffix++;
                    path = Path.Combine(directory, stableTag + "-" + stamp + "-" + suffix + ".log");
                }
                writer = new StreamWriter(path, false, Encoding.UTF8);
                writer.AutoFlush = false;
                Retain(directory, stableTag + "-", path, 20);
            }
            catch (Exception ex)
            {
                SafeFramework("[Logger] [INIT] file logger unavailable: " + ex, LogLevel.Error);
            }
            Info("Lifecycle", "START", "logger initialized");
        }

        private static void Retain(string directory, string prefix, string current, int keep)
        {
            try
            {
                FileInfo[] files = new DirectoryInfo(directory).GetFiles(prefix + "*.log");
                Array.Sort(files, delegate(FileInfo left, FileInfo right) { return left.CreationTime.CompareTo(right.CreationTime); });
                int remove = files.Length - keep;
                for (int i = 0; i < remove; i++)
                    if (!string.Equals(files[i].FullName, current, StringComparison.OrdinalIgnoreCase))
                        files[i].Delete();
            }
            catch (Exception ex) { SafeFramework("[Logger] [RETAIN] cleanup failed: " + ex, LogLevel.Error); }
        }

        internal static void Debug(string feature, string phase, string message) { Write("DEBUG", LogLevel.Debug, feature, phase, message); }
        internal static void Info(string feature, string phase, string message) { Write("INFO", LogLevel.Info, feature, phase, message); }
        internal static void Message(string feature, string phase, string message) { Write("MESSAGE", LogLevel.Message, feature, phase, message); }
        internal static void Warning(string feature, string phase, string message) { Write("WARNING", LogLevel.Warning, feature, phase, message); }
        internal static void Error(string feature, string phase, string message) { Write("ERROR", LogLevel.Error, feature, phase, message); }
        internal static void Fatal(string feature, string phase, string message) { Write("FATAL", LogLevel.Fatal, feature, phase, message); }

        // BepInEx source name this logger publishes under; PreparationLogListener skips its own lines by this tag.
        internal static string SourceTag { get { return tag; } }

        private static void Write(string level, LogLevel frameworkLevel, string feature, string phase, string message)
        {
            string safe = message ?? string.Empty;
            string framework = "[" + feature + "] [" + phase + "] " + safe;
            lock (Gate)
            {
                if (!closed && writer != null)
                {
                    try
                    {
                        writer.WriteLine("[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] [" + tag + "] [" + level + "] " + framework);
                        if (frameworkLevel == LogLevel.Error || frameworkLevel == LogLevel.Fatal) writer.Flush();
                    }
                    catch (Exception ex) { SafeFramework("[Logger] [WRITE] file logger failed: " + ex, LogLevel.Error); }
                }
            }
            SafeFramework(framework, frameworkLevel);
        }

        private static void SafeFramework(string message, LogLevel level)
        {
            try { if (source != null) source.Log(level, message); }
            catch { }
        }

        internal static void Close()
        {
            lock (Gate)
            {
                if (closed) return;
                closed = true;
                try { if (writer != null) writer.Flush(); } catch { }
                try { if (writer != null) writer.Close(); } catch { }
                writer = null;
            }
        }
    }
}
