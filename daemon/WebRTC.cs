using Serilog;
using SIPSorcery.Net;
using SIPSorcery.Media;
using SIPSorceryMedia.SDL2;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SIPSorceryMedia.Abstractions;
using netcore_cli;
using MathNet.Numerics.Statistics;

namespace daemon
{
    internal class WebRTC
    {
        private static SDL2AudioSource audioSource = null;
        private static SDL2AudioEndPoint audioEndPoint = null;
        private static RTCPeerConnection pc = null;

        private static AudioEncoder audioEncoder = null;
        private static OpusAudioEncoder opusEncoder = null;
        private static MediaStreamTrack audioTrack = null;

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

            // Create the audio encoder and source
            if (Codec == "opus")
            {
                opusEncoder = new OpusAudioEncoder();
                audioSource = new SDL2AudioSource(Daemon.Config.RxAudioDevice, opusEncoder);
                audioEndPoint = new SDL2AudioEndPoint(Daemon.Config.TxAudioDevice, opusEncoder);
                audioTrack = new MediaStreamTrack(opusEncoder.SupportedFormats);
            }
            else
            {
                audioEncoder = new AudioEncoder();
                audioSource = new SDL2AudioSource(Daemon.Config.RxAudioDevice, audioEncoder);
                audioEndPoint = new SDL2AudioEndPoint(Daemon.Config.TxAudioDevice, audioEncoder);
                audioTrack = new MediaStreamTrack(audioEncoder.SupportedFormats);
            }
            
            Log.Debug("Created SDL2 audio sources/sinks and encoder");

            // Add the track to the peer connection
            pc.addTrack(audioTrack);
            Log.Debug("Added audio track to peer connection");

            // Map callbacks
            audioSource.OnAudioSourceEncodedSample += pc.SendAudio;
            pc.OnAudioFormatsNegotiated += (formats) =>
            {

                Log.Verbose("Available audio formats:");
                foreach (var format in formats)
                {
                    Log.Verbose("{FormatName}", format.FormatName);
                }
                // We prefer G722 for now
                audioSource.SetAudioSourceFormat(formats.Find(f => f.FormatName == Codec));
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

        private static async Task StartAudio()
        {
            await audioSource.StartAudio();
            await audioEndPoint.StartAudioSink();
            Log.Debug("Audio started");
        }

        private static async Task CloseAudio()
        {
            // Close audio
            await audioSource.CloseAudio();
            await audioEndPoint.CloseAudioSink();
            // De-init SDL2
            SDL2Helper.QuitSDL();
            Log.Debug("SDL2 audio closed");
        }
    }
}
