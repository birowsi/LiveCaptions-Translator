using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace LiveCaptionsTranslator.utils
{
    /*
     * Captures the audio of one app only (and its child processes), using the Windows process
     * loopback API (Windows 10 2004 or later). Windows converts it to mono 16-bit PCM at 24 kHz.
     * Waits for the app to start, and starts again when it is restarted.
     */
    public sealed class ProcessLoopbackCapture : IPcmAudioSource
    {
        private const string VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK = @"VAD\Process_Loopback";
        private static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
        private static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

        private const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
        private const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
        private const uint AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;
        private const uint AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000;
        private const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
        private const long BUFFER_DURATION = 2_000_000;    // 200 ms in 100 ns units

        private readonly string processName;
        private CancellationTokenSource? cts = null;
        private Task? captureTask = null;

        public event Action<short[]>? SamplesAvailable;
        public event Action<string>? StatusChanged;

        public ProcessLoopbackCapture(string processName)
        {
            this.processName = Path.GetFileNameWithoutExtension(processName.Trim());
        }

        public void Start()
        {
            cts = new CancellationTokenSource();
            var token = cts.Token;
            captureTask = Task.Run(() => RunAsync(token));
        }

        public void Stop()
        {
            cts?.Cancel();
            try
            {
                captureTask?.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException) { }
        }

        public void Dispose() => Stop();

        private async Task RunAsync(CancellationToken token)
        {
            string? lastStatus = null;
            void Report(string status)
            {
                if (status == lastStatus)
                    return;
                lastStatus = status;
                StatusChanged?.Invoke(status);
            }

            while (!token.IsCancellationRequested)
            {
                using var process = FindProcess();
                if (process == null)
                {
                    Report($"Waiting for {processName} to start...");
                    await Task.Delay(3000, token).ContinueWith(_ => { });
                    continue;
                }

                try
                {
                    Report($"Listening to {processName} only");
                    CaptureUntilExit(process, token);
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    Report($"[ERROR] Cannot capture {processName}: {ex.Message}");
                    await Task.Delay(3000, token).ContinueWith(_ => { });
                }
            }
        }

        private Process? FindProcess()
        {
            var processes = Process.GetProcessesByName(processName);
            // Prefer the process that owns a window: that is the app itself, not a helper.
            var target = processes.FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero) ?? processes.FirstOrDefault();
            foreach (var process in processes.Where(p => p != target))
                process.Dispose();
            return target;
        }

        private void CaptureUntilExit(Process process, CancellationToken token)
        {
            var audioClient = Activate((uint)process.Id);
            IAudioCaptureClient? captureClient = null;
            using var bufferReady = new AutoResetEvent(false);
            try
            {
                var format = new WaveFormatEx
                {
                    wFormatTag = 1,     // PCM
                    nChannels = 1,
                    nSamplesPerSec = LoopbackAudioCapture.SAMPLE_RATE,
                    wBitsPerSample = 16,
                    nBlockAlign = 2,
                    nAvgBytesPerSec = LoopbackAudioCapture.SAMPLE_RATE * 2,
                    cbSize = 0
                };
                Check(audioClient.Initialize(0,
                    AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK |
                    AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY,
                    BUFFER_DURATION, 0, ref format, IntPtr.Zero));
                Check(audioClient.SetEventHandle(bufferReady.SafeWaitHandle.DangerousGetHandle()));
                Check(audioClient.GetService(IID_IAudioCaptureClient, out object service));
                captureClient = (IAudioCaptureClient)service;
                Check(audioClient.Start());

                int processId = process.Id;
                var lastExitCheck = DateTime.UtcNow;
                while (!token.IsCancellationRequested)
                {
                    // `Process.HasExited` needs rights we may not have (e.g. an elevated app): look the id up instead.
                    if (DateTime.UtcNow - lastExitCheck > TimeSpan.FromSeconds(1))
                    {
                        lastExitCheck = DateTime.UtcNow;
                        if (!IsRunning(processId))
                            break;
                    }

                    bufferReady.WaitOne(200);
                    while (captureClient.GetNextPacketSize(out uint packetFrames) == 0 && packetFrames > 0)
                    {
                        Check(captureClient.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out _));
                        var samples = new short[frames];
                        if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) == 0 && frames > 0)
                            Marshal.Copy(data, samples, 0, (int)frames);
                        captureClient.ReleaseBuffer(frames);
                        if (samples.Length > 0)
                            SamplesAvailable?.Invoke(samples);
                    }
                }
            }
            finally
            {
                audioClient.Stop();
                if (captureClient != null)
                    Marshal.ReleaseComObject(captureClient);
                Marshal.ReleaseComObject(audioClient);
            }
        }

        private static bool IsRunning(int processId)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private static IAudioClient Activate(uint processId)
        {
            var activationParams = new AudioClientActivationParams
            {
                ActivationType = 1,             // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
                TargetProcessId = processId,
                ProcessLoopbackMode = 0         // PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE
            };
            int paramsSize = Marshal.SizeOf<AudioClientActivationParams>();
            IntPtr paramsPtr = Marshal.AllocHGlobal(paramsSize);
            IntPtr variantPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlob>());
            try
            {
                Marshal.StructureToPtr(activationParams, paramsPtr, false);
                Marshal.StructureToPtr(new PropVariantBlob
                {
                    vt = 65,                    // VT_BLOB
                    cbSize = (uint)paramsSize,
                    pBlobData = paramsPtr
                }, variantPtr, false);

                var handler = new ActivationHandler();
                ActivateAudioInterfaceAsync(VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK, IID_IAudioClient,
                    variantPtr, handler, out var operation);
                if (!handler.Completed.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("Audio activation timed out.");

                operation.GetActivateResult(out int result, out object activated);
                Marshal.ThrowExceptionForHR(result);
                return (IAudioClient)activated;
            }
            finally
            {
                Marshal.FreeHGlobal(variantPtr);
                Marshal.FreeHGlobal(paramsPtr);
            }
        }

        private static void Check(int hr)
        {
            if (hr < 0)
                Marshal.ThrowExceptionForHR(hr);
        }

        #region Interop
        // Types that COM calls into must be public.

        [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
        private static extern void ActivateAudioInterfaceAsync(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            IntPtr activationParams,
            IActivateAudioInterfaceCompletionHandler completionHandler,
            out IActivateAudioInterfaceAsyncOperation activationOperation);

        [StructLayout(LayoutKind.Sequential)]
        private struct AudioClientActivationParams
        {
            public int ActivationType;
            public uint TargetProcessId;
            public int ProcessLoopbackMode;
        }

        // PROPVARIANT holding a VT_BLOB.
        [StructLayout(LayoutKind.Sequential)]
        private struct PropVariantBlob
        {
            public ushort vt;
            public ushort reserved1;
            public ushort reserved2;
            public ushort reserved3;
            public uint cbSize;
            public IntPtr pBlobData;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        public struct WaveFormatEx
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public int nSamplesPerSec;
            public int nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IActivateAudioInterfaceAsyncOperation
        {
            void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
        }

        [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IActivateAudioInterfaceCompletionHandler
        {
            void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
        }

        // Marks the handler as free-threaded, as the activation API requires.
        [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAgileObject
        {
        }

        [ComVisible(true)]
        [ClassInterface(ClassInterfaceType.None)]
        public sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
        {
            public ManualResetEventSlim Completed { get; } = new(false);

            public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
            {
                Completed.Set();
            }
        }

        [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioClient
        {
            [PreserveSig]
            int Initialize(int shareMode, uint streamFlags, long hnsBufferDuration, long hnsPeriodicity,
                [In] ref WaveFormatEx format, IntPtr audioSessionGuid);
            [PreserveSig] int GetBufferSize(out uint bufferSize);
            [PreserveSig] int GetStreamLatency(out long latency);
            [PreserveSig] int GetCurrentPadding(out uint padding);
            [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, IntPtr closestMatch);
            [PreserveSig] int GetMixFormat(out IntPtr format);
            [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
            [PreserveSig] int Start();
            [PreserveSig] int Stop();
            [PreserveSig] int Reset();
            [PreserveSig] int SetEventHandle(IntPtr eventHandle);
            [PreserveSig]
            int GetService([MarshalAs(UnmanagedType.LPStruct)] Guid riid,
                [MarshalAs(UnmanagedType.IUnknown)] out object service);
        }

        [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioCaptureClient
        {
            [PreserveSig]
            int GetBuffer(out IntPtr data, out uint numFramesToRead, out uint flags,
                out ulong devicePosition, out ulong qpcPosition);
            [PreserveSig] int ReleaseBuffer(uint numFramesRead);
            [PreserveSig] int GetNextPacketSize(out uint numFramesInNextPacket);
        }

        #endregion
    }
}
