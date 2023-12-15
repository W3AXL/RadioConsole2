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

namespace daemon
{
    internal class WebRTC
    {
        private static SDL2AudioSource audioSource = null;
        private static SDL2AudioEndPoint audioEndPoint = null;
        private static RTCPeerConnection pc = null;

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
            IAudioEncoder audioEncoder = new AudioEncoder();
            audioSource = new SDL2AudioSource(Daemon.Config.RxAudioDevice, audioEncoder);

            // Create the audio endpoint
            audioEndPoint = new SDL2AudioEndPoint(Daemon.Config.TxAudioDevice, audioEncoder);

            // Create the PCMU MediaStreamTrack and add it to the peer connection
            MediaStreamTrack audioTrack = new MediaStreamTrack(audioEncoder.SupportedFormats);
            pc.addTrack(audioTrack);

            // Map callbacks
            audioSource.OnAudioSourceEncodedSample += pc.SendAudio;
            pc.OnAudioFormatsNegotiated += (format) => audioSource.SetAudioSourceFormat(format.First());

            // Connection state change callback
            pc.onconnectionstatechange += ConnectionStateChange;

            // Debug Stuff
            pc.OnReceiveReport += (re, media, rr) => Log.Debug($"RTCP Receive for {media} from {re}\n{rr.GetDebugSummary()}");
            pc.OnSendReport += (media, sr) => Log.Debug($"RTCP Send for {media}\n{sr.GetDebugSummary()}");
            pc.GetRtpChannel().OnStunMessageReceived += (msg, ep, isRelay) => Log.Debug($"STUN {msg.Header.MessageType} received from {ep}.");
            pc.oniceconnectionstatechange += (state) => Log.Debug($"ICE connection state change to {state}.");

            return Task.FromResult(pc);
        }

        /// <summary>
        /// Handler for RTC connection state chagne
        /// </summary>
        /// <param name="state">the new connection state</param>
        private static async void ConnectionStateChange(RTCPeerConnectionState state)
        {
            Log.Information($"Peer connection state change to {state}.");

            if (state == RTCPeerConnectionState.failed)
            {
                Log.Error("Peer connection failed");
            }
            else if (state == RTCPeerConnectionState.closed)
            {
                await CloseAudio();
            }
            else if (state == RTCPeerConnectionState.connected)
            {
                await StartAudio();
            }
        }

        private static async Task StartAudio()
        {
            await audioSource.StartAudio();
        }

        private static async Task CloseAudio()
        {
            // Close audio
            await audioSource.CloseAudio();
            // De-init SDL2
            SDL2Helper.QuitSDL();
        }
    }
}
