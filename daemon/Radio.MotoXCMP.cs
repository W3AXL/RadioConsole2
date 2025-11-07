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
        public IPAddress xcmpAddress;
        /// <summary>
        /// Port name for the radio's XCMP interface when using serial PPP connection
        /// </summary>
        public string xcmpSerialPort;
    }

    public class MotoXcmpRadio : rc2_core.Radio
    {
        /// <summary>
        /// The underlying XCMP connection for this radio
        /// </summary>
        private XCMP xcmp;

        /// <summary>
        /// Initialize a new XCMP radio connection using IP
        /// </summary>
        /// <param name="name"></param>
        /// <param name="desc"></param>
        /// <param name="rxOnly"></param>
        /// <param name="listenAddress"></param>
        /// <param name="listenPort"></param>
        /// <param name="xcmpHostname"></param>
        /// <param name="xcmpPort"></param>
        /// <param name="txAudioCallback"></param>
        /// <param name="txAudioSampleRate"></param>
        /// <param name="rtcFormatCallback"></param>
        /// <param name="softkeys"></param>
        /// <param name="zoneLookups"></param>
        /// <param name="chanLookups"></param>
        public MotoXcmpRadio(
            string name, string desc, bool rxOnly,
            IPAddress listenAddress, int listenPort,
            string xcmpHostname, int xcmpPort,
            Action<short[]> txAudioCallback, int txAudioSampleRate, Action<AudioFormat> rtcFormatCallback,
            List<rc2_core.SoftkeyName> softkeys,
            List<rc2_core.TextLookup> zoneLookups = null, List<rc2_core.TextLookup> chanLookups = null
            ) : base(name, desc, rxOnly, listenAddress, listenPort, softkeys, zoneLookups, chanLookups, txAudioCallback, txAudioSampleRate, rtcFormatCallback)
        {
            // Init XCMP IP connection
            XCMPIPConnection xcmpConn = new XCMPIPConnection(xcmpHostname, xcmpPort);
            xcmp = new XCMP(xcmpConn);
        }

        /// <summary>
        /// Initialize a new XCMP connection using serial PPP
        /// </summary>
        /// <param name="name"></param>
        /// <param name="desc"></param>
        /// <param name="rxOnly"></param>
        /// <param name="listenAddress"></param>
        /// <param name="listenPort"></param>
        /// <param name="xcmpSerialPort"></param>
        /// <param name="txAudioCallback"></param>
        /// <param name="txAudioSampleRate"></param>
        /// <param name="rtcFormatCallback"></param>
        /// <param name="softkeys"></param>
        /// <param name="zoneLookups"></param>
        /// <param name="chanLookups"></param>
        public MotoXcmpRadio(
            string name, string desc, bool rxOnly,
            IPAddress listenAddress, int listenPort,
            string xcmpSerialPort,
            Action<short[]> txAudioCallback, int txAudioSampleRate, Action<AudioFormat> rtcFormatCallback,
            List<rc2_core.SoftkeyName> softkeys,
            List<rc2_core.TextLookup> zoneLookups = null, List<rc2_core.TextLookup> chanLookups = null
            ) : base(name, desc, rxOnly, listenAddress, listenPort, softkeys, zoneLookups, chanLookups, txAudioCallback, txAudioSampleRate, rtcFormatCallback)
        {
            // Init XCMP IP connection
            XCMPPPPConnection xcmpConn = new XCMPPPPConnection(xcmpSerialPort);
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
            xcmp.Connect();
            if (reset)
                xcmp.ResetRadio();
        }

        /// <summary>
        /// Stop the base radio as well as the SB9600 services
        /// </summary>
        public new void Stop()
        {
            base.Stop();
            xcmp.Disconnect();
        }

        public override bool ChangeChannel(bool down)
        {
            // TODO
            return false;
        }

        public override bool SetTransmit(bool tx)
        {
            // TODO
            return false;
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
