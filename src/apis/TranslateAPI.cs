using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;

using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.apis
{
    public static class TranslateAPI
    {
        /*
         * The key of this field is used as the content for `translateAPIBox` in the `SettingPage`.
         * If you'd like to add a new API, please insert the key-value pair here.
         */
        public static readonly Dictionary<string, Func<string, CancellationToken, Task<string>>>
            TRANSLATE_FUNCTIONS = new()
        {
            { "Google", Google },
            { "Google2", Google2 },
            { "Ollama", Ollama },
            { "OpenAI", OpenAI },
            { "GPT", GPT },
            { "ChatGPT", ChatGPT },
            { "LMStudio", LMStudio },
            { "DeepL", DeepL },
            { "OpenRouter", OpenRouter },
            { "Youdao", Youdao },
            { "MTranServer", MTranServer },
            { "Baidu", Baidu },
            { "LibreTranslate", LibreTranslate },
        };
        public static readonly List<string> LLM_BASED_APIS = new()
        {
            "Ollama", "OpenAI", "GPT", "ChatGPT", "OpenRouter", "LMStudio"
        };
        public static readonly List<string> NO_CONFIG_APIS = new()
        {
            "Google", "Google2"
        };

        public static Func<string, CancellationToken, Task<string>> TranslateFunction =>
            TRANSLATE_FUNCTIONS[Translator.Setting.ApiName];
        public static bool IsLLMBased => LLM_BASED_APIS.Contains(Translator.Setting.ApiName);
        public static string Prompt => Translator.Setting.Prompt;

        private static readonly HttpClient client = new HttpClient()
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
        private static int openai_fallback_index = 0;

        public const string RESPONSES_URL = "https://api.openai.com/v1/responses";
        private static readonly HttpClient responsesClient = new HttpClient()
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        private static readonly TimeSpan responsesTimeout = TimeSpan.FromSeconds(15);
        private static DateTime chatgptPausedUntil = DateTime.MinValue;
        private static string chatgptPauseMessage = string.Empty;

        public static async Task<string> OpenAI(string text, CancellationToken token = default)
        {
            var config = Translator.Setting["OpenAI"] as OpenAIConfig;
            string language = OpenAIConfig.SupportedLanguages.TryGetValue(
                Translator.Setting.TargetLanguage, out var langValue) ? langValue : Translator.Setting.TargetLanguage;

            var messages = new List<BaseLLMConfig.Message>
            {
                new BaseLLMConfig.Message { role = "system", content = string.Format(Prompt, language) },
                new BaseLLMConfig.Message { role = "user", content = $"🔤 {text} 🔤" }
            };

            if (Translator.Setting.ContextAware)
            {
                foreach (var entry in Translator.Caption.AwareContexts)
                {
                    string translatedText = entry.TranslatedText;
                    if (translatedText.Contains("[ERROR]") || translatedText.Contains("[WARNING]"))
                        continue;
                    translatedText = RegexPatterns.NoticePrefix().Replace(translatedText, "");

                    messages.InsertRange(1, [
                        new BaseLLMConfig.Message { role = "user", content = $"🔤 {entry.SourceText} 🔤" },
                        new BaseLLMConfig.Message { role = "assistant", content = $"{translatedText}" }
                    ]);
                }
            }

            client.DefaultRequestHeaders.Clear();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {config.ApiKey}");

            HttpResponseMessage response;
            try
            {
                while (true)
                {
                    var requestData = LLMRequestDataFactory.Create(openai_fallback_index,
                        config.ModelName, messages, config.Temperature);
                    string jsonContent = JsonSerializer.Serialize(requestData, requestData.GetType());
                    var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                    response = await client.PostAsync(TextUtil.NormalizeUrl(config.ApiUrl), content, token);
                    if (response.StatusCode != HttpStatusCode.BadRequest &&
                        response.StatusCode != HttpStatusCode.UnprocessableEntity)
                        break;
                    Thread.Sleep(15);

                    openai_fallback_index++;
                    if (openai_fallback_index >= LLMRequestDataFactory.FallbackCount)
                    {
                        openai_fallback_index = 0;
                        break;
                    }
                }
            }
            catch (OperationCanceledException ex)
            {
                if (ex.Message.StartsWith("The request"))
                    return $"[ERROR] Translation Failed: The request was canceled due to timeout (> 8 seconds), " +
                           $"please use a faster API or check network connection.";
                throw;
            }
            catch (Exception ex)
            {
                return $"[ERROR] Translation Failed: {ex.Message}";
            }

            if (response.IsSuccessStatusCode)
            {
                string responseString = await response.Content.ReadAsStringAsync();
                var responseObj = JsonSerializer.Deserialize<OpenAIConfig.Response>(responseString);
                var output = responseObj.choices[0].message.content;
                return RegexPatterns.ModelThinking().Replace(output, "");
            }
            else
            {
                // Show the API's own error message (e.g. invalid key, unknown model, unsupported parameter).
                string detail = string.Empty;
                try
                {
                    using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    if (doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object &&
                        error.TryGetProperty("message", out var message))
                        detail = $": {message.GetString()}";
                }
                catch (JsonException) { }
                return $"[ERROR] Translation Failed: HTTP Error - {response.StatusCode}{detail}";
            }
        }

        public static async Task<string> ChatGPT(string text, CancellationToken token = default)
        {
            var config = Translator.Setting["ChatGPT"] as ChatGPTConfig;
            if (string.IsNullOrWhiteSpace(config?.ModelName))
                return "[ERROR] Translation Failed: No ChatGPT model selected. Choose one in API Setting → ChatGPT.";
            // Pause requests after a usage-limit error instead of hammering the user's plan.
            if (DateTime.UtcNow < chatgptPausedUntil)
                return chatgptPauseMessage;

            string accessToken;
            try
            {
                accessToken = await ChatGPTAuth.GetAccessTokenAsync(token);
            }
            catch (ChatGPTAuthException ex)
            {
                return $"[ERROR] Translation Failed: {ex.Message}";
            }

            var requestData = CreateResponsesRequest(text, config.ModelName, config.ReasoningEffort,
                ChatGPTConfig.SupportedLanguages);
            // Usage is included in the ChatGPT plan, so it is not added to the API credit tracker.
            return await StreamResponsesAsync(accessToken, requestData, DescribeChatGPTError, trackUsage: false, token);
        }

        public static async Task<string> GPT(string text, CancellationToken token = default)
        {
            var config = Translator.Setting["GPT"] as GPTConfig;
            var (apiKey, keyError) = ResolveOpenAIApiKey();
            if (apiKey == null)
                return $"[ERROR] Translation Failed: {keyError}";
            if (string.IsNullOrWhiteSpace(config!.ModelName))
                return "[ERROR] Translation Failed: No model selected. Set it in API Setting → GPT.";

            var requestData = CreateResponsesRequest(text, config.ModelName, config.ReasoningEffort,
                GPTConfig.SupportedLanguages);
            // Keep the reply short: one subtitle line. Reasoning tokens also count towards this limit.
            requestData["max_output_tokens"] = config.ReasoningEffort == "none" ? 300 : 2000;
            return await StreamResponsesAsync(apiKey, requestData, DescribeAPIError, trackUsage: true, token);
        }

        /// <summary>
        /// The OpenAI API key shared by the GPT engine and OpenAI speech recognition:
        /// the first valid key of API Setting → GPT and API Setting → Speech Recognition.
        /// </summary>
        public static (string? key, string error) ResolveOpenAIApiKey()
        {
            var candidates = new[]
            {
                (Translator.Setting["GPT"] as GPTConfig)?.ApiKey,
                Translator.Setting.Transcription.ApiKey
            };

            bool hasInvalidKey = false;
            foreach (var candidate in candidates)
            {
                // Drop whitespace and invisible characters that sneak in when copying.
                string key = new string((candidate ?? "").Where(c => c > ' ' && c <= '~').ToArray());
                if (key.Length == 0)
                    continue;
                bool isPrintableAscii = (candidate ?? "").All(c => c <= '~' || char.IsWhiteSpace(c) ||
                                                                 c is '​' or '‌' or '‍' or '﻿');
                if (isPrintableAscii)
                    return (key, "");
                hasInvalidKey = true;
            }

            return hasInvalidKey
                ? (null, "The OpenAI API key contains non-English characters (typed with the Korean keyboard?). " +
                         "Paste the sk-... key again in API Setting → GPT.")
                : (null, "No OpenAI API key. Set it in API Setting → GPT.");
        }

        private static Dictionary<string, object> CreateResponsesRequest(string text, string model,
            string reasoningEffort, Dictionary<string, string> supportedLanguages)
        {
            string language = supportedLanguages.TryGetValue(
                Translator.Setting.TargetLanguage, out var langValue) ? langValue : Translator.Setting.TargetLanguage;

            // The prompt goes to `instructions` (system messages are rejected on the ChatGPT plan route).
            var input = new List<BaseLLMConfig.Message>();
            if (Translator.Setting.ContextAware)
            {
                foreach (var entry in Translator.Caption.AwareContexts)
                {
                    string translatedText = entry.TranslatedText;
                    if (translatedText.Contains("[ERROR]") || translatedText.Contains("[WARNING]"))
                        continue;
                    translatedText = RegexPatterns.NoticePrefix().Replace(translatedText, "");

                    input.Add(new BaseLLMConfig.Message { role = "user", content = $"🔤 {entry.SourceText} 🔤" });
                    input.Add(new BaseLLMConfig.Message { role = "assistant", content = translatedText });
                }
            }
            input.Add(new BaseLLMConfig.Message { role = "user", content = $"🔤 {text} 🔤" });

            // Fixed translations from the speech recognition keywords (`原文=번역`).
            string instructions = string.Format(Prompt, language);
            var glossary = Translator.Setting.Transcription.GetGlossary();
            if (glossary.Count > 0)
                instructions += "\nAlways translate these terms as given:\n" +
                                string.Join("\n", glossary.Select(g => $"{g.Source} → {g.Translation}"));

            var requestData = new Dictionary<string, object>
            {
                { "model", model },
                { "instructions", instructions },
                { "input", input },
                { "store", false },
                { "stream", true },
            };
            if (!string.IsNullOrEmpty(reasoningEffort) && reasoningEffort != ChatGPTConfig.DEFAULT_REASONING_EFFORT)
                requestData["reasoning"] = new Dictionary<string, string> { { "effort", reasoningEffort } };
            return requestData;
        }

        private static async Task<string> StreamResponsesAsync(string bearerToken, Dictionary<string, object> requestData,
            Func<int, string, string> describeError, bool trackUsage, CancellationToken token)
        {
            string model = requestData["model"] as string ?? "";

            using var timeoutCTS = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCTS.CancelAfter(responsesTimeout);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, RESPONSES_URL);
                request.Headers.Authorization = ChatGPTAuth.BearerHeader(bearerToken);
                request.Headers.Accept.ParseAdd("text/event-stream");
                request.Content = new StringContent(JsonSerializer.Serialize(requestData), Encoding.UTF8, "application/json");

                using var response = await responsesClient.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, timeoutCTS.Token);
                if (!response.IsSuccessStatusCode)
                {
                    string body = await response.Content.ReadAsStringAsync(timeoutCTS.Token);
                    return describeError((int)response.StatusCode, body);
                }

                var state = new ResponsesStreamState();
                using var stream = await response.Content.ReadAsStreamAsync(timeoutCTS.Token);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var data = new StringBuilder();
                while (!state.IsCompleted && state.Failure == null)
                {
                    string? line = await reader.ReadLineAsync(timeoutCTS.Token);
                    if (line == null || line.Length == 0)
                    {
                        if (data.Length > 0)
                        {
                            HandleResponsesEvent(data.ToString(), state, describeError);
                            data.Clear();
                        }
                        if (line == null)
                            break;
                        continue;
                    }
                    if (line.StartsWith("data:"))
                    {
                        if (data.Length > 0)
                            data.Append('\n');
                        data.Append(line.AsSpan(5).TrimStart());
                    }
                }

                // Failed and incomplete responses are billed too.
                if (trackUsage && state.HasUsage)
                    UsageTracker.AddTokens(model, state.InputTokens, state.CachedInputTokens, state.OutputTokens);

                if (state.Failure != null)
                    return state.Failure;
                if (!state.IsCompleted)
                    return "[ERROR] Translation Failed: The response stream ended before the response was completed.";
                return RegexPatterns.ModelThinking().Replace(state.Output.ToString(), "").Trim();
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                return $"[ERROR] Translation Failed: The request was canceled due to timeout (> {responsesTimeout.TotalSeconds} seconds), " +
                       $"please choose a faster model / lower reasoning effort or check network connection.";
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return $"[ERROR] Translation Failed: {ex.Message}";
            }
        }

        private class ResponsesStreamState
        {
            public StringBuilder Output { get; } = new();
            public string? Failure { get; set; }
            public bool IsCompleted { get; set; }
            public bool HasUsage { get; set; }
            public long InputTokens { get; set; }
            public long CachedInputTokens { get; set; }
            public long OutputTokens { get; set; }
        }

        private static void HandleResponsesEvent(string json, ResponsesStreamState state,
            Func<int, string, string> describeError)
        {
            if (json == "[DONE]")
                return;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? type = root.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;

            // Terminal events carry the final response object with its token usage.
            if (root.TryGetProperty("response", out var responseObj) && responseObj.ValueKind == JsonValueKind.Object &&
                responseObj.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                state.HasUsage = true;
                state.InputTokens = usage.TryGetProperty("input_tokens", out var input) ? input.GetInt64() : 0;
                state.OutputTokens = usage.TryGetProperty("output_tokens", out var output) ? output.GetInt64() : 0;
                state.CachedInputTokens =
                    usage.TryGetProperty("input_tokens_details", out var details) && details.ValueKind == JsonValueKind.Object &&
                    details.TryGetProperty("cached_tokens", out var cached) ? cached.GetInt64() : 0;
            }

            switch (type)
            {
                case "response.output_text.delta":
                    if (root.TryGetProperty("delta", out var delta))
                        state.Output.Append(delta.GetString());
                    break;
                case "response.completed":
                    state.IsCompleted = true;
                    break;
                case "response.failed":
                    var error = responseObj.ValueKind == JsonValueKind.Object &&
                                responseObj.TryGetProperty("error", out var err) ? err : default;
                    state.Failure = describeError(200, error.ValueKind == JsonValueKind.Object
                        ? $"{{\"error\":{error.GetRawText()}}}" : json);
                    break;
                case "response.incomplete":
                    string reason = responseObj.ValueKind == JsonValueKind.Object &&
                                    responseObj.TryGetProperty("incomplete_details", out var incomplete) &&
                                    incomplete.ValueKind == JsonValueKind.Object &&
                                    incomplete.TryGetProperty("reason", out var reasonProp)
                        ? reasonProp.GetString() ?? "unknown" : "unknown";
                    // A truncated translation is still better than nothing.
                    if (reason == "max_output_tokens" && state.Output.Length > 0)
                        state.IsCompleted = true;
                    else
                        state.Failure = $"[ERROR] Translation Failed: The response was incomplete ({reason}).";
                    break;
                case "error":
                    state.Failure = describeError(200, root.TryGetProperty("error", out _)
                        ? json : $"{{\"error\":{json}}}");
                    break;
            }
        }

        private static (string? code, string? message, string? param) ParseAPIError(string body)
        {
            string? code = null, message = null, param = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                {
                    code = error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                    message = error.TryGetProperty("message", out var m) ? m.GetString() : null;
                    param = error.TryGetProperty("param", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                }
                else if (root.TryGetProperty("detail", out var detail))
                    message = detail.ValueKind == JsonValueKind.String ? detail.GetString() : detail.GetRawText();
            }
            catch (JsonException)
            {
                message = body.Length > 200 ? body[..200] : body;
            }
            return (code, message, param);
        }

        private static string FormatAPIError(int statusCode, string? code, string? message)
        {
            string description = statusCode == 200 ? "" : $"HTTP {statusCode}";
            if (!string.IsNullOrEmpty(code))
                description += $" {code}";
            if (!string.IsNullOrEmpty(message))
                description += $" - {message}";
            return $"[ERROR] Translation Failed: {description.Trim(' ', '-')}";
        }

        private static string DescribeAPIError(int statusCode, string body)
        {
            var (code, message, _) = ParseAPIError(body);
            if (code == "insufficient_quota")
                return "[ERROR] Translation Failed: OpenAI API credit is used up. Check your balance at platform.openai.com.";
            return FormatAPIError(statusCode, code, message);
        }

        private static string DescribeChatGPTError(int statusCode, string body)
        {
            var (code, message, param) = ParseAPIError(body);

            switch (code)
            {
                case "subscription_sharing_usage_limit_exceeded":
                    chatgptPauseMessage = "[ERROR] ChatGPT usage limit reached. " +
                                          $"Manage usage at {ChatGPTAuth.USAGE_SETTINGS_URL} (paused for 1 minute).";
                    chatgptPausedUntil = DateTime.UtcNow.AddMinutes(1);
                    return chatgptPauseMessage;
                case "subscription_sharing_user_not_eligible":
                    return "[ERROR] Translation Failed: ChatGPT plan usage is not available for this account or workspace.";
                case "subscription_sharing_usage_unavailable":
                case "subscription_sharing_user_unavailable":
                    return "[ERROR] Translation Failed: ChatGPT usage is temporarily unavailable, will retry with the next sentence.";
                case "subscription_sharing_unsupported_capability":
                    return $"[ERROR] Translation Failed: Not supported with ChatGPT plan usage ({param ?? message}). " +
                           "Try another model or reasoning effort.";
                case "subscription_sharing_invalid_user":
                    ChatGPTAuth.InvalidateAccessToken();
                    return "[ERROR] Translation Failed: ChatGPT sign-in was rejected. If this repeats, sign in again in API Setting → ChatGPT.";
            }

            if (statusCode == 401)
                ChatGPTAuth.InvalidateAccessToken();
            return FormatAPIError(statusCode, code, message);
        }

        public static async Task<string> Ollama(string text, CancellationToken token = default)
        {
            var config = Translator.Setting["Ollama"] as OllamaConfig;
            string language = OllamaConfig.SupportedLanguages.TryGetValue(
                Translator.Setting.TargetLanguage, out var langValue) ? langValue : Translator.Setting.TargetLanguage;
            string apiUrl = TextUtil.NormalizeUrl(config.ApiUrl + "/api/chat");

            var messages = new List<BaseLLMConfig.Message>
            {
                new BaseLLMConfig.Message { role = "system", content = string.Format(Prompt, language) },
                new BaseLLMConfig.Message { role = "user", content = $"🔤 {text} 🔤" }
            };

            if (Translator.Setting.ContextAware)
            {
                foreach (var entry in Translator.Caption.AwareContexts)
                {
                    string translatedText = entry.TranslatedText;
                    if (translatedText.Contains("[ERROR]") || translatedText.Contains("[WARNING]"))
                        continue;
                    translatedText = RegexPatterns.NoticePrefix().Replace(translatedText, "");

                    messages.InsertRange(1, [
                        new BaseLLMConfig.Message { role = "user", content = $"🔤 {entry.SourceText} 🔤" },
                        new BaseLLMConfig.Message { role = "assistant", content = $"{translatedText}" }
                    ]);
                }
            }

            var requestData = LLMRequestDataFactory.Create("Ollama", config.ModelName, messages, config.Temperature);
            requestData.keep_alive = config.keep_alive;
            string jsonContent = JsonSerializer.Serialize(requestData, requestData.GetType());
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
            client.DefaultRequestHeaders.Clear();

            HttpResponseMessage response;
            try
            {
                response = await client.PostAsync(apiUrl, content, token);
            }
            catch (OperationCanceledException ex)
            {
                if (ex.Message.StartsWith("The request"))
                    return $"[ERROR] Translation Failed: The request was canceled due to timeout (> 8 seconds), " +
                           $"please use a faster API or check network connection.";
                throw;
            }
            catch (Exception ex)
            {
                return $"[ERROR] Translation Failed: {ex.Message}";
            }

            if (response.IsSuccessStatusCode)
            {
                string responseString = await response.Content.ReadAsStringAsync();
                var responseObj = JsonSerializer.Deserialize<OllamaConfig.Response>(responseString);
                var output = responseObj.message.content;
                return RegexPatterns.ModelThinking().Replace(output, "");
            }
            else
                return $"[ERROR] Translation Failed: HTTP Error - {response.StatusCode}";
        }

        public static async Task<string> LMStudio(string text, CancellationToken token = default)
        {
            var config = Translator.Setting["LMStudio"] as LMStudioConfig;
            string language = LMStudioConfig.SupportedLanguages.TryGetValue(
                Translator.Setting.TargetLanguage, out var langValue) ? langValue : Translator.Setting.TargetLanguage;
            string apiUrl = TextUtil.NormalizeUrl(config.ApiUrl) + "/chat";

            string systemPrompt = string.Format(Prompt, language);

            // Build input with optional context
            string input = $"🔤 {text} 🔤";
            if (Translator.Setting.ContextAware)
            {
                var contextLines = new List<string>();
                foreach (var entry in Translator.Caption.AwareContexts)
                {
                    string translatedText = entry.TranslatedText;
                    if (translatedText.Contains("[ERROR]") || translatedText.Contains("[WARNING]"))
                        continue;
                    translatedText = RegexPatterns.NoticePrefix().Replace(translatedText, "");
                    contextLines.Add($"🔤 {entry.SourceText} 🔤 → {translatedText}");
                }
                if (contextLines.Count > 0)
                    input = string.Join("\n", contextLines) + "\n" + input;
            }

            var requestData = new
            {
                model = config.ModelName,
                system_prompt = systemPrompt,
                input = input,
                temperature = config.Temperature
            };

            string jsonContent = JsonSerializer.Serialize(requestData);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
            client.DefaultRequestHeaders.Clear();

            HttpResponseMessage response;
            try
            {
                response = await client.PostAsync(apiUrl, content, token);
            }
            catch (OperationCanceledException ex)
            {
                if (ex.Message.StartsWith("The request"))
                    return $"[ERROR] Translation Failed: The request was canceled due to timeout (> 8 seconds), " +
                           $"please use a faster API or check network connection.";
                throw;
            }
            catch (Exception ex)
            {
                return $"[ERROR] Translation Failed: {ex.Message}";
            }

            if (response.IsSuccessStatusCode)
            {
                string responseString = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(responseString);
                var root = doc.RootElement;

                // LMStudio native /api/v1/chat response:
                // { "output": [ { "type": "message", "content": "..." }, ... ] }
                if (root.TryGetProperty("output", out var outputArray) &&
                    outputArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in outputArray.EnumerateArray())
                    {
                        if (item.TryGetProperty("type", out var typeProp) &&
                            typeProp.GetString() == "message" &&
                            item.TryGetProperty("content", out var contentProp))
                        {
                            return RegexPatterns.ModelThinking().Replace(contentProp.GetString() ?? "", "");
                        }
                    }
                }

                return "[ERROR] Translation Failed: Unexpected response format";
            }
            else
            {
                string body = await response.Content.ReadAsStringAsync();
                return $"[ERROR] Translation Failed: HTTP Error - {response.StatusCode}: {body}";
            }
        }

        public static async Task<string> OpenRouter(string text, CancellationToken token = default)
        {
            var config = Translator.Setting["OpenRouter"] as OpenRouterConfig;
            string language = OpenRouterConfig.SupportedLanguages.TryGetValue(
                Translator.Setting.TargetLanguage, out var langValue) ? langValue : Translator.Setting.TargetLanguage;
            string apiUrl = "https://openrouter.ai/api/v1/chat/completions";

            var messages = new List<BaseLLMConfig.Message>
            {
                new BaseLLMConfig.Message { role = "system", content = string.Format(Prompt, language) },
                new BaseLLMConfig.Message { role = "user", content = $"🔤 {text} 🔤" }
            };

            if (Translator.Setting.ContextAware)
            {
                foreach (var entry in Translator.Caption.AwareContexts)
                {
                    string translatedText = entry.TranslatedText;
                    if (translatedText.Contains("[ERROR]") || translatedText.Contains("[WARNING]"))
                        continue;
                    translatedText = RegexPatterns.NoticePrefix().Replace(translatedText, "");

                    messages.InsertRange(1, [
                        new BaseLLMConfig.Message { role = "user", content = $"🔤 {entry.SourceText} 🔤" },
                        new BaseLLMConfig.Message { role = "assistant", content = $"{translatedText}" }
                    ]);
                }
            }

            var requestData = LLMRequestDataFactory.Create("OpenRouter", config.ModelName, messages, config.Temperature);

            string jsonContent = JsonSerializer.Serialize(requestData, requestData.GetType());
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
            client.DefaultRequestHeaders.Clear();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {config?.ApiKey}");

            HttpResponseMessage response;
            try
            {
                response = await client.PostAsync(apiUrl, content, token);
            }
            catch (OperationCanceledException ex)
            {
                if (ex.Message.StartsWith("The request"))
                    return $"[ERROR] Translation Failed: The request was canceled due to timeout (> 8 seconds), " +
                           $"please use a faster API or check network connection.";
                throw;
            }
            catch (Exception ex)
            {
                return $"[ERROR] Translation Failed: {ex.Message}";
            }

            if (response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync();
                var jsonResponse = JsonSerializer.Deserialize<JsonElement>(responseContent);
                var output = jsonResponse.GetProperty("choices")[0]
                                         .GetProperty("message")
                                         .GetProperty("content")
                                         .GetString() ?? string.Empty;
                return RegexPatterns.ModelThinking().Replace(output, "");
            }
            else
                return $"[ERROR] Translation Failed: HTTP Error - {response.StatusCode}";
        }

        public static async Task<string> Google(string text, CancellationToken token = default)
        {
            var language = Translator.Setting?.TargetLanguage;

            string encodedText = Uri.EscapeDataString(text);
            var url = $"https://clients5.google.com/translate_a/t?" +
                      $"client=dict-chrome-ex&sl=auto&" +
                      $"tl={language}&" +
                      $"q={encodedText}";

            HttpResponseMessage response;
            try
            {
                response = await client.GetAsync(url, token);
            }
            catch (OperationCanceledException ex)
            {
                if (ex.Message.StartsWith("The request"))
                    return $"[ERROR] Translation Failed: The request was canceled due to timeout (> 8 seconds), " +
                           $"please use a faster API or check network connection.";
                throw;
            }
            catch (Exception ex)
            {
                return $"[ERROR] Translation Failed: {ex.Message}";
            }

            if (response.IsSuccessStatusCode)
            {
                string responseString = await response.Content.ReadAsStringAsync();

                var responseObj = JsonSerializer.Deserialize<List<List<string>>>(responseString);

                string translatedText = responseObj[0][0];
                return translatedText;
            }
            else
                return $"[ERROR] Translation Failed: HTTP Error - {response.StatusCode}";
        }

        public static async Task<string> Google2(string text, CancellationToken token = default)
        {
            string apiKey = "AIzaSyA6EEtrDCfBkHV8uU2lgGY-N383ZgAOo7Y";
            var language = Translator.Setting?.TargetLanguage;
            string strategy = "2";

            string encodedText = Uri.EscapeDataString(text);
            string url = $"https://dictionaryextension-pa.googleapis.com/v1/dictionaryExtensionData?" +
                         $"language={language}&" +
                         $"key={apiKey}&" +
                         $"term={encodedText}&" +
                         $"strategy={strategy}";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("x-referer", "chrome-extension://mgijmajocgfcbeboacabfgobmjgjcoja");

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, token);
            }
            catch (OperationCanceledException ex)
            {
                if (ex.Message.StartsWith("The request"))
                    return $"[ERROR] Translation Failed: The request was canceled due to timeout (> 8 seconds), " +
                           $"please use a faster API or check network connection.";
                throw;
            }
            catch (Exception ex)
            {
                return $"[ERROR] Translation Failed: {ex.Message}";
            }

            if (response.IsSuccessStatusCode)
            {
                string responseBody = await response.Content.ReadAsStringAsync();

                using var jsonDoc = JsonDocument.Parse(responseBody);
                var root = jsonDoc.RootElement;

                if (root.TryGetProperty("translateResponse", out JsonElement translateResponse))
                {
                    string translatedText = translateResponse.GetProperty("translateText").GetString();
                    return translatedText;
                }
                else
                    return "[ERROR] Translation Failed: Unexpected API response format";
            }
            else
                return $"[ERROR] Translation Failed: HTTP Error - {response.StatusCode}";
        }

        public static async Task<string> DeepL(string text, CancellationToken token = default)
        {
            var config = Translator.Setting["DeepL"] as DeepLConfig;
            string language = DeepLConfig.SupportedLanguages.TryGetValue(
                Translator.Setting.TargetLanguage, out var langValue) ? langValue : Translator.Setting.TargetLanguage;
            string apiUrl = TextUtil.NormalizeUrl(config.ApiUrl);

            var requestData = new
            {
                text = new[] { text },
                target_lang = language
            };

            string jsonContent = JsonSerializer.Serialize(requestData);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            client.DefaultRequestHeaders.Clear();
            client.DefaultRequestHeaders.Add("Authorization", $"DeepL-Auth-Key {config?.ApiKey}");

            HttpResponseMessage response;
            try
            {
                response = await client.PostAsync(apiUrl, content, token);
            }
            catch (OperationCanceledException ex)
            {
                if (ex.Message.StartsWith("The request"))
                    return $"[ERROR] Translation Failed: The request was canceled due to timeout (> 8 seconds), " +
                           $"please use a faster API or check network connection.";
                throw;
            }
            catch (Exception ex)
            {
                return $"[ERROR] Translation Failed: {ex.Message}";
            }

            if (response.IsSuccessStatusCode)
            {
                string responseString = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(responseString);

                if (doc.RootElement.TryGetProperty("translations", out var translations) &&
                    translations.ValueKind == JsonValueKind.Array && translations.GetArrayLength() > 0)
                {
                    return translations[0].GetProperty("text").GetString();
                }
                return "[ERROR] Translation Failed: No valid feedback";
            }
            else
                return $"[ERROR] Translation Failed: HTTP Error - {response.StatusCode}";
        }


        public static async Task<string> Youdao(string text, CancellationToken token = default)
        {
            var config = Translator.Setting["Youdao"] as YoudaoConfig;
            string language = YoudaoConfig.SupportedLanguages.TryGetValue(
                Translator.Setting.TargetLanguage, out var langValue) ? langValue : Translator.Setting.TargetLanguage;

            string salt = DateTime.Now.Millisecond.ToString();
            string sign = BitConverter.ToString(
                MD5.Create().ComputeHash(
                    Encoding.UTF8.GetBytes($"{config.AppKey}{text}{salt}{config.AppSecret}"))).Replace("-", "").ToLower();

            var parameters = new Dictionary<string, string>
            {
                ["q"] = text,
                ["from"] = "auto",
                ["to"] = language,
                ["appKey"] = config.AppKey,
                ["salt"] = salt,
                ["sign"] = sign
            };

            var content = new FormUrlEncodedContent(parameters);
            client.DefaultRequestHeaders.Clear();

            HttpResponseMessage response;
            try
            {
                response = await client.PostAsync(config.ApiUrl, content, token);
            }
            catch (OperationCanceledException ex)
            {
                if (ex.Message.StartsWith("The request"))
                    return $"[ERROR] Translation Failed: The request was canceled due to timeout (> 8 seconds), " +
                           $"please use a faster API or check network connection.";
                throw;
            }
            catch (Exception ex)
            {
                return $"[ERROR] Translation Failed: {ex.Message}";
            }

            if (response.IsSuccessStatusCode)
            {
                string responseString = await response.Content.ReadAsStringAsync();
                var responseObj = JsonSerializer.Deserialize<YoudaoConfig.TranslationResult>(responseString);

                if (responseObj.errorCode != "0")
                    return $"[ERROR] Translation Failed: Youdao Error - {responseObj.errorCode}";

                return responseObj.translation?.FirstOrDefault() ?? "[ERROR] Translation Failed: No content";
            }
            else
            {
                return $"[ERROR] Translation Failed: HTTP Error - {response.StatusCode}";
            }
        }

        public static async Task<string> MTranServer(string text, CancellationToken token = default)
        {
            var config = Translator.Setting["MTranServer"] as MTranServerConfig;
            string targetLanguage = MTranServerConfig.SupportedLanguages.TryGetValue(
                Translator.Setting.TargetLanguage, out var langValue) ? langValue : Translator.Setting.TargetLanguage;
            string sourceLanguage = config.SourceLanguage;
            string apiUrl = TextUtil.NormalizeUrl(config.ApiUrl);

            var requestData = new
            {
                text = text,
                to = targetLanguage,
                from = sourceLanguage
            };

            string jsonContent = JsonSerializer.Serialize(requestData);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            client.DefaultRequestHeaders.Clear();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {config?.ApiKey}");

            HttpResponseMessage response;
            try
            {
                response = await client.PostAsync(apiUrl, content, token);
            }
            catch (OperationCanceledException ex)
            {
                if (ex.Message.StartsWith("The request"))
                    return $"[ERROR] Translation Failed: The request was canceled due to timeout (> 8 seconds), " +
                           $"please use a faster API or check network connection.";
                throw;
            }
            catch (Exception ex)
            {
                return $"[ERROR] Translation Failed: {ex.Message}";
            }

            if (response.IsSuccessStatusCode)
            {
                string responseString = await response.Content.ReadAsStringAsync();
                var responseObj = JsonSerializer.Deserialize<MTranServerConfig.Response>(responseString);
                return responseObj.result;
            }
            else
                return $"[ERROR] Translation Failed: HTTP Error - {response.StatusCode}";
        }

        public static async Task<string> Baidu(string text, CancellationToken token = default)
        {
            var config = Translator.Setting["Baidu"] as BaiduConfig;
            string language = BaiduConfig.SupportedLanguages.TryGetValue(
                Translator.Setting.TargetLanguage, out var langValue) ? langValue : Translator.Setting.TargetLanguage;

            string salt = DateTime.Now.Millisecond.ToString();
            string sign = BitConverter.ToString(
                MD5.Create().ComputeHash(
                    Encoding.UTF8.GetBytes($"{config.AppId}{text}{salt}{config.AppSecret}"))).Replace("-", "").ToLower();

            var parameters = new Dictionary<string, string>
            {
                ["q"] = text,
                ["from"] = "auto",
                ["to"] = language,
                ["appid"] = config.AppId,
                ["salt"] = salt,
                ["sign"] = sign
            };

            var content = new FormUrlEncodedContent(parameters);
            client.DefaultRequestHeaders.Clear();

            HttpResponseMessage response;
            try
            {
                response = await client.PostAsync(config.ApiUrl, content, token);
            }
            catch (OperationCanceledException ex)
            {
                if (ex.Message.StartsWith("The request"))
                    return $"[ERROR] Translation Failed: The request was canceled due to timeout (> 8 seconds), " +
                           $"please use a faster API or check network connection.";
                throw;
            }
            catch (Exception ex)
            {
                return $"[ERROR] Translation Failed: {ex.Message}";
            }

            if (response.IsSuccessStatusCode)
            {
                string responseString = await response.Content.ReadAsStringAsync();
                var responseObj = JsonSerializer.Deserialize<BaiduConfig.TranslationResult>(responseString);

                if (responseObj.error_code is not null && responseObj.error_code != "0")
                    return $"[ERROR] Translation Failed: Baidu Error - {responseObj.error_code}";

                return responseObj.trans_result?.FirstOrDefault()?.dst ?? "[ERROR] Translation Failed: No content";
            }
            else
            {
                return $"[ERROR] Translation Failed: HTTP Error - {response.StatusCode}";
            }
        }

        public static async Task<string> LibreTranslate(string text, CancellationToken token = default)
        {
            var config = Translator.Setting["LibreTranslate"] as LibreTranslateConfig;
            string targetLanguage = LibreTranslateConfig.SupportedLanguages.TryGetValue(
                Translator.Setting.TargetLanguage, out var langValue) ? langValue : Translator.Setting.TargetLanguage;
            string apiUrl = TextUtil.NormalizeUrl(config.ApiUrl);

            var requestData = new
            {
                q = text,
                target = targetLanguage,
                source = "auto",
                format = "text",
                api_key = config?.ApiKey
            };

            string jsonContent = JsonSerializer.Serialize(requestData);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            client.DefaultRequestHeaders.Clear();

            HttpResponseMessage response;
            try
            {
                response = await client.PostAsync(apiUrl, content, token);
            }
            catch (OperationCanceledException ex)
            {
                if (ex.Message.StartsWith("The request"))
                    return $"[ERROR] Translation Failed: The request was canceled due to timeout (> 8 seconds), " +
                           $"please use a faster API or check network connection.";
                throw;
            }
            catch (Exception ex)
            {
                return $"[ERROR] Translation Failed: {ex.Message}";
            }

            if (response.IsSuccessStatusCode)
            {
                string responseString = await response.Content.ReadAsStringAsync();
                var responseObj = JsonSerializer.Deserialize<LibreTranslateConfig.Response>(responseString);
                return responseObj.translatedText;
            }
            else
                return $"[ERROR] Translation Failed: HTTP Error - {response.StatusCode}";
        }
    }

    public class ConfigDictConverter : JsonConverter<Dictionary<string, List<TranslateAPIConfig>>>
    {
        public override Dictionary<string, List<TranslateAPIConfig>> Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
                throw new JsonException("Expected a StartObject token.");
            var configs = new Dictionary<string, List<TranslateAPIConfig>>();

            reader.Read();
            while (reader.TokenType == JsonTokenType.PropertyName)
            {
                string key = reader.GetString();
                reader.Read();

                var configType = Type.GetType($"LiveCaptionsTranslator.models.{key}Config");
                TranslateAPIConfig config;

                if (reader.TokenType == JsonTokenType.StartArray)
                {
                    var list = new List<TranslateAPIConfig>();
                    reader.Read();

                    while (reader.TokenType != JsonTokenType.EndArray)
                    {
                        if (configType != null && typeof(TranslateAPIConfig).IsAssignableFrom(configType))
                            config = (TranslateAPIConfig)JsonSerializer.Deserialize(ref reader, configType, options);
                        else
                            config = (TranslateAPIConfig)JsonSerializer.Deserialize(ref reader, typeof(TranslateAPIConfig), options);

                        list.Add(config);
                        reader.Read();
                    }
                    configs[key] = list;
                }
                else
                    throw new JsonException("Expected a StartObject token or a StartArray token.");

                reader.Read();
            }

            if (reader.TokenType != JsonTokenType.EndObject)
                throw new JsonException("Expected an EndObject token.");
            return configs;
        }

        public override void Write(
            Utf8JsonWriter writer, Dictionary<string, List<TranslateAPIConfig>> value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            foreach (var kvp in value)
            {
                writer.WritePropertyName(kvp.Key);
                var configType = Type.GetType($"LiveCaptionsTranslator.models.{kvp.Key}Config");

                if (kvp.Value is IEnumerable<TranslateAPIConfig> configList)
                {
                    writer.WriteStartArray();
                    foreach (var config in configList)
                    {
                        if (configType != null && typeof(TranslateAPIConfig).IsAssignableFrom(configType))
                            JsonSerializer.Serialize(writer, config, configType, options);
                        else
                            JsonSerializer.Serialize(writer, config, typeof(TranslateAPIConfig), options);
                    }
                    writer.WriteEndArray();
                }
                else
                    throw new JsonException($"Unsupported config type: {kvp.Value.GetType()}");
            }
            writer.WriteEndObject();
        }
    }
}
