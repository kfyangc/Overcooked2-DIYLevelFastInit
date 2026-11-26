namespace DIYLevelFastInit
{
    internal static partial class OnDemandResources
    {
        // Direct UnityFS cover reads are fast on demand (~5-15 ms) and use an in-memory LRU,
        // so background startup disk prewarming is no longer needed.
        internal static void QueueThumbnailWarmup()
        {
            _warmGeneration++;
            _warmQueued = false;
            _warmRunning = false;
            _warmLoading = false;
            _warmBundles = null;
            ThumbnailDone = ThumbnailTotal = 0;
            if (!_thumbnailAdapter || _warmQueued) return;
        }
    }
}
