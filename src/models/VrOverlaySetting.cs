using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace LiveCaptionsTranslator.models
{
    /*
     * Subtitles shown inside VR as a SteamVR overlay.
     */
    public class VrOverlaySetting : INotifyPropertyChanged
    {
        public const string ANCHOR_HEAD = "Head";
        public const string ANCHOR_LEFT_HAND = "Left Hand";
        public const string ANCHOR_RIGHT_HAND = "Right Hand";

        [JsonIgnore]
        public static List<string> Anchors => [ANCHOR_HEAD, ANCHOR_LEFT_HAND, ANCHOR_RIGHT_HAND];

        public event PropertyChangedEventHandler? PropertyChanged;

        private bool enabled = false;
        private string anchor = ANCHOR_HEAD;
        private double distance = 1.2;
        private double height = -0.35;
        private double width = 0.9;
        private double backgroundOpacity = 0.55;
        private int previousLines = 2;
        private bool showOriginal = false;

        public bool Enabled
        {
            get => enabled;
            set
            {
                enabled = value;
                OnPropertyChanged("Enabled");
            }
        }
        // Where the subtitles follow: the headset, or a controller like a wrist display.
        public string Anchor
        {
            get => anchor;
            set
            {
                anchor = value;
                OnPropertyChanged("Anchor");
            }
        }
        // Meters in front of the headset (head anchor only).
        public double Distance
        {
            get => distance;
            set
            {
                distance = value;
                OnPropertyChanged("Distance");
            }
        }
        // Meters above (+) or below (-) eye level (head anchor only).
        public double Height
        {
            get => height;
            set
            {
                height = value;
                OnPropertyChanged("Height");
            }
        }
        // Width of the subtitle panel in meters.
        public double Width
        {
            get => width;
            set
            {
                width = value;
                OnPropertyChanged("Width");
            }
        }
        public double BackgroundOpacity
        {
            get => backgroundOpacity;
            set
            {
                backgroundOpacity = value;
                OnPropertyChanged("BackgroundOpacity");
            }
        }
        // Earlier translations shown above the current one.
        public int PreviousLines
        {
            get => previousLines;
            set
            {
                previousLines = value;
                OnPropertyChanged("PreviousLines");
            }
        }
        public bool ShowOriginal
        {
            get => showOriginal;
            set
            {
                showOriginal = value;
                OnPropertyChanged("ShowOriginal");
            }
        }

        public void OnPropertyChanged([CallerMemberName] string? propName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
            Translator.Setting?.Save();
        }
    }
}
