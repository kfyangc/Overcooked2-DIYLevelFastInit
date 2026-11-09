using System.Globalization;

namespace DIYLevelFastInit
{
    /// <summary>Separates startup work from native waits and cache fallbacks.</summary>
    internal sealed class StartupTimings
    {
        internal int Hits, Misses, MetadataSets, Skipped, Failed, Levels;
        internal double CacheReadMs, CreateMs, MetadataReadMs, BundleMs, AssetMs, ExtractMs, CleanupMs, SnapshotMs, MenuMs, PausedMs, YieldWaitMs;

        internal static string Ms(double value)
        {
            return value.ToString("F1", CultureInfo.InvariantCulture);
        }

        internal void Add(string name, InfoLoad load)
        {
            if (load.InfoFile == null) Skipped++;
            else if (load.CacheHit) Hits++;
            else Misses++;
            if (load.MetadataHit) MetadataSets++;
            if (load.InfoFile != null && load.So == null) Failed++;
            if (load.So != null) Levels += load.So.levelInfos.Length;
            CacheReadMs += load.CacheReadMs;
            CreateMs += load.CreateMs;
            MetadataReadMs += load.MetadataReadMs;
            PausedMs += load.PublicationPauseMs;
            BundleMs += load.BundleMs;
            AssetMs += load.AssetMs;
            ExtractMs += load.ExtractMs;
            CleanupMs += load.CleanupMs;
            FastInitPlugin.Log.LogInfo("catalog timing [" + name + "]: source="
                + (load.InfoFile == null ? "skipped" : load.CacheHit ? "cache" : load.MetadataHit ? "metadata" : "bundle")
                + ", cacheReadMs=" + Ms(load.CacheReadMs) + ", createMs=" + Ms(load.CreateMs)
                + ", metadataReadMs=" + Ms(load.MetadataReadMs)
                + ", publicationPauseMs=" + Ms(load.PublicationPauseMs)
                + ", bundleMs=" + Ms(load.BundleMs) + ", assetMs=" + Ms(load.AssetMs)
                + ", extractWriteMs=" + Ms(load.ExtractMs) + ", cleanupWaitMs=" + Ms(load.CleanupMs));
        }

        internal string Summary()
        {
            return "cacheHits=" + Hits + ", cacheMisses=" + Misses + ", metadataSets=" + MetadataSets
                + ", skippedSets=" + Skipped + ", failedSets=" + Failed + ", levels=" + Levels
                + ", cacheReadMs=" + Ms(CacheReadMs) + ", createMs=" + Ms(CreateMs)
                + ", metadataReadMs=" + Ms(MetadataReadMs)
                + ", bundleMs=" + Ms(BundleMs) + ", assetMs=" + Ms(AssetMs)
                + ", extractWriteMs=" + Ms(ExtractMs) + ", cleanupWaitMs=" + Ms(CleanupMs)
                + ", snapshotMs=" + Ms(SnapshotMs) + ", menuMs=" + Ms(MenuMs) + ", pausedMs=" + Ms(PausedMs)
                + ", yieldWaitMs=" + Ms(YieldWaitMs);
        }
    }
}
