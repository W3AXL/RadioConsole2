using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Serilog;
using SIPSorcery.Net;
using SIPSorcery.Media;
using SIPSorceryMedia.SDL2;
using WebSocketSharp.Server;
using SIPSorceryMedia.Abstractions;
using WebSocketSharp;
using netcore_cli;
using Newtonsoft.Json;

namespace daemon
{
    internal class DaemonWebsocket
    {
        public static WebSocketServer Wss {  get; set; }

        public static void StartWsServer()
        {
            Log.Information($"Starting websocket server on {Daemon.Config.DaemonIP}:{Daemon.Config.DaemonPort}");
            Wss = new WebSocketServer(Daemon.Config.DaemonIP, Daemon.Config.DaemonPort);   // May need to set up SSL later
            // Set up the WebRTC handler
            Wss.AddWebSocketService<WebRTCWebSocketPeer>("/rtc", (peer) => peer.CreatePeerConnection = WebRTC.CreatePeerConnection);
            // Set up the regular message handler
            Wss.AddWebSocketService<ClientProtocol>("/");
            // Start the service
            Wss.Start();
        }

        public static void StopWsServer()
        {
            Wss.Stop();
        }

        public static void SendRadioStatus(RadioStatus status)
        {
            SendClientMessage(status.Encode());
        }

        public static void SendClientMessage(string msg)
        {
            Wss.WebSocketServices["/"].Sessions.Broadcast(msg);
        }
    }

    internal class ClientProtocol : WebSocketBehavior
    {
        protected override void OnMessage(MessageEventArgs e)
        {
            var msg = e.Data;
            Serilog.Log.Debug($"Got client message from websocket: {msg}");
        }
    }
}
