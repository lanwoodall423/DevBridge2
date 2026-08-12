using System;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DevBridge2
{
    internal static class DevBridgeQuicktestMenuAdapter
    {
        internal static bool IsGenuineMainMenuReady()
        {
            try
            {
                Root root = Current.Root;
                Root_Entry entryRoot = Current.Root_Entry;
                UIRoot uiRoot = Find.UIRoot;
                WindowStack windowStack = Find.WindowStack;

                if (!UnityData.IsInMainThread || !GenScene.InEntryScene ||
                    Current.ProgramState != ProgramState.Entry || root == null || entryRoot == null ||
                    !(uiRoot is UIRoot_Entry) || windowStack == null || Current.Game != null ||
                    WorldRendererUtility.WorldSelected || !Prefs.DevMode ||
                    UIMenuBackgroundManager.background == null ||
                    LongEventHandler.AnyEventNowOrWaiting || LongEventHandler.ShouldWaitForEvent)
                {
                    return false;
                }

                // This is the same window predicate used by UIRoot_Entry.ShouldDoMainMenu
                // (Assembly-CSharp 1.6.9676.17735, method token 0x06004B6C).
                for (int index = 0; index < windowStack.Count; index++)
                {
                    Window window = windowStack[index];
                    if (window == null || (window.layer == WindowLayer.Dialog && !window.IsDebug))
                        return false;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static void QueueBuiltInDevQuicktest(Action<Exception> reportFailure)
        {
            if (reportFailure == null)
                throw new ArgumentNullException(nameof(reportFailure));

            // MainMenuDrawer's inline Dev Quicktest action (0x060123AE) queues this
            // callback as "GeneratingMap" with the same handler and flags. Its callback
            // (0x060123AF) calls SetupForQuickTestPlay before InitGameStart.
            LongEventHandler.QueueLongEvent(
                () =>
                {
                    try
                    {
                        Root_Play.SetupForQuickTestPlay();
                        PageUtility.InitGameStart();
                    }
                    catch (Exception exception)
                    {
                        reportFailure(exception);
                        throw;
                    }
                },
                "GeneratingMap",
                true,
                GameAndMapInitExceptionHandlers.ErrorWhileGeneratingMap,
                true,
                false,
                null);
        }
    }
}
