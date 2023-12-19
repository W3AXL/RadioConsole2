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

namespace daemon
{
    internal class WebRTC
    {
        private static SDL2AudioSource RxSource = null;
        private static AudioEncoder RxEncoder = null;
        private static MediaStreamTrack RxTrack = null;
        private static SDL2AudioEndPoint TxEndpoint = null;
        private static AudioEncoder TxEncoder = null;
        
        private static RTCPeerConnection pc = null;

        public static string Codec { get; set; } = "PCMU";

        public static Task<RTCPeerConnection> CreatePeerConnection()
        {
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
            RxSource = new SDL2AudioSource(Daemon.Config.RxAudioDevice, RxEncoder);
            RxTrack = new MediaStreamTrack(RxEncoder.SupportedFormats);
            Log.Debug("RX audio using input {RxInput}", Daemon.Config.RxAudioDevice);

            // TX audio setup
            TxEncoder = new AudioEncoder();
            TxEndpoint = new SDL2AudioEndPoint(Daemon.Config.TxAudioDevice, TxEncoder);
            Log.Debug("TX audio using output {TxOutput}", Daemon.Config.TxAudioDevice);
            
            Log.Debug("Created SDL2 audio sources/sinks and encoder");

            // Add the RX track to the peer connection
            pc.addTrack(RxTrack);
            Log.Debug("Added RX audio track to peer connection");

            // Map callbacks
            RxSource.OnAudioSourceEncodedSample += (durationRtpUnits, sample) => {
                Log.Verbose("Got {numSamples} encoded samples from RX audio source", sample.Length);
                pc.SendAudio(durationRtpUnits, sample);
            };

            pc.OnAudioFormatsNegotiated += (formats) =>
            {

                Log.Verbose("Available audio formats:");
                foreach (var format in formats)
                {
                    Log.Verbose("{FormatName}", format.FormatName);
                }
                RxSource.SetAudioSourceFormat(formats.Find(f => f.FormatName == Codec));
                TxEndpoint.SetAudioSinkFormat(formats.Find(f => f.FormatName == Codec));
                Log.Debug("Negotiated audio format {AudioFormat}", formats.Find(f => f.FormatName == Codec).FormatName);
            };

            // Connection state change callback
            pc.onconnectionstatechange += ConnectionStateChange;

            // Debug Stuff
            pc.OnReceiveReport += (re, media, rr) => Log.Verbose("RTCP Receive for {Media} from {RE}\n{Report}", media, re, rr.GetDebugSummary());
            pc.OnSendReport += (media, sr) => Log.Verbose("RTCP Send for {Media}\n{Summary}", media, sr.GetDebugSummary());
            pc.GetRtpChannel().OnStunMessageReceived += (msg, ep, isRelay) =>
            {
                Log.Verbose("STUN {MessageType} received from {Endpoint}.", msg.Header.MessageType, ep);
                //Log.Verbose(msg.ToString());
            };
            pc.oniceconnectionstatechange += (state) => Log.Verbose("ICE connection state change to {ICEState}.", state);

            // RTP callback
            pc.OnRtpPacketReceived += (IPEndPoint rep, SDPMediaTypesEnum media, RTPPacket rtpPkt) =>
            {
                if (media == SDPMediaTypesEnum.audio)
                {
                    //Log.Verbose("Got RTP audio from console ({length}-byte payload)", rtpPkt.Payload.Length);
                    TxEndpoint.GotAudioRtp(
                        rep, 
                        rtpPkt.Header.SyncSource, 
                        rtpPkt.Header.SequenceNumber, 
                        rtpPkt.Header.Timestamp, 
                        rtpPkt.Header.PayloadType, 
                        rtpPkt.Header.MarkerBit == 1,
                        rtpPkt.Payload
                    );
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
            await TxEndpoint.StartAudioSink();
            Log.Debug("Audio started");
        }

        private static async Task CloseAudio()
        {
            // Close audio
            await RxSource.CloseAudio();
            await TxEndpoint.CloseAudioSink();
            // De-init SDL2
            SDL2Helper.QuitSDL();
            Log.Debug("SDL2 audio closed");
        }
    }
}
