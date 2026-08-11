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

        public QuicktestActivationController(bool requested, Func<bool> mainMenuReady,
            Action activateBuiltInButton, int maxWaits)
        {
            Requested = requested;
            this.mainMenuReady = mainMenuReady ?? throw new ArgumentNullException(nameof(mainMenuReady));
            this.activateBuiltInButton = activateBuiltInButton ?? throw new ArgumentNullException(nameof(activateBuiltInButton));
            this.maxWaits = Math.Max(1, maxWaits);
        }

        public bool Requested { get; }
        public bool MainMenuReady { get; private set; }
        public bool ActivationRequested { get; private set; }
        public bool TerminalFailure { get; private set; }
        public string Failure { get; private set; }

        public QuicktestActivationResult Tick()
        {
            if (!Requested || ActivationRequested || TerminalFailure)
                return TerminalFailure ? QuicktestActivationResult.Failed : QuicktestActivationResult.Requested;

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
            {
                waits++;
                if (waits < maxWaits)
                    return QuicktestActivationResult.WaitingForMainMenu;
                return Fail("the genuine main menu did not become ready within the bounded activation window");
            }

            MainMenuReady = true;
            try
            {
                // This delegate is the actual MainMenuDrawer built-in button action.
                // It is deliberately not callable until the genuine entry UI is ready.
                activateBuiltInButton();
                ActivationRequested = true;
                return QuicktestActivationResult.Requested;
            }
            catch (Exception exception)
            {
                return Fail("built-in Dev Quicktest activation failed: " + Bounded(exception));
            }
        }

        private QuicktestActivationResult Fail(string reason)
        {
            TerminalFailure = true;
            Failure = reason;
            return QuicktestActivationResult.Failed;
        }

        private static string Bounded(Exception exception)
        {
            string value = exception.GetType().Name + ": " + exception.Message;
            return value.Length <= 240 ? value : value.Substring(0, 240);
        }
    }
}
