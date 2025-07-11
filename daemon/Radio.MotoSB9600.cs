﻿using daemon;
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

namespace moto_sb9600
{
    /// <summary>
    /// Config object used to parse YML config for SB9600 control
    /// </summary>
    public class MotoSb9600Config
    {
        /// <summary>
        /// Serial port name
        /// </summary>
        public string SerialPort = "";
        /// <summary>
        /// SB9600 control head type
        /// </summary>
        public SB9600.HeadType ControlHeadType = SB9600.HeadType.W9;
        /// <summary>
        /// Whether to use RX LEDs as an additional RX state trigger
        /// </summary>
        public bool UseLedsForRx = false;
        /// <summary>
        /// Softkey binding dictionary
        /// </summary>
        public Dictionary<ControlHeads.ButtonName, SoftkeyName> SoftkeyBindings;
    }

    public class MotoSb9600Radio : rc2_core.Radio
    {
        private SB9600 sb9600;

        private Dictionary<ControlHeads.ButtonName, SoftkeyName> softkeyBindings;

        /// <summary>
        /// Initialize a new Motorola SB9600 radio
        /// </summary>
        /// <param name="name">Radio name</param>
        /// <param name="desc">Radio description</param>
        /// <param name="rxOnly">Whether radio is rx-only or not</param>
        /// <param name="listenAddress">daemon listen address</param>
        /// <param name="listenPort">daemon list port</param>
        /// <param name="serialPortName">Serial port name for SB9600</param>
        /// <param name="headType">SB9600 head type</param>
        /// <param name="useLedsForRx">Whether to use the RX leds on the control head as an RX status indicator</param>
        /// <param name="softkeys">list of softkeys</param>
        /// <param name="zoneLookups">list of zone text lookups</param>
        /// <param name="chanLookups">list of channel text lookups</param>
        /// <param name="txAudioCallback">callback for tx audio samples</param>
        /// <param name="txAudioSampleRate">samplerate for tx audio</param>
        public MotoSb9600Radio(
            string name, string desc, bool rxOnly,
            IPAddress listenAddress, int listenPort,
            string serialPortName, SB9600.HeadType headType, bool useLedsForRx, Dictionary<ControlHeads.ButtonName, SoftkeyName> softkeyBindings,
            Action<short[]> txAudioCallback, int txAudioSampleRate, Action<AudioFormat> rtcFormatCallback,
            List<rc2_core.SoftkeyName> softkeys,
            List<rc2_core.TextLookup> zoneLookups = null, List<rc2_core.TextLookup> chanLookups = null
            ) : base(name, desc, rxOnly, listenAddress, listenPort, softkeys, zoneLookups, chanLookups, txAudioCallback, txAudioSampleRate, rtcFormatCallback)
        {
            // Save softkey lookups
            this.softkeyBindings = softkeyBindings;
            // Init SB9600
            sb9600 = new SB9600(serialPortName, headType, this.softkeyBindings, this, useLedsForRx);
            sb9600.StatusCallback += () => {
                this.RadioStatusCallback();
            };
        }

        /// <summary>
        /// Start the base radio as well as the SB9600 services
        /// </summary>
        /// <param name="reset"></param>
        public override void Start(bool reset = false)
        {
            Log.Information($"Starting new Motorola SB9600 radio instance");
            base.Start(reset);
            sb9600.Start(reset);
        }

        /// <summary>
        /// Stop the base radio as well as the SB9600 services
        /// </summary>
        public new void Stop()
        {
            base.Stop();
            sb9600.Stop();
        }

        public override bool ChangeChannel(bool down)
        {
            return sb9600.ChangeChannel(down);
        }

        public override bool SetTransmit(bool tx)
        {
            return sb9600.SetTransmit(tx);
        }

        public override bool PressButton(rc2_core.SoftkeyName name)
        {
            return sb9600.PressButton(name);
        }

        public override bool ReleaseButton(rc2_core.SoftkeyName name)
        {
            return sb9600.ReleaseButton(name);
        }

    }
}
