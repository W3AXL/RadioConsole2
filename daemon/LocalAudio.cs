using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using PortAudioSharp;
using Serilog;
using rc2_core; // for PcmRingBuffer — shared with AudioBridge's identical TX buffering problem

namespace daemon
{
    /// <summary>
    /// Helper class for PortAudio device enumeration for use with configuration validation
    /// and the list-audio command
    /// </summary>
    internal static class Audio
    {
        /// <summary>
        /// Whether PortAudio has been initialized or not
        /// </summary>
        private static bool initialized = false;

        /// <summary>
        /// Ensures that PortAudio is initialized, returns if it already is
        /// </summary>
        public static void EnsureInitialized()
        {
            if (initialized) return;
            PortAudio.Initialize();
            initialized = true;
        }

        /// <summary>
        /// Get a list of input device names for use with PortAudio
        /// </summary>
        /// <returns></returns>
        public static List<string> GetInputDeviceNames()
        {
            EnsureInitialized();
            var names = new List<string>();
            for (int i = 0; i < PortAudio.DeviceCount; i++)
            {
                var info = PortAudio.GetDeviceInfo(i);
                if (info.maxInputChannels > 0) names.Add(info.name);
            }
            return names;
        }

        /// <summary>
        /// Get a list of output device names for use with PortAudio
        /// </summary>
        /// <returns></returns>
        public static List<string> GetOutputDeviceNames()
        {
            EnsureInitialized();
            var names = new List<string>();
            for (int i = 0; i < PortAudio.DeviceCount; i++)
            {
                var info = PortAudio.GetDeviceInfo(i);
                if (info.maxOutputChannels > 0) names.Add(info.name);
            }
            return names;
        }

        /// <summary>
        /// Check if the given input device name exists in the list of input devices
        /// </summary>
        /// <param name="inputName"></param>
        /// <returns></returns>
        public static bool CheckInputExists(string inputName) =>
            GetInputDeviceNames().Any(n => n.Contains(inputName, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Check if the given output device name exists in the list of output devices
        /// </summary>
        /// <param name="outputName"></param>
        /// <returns></returns>
        public static bool CheckOutputExists(string outputName) =>
            GetOutputDeviceNames().Any(n => n.Contains(outputName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Local audio class handling raw PCM16 capture/playback via PortAudioSharp2.
    /// </summary>
    internal class LocalAudio
    {
        /// <summary>
        /// The RX audio device name
        /// </summary>
        private readonly string rxDeviceName;
        /// <summary>
        /// The TX audio device name
        /// </summary>
        private readonly string txDeviceName;
        /// <summary>
        /// The sample rate to use for both device connections
        /// </summary>
        private readonly int sampleRate;
        /// <summary>
        /// Whether this audio device is RX only (no TX microphone device)
        /// </summary>
        private readonly bool rxOnly;

        /// <summary>
        /// RX audio stream from PortAudio
        /// </summary>
        private PortAudioSharp.Stream rxStream;
        /// <summary>
        /// TX audio stream from PortAudio
        /// </summary>
        private PortAudioSharp.Stream txStream;

        /// <summary>
        /// The ringbuffer for storing outgoing TX audio
        /// </summary>
        private PcmRingBuffer txRing;

        /// <summary>Raised whenever a block of captured PCM16 samples is
        /// available from the RX (input) device. Wire this to
        /// Radio.RxSendPCM16Samples.</summary>
        public event Action<short[], uint> RxAudioAvailable;

        /// <param name="rxDevice">Substring match (case-insensitive) against
        /// the OS device name for the RX (capture) device — this is the
        /// radio's received audio coming INTO the sound card.</param>
        /// <param name="txDevice">Same, for the TX (playback) device — this
        /// feeds the radio's transmit audio input.</param>
        /// <param name="sampleRate">Rate to open both streams at (from
        /// config; 48000 is a safe cross-platform default). This becomes
        /// the rxAudioSampleRate passed to the Radio constructor, so
        /// AudioBridge knows exactly what it's receiving without guessing.</param>
        /// <param name="rxOnly">Skip opening a TX stream entirely if true.</param>
        public LocalAudio(string rxDevice, string txDevice, int sampleRate, bool rxOnly = false)
        {
            // Ensure PortAudio is initialized
            Audio.EnsureInitialized();

            // Save the device information
            rxDeviceName = rxDevice;
            txDeviceName = txDevice;
            this.sampleRate = sampleRate;
            this.rxOnly = rxOnly;

            // Startup prints
            Log.Logger.Information("Configured local audio devices:");
            Log.Logger.Information("    RX: {rxDevice}", rxDevice);
            if (!rxOnly)
            {
                Log.Logger.Information("    TX: {txDevice}", txDevice);
                // Create a new ringbuffer for TX samples, with a length matching the current sample rate
                txRing = new PcmRingBuffer(sampleRate);
            }
        }

        /// <summary>
        /// Open and start the audio streams to/from the devices
        /// </summary>
        public void Start()
        {
            int rxIndex = FindDevice(rxDeviceName, forInput: true);
            rxStream = OpenInputStream(rxIndex);
            rxStream.Start();
            Log.Logger.Debug("Started RX audio stream on device {index} @ {rate}Hz", rxIndex, sampleRate);

            if (!rxOnly)
            {
                int txIndex = FindDevice(txDeviceName, forInput: false);
                txStream = OpenOutputStream(txIndex);
                txStream.Start();
                Log.Logger.Debug("Started TX audio stream on device {index} @ {rate}Hz", txIndex, sampleRate);
            }
        }

        /// <summary>
        /// Stop audio to/from the devices
        /// </summary>
        /// <returns></returns>
        public Task Stop()
        {
            rxStream?.Stop();
            rxStream?.Dispose();
            if (!rxOnly)
            {
                txStream?.Stop();
                txStream?.Dispose();
            }
            Log.Logger.Debug("Local audio devices stopped");
            return Task.CompletedTask;
        }

        private static int FindDevice(string nameSubstring, bool forInput)
        {
            if (string.IsNullOrEmpty(nameSubstring))
            {
                return forInput ? PortAudio.DefaultInputDevice : PortAudio.DefaultOutputDevice;
            }
            for (int i = 0; i < PortAudio.DeviceCount; i++)
            {
                var info = PortAudio.GetDeviceInfo(i);
                bool candidate = forInput ? info.maxInputChannels > 0 : info.maxOutputChannels > 0;
                if (candidate && info.name.Contains(nameSubstring, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
            Log.Logger.Warning("No {direction} device matched '{query}', falling back to system default",
                forInput ? "input" : "output", nameSubstring);
            return forInput ? PortAudio.DefaultInputDevice : PortAudio.DefaultOutputDevice;
        }

        private PortAudioSharp.Stream OpenInputStream(int deviceIndex)
        {
            var parameters = new StreamParameters
            {
                device = deviceIndex,
                channelCount = 1,
                sampleFormat = SampleFormat.Int16,
                suggestedLatency = PortAudio.GetDeviceInfo(deviceIndex).defaultLowInputLatency,
            };
            return new PortAudioSharp.Stream(
                inParams: parameters, outParams: null,
                sampleRate: sampleRate, framesPerBuffer: 480,
                streamFlags: StreamFlags.NoFlag,
                callback: OnInputCallback, userData: IntPtr.Zero);
        }

        private PortAudioSharp.Stream OpenOutputStream(int deviceIndex)
        {
            var parameters = new StreamParameters
            {
                device = deviceIndex,
                channelCount = 1,
                sampleFormat = SampleFormat.Int16,
                suggestedLatency = PortAudio.GetDeviceInfo(deviceIndex).defaultLowOutputLatency,
            };
            return new PortAudioSharp.Stream(
                inParams: null, outParams: parameters,
                sampleRate: sampleRate, framesPerBuffer: 480,
                streamFlags: StreamFlags.NoFlag,
                callback: OnOutputCallback, userData: IntPtr.Zero);
        }

        // Runs on PortAudio's real-time audio thread — kept minimal.
        private StreamCallbackResult OnInputCallback(
            IntPtr input, IntPtr output, uint frameCount,
            ref StreamCallbackTimeInfo timeInfo, StreamCallbackFlags statusFlags, IntPtr userData)
        {
            short[] samples = new short[frameCount];
            Marshal.Copy(input, samples, 0, (int)frameCount);
            RxAudioAvailable?.Invoke(samples, (uint)sampleRate);
            return StreamCallbackResult.Continue;
        }

        private StreamCallbackResult OnOutputCallback(
            IntPtr input, IntPtr output, uint frameCount,
            ref StreamCallbackTimeInfo timeInfo, StreamCallbackFlags statusFlags, IntPtr userData)
        {
            short[] buffer = new short[frameCount];
            txRing.Read(buffer); // silence-fills on underrun — see PcmRingBuffer
            Marshal.Copy(buffer, 0, output, (int)frameCount);
            return StreamCallbackResult.Continue;
        }

        /// <summary>
        /// Play PCM16 samples out of the TX audio device
        /// </summary>
        /// <param name="samples">the sample array</param>
        /// <param name="sourceSampleRate">the samplerate used for the sample array</param>
        /// <exception cref="ArgumentException">if the sample rate does not match the expected sample rate</exception>
        public void PlayTxSamples(short[] samples, int sourceSampleRate)
        {
            if (rxOnly) return;
            if (sourceSampleRate != sampleRate)
            {
                throw new ArgumentException(
                    $"PlayTxSamples got {sourceSampleRate}Hz audio but this LocalAudio " +
                    $"instance is configured for {sampleRate}Hz");
            }
            txRing.Write(samples);
        }
    }
}