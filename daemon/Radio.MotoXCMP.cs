using daemon;
using FFmpeg.AutoGen;
using rc2_core;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using SIPSorceryMedia.Abstractions;
using xcmp;
using xcmp.connection;
using System.ComponentModel.DataAnnotations;
using System.Collections.Concurrent;
using System.Drawing;

namespace moto_xcmp
{
    /// <summary>
    /// Config object used to parse YML config for SB9600 control
    /// </summary>
    public class MotoXcmpConfig
    {
        /// <summary>
        /// Address for the radio's XCMP interface when using IP connection
        /// </summary>
        public IPAddress Address;
        /// <summary>
        /// Port name for the radio's XCMP interface when using serial PPP connection
        /// </summary>
        public string SerialPort;
        /// <summary>
        /// Baudrate to use for the serial connection
        /// </summary>
        public int Baudrate;
        /// <summary>
        /// Path to the pppd binary for use with wvdial
        /// </summary>
        public string PppdPath;
        /// <summary>
        /// List of 4 keys for XCMP authentication
        /// </summary>
        public List<UInt32> XcmpKeys;
        /// <summary>
        /// Delta value for XCMP autnentication
        /// </summary>
        public UInt32 XcmpDelta;
    }

    public class MotoXcmpRadio : rc2_core.Radio
    {
        /// <summary>
        /// The underlying XCMP connection for this radio
        /// </summary>
        private XCMP xcmp;
        /// <summary>
        /// Queue for outgoing XCMP messages
        /// </summary>
        private ConcurrentQueue<XCMP.XcmpMessage> msgQueue = new ConcurrentQueue<XCMP.XcmpMessage>();
        
        /// <summary>
        /// Trackers for the radio's current zone/channel numbers to detect channel changes
        /// </summary>
        private UInt16 zoneNumber;
        private UInt16 chanNumber;

        /// <summary>
        /// Initialize a new XCMP radio connection
        /// </summary>
        /// <param name="name"></param>
        /// <param name="desc"></param>
        /// <param name="rxOnly"></param>
        /// <param name="listenAddress"></param>
        /// <param name="listenPort"></param>
        /// <param name="xcmpConn"></param>
        /// <param name="txAudioCallback"></param>
        /// <param name="txAudioSampleRate"></param>
        /// <param name="rtcFormatCallback"></param>
        /// <param name="softkeys"></param>
        /// <param name="zoneLookups"></param>
        /// <param name="chanLookups"></param>
        public MotoXcmpRadio(
            string name, string desc, bool rxOnly,
            IPAddress listenAddress, int listenPort,
            XCMPBaseConnection xcmpConn,
            Action<short[]> txAudioCallback, int txAudioSampleRate, Action<AudioFormat> rtcFormatCallback,
            List<rc2_core.SoftkeyName> softkeys,
            List<rc2_core.TextLookup> zoneLookups = null, List<rc2_core.TextLookup> chanLookups = null
            ) : base(name, desc, rxOnly, listenAddress, listenPort, softkeys, zoneLookups, chanLookups, txAudioCallback, txAudioSampleRate, rtcFormatCallback)
        {
            // Init XCMP connection
            xcmp = new XCMP(xcmpConn);
            // Bind receive event
            xcmp.OnReceive += onMessage;
        }

        /// <summary>
        /// Start the base radio as well as the SB9600 services
        /// </summary>
        /// <param name="reset"></param>
        public override void Start(bool reset = false)
        {
            Log.Information("Starting new Motorola XCMP radio instance");
            base.Start(reset);
            xcmp.Connect().GetAwaiter().GetResult();
            Log.Debug("XCMP runtime started");
            // Get initial displays
            xcmp.QueryDisplayText(DisplayRegion.PRIMARY).GetAwaiter().GetResult(); // Channel
            xcmp.QueryDisplayText(DisplayRegion.SECONDARY).GetAwaiter().GetResult(); // Zone
            xcmp.QueryDisplayText(DisplayRegion.TERTIARY).GetAwaiter().GetResult(); // Softkeys
        }

        /// <summary>
        /// Stop the base radio as well as the SB9600 services
        /// </summary>
        public override void Stop()
        {
            Log.Information($"Stopping XCMP radio...");
            // Stop base radio
            base.Stop();
            // Dekey if transmitting
            if (Status.State == RadioState.Transmitting)
                xcmp.Dekey().GetAwaiter().GetResult();
            // Disconnect from xcmp
            xcmp.Disconnect().GetAwaiter().GetResult();
        }

        private void onMessage(object sender, XCMP.XcmpMessage msg)
        {
            // Flag that we need to update radio status
            bool updated = false;
            // Handle different XCMP messages
            switch (msg.Opcode)
            {
                // Channel/Zone Select Message
                case Opcode.CHZNSEL:
                    // Decode
                    XCMP.ChanZoneSelectMsg chznsel = new XCMP.ChanZoneSelectMsg(msg);
                    // Upon receipt of a zone/channel broadcast, we query for the updated zone/channel text
                    if (chznsel.MsgType == MsgType.BROADCAST)
                    {
                        if (chznsel.ZoneNumber != zoneNumber)
                        {
                            zoneNumber = chznsel.ZoneNumber;
                            Log.Verbose("Got new zone number {num} from radio, querying for new display text", zoneNumber);
                            xcmp.QueryDisplayText(DisplayRegion.SECONDARY).GetAwaiter().GetResult(); // Zone
                        }
                        if (chznsel.ChanNumber != chanNumber)
                        {
                            chanNumber = chznsel.ChanNumber;
                            Log.Verbose("Got new channel number {num} from radio, querying for new display text", chanNumber);
                            xcmp.QueryDisplayText(DisplayRegion.PRIMARY).GetAwaiter().GetResult(); // Channel
                        }
                    }
                    break;
                case Opcode.DISPTXT:
                    // Decode
                    XCMP.DisplayTextMsg dispMsg = new XCMP.DisplayTextMsg(msg);
                    // Obtain display text from a response or broadcast
                    if (dispMsg.MsgType == MsgType.RESPONSE || dispMsg.MsgType == MsgType.BROADCAST)
                    {
                        // Get text with whitespace stripped
                        string text = dispMsg.Text.Replace("\u0000","").Trim();
                        // Channel Name
                        if (dispMsg.Region == DisplayRegion.PRIMARY)
                        {
                            if (Status.ChannelName != text)
                            {
                                Log.Information("Got new channel name: {name}", text);
                                Status.ChannelName = text;
                                updated = true;
                            }
                        }
                        // Zone Name
                        else if (dispMsg.Region == DisplayRegion.SECONDARY)
                        {
                            if (Status.ZoneName != text)
                            {
                                Log.Information("Got new zone name: {name}", text);
                                Status.ZoneName = text;
                                updated = true;
                            }
                        }
                        // Softkeys
                        else if (dispMsg.Region == DisplayRegion.TERTIARY)
                        {
                            
                        }
                        else
                        {
                            Log.Warning("XCMP: Got unhandled display region {region}", dispMsg.Region);
                        }
                    }
                    break;
                default:
                    Log.Warning("Radio.MotoXCMP Unhandled XCMP message opcode {name} ({opcode:X})", Enum.GetName(msg.Opcode), msg.Opcode);
                    break;
            }
            // Send a status update if needed
            if (updated)
                StatusCallback();
        }

        public override bool ChangeChannel(bool down)
        {
            return xcmp.ChangeChannel(down).GetAwaiter().GetResult();
        }

        public override bool SetTransmit(bool tx)
        {
            if (tx)
                return xcmp.Keyup().GetAwaiter().GetResult();
            else
                return xcmp.Dekey().GetAwaiter().GetResult();
        }

        public override bool PressButton(rc2_core.SoftkeyName name)
        {
            // TODO
            return false;
        }

        public override bool ReleaseButton(rc2_core.SoftkeyName name)
        {
            // TODO
            return false;
        }
    }
}
