using System;
using System.Collections.Generic;
using System.IO;

namespace DIYLevelFastInit
{
    /// <summary>Compares shallow level-directory metadata without opening bundle payloads.</summary>
    internal sealed class LevelDirectorySnapshot
    {
        internal sealed class FileStamp
        {
            internal string Name;
            internal long Length, Modified;
        }

        internal readonly DirectoryInfo[] Directories;
        internal readonly Dictionary<string, SetSnapshot> Sets;

        internal LevelDirectorySnapshot(DirectoryInfo[] directories, Dictionary<string, SetSnapshot> sets)
        {
            Directories = directories;
            Sets = new Dictionary<string, SetSnapshot>(sets, StringComparer.OrdinalIgnoreCase);
        }

        internal static LevelDirectorySnapshot Read(string path)
        {
            DirectoryInfo[] directories = new DirectoryInfo(path).GetDirectories();
            Dictionary<string, SetSnapshot> sets = new Dictionary<string, SetSnapshot>(StringComparer.OrdinalIgnoreCase);
            foreach (DirectoryInfo directory in directories) sets.Add(directory.FullName, ReadSet(directory, null));
            return new LevelDirectorySnapshot(directories, sets);
        }

        internal static SetSnapshot ReadSet(DirectoryInfo directory, string selectedInfo)
        {
            FileInfo[] files = directory.GetFiles();
            FileStamp[] stamps = new FileStamp[files.Length];
            SetSnapshot result = new SetSnapshot { DirectoryName = directory.Name };
            for (int i = 0; i < files.Length; i++)
            {
                FileInfo file = files[i];
                FileStamp stamp = new FileStamp { Name = file.Name, Length = file.Length, Modified = file.LastWriteTimeUtc.Ticks };
                stamps[i] = stamp;
                if ((selectedInfo == null && result.InfoFileName == null && file.Name.StartsWith("info", StringComparison.OrdinalIgnoreCase))
                    || string.Equals(file.Name, selectedInfo, StringComparison.OrdinalIgnoreCase))
                {
                    result.InfoFileName = file.Name;
                    result.InfoLength = stamp.Length;
                    result.InfoMtimeUtc = new DateTime(stamp.Modified, DateTimeKind.Utc);
                }
            }
            Array.Sort(stamps, delegate(FileStamp first, FileStamp second) { return StringComparer.Ordinal.Compare(first.Name, second.Name); });
            result.Files = stamps;
            return result;
        }

        internal bool SameAs(LevelDirectorySnapshot other)
        {
            if (other == null || Sets.Count != other.Sets.Count) return false;
            foreach (KeyValuePair<string, SetSnapshot> pair in Sets)
            {
                SetSnapshot previous;
                if (!other.Sets.TryGetValue(pair.Key, out previous) || !SameSet(pair.Value, previous)) return false;
            }
            return true;
        }

        internal static bool SameInfo(SetSnapshot first, SetSnapshot second)
        {
            return first.InfoFileName == second.InfoFileName && first.InfoLength == second.InfoLength
                && first.InfoMtimeUtc == second.InfoMtimeUtc;
        }

        internal static bool SameSet(SetSnapshot first, SetSnapshot second)
        {
            if (first == null || second == null || first.DirectoryName != second.DirectoryName || !SameInfo(first, second)
                || first.Files == null || second.Files == null || first.Files.Length != second.Files.Length) return false;
            for (int i = 0; i < first.Files.Length; i++)
            {
                FileStamp a = first.Files[i], b = second.Files[i];
                if (a.Name != b.Name || a.Length != b.Length || a.Modified != b.Modified) return false;
            }
            return true;
        }
    }
}
