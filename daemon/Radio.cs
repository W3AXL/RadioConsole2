using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SIPSorceryMedia.SDL2;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.FFmpeg;
using Serilog;
using Newtonsoft.Json;

namespace daemon
{
    // Valid states a radio can be in
    public enum RadioState
    {
        Disconnected,
        Connecting,
        Idle,
        Transmitting,
        Receiving,
        Error,
        Disconnecting
    }

    /// <summary>
    /// Valid radio types to be controlled
    /// </summary>
    public enum RadioType
    {
        ListenOnly,     // Generic single channel radio, RX state is controlled by VOX threshold on receive audio
        CM108,   // Generic single channel radio, controlled by CM108 soundcard GPIO
        SB9600,     // SB9600 radio controlled via seria
    }

    /// <summary>
    /// Valid scanning states (used for scan icons on radio cards in the client)
    /// </summary>
    public enum ScanState
    {
        NotScanning,
        Scanning,
        Priority1,
        Priority2
    }

    public enum SoftkeyState
    {
        Off,
        On,
        Flashing
    }

    /// <summary>
    /// Softkey object to hold key text, description (for hover) and state
    /// </summary>
    public class Softkey
    {
        public string Text { get; set; }
        public string Description { get; set; }
        public SoftkeyState State { get; set; }
        public byte SB9600ButtonCode {  get; set; }
    }

    /// <summary>
    /// Class for text-replacement lookup objects
    /// </summary>
    public class TextLookup
    {
        // The text string to match
        public string Match { get; set; }
        // The text string to replace the matched text with
        public string Replacement { get; set; }

        public TextLookup(string match, string replacement)
        {
            Match = match;
            Replacement = replacement;
        }
    }

    /// <summary>
    /// Radio status object, contains all the possible radio states sent to the client during status updates
    /// </summary>
    internal class RadioStatus
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string ZoneName { get; set; } = "";
        public string ChannelName { get; set; } = "";
        public RadioState State { get; set; } = RadioState.Disconnected;
        public ScanState ScanState { get; set; } = ScanState.NotScanning;
        public List<Softkey> Softkeys { get; set; } = new List<Softkey>();
        public bool Monitor { get; set; } = false;

        /// <summary>
        /// Encode the RadioStatus object into a JSON string for sending to the client
        /// </summary>
        /// <returns></returns>
        public string Encode()
        {
            // convert the status object to a string
            return JsonConvert.SerializeObject(this);
        }
    }

    /// <summary>
    /// Radio class representing a radio to be controlled by the daemon
    /// </summary>
    internal class Radio
    {
        // Radio configuration
        public RadioType Type { get; set; }
        public bool RxOnly {  get; set; }

        // SB9600 interface
        public SB9600 IntSB9600 { get; set; }

        // Radio status
        public RadioStatus Status { get; set; }

        // Lookup lists for zone & channel text
        public List<TextLookup> ZoneLookups { get; set; }
        public List<TextLookup> ChanLookups { get; set; }

        public delegate void Callback(RadioStatus status);
        public Callback StatusCallback { get; set; }

        /// <summary>
        /// Overload for a listen-only radio
        /// </summary>
        /// <param name="type"></param>
        /// <param name="rxOnly"></param>
        /// <param name="zoneName"></param>
        /// <param name="channelName"></param>
        public Radio(RadioType type, string zoneName, string channelName)
        {
            Type = type;
            RxOnly = true;
            // Create status and assign static names
            Status = new RadioStatus();
            Status.ZoneName = zoneName;
            Status.ChannelName = channelName;
        }

        /// <summary>
        /// Overload for an SB9600 radio
        /// </summary>
        /// <param name="type"></param>
        /// <param name="head"></param>
        /// <param name="comPort"></param>
        /// <param name="rxOnly"></param>
        /// <param name="zoneLookups"></param>
        /// <param name="chanLookups"></param>
        public Radio(RadioType type, SB9600.HeadType head, string comPort, bool rxOnly, List<TextLookup> zoneLookups = null, List<TextLookup> chanLookups = null)
        {
            // Get basic info
            Type = type;
            RxOnly = rxOnly;
            // Parse Lookups
            ZoneLookups = zoneLookups;
            ChanLookups = chanLookups;
            // Create Interface
            IntSB9600 = new SB9600(comPort, head, ZoneLookups, ChanLookups);
            IntSB9600.StatusCallback = RadioStatusCallback;
            // Create status
            Status = new RadioStatus();
        }

        /// <summary>
        /// Start the radio
        /// </summary>
        /// <returns></returns>
        public void Start(bool noreset)
        {
            // Create a new status object if it's currently null
            if (Status == null)
            {
                Status = new RadioStatus();
            }
            // Start runtimes depending on control type
            if (Type == RadioType.SB9600)
            {
                IntSB9600.radioStatus = Status;
                IntSB9600.Start(noreset);
            }
        }

        public void Stop()
        {
            // Stop runtimes depending on control type
            if (Type == RadioType.SB9600)
            {
                IntSB9600.Stop();
            }
        }

        /// <summary>
        /// Callback function called by the interface class, which in turn calls the callback in the main program for reporting status
        /// Confusing, I know
        /// Basically it goes like this (for SB9600) SB9600.StatusCallback() -> Radio.RadioStatusCallback() -> DaemonWebsocket.SendRadioStatus()
        /// </summary>
        private void RadioStatusCallback()
        {
            StatusCallback(Status);
        }
    }
}
