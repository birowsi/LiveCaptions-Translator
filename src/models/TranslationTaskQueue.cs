namespace LiveCaptionsTranslator.models
{
    public class TranslationTaskQueue
    {
        private readonly object _lock = new object();
        private readonly List<TranslationTask> tasks;
        // Serializes output and logging so that finished lines are logged in order.
        private readonly SemaphoreSlim outputLock = new(1, 1);

        private (string translatedText, bool isChoke) output;
        public (string translatedText, bool isChoke) Output => output;

        public TranslationTaskQueue()
        {
            tasks = new List<TranslationTask>();
            output = (string.Empty, false);
        }

        public void Enqueue(Func<CancellationToken, Task<(string, bool)>> worker, string originalText)
        {
            var newTranslationTask = new TranslationTask(worker, originalText, new CancellationTokenSource());
            lock (_lock)
            {
                tasks.Add(newTranslationTask);
            }
            // Run `OnTaskCompleted` in a new thread.
            newTranslationTask.Task.ContinueWith(
                task => OnTaskCompleted(newTranslationTask),
                TaskContinuationOptions.OnlyOnRanToCompletion
            );
        }

        private async Task OnTaskCompleted(TranslationTask translationTask)
        {
            // LiveCaptions keeps rewriting the same sentence, so an older task is an outdated version of
            // a newer one and can be dropped. OpenAI speech recognition emits each sentence once and final,
            // so every task must be kept and shown in order.
            if (Translator.Setting.Transcription.IsOpenAI)
            {
                await OnFinalSentenceCompleted();
                return;
            }

            lock (_lock)
            {
                var index = tasks.IndexOf(translationTask);
                for (int i = 0; i < index; i++)
                    tasks[i].CTS.Cancel();
                for (int i = index; i >= 0; i--)
                    tasks.RemoveAt(i);
            }

            output = translationTask.Task.Result;
            var translatedText = output.Item1;

            // Log after translation.
            bool isOverwrite = await Translator.IsOverwrite(translationTask.OriginalText);
            if (!isOverwrite)
                await Translator.AddContexts();
            await Translator.Log(translationTask.OriginalText, translatedText, isOverwrite);
        }

        private async Task OnFinalSentenceCompleted()
        {
            await outputLock.WaitAsync();
            try
            {
                while (true)
                {
                    TranslationTask head;
                    lock (_lock)
                    {
                        // Wait for earlier sentences; tasks canceled by a source switch are skipped.
                        tasks.RemoveAll(task => task.Task.IsCanceled || task.Task.IsFaulted);
                        if (tasks.Count == 0 || !tasks[0].Task.IsCompletedSuccessfully)
                            return;
                        head = tasks[0];
                        tasks.RemoveAt(0);
                    }

                    output = head.Task.Result;
                    // Same order as below: the previous line becomes context, then this line is logged.
                    await Translator.AddContexts();
                    await Translator.Log(head.OriginalText, output.translatedText);
                }
            }
            finally
            {
                outputLock.Release();
            }
        }
    }

    public class TranslationTask
    {
        public Task<(string, bool)> Task { get; }
        public string OriginalText { get; }
        public CancellationTokenSource CTS { get; }

        public TranslationTask(Func<CancellationToken, Task<(string, bool)>> worker,
            string originalText, CancellationTokenSource cts)
        {
            Task = worker(cts.Token);
            OriginalText = originalText;
            CTS = cts;
        }
    }
}
