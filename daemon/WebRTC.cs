using Serilog;
using SIPSorcery.Net;
using SIPSorcery.Media;
using SIPSorceryMedia.SDL2;
using SIPSorceryMedia.FFmpeg;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SIPSorceryMedia.Abstractions;
using netcore_cli;
using MathNet.Numerics.Statistics;
using System.Net;
using NAudio;
using NAudio.Wave;
using NAudio.Utils;
using NAudio.Wave.SampleProviders;

namespace daemon
{
    internal class WebRTC
    {
        // Objects for RX audio processing
        private static SDL2AudioSource RxSource = null;
        private static AudioEncoder RxEncoder = null;
        private static AudioFormat RxFormat = AudioFormat.Empty;
        
        // Objects for TX audio processing
        private static SDL2AudioEndPoint TxEndpoint = null;
        private static AudioEncoder TxEncoder = null;
        private static AudioFormat TxFormat = AudioFormat.Empty;
        
        // We make separate encoders for recording since some codecs can be time-variant
        private static AudioEncoder RecRxEncoder = null;
        private static AudioEncoder RecTxEncoder = null;

        // Objects for TX/RX audio recording
        public static bool Record = false;          // Whether or not recording to audio files is enabled
        public static string RecPath = null;        // Folder to store recordings
        public static string RecTsFmt = "yyyy-MM-dd_HHmmss";       // Timestamp format string
        public static bool RecTxInProgress = false;   // Flag to indicate if a file is currently being recorded
        public static bool RecRxInProgress = false;
        private static float recRxGain = 1;
        private static float recTxGain = 1;

        // Recording format (TODO: Make configurable)
        private static WaveFormat recFormat = null;
        // Output wave file writers
        private static WaveFileWriter recTxWriter = null;
        private static WaveFileWriter recRxWriter = null;

        // WebRTC variables
        private static MediaStreamTrack RtcTrack = null;
        private static RTCPeerConnection pc = null;
        public static string Codec { get; set; } = "G722";

        // Flag whether our radio is RX only
        public static bool RxOnly {get; set;} = false;

        public static Task<RTCPeerConnection> CreatePeerConnection()
        {
            Log.Debug("New client connected to RTC endpoint, creating peer connection");
            // Create RTC configuration and peer connection
            RTCConfiguration config = new RTCConfiguration
            {
            };
            pc = new RTCPeerConnection(config);

            // Init SDL2
            SDL2Helper.InitSDL();
            Log.Debug("SDL2 init done");

            // RX audio setup
            RxEncoder = new AudioEncoder();
            RecRxEncoder = new AudioEncoder();
            RxSource = new SDL2AudioSource(Daemon.Config.RxAudioDevice, RxEncoder);
            Log.Debug("RX audio using input {RxInput}", Daemon.Config.RxAudioDevice);

            RxSource.OnAudioSourceError += (e) => {
                Log.Error("Got RX source error: {error}", e);
            };

            // TX audio setup
            if (!RxOnly)
            {
                TxEncoder = new AudioEncoder();
                RecTxEncoder = new AudioEncoder();
                TxEndpoint = new SDL2AudioEndPoint(Daemon.Config.TxAudioDevice, TxEncoder);
                Log.Debug("TX audio using output {TxOutput}", Daemon.Config.TxAudioDevice);

                TxEndpoint.OnAudioSinkError += (e) => {
                    Log.Error("Got TX endpoint error: {error}", e);
                };
            }
            else
            {
                Log.Warning("RX only radio defined, skipping TX audio setup");
            }
            
            
            Log.Debug("Created SDL2 audio sources/sinks and encoder");

            Log.Verbose("Client supported formats:");
            foreach (var format in RxEncoder.SupportedFormats)
            {
                Log.Verbose("{FormatName}", format.FormatName);
            }

            // Add the RX track to the peer connection
            if (!RxEncoder.SupportedFormats.Any(f => f.FormatName == Codec))
            {
                Log.Error("Specified format {SpecFormat} not supported by audio encoder!", Codec);
                throw new ArgumentException("Invalid codec specified!");
            }
            if (!RxOnly)
            {
                RtcTrack = new MediaStreamTrack(RxEncoder.SupportedFormats.Find(f => f.FormatName == Codec), MediaStreamStatusEnum.SendRecv);
                Log.Debug("Added send/recv audio track to peer connection");
            } 
            else
            {
                RtcTrack = new MediaStreamTrack(RxEncoder.SupportedFormats.Find(f => f.FormatName == Codec), MediaStreamStatusEnum.SendOnly);
                Log.Debug("Added send-only audio track to peer connection");
            }
            pc.addTrack(RtcTrack);
            

            // RX Audio Sample Callback
            RxSource.OnAudioSourceEncodedSample += (durationRtpUnits, samples) => {
                //Log.Verbose("Got {numSamples} encoded samples from RX audio source", sample.Length);
                pc.SendAudio(durationRtpUnits, samples);
                // Optional write to file
                if (Record && recRxWriter != null)
                {
                    // Decode samples to pcm
                    short[] pcmSamples = RecRxEncoder.DecodeAudio(samples, RxFormat);
                    // Convert to float s16
                    float[] s16Samples = new float[pcmSamples.Length];
                    for (int n = 0; n < pcmSamples.Length; n++)
                    {
                        s16Samples[n] = pcmSamples[n] / 32768f * recRxGain;
                    }
                    // Add to buffer
                    recRxWriter.WriteSamples(s16Samples, 0, s16Samples.Length);
                }
            };

            // Audio format negotiation callback
            pc.OnAudioFormatsNegotiated += (formats) =>
            {
                // Get the format
                RxFormat = formats.Find(f => f.FormatName == Codec);
                // Set the source to use the format
                RxSource.SetAudioSourceFormat(RxFormat);
                Log.Debug("Negotiated RX audio format {AudioFormat} ({ClockRate}/{Chs})", RxFormat.FormatName, RxFormat.ClockRate, RxFormat.ChannelCount);
                // Set our wave and buffer writers to the proper sample rate
                recFormat = new WaveFormat(RxFormat.ClockRate, 16, 1);
                if (!RxOnly)
                {
                    TxFormat = formats.Find(f => f.FormatName == Codec);
                    TxEndpoint.SetAudioSinkFormat(TxFormat);
                    Log.Debug("Negotiated TX audio format {AudioFormat} ({ClockRate}/{Chs})", TxFormat.FormatName, TxFormat.ClockRate, TxFormat.ChannelCount);
                }
            };

            // Connection state change callback
            pc.onconnectionstatechange += ConnectionStateChange;

            // Debug Stuff
            pc.OnReceiveReport += (re, media, rr) => Log.Verbose("RTCP report received {Media} from {RE}\n{Report}", media, re, rr.GetDebugSummary());
            pc.OnSendReport += (media, sr) => Log.Verbose("RTCP report sent for {Media}\n{Summary}", media, sr.GetDebugSummary());
            pc.GetRtpChannel().OnStunMessageSent += (msg, ep, isRelay) =>
            {
                Log.Verbose("STUN {MessageType} sent to {Endpoint}.", msg.Header.MessageType, ep);
            };
            pc.GetRtpChannel().OnStunMessageReceived += (msg, ep, isRelay) =>
            {
                Log.Verbose("STUN {MessageType} received from {Endpoint}.", msg.Header.MessageType, ep);
                //Log.Verbose(msg.ToString());
            };
            pc.oniceconnectionstatechange += (state) => Log.Verbose("ICE connection state change to {ICEState}.", state);

            // RTP Samples callback
            pc.OnRtpPacketReceived += (IPEndPoint rep, SDPMediaTypesEnum media, RTPPacket rtpPkt) =>
            {
                if (media == SDPMediaTypesEnum.audio)
                {
                    //Log.Verbose("Got RTP audio from {Endpoint} - ({length}-byte payload)", rep.ToString(), rtpPkt.Payload.Length);
                    if (!RxOnly)
                        TxEndpoint.GotAudioRtp(
                            rep, 
                            rtpPkt.Header.SyncSource, 
                            rtpPkt.Header.SequenceNumber, 
                            rtpPkt.Header.Timestamp, 
                            rtpPkt.Header.PayloadType, 
                            rtpPkt.Header.MarkerBit == 1,
                            rtpPkt.Payload
                        );
                    // Save TX audio to file, if we're supposed to and the file is open
                    if (Record && recTxWriter != null)
                    {
                        // Get samples
                        byte[] samples = rtpPkt.Payload;
                        // Decode samples
                        short[] pcmSamples = RecTxEncoder.DecodeAudio(samples, TxFormat);
                        // Convert to float s16
                        float[] s16Samples = new float[pcmSamples.Length];
                        for (int n = 0; n < pcmSamples.Length; n++)
                        {
                            s16Samples[n] = pcmSamples[n] / 32768f * recTxGain;
                        }
                        // Add to buffer
                        recTxWriter.WriteSamples(s16Samples, 0, s16Samples.Length);
                    }
                }
            };

            return Task.FromResult(pc);
        }

        /// <summary>
        /// Handler for RTC connection state chagne
        /// </summary>
        /// <param name="state">the new connection state</param>
        private static async void ConnectionStateChange(RTCPeerConnectionState state)
        {
            Log.Information("Peer connection state change to {PCState}.", state);

            if (state == RTCPeerConnectionState.failed)
            {
                Log.Error("Peer connection failed");
                Log.Debug("Closing peer connection");
                pc.Close("Connection failed");
            }
            else if (state == RTCPeerConnectionState.closed)
            {
                Log.Debug("Closing audio");
                await CloseAudio();
            }
            else if (state == RTCPeerConnectionState.connected)
            {
                Log.Debug("Starting audio");
                await StartAudio();
            }
        }

        public static void Stop(string reason)
        {
            Log.Warning("Stopping WebRTC with reason {Reason}", reason);
            pc.Close(reason);
        }

        private static async Task StartAudio()
        {
            await RxSource.StartAudio();
            if (!RxOnly)
                await TxEndpoint.StartAudioSink();
            Log.Debug("Audio started");
        }

        private static async Task CloseAudio()
        {
            // Close audio
            await RxSource.CloseAudio();
            if (!RxOnly)
                await TxEndpoint.CloseAudioSink();
            // De-init SDL2
            SDL2Helper.QuitSDL();
            Log.Debug("SDL2 audio closed");
        }

        /// <summary>
        /// Start a wave recording with the specified file prefix
        /// </summary>
        /// <param name="prefix">filename prefix, appended with timestamp</param>
        public static void RecStartTx(string name)
        {
            // Only create a new file if recording is enabled
            if (Record && !RecTxInProgress)
            {
                // Get full filepath
                string filename = $"{RecPath}/{DateTime.Now.ToString(RecTsFmt)}_{name.Replace(' ', '_')}_TX.wav";
                // Create writer
                recTxWriter = new WaveFileWriter(filename, recFormat);
                Log.Debug("Starting new TX recording: {file}", filename);
                // Set Flag
                RecTxInProgress = true;
            }
        }

        public static void RecStartRx(string name)
        {
            // Only create a new file if recording is enabled
            if (Record && !RecRxInProgress)
            {
                // Get full filepath
                string filename = $"{RecPath}/{DateTime.Now.ToString(RecTsFmt)}_{name.Replace(' ', '_')}_RX.wav";
                // Create writer
                recRxWriter = new WaveFileWriter(filename, recFormat);
                Log.Debug("Starting new RX recording: {file}", filename);
                // Set Flag
                RecRxInProgress = true;
            }
        }

        /// <summary>
        /// Stop a wave recording
        /// </summary>
        public static void RecStop()
        {
            if (recTxWriter != null)
            {
                recTxWriter.Close();
                recTxWriter = null;
            }
            if (recRxWriter != null)
            {
                recRxWriter.Close();
                recRxWriter = null;
            }
            RecTxInProgress = false;
            RecRxInProgress = false;
            Log.Debug("Stopped recording");
        }

        public static void SetRecGains(double rxGainDb, double txGainDb)
        {
            recRxGain = (float)Math.Pow(10, rxGainDb/20);
            recTxGain = (float)Math.Pow(10, txGainDb/20);
        }
    }
}
