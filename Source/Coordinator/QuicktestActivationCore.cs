using System;

namespace DevBridge2
{
    public enum QuicktestActivationResult
    {
        WaitingForMainMenu,
        Requested,
        Failed
    }

    public sealed class QuicktestActivationController
    {
        private readonly Func<bool> mainMenuReady;
        private readonly Action activateBuiltInButton;
        private readonly int maxWaits;
        private int waits;
        private bool pending;

        public QuicktestActivationController(bool requested, Func<bool> mainMenuReady,
            Action activateBuiltInButton, int maxWaits)
        {
            Requested = requested;
            pending = requested;
            this.mainMenuReady = mainMenuReady ?? throw new ArgumentNullException(nameof(mainMenuReady));
            this.activateBuiltInButton = activateBuiltInButton ?? throw new ArgumentNullException(nameof(activateBuiltInButton));
            this.maxWaits = Math.Max(1, maxWaits);
        }

        public bool Requested { get; }
        public bool MainMenuReady { get; private set; }
        public bool ActivationRequested { get; private set; }
        public bool TerminalFailure { get; private set; }
        public bool Pending => pending;
        public string Failure { get; private set; }

        public QuicktestActivationResult Tick(bool onGameUiThread)
        {
            if (!Requested || !Pending || ActivationRequested || TerminalFailure)
                return TerminalFailure ? QuicktestActivationResult.Failed : QuicktestActivationResult.Requested;

            if (!onGameUiThread)
                return WaitOrFail("the game/UI-thread boundary was not available");

            bool ready;
            try
            {
                ready = mainMenuReady();
            }
            catch (Exception exception)
            {
                return Fail("main-menu readiness inspection failed: " + Bounded(exception));
            }

            if (!ready)
                return WaitOrFail("the genuine main menu did not become ready within the bounded activation window");

            MainMenuReady = true;
            try
            {
                // This delegate is the actual MainMenuDrawer built-in button action.
                // It is deliberately not callable until the genuine entry UI is ready.
                activateBuiltInButton();
                if (TerminalFailure)
                    return QuicktestActivationResult.Failed;
                ActivationRequested = true;
                pending = false;
                return QuicktestActivationResult.Requested;
            }
            catch (Exception exception)
            {
                return Fail("built-in Dev Quicktest activation failed: " + Bounded(exception));
            }
        }

        public void ReportActivationFailure(Exception exception)
        {
            if (TerminalFailure)
                return;

            ActivationRequested = false;
            Fail("built-in Dev Quicktest activation failed after queueing: " + Bounded(exception));
        }

        private QuicktestActivationResult WaitOrFail(string reason)
        {
            waits++;
            if (waits < maxWaits)
                return QuicktestActivationResult.WaitingForMainMenu;
            return Fail(reason);
        }

        private QuicktestActivationResult Fail(string reason)
        {
            TerminalFailure = true;
            pending = false;
            Failure = reason;
            return QuicktestActivationResult.Failed;
        }

        private static string Bounded(Exception exception)
        {
            if (exception == null)
                return "unknown failure";

            string value = exception.GetType().Name + ": " + exception.Message;
            return value.Length <= 240 ? value : value.Substring(0, 240);
        }
    }
}
