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
        }

        /// <summary>
        /// Start the base radio as well as the SB9600 services
        /// </summary>
        /// <param name="reset"></param>
        public override void Start(bool reset = false)
        {
            Log.Information($"Starting new Motorola XCMP radio instance");
            base.Start(reset);
            xcmp.Connect(underTest: false, waitForInit: true);
            /*if (reset)
                xcmp.ResetRadio();*/
            // Query for display texts
            Status.ZoneName = xcmp.GetDisplayText(DisplayRegion.PRIMARY).Text;
            Status.ChannelName = xcmp.GetDisplayText(DisplayRegion.SECONDARY).Text;
        }

        /// <summary>
        /// Stop the base radio as well as the SB9600 services
        /// </summary>
        public override void Stop()
        {
            Log.Information($"Stopping XCMP radio...");
            base.Stop();
            if (Status.State == RadioState.Transmitting)
                xcmp.Dekey();
            xcmp.Disconnect();
        }

        public override bool ChangeChannel(bool down)
        {
            // TODO
            return false;
        }

        public override bool SetTransmit(bool tx)
        {
            if (tx)
                return xcmp.Keyup();
            else
                return xcmp.Dekey();
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
