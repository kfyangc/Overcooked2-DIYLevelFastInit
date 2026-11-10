namespace DIYLevelFastInit
{
    public partial class FastInitPlugin
    {
        internal static void CancelFailedResourcePreparation()
        {
            ClientLoadGate.Cancel("resource read failure");
            OnDemandResources.CancelServerPreparation("resource read failure");
            OnDemandResources.ResetSaveSelection();
        }
    }
}
