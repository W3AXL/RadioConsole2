﻿using Serilog;
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
        
        private static SDL2AudioEndPoint TxEndpoint = null;
        private static AudioEncoder TxEncoder = null;

        private static MediaStreamTrack RtcTrack = null;

        private static RTCPeerConnection pc = null;

        public static string Codec { get; set; } = "G722";

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
            RxSource = new SDL2AudioSource(Daemon.Config.RxAudioDevice, RxEncoder);
            Log.Debug("RX audio using input {RxInput}", Daemon.Config.RxAudioDevice);

            RxSource.OnAudioSourceError += (e) => {
                Log.Error("Got RX source error: {error}", e);
            };

            // TX audio setup
            if (!RxOnly)
            {
                TxEncoder = new AudioEncoder();
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
            

            // Map callbacks
            RxSource.OnAudioSourceEncodedSample += (durationRtpUnits, sample) => {
                //Log.Verbose("Got {numSamples} encoded samples from RX audio source", sample.Length);
                pc.SendAudio(durationRtpUnits, sample);
            };

            pc.OnAudioFormatsNegotiated += (formats) =>
            {
                RxSource.SetAudioSourceFormat(formats.Find(f => f.FormatName == Codec));
                if (!RxOnly)
                    TxEndpoint.SetAudioSinkFormat(formats.Find(f => f.FormatName == Codec));
                Log.Debug("Negotiated audio format {AudioFormat}", formats.Find(f => f.FormatName == Codec).FormatName);
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

            // RTP callback
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
    }
}
