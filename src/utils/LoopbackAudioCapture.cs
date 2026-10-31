using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace LiveCaptionsTranslator.utils
{
    /*
     * Captures what the PC is playing (WASAPI loopback of the default output device)
     * and converts it to mono 16-bit PCM at 24 kHz, the format the Realtime API expects.
     */
    public sealed class LoopbackAudioCapture : IDisposable
    {
        public const int SAMPLE_RATE = 24000;

        private readonly WasapiLoopbackCapture capture;
        private readonly BufferedWaveProvider buffer;
        private readonly ISampleProvider resampler;
        private readonly float[] readBuffer = new float[SAMPLE_RATE];
        private readonly object readLock = new();

        /// <summary>
        /// Raised on the capture thread with a chunk of mono PCM16 samples at 24 kHz.
        /// </summary>
        public event Action<short[]>? SamplesAvailable;
        public event Action<Exception?>? Stopped;

        public LoopbackAudioCapture()
        {
            capture = new WasapiLoopbackCapture();
            buffer = new BufferedWaveProvider(capture.WaveFormat)
            {
                BufferDuration = TimeSpan.FromSeconds(10),
                DiscardOnBufferOverflow = true,
                ReadFully = false
            };

            ISampleProvider source = buffer.ToSampleProvider();
            if (source.WaveFormat.Channels > 1)
                source = new DownmixSampleProvider(source);
            resampler = source.WaveFormat.SampleRate == SAMPLE_RATE
                ? source
                : new WdlResamplingSampleProvider(source, SAMPLE_RATE);

            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += (s, e) => Stopped?.Invoke(e.Exception);
        }

        public void Start() => capture.StartRecording();

        public void Stop() => capture.StopRecording();

        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (e.BytesRecorded <= 0)
                return;

            lock (readLock)
            {
                buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);

                int read;
                while ((read = resampler.Read(readBuffer, 0, readBuffer.Length)) > 0)
                {
                    var samples = new short[read];
                    for (int i = 0; i < read; i++)
                        samples[i] = (short)Math.Clamp(readBuffer[i] * short.MaxValue, short.MinValue, short.MaxValue);
                    SamplesAvailable?.Invoke(samples);
                }
            }
        }

        public void Dispose()
        {
            capture.DataAvailable -= OnDataAvailable;
            capture.Dispose();
        }

        // Averages any number of channels into one.
        private sealed class DownmixSampleProvider : ISampleProvider
        {
            private readonly ISampleProvider source;
            private readonly int channels;
            private float[] sourceBuffer = [];

            public WaveFormat WaveFormat { get; }

            public DownmixSampleProvider(ISampleProvider source)
            {
                this.source = source;
                channels = source.WaveFormat.Channels;
                WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
            }

            public int Read(float[] buffer, int offset, int count)
            {
                int needed = count * channels;
                if (sourceBuffer.Length < needed)
                    sourceBuffer = new float[needed];

                int read = source.Read(sourceBuffer, 0, needed);
                int frames = read / channels;
                for (int frame = 0; frame < frames; frame++)
                {
                    float sum = 0;
                    for (int ch = 0; ch < channels; ch++)
                        sum += sourceBuffer[frame * channels + ch];
                    buffer[offset + frame] = sum / channels;
                }
                return frames;
            }
        }
    }
}
