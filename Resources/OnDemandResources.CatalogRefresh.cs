namespace DIYLevelFastInit
{
    internal static partial class OnDemandResources
    {
        internal static bool CatalogFilesAreIdle
        {
            get
            {
                return !IsTransitioning && !_serverPreparing && _catalogBundles == null
                    && _previewBundles == null && _warmBundles == null && _preparingBundles == null
                    && _clientPreparingBundles == null && PendingBundles.Count == 0 && PendingInfos.Count == 0
                    && PendingLevelInfos.Count == 0 && InfoBundles.Count == 0;
            }
        }
    }
}
