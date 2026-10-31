using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace LiveCaptionsTranslator.models
{
    /*
     * Where the source captions come from.
     * - LiveCaptions: Windows LiveCaptions (free, on-device).
     * - OpenAI: PC audio (WASAPI loopback) streamed to an OpenAI Realtime transcription session.
     */
    public class TranscriptionSetting : INotifyPropertyChanged
    {
        public const string SOURCE_LIVECAPTIONS = "LiveCaptions";
        public const string SOURCE_OPENAI = "OpenAI";

        [JsonIgnore]
        public static List<string> Sources => [SOURCE_LIVECAPTIONS, SOURCE_OPENAI];
        [JsonIgnore]
        public static List<string> Models => [MODEL_TRANSCRIBE, MODEL_LIVE_TRANSCRIBE];
        [JsonIgnore]
        public static List<string> Delays => ["minimal", "low", "medium", "high"];

        // Transcribes each finished line; cheapest ($0.0045/min).
        public const string MODEL_TRANSCRIBE = "gpt-transcribe";
        // Streams text while someone is still talking; faster but pricier ($0.017/min).
        public const string MODEL_LIVE_TRANSCRIBE = "gpt-live-transcribe";

        public event PropertyChangedEventHandler? PropertyChanged;

        private string source = SOURCE_LIVECAPTIONS;
        private string apiKey = "";
        private string model = "gpt-transcribe";
        private string language = "ja";
        private string keywords = "";
        private string prompt = "";
        private double vadThreshold = 0.5;
        private int silenceDurationMs = 500;
        private int maxTurnSeconds = 12;
        private string delay = "low";

        public string Source
        {
            get => source;
            set
            {
                source = value;
                OnPropertyChanged("Source");
            }
        }
        // Leave empty to use the key of the GPT translation engine.
        public string ApiKey
        {
            get => apiKey;
            set
            {
                apiKey = value;
                OnPropertyChanged("ApiKey");
            }
        }
        public string Model
        {
            get => model;
            set
            {
                model = value;
                OnPropertyChanged("Model");
            }
        }
        // ISO 639-1 code of the spoken language, e.g. `ja`. Empty for auto-detection.
        public string Language
        {
            get => language;
            set
            {
                language = value;
                OnPropertyChanged("Language");
            }
        }
        // Names and terms that may appear in the audio, one per line or comma-separated.
        public string Keywords
        {
            get => keywords;
            set
            {
                keywords = value;
                OnPropertyChanged("Keywords");
            }
        }
        // Free-form description of what is being listened to.
        public string Prompt
        {
            get => prompt;
            set
            {
                prompt = value;
                OnPropertyChanged("Prompt");
            }
        }
        public double VadThreshold
        {
            get => vadThreshold;
            set
            {
                vadThreshold = value;
                OnPropertyChanged("VadThreshold");
            }
        }
        public int SilenceDurationMs
        {
            get => silenceDurationMs;
            set
            {
                silenceDurationMs = value;
                OnPropertyChanged("SilenceDurationMs");
            }
        }
        // Force a subtitle line when someone talks without pausing for this long.
        public int MaxTurnSeconds
        {
            get => maxTurnSeconds;
            set
            {
                maxTurnSeconds = value;
                OnPropertyChanged("MaxTurnSeconds");
            }
        }

        // Latency/accuracy trade-off of gpt-live-transcribe.
        public string Delay
        {
            get => delay;
            set
            {
                delay = value;
                OnPropertyChanged("Delay");
            }
        }

        [JsonIgnore]
        public bool IsOpenAI => source == SOURCE_OPENAI;
        // Live models stream text before a line ends and need the app to decide where lines end.
        [JsonIgnore]
        public bool IsLiveModel => model is MODEL_LIVE_TRANSCRIBE or "gpt-realtime-whisper";

        // Each keyword may carry a fixed translation: `原文=번역`.
        private IEnumerable<(string Source, string? Translation)> ParseKeywords()
        {
            foreach (var entry in keywords.Split(['\n', '\r', ',', '、'],
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = entry.Replace("<", "").Replace(">", "").Split('=', 2, StringSplitOptions.TrimEntries);
                if (parts[0].Length == 0)
                    continue;
                yield return (parts[0], parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null);
            }
        }

        public List<string> GetKeywordList()
        {
            return ParseKeywords().Select(k => k.Source).Distinct().ToList();
        }

        public List<(string Source, string Translation)> GetGlossary()
        {
            return ParseKeywords()
                .Where(k => k.Translation != null)
                .Select(k => (k.Source, k.Translation!))
                .ToList();
        }

        public void OnPropertyChanged([CallerMemberName] string? propName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
            Translator.Setting?.Save();
        }
    }
}
