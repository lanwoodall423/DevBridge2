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
                    LongEventHandler.AnyEventNowOrWaiting || LongEventHandler.ShouldWaitForEvent)
                {
                    return false;
                }

                // Dialogs such as RimWorld's startup error log may cover the menu, but they do
                // not invalidate the initialized Root_Entry/UIRoot_Entry lifecycle required by
                // the built-in Quicktest callback.
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static void QueueBuiltInDevQuicktest(Action<Exception, string> reportFailure)
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
                        try
                        {
                            reportFailure(exception, "ROOT_PLAY_SETUP_FOR_QUICKTEST_PLAY_OR_INIT_GAME_START");
                        }
                        catch
                        {
                            // The game's original exception must remain the
                            // exception delivered to RimWorld's handler.
                        }
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
