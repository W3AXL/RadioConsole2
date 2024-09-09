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
using MathNet.Numerics;

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
        ListenOnly, // Generic single channel radio, RX state is controlled by VOX threshold on receive audio
        CM108,      // Generic single channel radio, controlled by CM108 soundcard GPIO
        SB9600,     // SB9600 radio controlled via serial
    }

    /// <summary>
    /// Valid scanning states (used for scan icons on radio cards in the client)
    /// </summary>
    public enum ScanState
    {
        NotScanning,
        Scanning
    }

    public enum PriorityState
    {
        NoPriority,
        Priority1,
        Priority2
    }

    public enum PowerState
    {
        LowPower,
        MidPower,
        HighPower
    }

    public enum SoftkeyState
    {
        Off,
        On,
        Flashing
    }

    /// <summary>
    /// These are the valid softkey bindings which can be used to setup softkeys on radios which don't have them
    /// </summary>
    /// Pruned from the Astro25 mobile CPS help section on button bindings
    public enum SoftkeyName
    {
        CALL,   // Signalling call
        CHAN,   // Channel Select
        CHUP,   // Channel Up
        CHDN,   // Channel Down
        DEL,    // Nuisance Delete
        DIR,    // Talkaround/direct
        EMER,   // Emergency
        DYNP,   // Dynamic Priority
        HOME,   // Home
        LOCK,   // Trunking site lock
        LPWR,   // Low power
        MON,    // Monitor (PL defeat)
        PAGE,   // Signalling page
        PHON,   // Phone operation
        RAB1,   // Repeater access button 1
        RAB2,   // Repeater access button 2
        RCL,    // Scan recall
        SCAN,   // Scan mode, etc
        SEC,    // Secure mode
        SEL,    // Select
        SITE,   // Site alias
        TCH1,   // One-touch 1
        TCH2,   // One-touch 2
        TCH3,   // One-touch 3
        TCH4,   // One-touch 4
        TGRP,   // Talkgroup select
        TMS,    // Text messaging
        TMSQ,   // Quick message
        ZNUP,   // Zone up
        ZNDN,   // Zone down
        ZONE,   // Zone select
    }

    /// <summary>
    /// Softkey object to hold key text, description (for hover) and state
    /// </summary>
    public class Softkey
    {
        public SoftkeyName Name { get; set; }
        public string Description { get; set; }
        public SoftkeyState State { get; set; }
        public ControlHeads.Button Button { get; set; }
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
    public class RadioStatus
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string ZoneName { get; set; } = "";
        public string ChannelName { get; set; } = "";
        public RadioState State { get; set; } = RadioState.Disconnected;
        public ScanState ScanState { get; set; } = ScanState.NotScanning;
        public PriorityState PriorityState {get; set;} = PriorityState.NoPriority;
        public PowerState PowerState {get; set;} = PowerState.LowPower;
        public List<Softkey> Softkeys { get; set; } = new List<Softkey>();
        public bool Monitor { get; set; } = false;
        public bool Direct {get; set;} = false;
        public bool Error { get; set; } = false;
        public string ErrorMsg { get; set; } = "";

        /// <summary>
        /// Encode the RadioStatus object into a JSON string for sending to the client
        /// </summary>
        /// <returns></returns>
        public string Encode()
        {
            // convert the status object to a string
            return JsonConvert.SerializeObject(this, new Newtonsoft.Json.Converters.StringEnumConverter());
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

        public delegate void Callback();
        public Callback StatusCallback { get; set; }

        public int RecTimeout { get; set; } = 0;

        /// <summary>
        /// Overload for a listen-only radio
        /// </summary>
        /// <param name="type"></param>
        /// <param name="rxOnly"></param>
        /// <param name="zoneName"></param>
        /// <param name="channelName"></param>
        public Radio(string name, string desc, RadioType type, string zoneName, string channelName)
        {
            Type = type;
            RxOnly = true;
            // Create status and assign static names
            Status = new RadioStatus();
            Status.Name = name;
            Status.Description = desc;
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
        public Radio(string name, string desc, RadioType type, SB9600.HeadType head, string comPort, bool rxOnly, List<TextLookup> zoneLookups = null, List<TextLookup> chanLookups = null, List<Softkey> softkeys = null, bool rxLeds = false)
        {
            // Get basic info
            Type = type;
            RxOnly = rxOnly;
            // Parse Lookups
            ZoneLookups = zoneLookups;
            ChanLookups = chanLookups;
            // Create Interface
            IntSB9600 = new SB9600(comPort, head, rxLeds);
            IntSB9600.StatusCallback = RadioStatusCallback;
            // Create status
            Status = new RadioStatus();
            Status.Name = name;
            Status.Description = desc;
            Status.Softkeys = softkeys;
        }

        /// <summary>
        /// Start the radio
        /// </summary>
        /// <returns></returns>
        public void Start(bool noreset)
        {
            // Update the radio status to connecting
            Status.State = RadioState.Connecting;
            RadioStatusCallback();
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
            Log.Verbose("Got radio status callback from interface");
            // Perform lookups on zone/channel names (radio-control-type agnostic)
            if (ZoneLookups.Count > 0)
            {
                foreach (TextLookup lookup in ZoneLookups)
                {
                    // An empty string for the match indicates we should always replace the zone name with the replacement
                    if (lookup.Match == "")
                    {
                        Log.Verbose("Empty lookup {replacement} found for zone name, overriding all other lookups", lookup.Replacement);
                        Status.ZoneName = lookup.Replacement;
                        break;
                    }
                    if (Status.ZoneName.Contains(lookup.Match))
                    {
                        Log.Verbose("Found zone text {ZoneName} from {Match} in original text {Text}", lookup.Replacement, lookup.Match, Status.ZoneName);
                        Status.ZoneName = lookup.Replacement;
                    }
                    // On Moto W9, we also look for zone in the channel text since it's a one-liner display
                    if (Type == RadioType.SB9600 && IntSB9600.Head == SB9600.HeadType.W9)
                    {
                        if (Status.ChannelName.Contains(lookup.Match))
                        {
                            Log.Verbose("Found zone text {ZoneName} from {Match} in channel text {Text} on W9 head", lookup.Replacement, lookup.Match, Status.ChannelName);
                            Status.ZoneName = lookup.Replacement;
                        }
                    }
                }
            }
            if (ChanLookups.Count > 0)
            {
                foreach (TextLookup lookup in ChanLookups)
                {
                    if (Status.ChannelName.Contains(lookup.Match))
                    {
                        Log.Verbose("Found channel text {ChannelName} from {Match} in original text {Text}", lookup.Replacement, lookup.Match, Status.ChannelName);
                        Status.ChannelName = lookup.Replacement;
                    }
                }
            }
            // Call recording start/stop callbacks which will trigger audio recording file start/stop if enabled
            if (Status.State == RadioState.Transmitting)
            {
                Task.Delay(100).ContinueWith(t => RecTxCallback());
            }
            else if (Status.State == RadioState.Receiving)
            {
                Task.Delay(100).ContinueWith(t => RecRxCallback());
            }
            // Stop recording if we're not either of the above
            else
            {
                if (WebRTC.RecTxInProgress || WebRTC.RecRxInProgress)
                {
                    Task.Delay(RecTimeout).ContinueWith(t => RecStopCallback());
                }
            }
            // Call the next callback up
            StatusCallback();
        }

        /// <summary>
        /// Sets transmit state of the connected radio
        /// </summary>
        /// <param name="tx">true to transmit, false to stop</param>
        /// <returns>true on success</returns>
        public bool SetTransmit(bool tx)
        {
            if (RxOnly)
            {
                return false;
            }
            else
            {
                if (Type == RadioType.SB9600)
                {
                    IntSB9600.SetTransmit(tx);
                    return true;
                }
                else
                {
                    Log.Error("SetTransmit not defined for interface type {IntType}", Type);
                    return false;
                }
            }
        }

        public bool ChangeChannel(bool down)
        {
            if (Type == RadioType.SB9600)
            {
                IntSB9600.ChangeChannel(down);
                return true;
            }
            else
            {
                Log.Error("ChangeChannel not defined for interface type {IntType}", Type);
                return false;
            }
        }

        public bool PressButton(SoftkeyName name)
        {
            if (Type == RadioType.SB9600)
            {
                IntSB9600.PressButton(name);
                return true;
            }
            else
            {
                Log.Error("PressButton not defined for interface type {IntType}", Type);
                return false;
            }
        }

        public bool ReleaseButton(SoftkeyName name)
        {
            if (Type == RadioType.SB9600)
            {
                IntSB9600.ReleaseButton(name);
                return true;
            }
            else
            {
                Log.Error("ReleaseButton not defined for interface type {IntType}", Type);
                return false;
            }
        }

        private void RecTxCallback()
        {
            // If we were recording RX, stop
            if (WebRTC.RecRxInProgress)
            {
                WebRTC.RecStop();
            }
            if (!WebRTC.RecTxInProgress)
            {
                WebRTC.RecStartTx(Status.ChannelName.Trim());
            }
        }

        private void RecRxCallback()
        {
            // If we were recording TX, stop
            if (WebRTC.RecTxInProgress)
            {
                WebRTC.RecStop();
            }
            // Start recording RX if we're not
            if (!WebRTC.RecRxInProgress)
            {
                WebRTC.RecStartRx(Status.ChannelName.Trim());
            }
        }

        private void RecStopCallback()
        {
            // Only stop recording if we have to
            if (WebRTC.RecTxInProgress && Status.State != RadioState.Transmitting)
            {
                WebRTC.RecStop();
            }
            if (WebRTC.RecRxInProgress && Status.State != RadioState.Receiving)
            {
                WebRTC.RecStop();
            }
        }
    }
}
