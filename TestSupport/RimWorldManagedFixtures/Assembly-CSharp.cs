using System;

namespace Verse
{
    public class ModContentPack
    {
    }

    public class Mod
    {
        public Mod(ModContentPack content)
        {
        }
    }

    public class Game
    {
    }

    public class GameComponent
    {
        public GameComponent()
        {
        }

        public GameComponent(Game game)
        {
        }

        public virtual void GameComponentTick()
        {
        }
    }

    public class Root
    {
    }

    public class Root_Entry : Root
    {
    }

    public class UIRoot
    {
    }

    public class UIRoot_Entry : UIRoot
    {
    }

    public class WindowStack
    {
    }

    public enum ProgramState
    {
        Entry
    }

    public static class Current
    {
        public static Root Root { get; set; }
        public static Root_Entry Root_Entry { get; set; }
        public static ProgramState ProgramState { get; set; }
        public static Game Game { get; set; }
    }

    public static class Find
    {
        public static UIRoot UIRoot { get; set; }
        public static WindowStack WindowStack { get; set; }
        public static object CurrentMap { get; set; }
        public static object TickManager { get; set; }
    }

    public static class UnityData
    {
        public static bool IsInMainThread { get; set; }
    }

    public static class GenScene
    {
        public static bool InEntryScene { get; set; }
        public static bool InPlayScene { get; set; }
    }

    public static class Prefs
    {
        public static bool DevMode { get; set; }
    }

    public static class LongEventHandler
    {
        public static bool AnyEventNowOrWaiting { get; set; }
        public static bool ShouldWaitForEvent { get; set; }

        public static void ExecuteWhenFinished(Action action)
        {
            action?.Invoke();
        }

        public static void QueueLongEvent(Action action, string text, bool doAsynchronously,
            Action<Exception> exceptionHandler, bool waitUntilFinished, bool handleExceptions, object state)
        {
            action?.Invoke();
        }
    }

    public static class GameAndMapInitExceptionHandlers
    {
        public static Action<Exception> ErrorWhileGeneratingMap { get; } = _ => { };
    }

    public static class Log
    {
        public static void Message(string message)
        {
        }

        public static void Warning(string message)
        {
        }

        public static void Error(string message)
        {
        }
    }
}

namespace RimWorld
{
    public static class Root_Play
    {
        public static void SetupForQuickTestPlay()
        {
        }
    }

    public static class PageUtility
    {
        public static void InitGameStart()
        {
        }
    }
}

namespace RimWorld.Planet
{
    public static class WorldRendererUtility
    {
        public static bool WorldSelected { get; set; }
    }
}
