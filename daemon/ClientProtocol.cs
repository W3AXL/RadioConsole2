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
using MathNet.Numerics.Statistics;

namespace daemon
{
    internal class DaemonWebsocket
    {
        public static WebSocketServer Wss {  get; set; }

        public static Radio radio { get; set; }

        public static void StartWsServer()
        {
            Log.Information("Starting websocket server on {IP}:{Port}", Daemon.Config.DaemonIP, Daemon.Config.DaemonPort);
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

        public static void SendRadioStatus()
        {
            string statusJson = radio.Status.Encode();
            Log.Debug("Sending radio status via websocket");
            Log.Verbose(statusJson);
            SendClientMessage("{\"status\": " + statusJson + " }");
        }

        public static void SendClientMessage(string msg)
        {
            Wss.WebSocketServices["/"].Sessions.Broadcast(msg);
        }

        public static void SendAck()
        {
            Wss.WebSocketServices["/"].Sessions.Broadcast("{\"ack\": {}}");
        }

        public static void SendNack()
        {
            Wss.WebSocketServices["/"].Sessions.Broadcast("{\"nack\": {}}");
        }
    }

    internal class ClientProtocol : WebSocketBehavior
    {
        protected override void OnMessage(MessageEventArgs e)
        {
            var msg = e.Data;
            Serilog.Log.Verbose("Got client message from websocket: {WSMessage}", msg);
            dynamic jsonObj = JsonConvert.DeserializeObject(msg);
            // Handle commands
            if (jsonObj.ContainsKey("radio"))
            {
                // Radio Status Query
                if (jsonObj.radio.command == "query")
                {
                    DaemonWebsocket.SendRadioStatus();
                }
                // Radio Start Transmit Command
                else if (jsonObj.radio.command == "startTx")
                {
                    if (DaemonWebsocket.radio.SetTransmit(true))
                        DaemonWebsocket.SendAck();
                    else
                        DaemonWebsocket.SendNack();
                }
                // Radio Stop Transmit Command
                else if (jsonObj.radio.command == "stopTx")
                {
                    if (DaemonWebsocket.radio.SetTransmit(false))
                        DaemonWebsocket.SendAck();
                    else
                        DaemonWebsocket.SendNack();
                }
                // Channel Up/Down
                else if (jsonObj.radio.command == "chanUp")
                {
                    if (DaemonWebsocket.radio.ChangeChannel(false))
                        DaemonWebsocket.SendAck();
                    else
                        DaemonWebsocket.SendNack();
                }
                else if (jsonObj.radio.command == "chanDn")
                {
                    if (DaemonWebsocket.radio.ChangeChannel(true))
                        DaemonWebsocket.SendAck();
                    else
                        DaemonWebsocket.SendNack();
                }
            }
        }

        protected override void OnClose(CloseEventArgs e)
        {
            Serilog.Log.Warning("Websocket connection closed!");
            WebRTC.Stop("Websocket closed");
            DaemonWebsocket.radio.Stop();
        }
    }
}
