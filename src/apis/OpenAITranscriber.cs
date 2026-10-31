using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.apis
{
    /*
     * Speech-to-text for PC audio with an OpenAI Realtime transcription session.
     * See https://developers.openai.com/api/docs/guides/realtime-transcription
     *
     * PC audio (24 kHz mono PCM16) is streamed over a WebSocket; server VAD splits it into turns
     * and each committed turn is transcribed by `gpt-transcribe` (billed by audio duration).
     * To save credit, long digital silence (nothing playing) is not sent at all.
     */
    public static class OpenAITranscriber
    {
        public const string REALTIME_URL = "wss://api.openai.com/v1/realtime?intent=transcription";

        private const int CHUNK_MS = 100;
        private const int SAMPLES_PER_CHUNK = LoopbackAudioCapture.SAMPLE_RATE * CHUNK_MS / 1000;
        // Below this peak amplitude a chunk counts as silence (about -56 dBFS).
        private const short SILENCE_PEAK = 50;
        // Keep sending silence this long after sound stops so that server VAD can end the turn.
        private static readonly TimeSpan SILENCE_TAIL = TimeSpan.FromSeconds(2);
        // Above this RMS a chunk counts as voice for the live model's own line breaks (about -40 dBFS).
        private const double VOICE_RMS = 330;
        // Sentence-final characters that let the live model end a line without waiting for a pause.
        private static readonly char[] LINE_END_CHARS = ['。', '？', '！', '?', '!', '…', '」', '』'];
        // Give up waiting for a turn's transcript after this long and move on to the next one.
        private static readonly TimeSpan TRANSCRIPT_TIMEOUT = TimeSpan.FromSeconds(15);

        // Common phantom transcripts of music or silence.
        private static readonly string[] HALLUCINATIONS =
        [
            "ご視聴ありがとうございました", "ご視聴ありがとうございました。",
            "チャンネル登録よろしくお願いします", "チャンネル登録よろしくお願いします。",
        ];

        private static readonly object stateLock = new();
        private static CancellationTokenSource? runCTS = null;

        public static event Action? StatusChanged;

        public static bool IsRunning => runCTS != null;
        public static string Status { get; private set; } = "Stopped";

        public static void Start()
        {
            lock (stateLock)
            {
                if (runCTS != null)
                    return;
                runCTS = new CancellationTokenSource();
                var token = runCTS.Token;
                Task.Run(() => RunAsync(token));
            }
            StatusChanged?.Invoke();
        }

        /// <summary>
        /// Restarts listening so that changed settings (model, language, keywords...) take effect.
        /// </summary>
        public static void Restart()
        {
            if (!IsRunning)
                return;
            Stop();
            Start();
        }

        public static void Stop()
        {
            lock (stateLock)
            {
                if (runCTS == null)
                    return;
                runCTS.Cancel();
                runCTS = null;
            }
            SetStatus("Stopped");
        }


        private static void SetStatus(string status)
        {
            Status = status;
            StatusChanged?.Invoke();
        }

        private static async Task RunAsync(CancellationToken token)
        {
            // Per run, so that a quick restart never shares audio with the previous run.
            LoopbackAudioCapture? capture = null;
            var audioQueue = new ConcurrentQueue<short[]>();
            try
            {
                capture = StartCapture(audioQueue);
            }
            catch (Exception ex)
            {
                SetStatus($"[ERROR] Cannot capture PC audio: {ex.Message}");
                Translator.ShowNotice($"[ERROR] Cannot capture PC audio: {ex.Message}");
                lock (stateLock)
                {
                    if (runCTS?.Token == token)
                        runCTS = null;
                }
                return;
            }

            int failures = 0;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var (apiKey, keyError) = TranslateAPI.ResolveOpenAIApiKey();
                    if (apiKey == null)
                    {
                        SetStatus($"[ERROR] {keyError}");
                        Translator.ShowNotice($"[ERROR] {keyError}");
                        await Task.Delay(TimeSpan.FromSeconds(5), token);
                        continue;
                    }

                    if (ApiCreditGuard.IsEnabled && ApiCreditGuard.IsApiCreditExhausted)
                    {
                        SwitchToLiveCaptionsForCredit();
                        break;
                    }

                    var result = await RunSessionAsync(apiKey, audioQueue, token);
                    if (token.IsCancellationRequested)
                        break;

                    if (result.IsCreditExhausted)
                    {
                        ApiCreditGuard.MarkApiCreditExhausted();
                        if (ApiCreditGuard.IsEnabled)
                        {
                            SwitchToLiveCaptionsForCredit();
                            break;
                        }
                    }

                    if (result.IsFatal)
                    {
                        SetStatus($"[ERROR] {result.Message}");
                        Translator.ShowNotice($"[ERROR] Speech recognition stopped: {result.Message}");
                        break;
                    }

                    failures = result.WasConnected ? 0 : failures + 1;
                    var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(failures, 5))));
                    SetStatus($"Reconnecting in {delay.TotalSeconds:0}s... ({result.Message})");
                    await Task.Delay(delay, token);
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                StopCapture(capture);
                lock (stateLock)
                {
                    if (runCTS?.Token == token)
                        runCTS = null;
                }
                StatusChanged?.Invoke();
            }
        }

        private static LoopbackAudioCapture StartCapture(ConcurrentQueue<short[]> audioQueue)
        {
            var capture = new LoopbackAudioCapture();
            capture.SamplesAvailable += samples => audioQueue.Enqueue(samples);
            capture.Start();
            return capture;
        }

        private static void SwitchToLiveCaptionsForCredit()
        {
            SetStatus("OpenAI API credit is used up: switched to Windows LiveCaptions.");
            Task.Run(Translator.SwitchToLiveCaptionsForCredit);
        }

        private static void StopCapture(LoopbackAudioCapture? current)
        {
            if (current == null)
                return;
            try
            {
                current.Stop();
                current.Dispose();
            }
            catch (Exception) { }
        }

        private record SessionResult(bool IsFatal, bool WasConnected, string Message, bool IsCreditExhausted = false);

        private class SessionState
        {
            public string Model = "gpt-transcribe";
            public bool IsLive = false;
            public DateTime LastSoundTime = DateTime.MinValue;
            // Live model: the app decides where lines end (no server VAD).
            public DateTime LastVoiceTime = DateTime.MinValue;
            public DateTime LastDeltaTime = DateTime.MinValue;
            public bool HasUncommittedSpeech = false;
            public DateTime CommitSentTime = DateTime.MinValue;
            public readonly StringBuilder LiveText = new();
            // Audio sent since the last commit, as a usage estimate when an event carries no duration.
            public double SentSecondsSinceCommit = 0;
            public readonly Dictionary<string, double> ItemAudioSeconds = new();
            public bool IsSpeaking = false;
            public DateTime TurnStartTime = DateTime.MinValue;
            // Committed items in order, to emit transcripts in speech order.
            public readonly List<string> CommittedItems = new();
            public readonly Dictionary<string, string> Completed = new();
            public readonly Dictionary<string, StringBuilder> Partials = new();
            public readonly Dictionary<string, DateTime> CommitTimes = new();
            public readonly Dictionary<string, double> SpeechStartMs = new();
            public readonly Dictionary<string, double> SpeechDurations = new();
            public string? FatalError = null;
            public bool IsCreditExhausted = false;
        }

        private static async Task<SessionResult> RunSessionAsync(string apiKey, ConcurrentQueue<short[]> audioQueue,
            CancellationToken token)
        {
            using var ws = new ClientWebSocket();
            ws.Options.SetRequestHeader("Authorization", $"Bearer {apiKey}");
            ws.Options.CollectHttpResponseDetails = true;

            SetStatus("Connecting...");
            try
            {
                using var connectCTS = CancellationTokenSource.CreateLinkedTokenSource(token);
                connectCTS.CancelAfter(TimeSpan.FromSeconds(15));
                await ws.ConnectAsync(new Uri(REALTIME_URL), connectCTS.Token);
            }
            catch (Exception ex) when (ex is WebSocketException || ex is OperationCanceledException && !token.IsCancellationRequested)
            {
                if (ws.HttpStatusCode == HttpStatusCode.Unauthorized)
                    return new SessionResult(true, false, "The OpenAI API key was rejected (401).");
                if (ws.HttpStatusCode == HttpStatusCode.Forbidden)
                    return new SessionResult(true, false, "Access denied (403). Check the API key's project permissions.");
                string code = ws.HttpStatusCode != 0 ? $"HTTP {(int)ws.HttpStatusCode}" : ex.Message;
                return new SessionResult(false, false, $"Connection failed: {code}");
            }

            var settings = Translator.Setting.Transcription;
            var state = new SessionState
            {
                Model = string.IsNullOrWhiteSpace(settings.Model) ? TranscriptionSetting.MODEL_TRANSCRIBE : settings.Model,
                IsLive = settings.IsLiveModel
            };
            await SendJsonAsync(ws, CreateSessionUpdate(settings, state.Model), token);
            SetStatus("Listening");

            using var sessionCTS = CancellationTokenSource.CreateLinkedTokenSource(token);
            audioQueue.Clear();
            var sendTask = SendLoopAsync(ws, state, audioQueue, sessionCTS.Token);
            string closeReason;
            try
            {
                closeReason = await ReceiveLoopAsync(ws, state, sessionCTS.Token);
            }
            catch (Exception ex) when (ex is WebSocketException || ex is IOException)
            {
                closeReason = $"Connection lost: {ex.Message}";
            }
            finally
            {
                sessionCTS.Cancel();
                try { await sendTask; } catch (Exception) { }
            }

            if (ws.State == WebSocketState.Open)
            {
                try
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                }
                catch (Exception) { }
            }

            if (state.IsCreditExhausted)
                return new SessionResult(true, true, state.FatalError ?? "OpenAI API credit is used up.", true);
            if (state.FatalError != null)
                return new SessionResult(true, true, state.FatalError);
            return new SessionResult(false, true, closeReason);
        }

        private static object CreateSessionUpdate(TranscriptionSetting settings, string model)
        {
            var transcription = new Dictionary<string, object> { { "model", model } };
            if (!string.IsNullOrWhiteSpace(settings.Language))
                transcription["languages"] = new[] { settings.Language.Trim().ToLowerInvariant() };
            var keywords = settings.GetKeywordList();
            if (keywords.Count > 0)
                transcription["keywords"] = keywords;
            if (!string.IsNullOrWhiteSpace(settings.Prompt))
                transcription["prompt"] = settings.Prompt.Trim();
            if (settings.IsLiveModel && !string.IsNullOrWhiteSpace(settings.Delay))
                transcription["delay"] = settings.Delay;

            // Live models don't support server VAD: the app commits each line itself.
            object? turnDetection = settings.IsLiveModel
                ? null
                : new
                {
                    type = "server_vad",
                    threshold = Math.Clamp(settings.VadThreshold, 0.0, 1.0),
                    prefix_padding_ms = 300,
                    silence_duration_ms = Math.Clamp(settings.SilenceDurationMs, 200, 3000),
                };

            return new
            {
                type = "session.update",
                session = new
                {
                    type = "transcription",
                    audio = new
                    {
                        input = new Dictionary<string, object?>
                        {
                            { "format", new { type = "audio/pcm", rate = LoopbackAudioCapture.SAMPLE_RATE } },
                            { "transcription", transcription },
                            { "turn_detection", turnDetection },
                        }
                    }
                }
            };
        }

        private static async Task SendLoopAsync(ClientWebSocket ws, SessionState state,
            ConcurrentQueue<short[]> audioQueue, CancellationToken token)
        {
            var pending = new List<short>(SAMPLES_PER_CHUNK * 4);
            var lastTick = DateTime.UtcNow;

            while (!token.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                await Task.Delay(CHUNK_MS, token);
                var now = DateTime.UtcNow;

                bool hasAudio = false;
                while (audioQueue.TryDequeue(out var samples))
                {
                    pending.AddRange(samples);
                    hasAudio = true;
                }
                // Loopback delivers nothing while nothing plays; treat that gap as silence.
                if (!hasAudio)
                {
                    int gap = (int)Math.Min((now - lastTick).TotalMilliseconds, 500) * LoopbackAudioCapture.SAMPLE_RATE / 1000;
                    pending.AddRange(new short[gap]);
                }
                lastTick = now;

                if (pending.Count == 0)
                    continue;

                bool isSilent = true;
                double sumSquares = 0;
                foreach (var sample in pending)
                {
                    if (sample > SILENCE_PEAK || sample < -SILENCE_PEAK)
                        isSilent = false;
                    sumSquares += (double)sample * sample;
                }
                bool isVoice = Math.Sqrt(sumSquares / pending.Count) > VOICE_RMS;
                if (!isSilent)
                    state.LastSoundTime = now;

                // Save credit: do not stream long stretches where nothing is playing.
                if (!isSilent || now - state.LastSoundTime < SILENCE_TAIL)
                {
                    var bytes = new byte[pending.Count * 2];
                    Buffer.BlockCopy(pending.ToArray(), 0, bytes, 0, bytes.Length);
                    await SendJsonAsync(ws, new { type = "input_audio_buffer.append", audio = Convert.ToBase64String(bytes) }, token);
                    lock (state)
                        state.SentSecondsSinceCommit += (double)pending.Count / LoopbackAudioCapture.SAMPLE_RATE;
                }
                pending.Clear();

                if (ApiCreditGuard.IsEnabled)
                {
                    // Get LiveCaptions ready while there is still credit, then hand over when it runs out.
                    if (ApiCreditGuard.IsApiCreditLow)
                        Translator.PrewarmLiveCaptions();
                    if (ApiCreditGuard.IsApiCreditExhausted)
                    {
                        state.IsCreditExhausted = true;
                        ws.Abort();
                        return;
                    }
                }

                if (state.IsLive)
                    await CommitLiveLineIfEndedAsync(ws, state, isVoice, now, token);
                else
                {
                    // Someone talking without a pause: cut the turn so the subtitle doesn't lag too far behind.
                    int maxTurn = Translator.Setting.Transcription.MaxTurnSeconds;
                    if (state.IsSpeaking && maxTurn > 0 && now - state.TurnStartTime > TimeSpan.FromSeconds(maxTurn))
                    {
                        state.TurnStartTime = now;
                        await SendJsonAsync(ws, new { type = "input_audio_buffer.commit" }, token);
                    }
                }

                lock (state)
                    FlushTranscripts(state, now);
            }
        }

        /// <summary>
        /// Live model: end the current line on a pause, a sentence-final character, or when it gets too long.
        /// </summary>
        private static async Task CommitLiveLineIfEndedAsync(ClientWebSocket ws, SessionState state, bool isVoice,
            DateTime now, CancellationToken token)
        {
            var settings = Translator.Setting.Transcription;
            bool shouldCommit;
            lock (state)
            {
                if (isVoice)
                {
                    state.LastVoiceTime = now;
                    if (!state.HasUncommittedSpeech)
                    {
                        state.HasUncommittedSpeech = true;
                        state.TurnStartTime = now;
                    }
                }
                // Wait for the previous commit to be acknowledged before deciding again.
                if (!state.HasUncommittedSpeech || now - state.CommitSentTime < TimeSpan.FromSeconds(1))
                    return;

                string text = state.LiveText.ToString().TrimEnd();
                bool isPause = now - state.LastVoiceTime >= TimeSpan.FromMilliseconds(Math.Clamp(settings.SilenceDurationMs, 200, 3000));
                bool isSentenceEnd = text.Length > 0 && LINE_END_CHARS.Contains(text[^1]) &&
                                     now - state.LastDeltaTime >= TimeSpan.FromMilliseconds(300);
                bool isTooLong = settings.MaxTurnSeconds > 0 &&
                                 now - state.TurnStartTime >= TimeSpan.FromSeconds(settings.MaxTurnSeconds);

                shouldCommit = isPause || isSentenceEnd || isTooLong;
                if (shouldCommit)
                {
                    state.HasUncommittedSpeech = isVoice && !isPause;
                    state.TurnStartTime = now;
                    state.CommitSentTime = now;
                    state.LiveText.Clear();
                }
            }
            if (shouldCommit)
                await SendJsonAsync(ws, new { type = "input_audio_buffer.commit" }, token);
        }

        private static async Task<string> ReceiveLoopAsync(ClientWebSocket ws, SessionState state, CancellationToken token)
        {
            var buffer = new byte[64 * 1024];
            using var message = new MemoryStream();

            while (!token.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(buffer, token);
                if (result.MessageType == WebSocketMessageType.Close)
                    return $"Server closed the session ({ws.CloseStatusDescription ?? ws.CloseStatus?.ToString()})";

                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                    continue;

                string json = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                message.SetLength(0);
                try
                {
                    lock (state)
                        HandleEvent(json, state);
                }
                catch (JsonException) { }

                if (state.FatalError != null)
                    return state.FatalError;
            }
            return "Session ended";
        }

        private static void HandleEvent(string json, SessionState state)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? type = root.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;
            string? itemId = root.TryGetProperty("item_id", out var itemProp) ? itemProp.GetString() : null;

            switch (type)
            {
                case "input_audio_buffer.speech_started":
                    state.IsSpeaking = true;
                    state.TurnStartTime = DateTime.UtcNow;
                    if (itemId != null && root.TryGetProperty("audio_start_ms", out var startMs))
                        state.SpeechStartMs[itemId] = startMs.GetDouble();
                    break;

                case "input_audio_buffer.speech_stopped":
                    state.IsSpeaking = false;
                    if (itemId != null && root.TryGetProperty("audio_end_ms", out var endMs) &&
                        state.SpeechStartMs.TryGetValue(itemId, out var start))
                        state.SpeechDurations[itemId] = Math.Max(0, endMs.GetDouble() - start) / 1000;
                    break;

                case "input_audio_buffer.committed":
                    if (itemId != null && !state.CommittedItems.Contains(itemId))
                    {
                        state.CommittedItems.Add(itemId);
                        state.CommitTimes[itemId] = DateTime.UtcNow;
                        state.ItemAudioSeconds[itemId] = state.SentSecondsSinceCommit;
                        state.SentSecondsSinceCommit = 0;
                    }
                    state.CommitSentTime = DateTime.MinValue;
                    break;

                case "conversation.item.input_audio_transcription.delta":
                    if (itemId != null && root.TryGetProperty("delta", out var delta))
                    {
                        if (!state.Partials.TryGetValue(itemId, out var partial))
                            state.Partials[itemId] = partial = new StringBuilder();
                        partial.Append(delta.GetString());
                        if (!state.CommittedItems.Contains(itemId))
                        {
                            // Text of the line still being spoken (live model).
                            state.LiveText.Append(delta.GetString());
                            state.LastDeltaTime = DateTime.UtcNow;
                        }
                        // Only show the partial text of the oldest pending turn to keep the order.
                        if (state.CommittedItems.Count == 0 || state.CommittedItems[0] == itemId)
                            Translator.ShowPartialTranscript(partial.ToString());
                    }
                    break;

                case "conversation.item.input_audio_transcription.completed":
                    if (itemId != null)
                    {
                        string transcript = root.TryGetProperty("transcript", out var t) ? t.GetString() ?? "" : "";
                        state.Completed[itemId] = transcript;
                        TrackUsage(root, itemId, state);
                        if (!state.CommittedItems.Contains(itemId))
                            state.CommittedItems.Add(itemId);
                        FlushTranscripts(state, DateTime.UtcNow);
                    }
                    break;

                case "conversation.item.input_audio_transcription.failed":
                    if (itemId != null)
                    {
                        state.Completed[itemId] = "";
                        FlushTranscripts(state, DateTime.UtcNow);
                    }
                    break;

                case "error":
                    HandleError(root, state);
                    break;
            }
        }

        private static void TrackUsage(JsonElement root, string itemId, SessionState state)
        {
            double seconds = 0;
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                if (usage.TryGetProperty("seconds", out var s) && s.ValueKind == JsonValueKind.Number)
                    seconds = s.GetDouble();
                else if (usage.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number)
                    seconds = d.GetDouble();
            }
            // Fall back to the VAD timestamps, then to the audio sent for this line.
            if (seconds <= 0)
                state.SpeechDurations.TryGetValue(itemId, out seconds);
            if (seconds <= 0)
                state.ItemAudioSeconds.TryGetValue(itemId, out seconds);
            UsageTracker.AddTranscription(state.Model, seconds);
        }

        private static void HandleError(JsonElement root, SessionState state)
        {
            var error = root.TryGetProperty("error", out var e) ? e : root;
            string? code = error.TryGetProperty("code", out var c) ? c.GetString() : null;
            string message = error.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";

            switch (code)
            {
                // Harmless: a forced commit raced with server VAD.
                case "input_audio_buffer_commit_empty":
                    return;
                case "invalid_api_key":
                    state.FatalError = "The OpenAI API key was rejected.";
                    return;
                case "insufficient_quota":
                    state.IsCreditExhausted = true;
                    state.FatalError = "OpenAI API credit is used up. Check your balance at platform.openai.com.";
                    return;
            }

            // A rejected session configuration will not fix itself by reconnecting.
            if (error.TryGetProperty("param", out var param) && param.GetString()?.StartsWith("session") == true)
            {
                state.FatalError = $"Invalid speech recognition setting: {message}";
                return;
            }
            Translator.ShowNotice($"[WARNING] Speech recognition: {message}");
        }

        // Emit finished transcripts in the order their turns were committed.
        private static void FlushTranscripts(SessionState state, DateTime now)
        {
            while (state.CommittedItems.Count > 0)
            {
                string head = state.CommittedItems[0];
                if (!state.Completed.TryGetValue(head, out var transcript))
                {
                    if (state.CommitTimes.TryGetValue(head, out var committedAt) && now - committedAt > TRANSCRIPT_TIMEOUT)
                        transcript = state.Partials.TryGetValue(head, out var partial) ? partial.ToString() : "";
                    else
                        return;
                }

                state.CommittedItems.RemoveAt(0);
                state.Completed.Remove(head);
                state.Partials.Remove(head);
                state.CommitTimes.Remove(head);
                state.SpeechStartMs.Remove(head);
                state.SpeechDurations.Remove(head);
                state.ItemAudioSeconds.Remove(head);

                transcript = transcript.Trim();
                if (transcript.Length > 0 && !HALLUCINATIONS.Contains(transcript))
                    Translator.SubmitTranscript(transcript);
            }
        }

        private static readonly SemaphoreSlim sendLock = new(1, 1);

        private static async Task SendJsonAsync(ClientWebSocket ws, object payload, CancellationToken token)
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
            await sendLock.WaitAsync(token);
            try
            {
                await ws.SendAsync(bytes, WebSocketMessageType.Text, true, token);
            }
            finally
            {
                sendLock.Release();
            }
        }
    }
}
