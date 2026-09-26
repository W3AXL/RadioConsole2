using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Serilog;
using rc2_core;
using Google.Protobuf;
using System.Runtime.InteropServices.Marshalling;

namespace daemon
{
    /// <summary>
    /// Helper class for PortAudio device enumeration for use with configuration validation
    /// and the list-audio command
    /// </summary>
    internal static class Audio
    {
        /// <summary>
        /// Get list of available input device names
        /// </summary>
        /// <returns></returns>
        public static List<string> GetInputDeviceNames() => Enumerate().inputs;
        /// <summary>
        /// Get list of available output device names
        /// </summary>
        /// <returns></returns>
        public static List<string> GetOutputDeviceNames() => Enumerate().outputs;
 
        /// <summary>
        /// Check if the specified input exists in the list of valid inputs
        /// </summary>
        /// <param name="inputName"></param>
        /// <returns></returns>
        public static bool CheckInputExists(string inputName) =>
            GetInputDeviceNames().Any(n => n.Contains(inputName, StringComparison.OrdinalIgnoreCase));
 
        /// <summary>
        /// Check if the specified output exists in the list of valid outputs
        /// </summary>
        /// <param name="outputName"></param>
        /// <returns></returns>
        public static bool CheckOutputExists(string outputName) =>
            GetOutputDeviceNames().Any(n => n.Contains(outputName, StringComparison.OrdinalIgnoreCase));
 
        /// <summary>
        /// Enumerate all available sound devices presented to RtAudio
        /// </summary>
        /// <returns></returns>
        private static (List<string> inputs, List<string> outputs) Enumerate()
        {
            var inputs = new List<string>();
            var outputs = new List<string>();
 
            IntPtr audio = RtAudioNative.CreateAudioInstance();
            if (audio == IntPtr.Zero)
            {
                Log.Logger.Error("Failed to create RtAudio instance for device enumeration");
                return (inputs, outputs);
            }

            try
            {
                RtAudioNative.rtaudio_show_warnings(audio, 0);
                int count = RtAudioNative.rtaudio_device_count(audio);
                for (int i = 0; i < count; i++)
                {
                    uint id = RtAudioNative.rtaudio_get_device_id(audio, i);
                    var info = RtAudioNative.Device.Get(audio, id);
                    if (info.InputChannels > 0) inputs.Add(info.Name);
                    if (info.OutputChannels > 0) outputs.Add(info.Name);
                }
            }
            finally
            {
                RtAudioNative.rtaudio_destroy(audio);
            }
 
            return (inputs, outputs);
        }
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
        /// Pointer to the RTAudio handle
        /// </summary>
        private IntPtr audioHandle = IntPtr.Zero;

        /// <summary>
        /// The ringbuffer for storing outgoing TX audio
        /// </summary>
        private PcmRingBuffer txRing;

        /// <summary>
        /// Callback for RTAudio
        /// </summary>
        private RtAudioNative.AudioCallback nativeCallback;

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
        /// Open and start the duplex RtAudio stream
        /// </summary>
        public void Start()
        {
            // Create a new handle
            audioHandle = RtAudioNative.rtaudio_create(RtAudioNative.RTAUDIO_API_UNSPECIFIED);
            // Make sure we got it
            if (audioHandle == IntPtr.Zero)
            {
                throw new InvalidOperationException("Failed to create RtAudio instance!");
            }
            // Disable warning prints
            RtAudioNative.rtaudio_show_warnings(audioHandle, 0);

            // Get RX device ID
            uint rxId = FindDevice(rxDeviceName, forInput: true);
            // Prepare the RX stream options and store them in a pointer for use directly
            RtAudioNative.StreamParameters inputParams = new RtAudioNative.StreamParameters { device_id = rxId, num_channels = 1, first_channel = 0};
            IntPtr pInputParams = Marshal.AllocHGlobal(Marshal.SizeOf<RtAudioNative.StreamParameters>());
            Marshal.StructureToPtr(inputParams, pInputParams, false);
            
            IntPtr pOutputParams = Marshal.AllocHGlobal(Marshal.SizeOf<RtAudioNative.StreamParameters>());
            // Do the same for tx, if we're not RX only
            if (!rxOnly)
            {
                uint txId = FindDevice(txDeviceName, forInput: false);
                RtAudioNative.StreamParameters outputParams = new RtAudioNative.StreamParameters { device_id = txId, num_channels = 1, first_channel = 0 };
                Marshal.StructureToPtr(outputParams, pOutputParams, false);
            }

            // Set up the audio callback (don't += here since we need it to stay mapped as long as the native library is alive)
            nativeCallback = OnAudioCallback;

            // Starting buffer size for RtAudio, 10ms @ 48kHz
            uint bufferFrames = 480;

            try
            {
                int result = RtAudioNative.rtaudio_open_stream(
                    audioHandle,
                    pOutputParams,
                    pInputParams,
                    RtAudioNative.RTAUDIO_FORMAT_SINT16,
                    (uint)sampleRate,
                    ref bufferFrames,
                    nativeCallback,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero
                );

                if (result != 0)
                {
                    string err = Marshal.PtrToStringAnsi(RtAudioNative.rtaudio_error(audioHandle));
                    throw new InvalidOperationException($"Failed to open RtAudio stream: {err}");
                }
            }
            finally
            {
                // Free our native audio handles regardless of what happened
                if (pInputParams != IntPtr.Zero) Marshal.FreeHGlobal(pInputParams);
                if (pOutputParams != IntPtr.Zero) Marshal.FreeHGlobal(pOutputParams);
            }

            Log.Logger.Debug("Opened RtAudio duplex stream at {rate} Hz, {frames}-frame buffer", sampleRate, bufferFrames);

            // Start the stream
            int startResult = RtAudioNative.rtaudio_start_stream(audioHandle);
            // Check if it worked
            if (startResult != 0)
            {
                string err = Marshal.PtrToStringAnsi(RtAudioNative.rtaudio_error(audioHandle));
                throw new InvalidOperationException($"Failed to start RtAudio stream: {err}");
            }
        }

        /// <summary>
        /// Stop audio to/from the devices
        /// </summary>
        /// <returns></returns>
        public Task Stop()
        {
            // Clean up RtAudio if we still have a handle
            if (audioHandle != IntPtr.Zero)
            {
                RtAudioNative.rtaudio_stop_stream(audioHandle);
                RtAudioNative.rtaudio_close_stream(audioHandle);
                RtAudioNative.rtaudio_destroy(audioHandle);
                audioHandle = IntPtr.Zero;
            }
            Log.Logger.Debug("Local audio device stopped");
            return Task.CompletedTask;
        }

        /// <summary>
        /// Find an audio device with the given name
        /// </summary>
        /// <param name="nameSubstring">the device name</param>
        /// <param name="forInput">whether the device is an input or output</param>
        /// <returns></returns>
        /// <exception cref="ArgumentException">thrown if no device matching the name exists</exception>
        private uint FindDevice(string nameSubstring, bool forInput)
        {
            if (string.IsNullOrEmpty(nameSubstring))
            {
                return forInput
                    ? RtAudioNative.rtaudio_get_default_input_device(audioHandle)
                    : RtAudioNative.rtaudio_get_default_output_device(audioHandle);
            }
 
            int count = RtAudioNative.rtaudio_device_count(audioHandle);
            for (int i = 0; i < count; i++)
            {
                uint id = RtAudioNative.rtaudio_get_device_id(audioHandle, i);
                var info = RtAudioNative.Device.Get(audioHandle, id);
                bool candidate = forInput ? info.InputChannels > 0 : info.OutputChannels > 0;
                if (candidate && info.Name.Contains(nameSubstring, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Logger.Information("Matched {direction} device '{name}' for query '{query}'",
                        forInput ? "input" : "output", info.Name, nameSubstring);
                    return id;
                }
            }

            // Throw an exception if we didn't find the device
            throw new ArgumentException($"Could not find {(forInput ? "input" : "output")} device by name {nameSubstring}");
        }

        /// <summary>
        /// Callback for handling audio to/from RtAudio
        /// </summary>
        /// <param name="output">pointer to output frames to put samples into</param>
        /// <param name="input">pointer to get input frames from</param>
        /// <param name="nFrames">number of frames to process, same for input & output</param>
        /// <param name="streamTime"></param>
        /// <param name="status"></param>
        /// <param name="userData"></param>
        /// <returns></returns>
        private int OnAudioCallback(IntPtr output, IntPtr input, uint nFrames, double streamTime, uint status, IntPtr userData)
        {
            // If we have an input pointer, copy samples
            if (input != IntPtr.Zero)
            {
                short[] samples = new short[nFrames];
                Marshal.Copy(input, samples, 0, (int)nFrames);
                RxAudioAvailable?.Invoke(samples, (uint)sampleRate);
            }
            // Same for the output pointer
            if (output != IntPtr.Zero)
            {
                short[] buffer = new short[nFrames];
                txRing.Read(buffer); // PcmRingBuffer fills silence on underrun
                Marshal.Copy(buffer, 0, output, (int)nFrames);
            }
            // We return 0 to indicate the stream should continue
            return 0;
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