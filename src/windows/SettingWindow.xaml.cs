using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

using LiveCaptionsTranslator.apis;
using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = Wpf.Ui.Controls.TextBlock;
using ComboBox = System.Windows.Controls.ComboBox;

namespace LiveCaptionsTranslator
{
    public partial class SettingWindow : FluentWindow
    {
        private System.Windows.Controls.Button currentSelected;
        private Dictionary<string, FrameworkElement> sectionReferences;

        public SettingWindow()
        {
            InitializeComponent();
            ApplicationThemeManager.ApplySystemTheme();
            DataContext = Translator.Setting;

            Loaded += (sender, args) =>
            {
                SystemThemeWatcher.Watch(this, WindowBackdropType.Mica, true);
                Initialize();
                SelectButton(PromptButton);
            };
        }

        private void Initialize()
        {
            sectionReferences = new Dictionary<string, FrameworkElement>
            {
                { "General", ContentPanel },
                { "Prompt", PromptSection }
            };

            foreach (var apiName in TranslateAPI.TRANSLATE_FUNCTIONS.Keys.Where(apiName =>
                         !TranslateAPI.NO_CONFIG_APIS.Contains(apiName)))
            {
                sectionReferences[apiName] = FindName($"{apiName}Section") as StackPanel;
                SwitchConfig(apiName, Translator.Setting.ConfigIndices[apiName]);
            }

            InitializeChatGPT();
            InitializeTranscriptionAndUsage();
        }

        private void InitializeTranscriptionAndUsage()
        {
            sectionReferences["SpeechRecognition"] = SpeechRecognitionSection;
            sectionReferences["Usage"] = UsageSection;

            TranscriptionSourceBox.ItemsSource = TranscriptionSetting.Sources;
            TranscriptionSourceBox.SelectedItem = Translator.Setting.Transcription.Source;
            TranscriptionModelBox.ItemsSource = TranscriptionSetting.Models;
            TranscriptionModelBox.SelectedItem = Translator.Setting.Transcription.Model;
            TranscriptionDelayBox.ItemsSource = TranscriptionSetting.Delays;
            TranscriptionDelayBox.SelectedItem = Translator.Setting.Transcription.Delay;
            // Apply a new model right away instead of waiting for the next restart.
            TranscriptionModelBox.SelectionChanged += (sender, args) => OpenAITranscriber.Restart();
            TranscriptionDelayBox.SelectionChanged += (sender, args) => OpenAITranscriber.Restart();
            GPTModelComboBox.ItemsSource = GPTConfig.SuggestedModels;
            GPTReasoningEffortBox.ItemsSource = ChatGPTConfig.ReasoningEfforts;
            StartingBalanceBox.Value = UsageTracker.StartingBalance;

            Action onStatusChanged = () => Dispatcher.BeginInvoke(new Action(UpdateTranscriptionStatus));
            Action onUsageChanged = () => Dispatcher.BeginInvoke(new Action(UpdateUsage));
            OpenAITranscriber.StatusChanged += onStatusChanged;
            UsageTracker.Changed += onUsageChanged;
            Closed += (sender, args) =>
            {
                OpenAITranscriber.StatusChanged -= onStatusChanged;
                UsageTracker.Changed -= onUsageChanged;
            };

            UpdateTranscriptionStatus();
            UpdateUsage();
        }

        private void UpdateTranscriptionStatus()
        {
            TranscriptionStatusText.Text = Translator.Setting.Transcription.IsOpenAI
                ? $"Status: {(OpenAITranscriber.IsRunning ? OpenAITranscriber.Status : "Stopped (press the microphone button on the main window)")}"
                : "Status: using Windows LiveCaptions.";
        }

        private void UpdateUsage()
        {
            var usage = UsageTracker.Snapshot();
            string remaining = usage.StartingBalance > 0
                ? $"  ·  Remaining {UsageTracker.FormatUSD(usage.StartingBalance - usage.TotalCost)} of {UsageTracker.FormatUSD(usage.StartingBalance)}"
                : "";
            UsageSummaryText.Text = $"Spent {UsageTracker.FormatUSD(usage.TotalCost)}{remaining}";
            UsageDetailText.Text =
                $"Since {usage.Since:yyyy-MM-dd HH:mm}  ·  This session {UsageTracker.FormatUSD(UsageTracker.SessionCost)}\n" +
                $"Speech recognition: {UsageTracker.FormatUSD(usage.TranscriptionCost)} ({usage.TranscriptionSeconds / 60:0.0} min of audio)\n" +
                $"Translation: {UsageTracker.FormatUSD(usage.TranslationCost)} ({usage.Requests:N0} requests, " +
                $"{usage.InputTokens:N0} input tokens of which {usage.CachedInputTokens:N0} cached, {usage.OutputTokens:N0} output tokens)" +
                (usage.HasUnpricedUsage ? "\nSome usage was from a model with an unknown price and is not included." : "");
        }

        private void TranscriptionSourceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count == 0 || e.AddedItems[0] is not string source)
                return;
            var transcription = Translator.Setting.Transcription;
            if (transcription.Source != source)
                transcription.Source = source;

            // Only switch when the running source differs from the selection.
            bool needsSwitch = transcription.IsOpenAI ? !OpenAITranscriber.IsRunning : Translator.Window == null;
            if (needsSwitch)
                Task.Run(Translator.ApplyCaptionSource);
            (App.Current.MainWindow as MainWindow)?.UpdateListenButton();
            UpdateTranscriptionStatus();
        }

        private void SetBalanceButton_Click(object sender, RoutedEventArgs e)
        {
            UsageTracker.SetStartingBalance(StartingBalanceBox.Value ?? 0);
        }

        private void ResetUsageButton_Click(object sender, RoutedEventArgs e)
        {
            var result = System.Windows.MessageBox.Show(this,
                "Reset the spent counter to zero? Use this after topping up your credit.",
                "Reset Counter", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Question);
            if (result == System.Windows.MessageBoxResult.OK)
                UsageTracker.Reset(StartingBalanceBox.Value ?? UsageTracker.StartingBalance);
        }

        private void InitializeChatGPT()
        {
            ChatGPTReasoningEffortBox.ItemsSource = ChatGPTConfig.ReasoningEfforts;
            var config = Translator.Setting["ChatGPT"] as ChatGPTConfig;
            if (config != null && !ChatGPTConfig.ReasoningEfforts.Contains(config.ReasoningEffort))
                config.ReasoningEffort = ChatGPTConfig.DEFAULT_REASONING_EFFORT;

            Action onStateChanged = () => Dispatcher.BeginInvoke(new Action(UpdateChatGPTState));
            ChatGPTAuth.StateChanged += onStateChanged;
            Closed += (sender, args) => ChatGPTAuth.StateChanged -= onStateChanged;

            UpdateChatGPTState();
            if (ChatGPTAuth.IsPlanUsageEnabled)
                _ = LoadChatGPTModels(showResult: false);
        }

        private void UpdateChatGPTState()
        {
            bool isSigningIn = ChatGPTAuth.IsSigningIn;
            bool isSignedIn = ChatGPTAuth.IsSignedIn;
            bool isPlanUsageEnabled = ChatGPTAuth.IsPlanUsageEnabled;
            string account = string.IsNullOrEmpty(ChatGPTAuth.Email) ? "your ChatGPT account" : ChatGPTAuth.Email;

            ChatGPTSignInButton.IsEnabled = !isSigningIn;
            ChatGPTSignInButton.Visibility = isPlanUsageEnabled ? Visibility.Collapsed : Visibility.Visible;
            ChatGPTSignInButton.Content = isSignedIn ? "Enable plan usage" : "Continue with ChatGPT";
            ChatGPTCancelButton.Visibility = isSigningIn ? Visibility.Visible : Visibility.Collapsed;
            ChatGPTSignOutButton.Visibility = isSignedIn && !isSigningIn ? Visibility.Visible : Visibility.Collapsed;
            ChatGPTSwitchAccountButton.Visibility = !isSigningIn && (isSignedIn || ChatGPTAuth.HasSavedRegistration)
                ? Visibility.Visible : Visibility.Collapsed;
            ChatGPTLoadModelsButton.IsEnabled = isPlanUsageEnabled;

            if (isSigningIn)
                ChatGPTStatusText.Text = "Waiting for you to finish signing in in your browser...";
            else if (isPlanUsageEnabled)
                ChatGPTStatusText.Text = $"Signed in as {account}.";
            else if (isSignedIn)
                ChatGPTStatusText.Text = $"Signed in as {account}, but ChatGPT plan usage is not enabled. " +
                                         "Click \"Enable plan usage\" to allow this app to use your plan.";
            else
                ChatGPTStatusText.Text = "Not signed in.";

            ChatGPTPlanUsageText.Text = isPlanUsageEnabled
                ? "Using ChatGPT plan. "
                : "Review your ChatGPT plan usage and this app's limit in ChatGPT settings. ";
        }

        private async void ChatGPTSignInButton_Click(object sender, RoutedEventArgs e)
        {
            // Signed in without plan usage: ask for consent again with the full scope set.
            bool forceConsent = ChatGPTAuth.IsSignedIn && !ChatGPTAuth.IsPlanUsageEnabled;
            await SignInChatGPT(newAccount: false, forceConsent);
        }

        private async void ChatGPTSwitchAccountButton_Click(object sender, RoutedEventArgs e)
        {
            await SignInChatGPT(newAccount: true, forceConsent: false);
        }

        private async Task SignInChatGPT(bool newAccount, bool forceConsent)
        {
            try
            {
                bool isFirstPlanUsage = await ChatGPTAuth.SignInAsync(newAccount, forceConsent);
                Activate();
                if (isFirstPlanUsage)
                    System.Windows.MessageBox.Show(this,
                        "Translations in LiveCaptions Translator will use your ChatGPT plan.\n\n" +
                        "You can review usage and set this app's limit in ChatGPT settings → Usage.",
                        "You're using your ChatGPT plan", System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Information);
                if (ChatGPTAuth.IsPlanUsageEnabled)
                    await LoadChatGPTModels(showResult: false);
            }
            catch (ChatGPTAuthException ex)
            {
                Activate();
                System.Windows.MessageBox.Show(this, ex.Message, "Sign in with ChatGPT",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                Activate();
                System.Windows.MessageBox.Show(this, $"Sign-in failed: {ex.Message}", "Sign in with ChatGPT",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }

        private void ChatGPTCancelButton_Click(object sender, RoutedEventArgs e)
        {
            ChatGPTAuth.CancelSignIn();
        }

        private async void ChatGPTSignOutButton_Click(object sender, RoutedEventArgs e)
        {
            ChatGPTSignOutButton.IsEnabled = false;
            try
            {
                bool isRevoked = await ChatGPTAuth.SignOutAsync();
                ChatGPTModelComboBox.ItemsSource = null;
                if (!isRevoked)
                    System.Windows.MessageBox.Show(this,
                        "Signed out on this PC, but the session could not be revoked on the server.\n" +
                        "You can disconnect this app in ChatGPT settings.",
                        "Sign out", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            }
            finally
            {
                ChatGPTSignOutButton.IsEnabled = true;
            }
        }

        private async void ChatGPTLoadModelsButton_Click(object sender, RoutedEventArgs e)
        {
            await LoadChatGPTModels(showResult: true);
        }

        private async Task LoadChatGPTModels(bool showResult)
        {
            ChatGPTLoadModelsButton.IsEnabled = false;
            try
            {
                var models = await ModelsApiService.FetchChatGPTModelsAsync();
                ChatGPTModelComboBox.ItemsSource = models;

                var config = Translator.Setting["ChatGPT"] as ChatGPTConfig;
                if (config != null && string.IsNullOrWhiteSpace(config.ModelName) && models.Count > 0)
                    config.ModelName = models[0].Id;
                ChatGPTModelComboBox.Text = config?.ModelName ?? "";

                if (showResult)
                    System.Windows.MessageBox.Show(this,
                        models.Count > 0 ? $"Loaded {models.Count} model(s)." : "No models are available for this account.",
                        "Load Models", System.Windows.MessageBoxButton.OK,
                        models.Count > 0 ? System.Windows.MessageBoxImage.Information : System.Windows.MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                if (showResult)
                    System.Windows.MessageBox.Show(this, $"Unable to load models: {ex.Message}", "Load Models",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            }
            finally
            {
                ChatGPTLoadModelsButton.IsEnabled = ChatGPTAuth.IsPlanUsageEnabled;
            }
        }

        private void ChatGPTModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count > 0 && e.AddedItems[0] is ModelsApiService.ModelInfo mi)
            {
                var config = Translator.Setting["ChatGPT"] as ChatGPTConfig;
                if (config != null)
                {
                    config.ModelName = mi.Id;
                    if (sender is ComboBox cb)
                        cb.Text = mi.Id;
                }
            }
        }

        private void ChatGPTManageUsage_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        }

        private void NewButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button)
            {
                string apiName = button.Tag as string;
                var configs = Translator.Setting.Configs[apiName];
                var configIndex = Translator.Setting.ConfigIndices[apiName];

                var type = Type.GetType($"LiveCaptionsTranslator.models.{apiName}Config");
                var config = Activator.CreateInstance(type) as TranslateAPIConfig;
                configs.Insert(configIndex + 1, config);
                SwitchConfig(apiName, configIndex + 1);

                Translator.Setting.OnPropertyChanged("Configs");
            }
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button)
            {
                string apiName = button.Tag as string;
                var configs = Translator.Setting.Configs[apiName];
                var configIndex = Translator.Setting.ConfigIndices[apiName];

                if (configs.Count <= 1)
                {
                    (FindName($"{apiName}DeleteFlyout") as Flyout)?.Show();
                    return;
                }
                configs.RemoveAt(configIndex);
                SwitchConfig(apiName, Math.Max(0, Math.Min(configs.Count - 1, configIndex)));

                Translator.Setting.OnPropertyChanged("Configs");
            }
        }

        private void PriorButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button)
            {
                string apiName = button.Tag as string;
                var configIndex = Translator.Setting.ConfigIndices[apiName];
                SwitchConfig(apiName, configIndex - 1);
            }
        }

        private void NextButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button)
            {
                string apiName = button.Tag as string;
                var configIndex = Translator.Setting.ConfigIndices[apiName];
                SwitchConfig(apiName, configIndex + 1);
            }
        }

        private void NavigationButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button button)
            {
                SelectButton(button);
                string targetSection = button.Tag.ToString();
                if (sectionReferences.TryGetValue(targetSection, out FrameworkElement element))
                    element.BringIntoView();
            }
        }

        private void OpenAIAPIUrlInfo_MouseEnter(object sender, MouseEventArgs e)
        {
            OpenAIAPIUrlInfoFlyout.Show();
        }

        private void OpenAIAPIUrlInfo_MouseLeave(object sender, MouseEventArgs e)
        {
            OpenAIAPIUrlInfoFlyout.Hide();
        }

        private void OllamaAPIUrlInfo_MouseEnter(object sender, MouseEventArgs e)
        {
            OllamaAPIUrlInfoFlyout.Show();
        }

        private void OllamaAPIUrlInfo_MouseLeave(object sender, MouseEventArgs e)
        {
            OllamaAPIUrlInfoFlyout.Hide();
        }

        private void LMStudioAPIUrlInfo_MouseEnter(object sender, MouseEventArgs e)
        {
            LMStudioAPIUrlInfoFlyout.Show();
        }

        private void LMStudioAPIUrlInfo_MouseLeave(object sender, MouseEventArgs e)
        {
            LMStudioAPIUrlInfoFlyout.Hide();
        }

        private async void LoadModelsButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string apiName && apiName == "LMStudio")
            {
                string baseUrl = (Translator.Setting["LMStudio"] as LMStudioConfig)?.ApiUrl ?? "";

                if (string.IsNullOrWhiteSpace(baseUrl))
                {
                    System.Windows.MessageBox.Show("Please set the API URL first.", "Load Models", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }

                button.IsEnabled = false;
                try
                {
                    var models = await ModelsApiService.FetchModelsAsync(apiName, baseUrl);
                    var comboBox = FindName($"{apiName}ModelComboBox") as ComboBox;
                    if (comboBox != null)
                    {
                        comboBox.ItemsSource = models;
                        if (models.Count > 0)
                            System.Windows.MessageBox.Show($"Loaded {models.Count} model(s).", "Load Models", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                        else
                            System.Windows.MessageBox.Show("No models found or unable to connect. Check that the server is running.", "Load Models", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    }
                }
                finally
                {
                    button.IsEnabled = true;
                }
            }
        }

        private void LMStudioModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count > 0 && e.AddedItems[0] is ModelsApiService.ModelInfo mi)
            {
                var config = Translator.Setting["LMStudio"] as LMStudioConfig;
                if (config != null)
                {
                    config.ModelName = mi.Id;
                    if (sender is ComboBox cb)
                        cb.Text = mi.Id;
                }
            }
        }

        private void SwitchConfig(string apiName, int index)
        {
            if (index < 0 || index >= Translator.Setting.Configs[apiName].Count)
                return;

            if (Translator.Setting.ConfigIndices[apiName] != index)
                Translator.Setting.ConfigIndices[apiName] = index;

            if (FindName($"{apiName}Index") is TextBlock indexTextBlock)
            {
                int total = Translator.Setting.Configs[apiName].Count;
                indexTextBlock.Text = $"{index + 1}/{total}";
            }
            Translator.Setting.OnPropertyChanged(null);
        }

        private void SelectButton(System.Windows.Controls.Button button)
        {
            if (currentSelected != null)
                currentSelected.Background = new SolidColorBrush(Colors.Transparent);
            button.Background = (Brush)FindResource("ControlFillColorSecondaryBrush");
            currentSelected = button;
        }
    }
}