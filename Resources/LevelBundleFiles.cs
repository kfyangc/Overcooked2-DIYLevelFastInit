using System;
using System.IO;

namespace DIYLevelFastInit
{
    /// <summary>Recognizes top-level UnityFS bundles without reading their payloads.</summary>
    internal static class LevelBundleFiles
    {
        internal static bool IsBundle(string path)
        {
            try
            {
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 8))
                {
                    byte[] signature = { 85, 110, 105, 116, 121, 70, 83, 0 };
                    for (int i = 0; i < signature.Length; i++)
                        if (stream.ReadByte() != signature[i]) return false;
                    return true;
                }
            }
            catch (Exception) { return false; }
        }
    }
}
