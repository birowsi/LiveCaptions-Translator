using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Valve.VR;

using LiveCaptionsTranslator.models;

using OVR = Valve.VR.OpenVR;

namespace LiveCaptionsTranslator.utils
{
    /*
     * Shows the subtitles inside VR as a SteamVR overlay that follows the headset or a controller.
     * Runs on its own thread; connects when SteamVR is running and the overlay is enabled,
     * and lets go when SteamVR quits. It never starts SteamVR by itself.
     */
    public static class VrSubtitleOverlay
    {
        private const string OVERLAY_KEY = "livecaptions.translator.subtitles";
        private const string OVERLAY_NAME = "LiveCaptions Translator";
        // Fixed texture size, text anchored to the bottom so the panel doesn't jump around.
        // Kept under 1 MB, the limit for raw overlay uploads.
        private const int TEXTURE_WIDTH = 768;
        private const int TEXTURE_HEIGHT = 320;

        private static Thread? thread = null;
        private static volatile bool isStopping = false;
        private static volatile bool isDirty = true;
        private static ulong overlayHandle = 0;
        private static bool isConnected = false;

        public static event Action? StatusChanged;
        public static string Status { get; private set; } = "Off";

        private static VrOverlaySetting Settings => Translator.Setting.VrOverlay;

        public static void Start()
        {
            if (thread != null)
                return;
            Translator.Caption.PropertyChanged += OnCaptionChanged;
            Settings.PropertyChanged += (sender, args) => isDirty = true;
            thread = new Thread(Run) { IsBackground = true, Name = "VR subtitle overlay" };
            thread.Start();
        }

        public static void Stop()
        {
            isStopping = true;
            thread?.Join(TimeSpan.FromSeconds(2));
        }

        private static void OnCaptionChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is "DisplayTranslatedCaption" or "DisplayPreviousTranslations" or
                "DisplayLogCards" or "DisplayOriginalCaption" or null)
                isDirty = true;
        }

        private static void SetStatus(string status)
        {
            if (Status == status)
                return;
            Status = status;
            StatusChanged?.Invoke();
        }

        private static void Run()
        {
            var lastTransformUpdate = DateTime.MinValue;
            while (!isStopping)
            {
                try
                {
                    if (!Settings.Enabled)
                    {
                        Disconnect();
                        SetStatus("Off");
                        Thread.Sleep(500);
                        continue;
                    }

                    if (!isConnected)
                    {
                        // Only connect to a running SteamVR; connecting would otherwise launch it.
                        var vrServers = Process.GetProcessesByName("vrserver");
                        bool isSteamVRRunning = vrServers.Length > 0;
                        foreach (var vrServer in vrServers)
                            vrServer.Dispose();
                        if (!isSteamVRRunning)
                        {
                            SetStatus("Waiting for SteamVR...");
                            Thread.Sleep(3000);
                            continue;
                        }
                        Connect();
                        isDirty = true;
                        lastTransformUpdate = DateTime.MinValue;
                    }

                    if (!PumpEvents())
                        continue;

                    // Controllers can switch on or change index at any time.
                    if (isDirty || DateTime.UtcNow - lastTransformUpdate > TimeSpan.FromSeconds(1))
                    {
                        UpdateTransform();
                        lastTransformUpdate = DateTime.UtcNow;
                    }
                    if (isDirty)
                    {
                        isDirty = false;
                        UpdateTexture();
                    }
                    Thread.Sleep(50);
                }
                catch (Exception ex)
                {
                    SetStatus($"[ERROR] {ex.Message}");
                    Disconnect();
                    Thread.Sleep(3000);
                }
            }
            Disconnect();
        }

        private static void Connect()
        {
            var error = EVRInitError.None;
            OVR.Init(ref error, EVRApplicationType.VRApplication_Overlay, "");
            if (error != EVRInitError.None)
                throw new InvalidOperationException($"Cannot connect to SteamVR ({error}).");
            isConnected = true;

            var overlayError = OVR.Overlay.CreateOverlay(OVERLAY_KEY, OVERLAY_NAME, ref overlayHandle);
            if (overlayError != EVROverlayError.None)
                throw new InvalidOperationException($"Cannot create the VR overlay ({overlayError}).");
            SetStatus("Showing subtitles in VR");
        }

        private static void Disconnect()
        {
            if (!isConnected)
                return;
            try
            {
                if (overlayHandle != 0)
                    OVR.Overlay?.DestroyOverlay(overlayHandle);
                OVR.Shutdown();
            }
            catch (Exception) { }
            overlayHandle = 0;
            isConnected = false;
        }

        // Returns false when SteamVR is quitting.
        private static bool PumpEvents()
        {
            var vrEvent = new VREvent_t();
            uint size = (uint)Marshal.SizeOf<VREvent_t>();
            while (OVR.System.PollNextEvent(ref vrEvent, size))
            {
                if (vrEvent.eventType == (uint)EVREventType.VREvent_Quit)
                {
                    OVR.System.AcknowledgeQuit_Exiting();
                    Disconnect();
                    SetStatus("Waiting for SteamVR...");
                    return false;
                }
            }
            return true;
        }

        private static void UpdateTransform()
        {
            var settings = Settings;
            OVR.Overlay.SetOverlayWidthInMeters(overlayHandle, (float)Math.Clamp(settings.Width, 0.1, 3.0));

            uint device = OVR.k_unTrackedDeviceIndex_Hmd;
            HmdMatrix34_t transform;
            var role = settings.Anchor switch
            {
                VrOverlaySetting.ANCHOR_LEFT_HAND => ETrackedControllerRole.LeftHand,
                VrOverlaySetting.ANCHOR_RIGHT_HAND => ETrackedControllerRole.RightHand,
                _ => ETrackedControllerRole.Invalid
            };
            uint controller = role == ETrackedControllerRole.Invalid
                ? OVR.k_unTrackedDeviceIndexInvalid
                : OVR.System.GetTrackedDeviceIndexForControllerRole(role);

            if (controller != OVR.k_unTrackedDeviceIndexInvalid)
            {
                // Like a wrist display: just above the controller, tilted towards the face.
                device = controller;
                transform = Matrix(-50, 0, 0.05, 0.10);
            }
            else
            {
                // In front of the eyes, slightly below eye level by default.
                transform = Matrix(0, 0, settings.Height, -Math.Clamp(settings.Distance, 0.3, 5.0));
            }
            OVR.Overlay.SetOverlayTransformTrackedDeviceRelative(overlayHandle, device, ref transform);
        }

        // Rotation around the X axis (degrees) plus a translation (meters).
        private static HmdMatrix34_t Matrix(double pitchDegrees, double x, double y, double z)
        {
            double angle = pitchDegrees * Math.PI / 180;
            float cos = (float)Math.Cos(angle), sin = (float)Math.Sin(angle);
            return new HmdMatrix34_t
            {
                m0 = 1,
                m1 = 0,
                m2 = 0,
                m3 = (float)x,
                m4 = 0,
                m5 = cos,
                m6 = -sin,
                m7 = (float)y,
                m8 = 0,
                m9 = sin,
                m10 = cos,
                m11 = (float)z
            };
        }

        private static void UpdateTexture()
        {
            var settings = Settings;
            var caption = Translator.Caption;
            string current = RegexPatterns.NoticePrefix().Replace(caption.DisplayTranslatedCaption ?? "", "").Trim();
            var previous = caption.GetPreviousContexts(Math.Clamp(settings.PreviousLines, 0, 5))
                .Select(entry => RegexPatterns.NoticePrefix().Replace(entry.TranslatedText, "").Trim())
                .Where(text => text.Length > 0 && text != current)
                .ToList();
            string original = settings.ShowOriginal ? caption.DisplayOriginalCaption ?? "" : "";

            if (current.Length == 0 && previous.Count == 0)
            {
                OVR.Overlay.HideOverlay(overlayHandle);
                return;
            }

            // WPF can only render on the UI thread.
            byte[] rgba = Application.Current.Dispatcher.Invoke(
                () => Render(original, previous, current, settings.BackgroundOpacity));
            var pinned = GCHandle.Alloc(rgba, GCHandleType.Pinned);
            try
            {
                OVR.Overlay.SetOverlayRaw(overlayHandle, pinned.AddrOfPinnedObject(),
                    TEXTURE_WIDTH, TEXTURE_HEIGHT, 4);
            }
            finally
            {
                pinned.Free();
            }
            OVR.Overlay.ShowOverlay(overlayHandle);
        }

        private static byte[] Render(string original, List<string> previous, string current, double backgroundOpacity)
        {
            var lines = new StackPanel();
            if (!string.IsNullOrWhiteSpace(original))
                lines.Children.Add(Line(original, 22, 0.75));
            foreach (var text in previous)
                lines.Children.Add(Line(text, 26, 0.6));
            if (current.Length > 0)
                lines.Children.Add(Line(current, 32, 1.0));

            var panel = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb((byte)(Math.Clamp(backgroundOpacity, 0, 1) * 255), 0, 0, 0)),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(20, 12, 20, 14),
                VerticalAlignment = VerticalAlignment.Bottom,
                Child = lines
            };
            var root = new Grid { Width = TEXTURE_WIDTH, Height = TEXTURE_HEIGHT };
            root.Children.Add(panel);
            root.Measure(new Size(TEXTURE_WIDTH, TEXTURE_HEIGHT));
            root.Arrange(new Rect(0, 0, TEXTURE_WIDTH, TEXTURE_HEIGHT));
            root.UpdateLayout();

            var bitmap = new RenderTargetBitmap(TEXTURE_WIDTH, TEXTURE_HEIGHT, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var pixels = new byte[TEXTURE_WIDTH * TEXTURE_HEIGHT * 4];
            bitmap.CopyPixels(pixels, TEXTURE_WIDTH * 4, 0);

            // Premultiplied BGRA → straight RGBA.
            for (int i = 0; i < pixels.Length; i += 4)
            {
                byte b = pixels[i], g = pixels[i + 1], r = pixels[i + 2], a = pixels[i + 3];
                if (a > 0 && a < 255)
                {
                    r = (byte)Math.Min(255, r * 255 / a);
                    g = (byte)Math.Min(255, g * 255 / a);
                    b = (byte)Math.Min(255, b * 255 / a);
                }
                pixels[i] = r;
                pixels[i + 1] = g;
                pixels[i + 2] = b;
            }
            return pixels;
        }

        private static TextBlock Line(string text, double fontSize, double opacity)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = fontSize,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Malgun Gothic, Yu Gothic UI, Segoe UI"),
                Foreground = Brushes.White,
                Opacity = opacity,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 2)
            };
        }
    }
}
