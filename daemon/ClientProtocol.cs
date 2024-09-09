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
            // Keeps the thing alive
            Wss.KeepClean = false;
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

        public static void SendAck(String cmd = "")
        {
            Wss.WebSocketServices["/"].Sessions.Broadcast($"{{\"ack\": \"{cmd}\"}}");
        }

        public static void SendNack(String cmd = "")
        {
            Wss.WebSocketServices["/"].Sessions.Broadcast($"{{\"nack\": \"{cmd}\"}}");
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
                        DaemonWebsocket.SendAck("startTx");
                    else
                        DaemonWebsocket.SendNack("startTx");
                }
                // Radio Stop Transmit Command
                else if (jsonObj.radio.command == "stopTx")
                {
                    if (DaemonWebsocket.radio.SetTransmit(false))
                        DaemonWebsocket.SendAck("stopTx");
                    else
                        DaemonWebsocket.SendNack("stopTx");
                }
                // Channel Up/Down
                else if (jsonObj.radio.command == "chanUp")
                {
                    if (DaemonWebsocket.radio.ChangeChannel(false))
                        DaemonWebsocket.SendAck("chanUp");
                    else
                        DaemonWebsocket.SendNack("chanUp");
                }
                else if (jsonObj.radio.command == "chanDn")
                {
                    if (DaemonWebsocket.radio.ChangeChannel(true))
                        DaemonWebsocket.SendAck("chanDn");
                    else
                        DaemonWebsocket.SendNack("chanDn");
                }
                // Button press/release
                else if (jsonObj.radio.command == "buttonPress")
                {
                    if (DaemonWebsocket.radio.PressButton((SoftkeyName)Enum.Parse(typeof(SoftkeyName),(string)jsonObj.radio.options)))
                        DaemonWebsocket.SendAck("buttonPress");
                    else
                        DaemonWebsocket.SendNack("buttonPress");
                }
                else if (jsonObj.radio.command == "buttonRelease")
                {
                    if (DaemonWebsocket.radio.ReleaseButton((SoftkeyName)Enum.Parse(typeof(SoftkeyName),(string)jsonObj.radio.options)))
                        DaemonWebsocket.SendAck("buttonRelease");
                    else
                        DaemonWebsocket.SendNack("buttonRelease");
                }
                // Reset
                else if (jsonObj.radio.command == "reset")
                {
                    Serilog.Log.Information("Resetting and restarting radio interface");
                    // Stop
                    DaemonWebsocket.radio.Stop();
                    // Restart with reset
                    DaemonWebsocket.radio.Start(false);
                }
            }
        }

        protected override void OnClose(CloseEventArgs e)
        {
            Serilog.Log.Warning("Websocket connection closed: {args}", e.Reason);
            WebRTC.Stop("Websocket closed");
            //DaemonWebsocket.radio.Stop();
        }

        protected override void OnError(WebSocketSharp.ErrorEventArgs e)
        {
            Serilog.Log.Error("Websocket encountered an error! {error}", e.Message);
        }
    }
}
