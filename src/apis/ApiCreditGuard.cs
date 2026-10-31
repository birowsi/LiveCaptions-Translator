using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.apis
{
    /*
     * Keeps subtitles going when the OpenAI API credit runs out (if `Setting.CreditFallback` is on):
     * - Translation: GPT (API key) → ChatGPT plan (Sign in with ChatGPT) → Google.
     * - Speech recognition: OpenAI → Windows LiveCaptions (ChatGPT plan usage does not cover audio).
     * Credit counts as used up when the API says so (`insufficient_quota`) or when the tracked
     * remaining balance reaches zero. Setting the balance or resetting the counter clears it.
     */
    public static class ApiCreditGuard
    {
        public const string ROUTE_API = "API key";
        public const string ROUTE_CHATGPT = "ChatGPT plan";
        public const string ROUTE_GOOGLE = "Google";

        // Start LiveCaptions in the background below this balance, so the switch is instant.
        public const double LOW_BALANCE = 0.30;
        // Stop using the API at this balance, before requests start failing.
        public const double EMPTY_BALANCE = 0.01;

        private static volatile bool isApiCreditExhausted = false;

        public static event Action? Changed;

        public static bool IsEnabled => Translator.Setting?.CreditFallback ?? false;

        public static string TranslationRoute { get; private set; } = ROUTE_API;

        public static bool IsApiCreditExhausted =>
            isApiCreditExhausted ||
            (UsageTracker.StartingBalance > 0 && UsageTracker.RemainingBalance <= EMPTY_BALANCE);

        public static bool IsApiCreditLow =>
            UsageTracker.StartingBalance > 0 && UsageTracker.RemainingBalance <= LOW_BALANCE;

        public static void MarkApiCreditExhausted()
        {
            if (isApiCreditExhausted)
                return;
            isApiCreditExhausted = true;
            Changed?.Invoke();
        }

        public static void SetTranslationRoute(string route)
        {
            if (TranslationRoute == route)
                return;
            TranslationRoute = route;
            Changed?.Invoke();
        }

        /// <summary>
        /// After topping up: use the API again.
        /// </summary>
        public static void Reset()
        {
            isApiCreditExhausted = false;
            TranslationRoute = ROUTE_API;
            Changed?.Invoke();
        }
    }
}
