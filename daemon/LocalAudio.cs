using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MathNet.Numerics.Statistics;
using Serilog;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.FFmpeg;
using SIPSorceryMedia.SDL2;

namespace daemon
{
    internal class Audio
    {
        public static bool IsSDLInit = false;
        /// <summary>
        /// Inits SDL2 if necessary
        /// </summary>
        public static void InitSDL()
        {
            if (!IsSDLInit)
            {
                SDL2Helper.InitSDL();
                IsSDLInit = true;
            }
        }

        public static bool CheckInputExists(string inputName)
        {
            InitSDL();
            if (SDL2Helper.GetAudioRecordingDevices().Contains(inputName)) { return true; } else { return false; }
        }

        public static bool CheckOutputExists(string outputName)
        {
            InitSDL();
            if (SDL2Helper.GetAudioPlaybackDevices().Contains(outputName)) { return true; } else { return false; }
        }
    }

    /// <summary>
    /// Local audio class which assists with sending & receiving audio from local SDL2 audio devices to WebRTC endpoints
    /// </summary>
    internal class LocalAudio
    {
        // RX Audio Objects
        private SDL2AudioSource rxSource;
        private AudioEncoder rxEncoder;

        // TX Audio Objects
        private SDL2AudioEndPoint txEndpoint;
        private AudioEncoder txEncoder;

        // Radio to obtain statuses from
        private rc2_core.Radio radio;

        // Whether we're RX-only
        private bool rxOnly;

        // RX audio callback action
        public Action<uint, byte[]> RxEncodedSampleCallback;

        public LocalAudio(string rxDevice, string txDevice, rc2_core.Radio radio, bool rxOnly = false)
        {
            // Store radio
            this.radio = radio;

            // Store RX only
            this.rxOnly = rxOnly;

            // Init SDL2
            SDL2Helper.InitSDL();

            Log.Information("Creating SDL2 local audio devices:");

            // Setup RX audio devices
            rxEncoder = new AudioEncoder();
            rxSource = new SDL2AudioSource(rxDevice, rxEncoder);
            rxSource.OnAudioSourceError += (e) => {
                Log.Error("Got RX audio error: {error}", e);
            };
            // Setup RX sample callback
            rxSource.OnAudioSourceEncodedSample += (uint durationRtpUnits, byte[] samples) => {
                //Log.Verbose("Got {count} encoded RX samples", samples.Length);
                RxEncodedSampleCallback(durationRtpUnits, samples);
            };
            Log.Information("    RX: {rxDevice}", rxDevice);
            // Setup TX audio devices if we aren't rx-only
            if (!rxOnly)
            {
                txEncoder = new AudioEncoder();
                txEndpoint = new SDL2AudioEndPoint(txDevice, txEncoder);
                txEndpoint.OnAudioSinkError += (e) =>
                {
                    Log.Error("Got RX audio error: {error}", e);
                };
                Log.Information("    TX: {txDevice}", txDevice);
            }
        }

        public void Start(AudioFormat audioFormat)
        {
            // Set audio formats
            rxSource.SetAudioSourceFormat(audioFormat);
            if (!rxOnly)
            {
                txEndpoint.SetAudioSinkFormat(audioFormat);
            }
            // Start!
            rxSource.StartAudio();
            if (!rxOnly)
            {
                txEndpoint.StartAudioSink();
            }
            Log.Debug("Audio device(s) started using format {format}/{rate}/{chans}", audioFormat.FormatName, audioFormat.ClockRate, audioFormat.ChannelCount);
        }

        public async Task Stop()
        {
            await rxSource.CloseAudio();
            if (!rxOnly)
            {
                await txEndpoint.CloseAudioSink();
            }
            // De-init SDL2
            SDL2Helper.QuitSDL();
            Log.Debug("Audio devices stopped");
        }

        public void TxAudioCallback(short[] pcm16Samples)
        {
            // Do nothing if we're RX only
            if (rxOnly) { return; }
            // Convert the short[] samples into byte[] samples
            byte[] pcm16Bytes = new byte[pcm16Samples.Length * 2];
            Buffer.BlockCopy(pcm16Samples, 0, pcm16Bytes, 0, pcm16Samples.Length * 2);
            // Send TX samples to the TX audio device
            txEndpoint.GotAudioSample(pcm16Bytes);
        }
    }
}
