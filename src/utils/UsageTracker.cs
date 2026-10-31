using System.IO;
using System.Text.Json;

namespace LiveCaptionsTranslator.utils
{
    /*
     * Tracks OpenAI API credit spent by this app, from the usage that each API response reports
     * (translation tokens, transcribed audio duration) multiplied by the published prices.
     * The total persists in `usage.json` so it survives restarts.
     * Official numbers: https://platform.openai.com/usage
     */
    public static class UsageTracker
    {
        public const string FILENAME = "usage.json";
        public const string USAGE_PAGE_URL = "https://platform.openai.com/usage";

        // USD per 1M tokens: (input, cached input, output). Standard tier, short context.
        private static readonly Dictionary<string, (double Input, double CachedInput, double Output)> TOKEN_PRICES = new()
        {
            { "gpt-6-luna", (0.10, 0.01, 0.50) },
            { "gpt-6.1-terra", (1.00, 0.05, 5.00) },
            { "gpt-6.1-sol", (2.00, 0.10, 10.00) },
            { "gpt-5.6-luna", (0.20, 0.02, 1.20) },
            { "gpt-5.4-nano", (0.20, 0.02, 1.25) },
            { "gpt-5.4-mini", (0.75, 0.075, 4.50) },
            { "gpt-5-nano", (0.05, 0.005, 0.40) },
            { "gpt-5-mini", (0.25, 0.025, 2.00) },
            { "gpt-4.1-nano", (0.10, 0.025, 0.40) },
            { "gpt-4.1-mini", (0.40, 0.10, 1.60) },
            { "gpt-4o-mini", (0.15, 0.075, 0.60) },
        };

        // USD per minute of audio.
        private static readonly Dictionary<string, double> AUDIO_PRICES = new()
        {
            { "gpt-transcribe", 0.0045 },
            { "gpt-live-transcribe", 0.017 },
            { "gpt-realtime-whisper", 0.017 },
            { "gpt-4o-mini-transcribe", 0.003 },
            { "gpt-4o-transcribe", 0.006 },
            { "whisper-1", 0.006 },
        };

        public class UsageData
        {
            public double StartingBalance { get; set; } = 0;
            public double TotalCost { get; set; } = 0;
            public double TranslationCost { get; set; } = 0;
            public double TranscriptionCost { get; set; } = 0;
            public double TranscriptionSeconds { get; set; } = 0;
            public long InputTokens { get; set; } = 0;
            public long CachedInputTokens { get; set; } = 0;
            public long OutputTokens { get; set; } = 0;
            public long Requests { get; set; } = 0;
            public bool HasUnpricedUsage { get; set; } = false;
            public DateTime Since { get; set; } = DateTime.Now;

            public UsageData Clone() => (UsageData)MemberwiseClone();
        }

        private static readonly object usageLock = new();
        private static readonly UsageData data = Load();

        public static double SessionCost { get; private set; } = 0;

        public static event Action? Changed;

        public static double TotalCost => data.TotalCost;
        public static double StartingBalance => data.StartingBalance;
        public static double RemainingBalance => data.StartingBalance - data.TotalCost;

        public static UsageData Snapshot()
        {
            lock (usageLock)
            {
                return data.Clone();
            }
        }

        public static void AddTokens(string model, long inputTokens, long cachedInputTokens, long outputTokens)
        {
            double cost = 0;
            bool isPriced = TryFindPrice(TOKEN_PRICES, model, out var price);
            if (isPriced)
            {
                long uncachedInput = Math.Max(0, inputTokens - cachedInputTokens);
                cost = (uncachedInput * price.Input + cachedInputTokens * price.CachedInput + outputTokens * price.Output) / 1_000_000;
            }

            lock (usageLock)
            {
                data.InputTokens += inputTokens;
                data.CachedInputTokens += cachedInputTokens;
                data.OutputTokens += outputTokens;
                data.Requests++;
                data.TranslationCost += cost;
                data.TotalCost += cost;
                data.HasUnpricedUsage |= !isPriced;
                SessionCost += cost;
                Save();
            }
            Changed?.Invoke();
        }

        public static void AddTranscription(string model, double seconds)
        {
            if (seconds <= 0)
                return;
            double cost = 0;
            bool isPriced = TryFindPrice(AUDIO_PRICES, model, out var pricePerMinute);
            if (isPriced)
                cost = seconds / 60 * pricePerMinute;

            lock (usageLock)
            {
                data.TranscriptionSeconds += seconds;
                data.TranscriptionCost += cost;
                data.TotalCost += cost;
                data.HasUnpricedUsage |= !isPriced;
                SessionCost += cost;
                Save();
            }
            Changed?.Invoke();
        }

        public static void SetStartingBalance(double balance)
        {
            lock (usageLock)
            {
                data.StartingBalance = Math.Max(0, balance);
                Save();
            }
            Changed?.Invoke();
        }

        /// <summary>
        /// Starts counting from zero, e.g. after topping up credit. Keeps the starting balance.
        /// </summary>
        public static void Reset(double startingBalance)
        {
            lock (usageLock)
            {
                data.StartingBalance = Math.Max(0, startingBalance);
                data.TotalCost = data.TranslationCost = data.TranscriptionCost = data.TranscriptionSeconds = 0;
                data.InputTokens = data.CachedInputTokens = data.OutputTokens = data.Requests = 0;
                data.HasUnpricedUsage = false;
                data.Since = DateTime.Now;
                SessionCost = 0;
                Save();
            }
            Changed?.Invoke();
        }

        public static string FormatUSD(double value)
        {
            return value < 1 ? $"${value:0.0000}" : $"${value:0.00}";
        }

        private static bool TryFindPrice<T>(Dictionary<string, T> prices, string model, out T price)
        {
            // Exact match first, then the longest known prefix (dated snapshots such as `gpt-6-luna-2026-05-18`).
            if (prices.TryGetValue(model, out price!))
                return true;
            var key = prices.Keys.Where(k => model.StartsWith(k + "-")).OrderByDescending(k => k.Length).FirstOrDefault();
            if (key != null)
            {
                price = prices[key];
                return true;
            }
            price = default!;
            return false;
        }

        private static UsageData Load()
        {
            try
            {
                string path = Path.Combine(Directory.GetCurrentDirectory(), FILENAME);
                if (File.Exists(path))
                    return JsonSerializer.Deserialize<UsageData>(File.ReadAllText(path)) ?? new UsageData();
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException) { }
            return new UsageData();
        }

        private static void Save()
        {
            try
            {
                string path = Path.Combine(Directory.GetCurrentDirectory(), FILENAME);
                string tempPath = path + ".tmp";
                File.WriteAllText(tempPath, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tempPath, path, overwrite: true);
            }
            catch (IOException) { }
        }
    }
}
