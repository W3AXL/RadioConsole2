/**
*
*   Main Runtime for the Radio Control Daemon
*
*   Handles radio control and WebRTC audio processing from the command line
*
*
*/

using System;
using System.CommandLine;
using System.CommandLine.Binding;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.ComponentModel;

using Serilog;
using Serilog.Core;
using Serilog.Events;

using Tomlyn;
using Tomlyn.Model;

using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorcery.Media;
using SIPSorceryMedia.SDL2;

using Org.BouncyCastle.Asn1.IsisMtt.X509;
using daemon;
using System.Runtime;
using DirectShowLib;
using MathNet.Numerics;

namespace netcore_cli
{
    internal class Daemon
    {
        // Log Level Switch
        static LoggingLevelSwitch loggerSwitch = new LoggingLevelSwitch();

        private static bool shutdown = false;
        
        // Global Config Variables for the Daemon
        public class Config
        {
            public static string DaemonName { get; set; }
            public static string DaemonDesc { get; set; }
            public static IPAddress DaemonIP { get; set; }
            public static int DaemonPort { get; set; }
            public static string TxAudioDevice { get; set; }
            public static int TxAudioDeviceIdx { get; set; }
            public static string RxAudioDevice { get; set; }
            public static int RxAudioDeviceIdx { get; set; }
        }

        // Radio object
        static Radio radio = null;

        // Main Program Entry
        static async Task<int> Main(string[] args)
        {
            // Logging
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.ControlledBy(loggerSwitch)
                .WriteTo.Console()
                .CreateLogger();

            /*
             * Command Line Argument Handling
            */
            // Root Command
            var cmdRoot = new RootCommand();
            // List Audio Devices Command
            var cmdListAudio = new Command("list-audio", "List available audio input/output devices");
            cmdListAudio.SetHandler(handler =>
            {
                ListAudioDeices();
            });
            cmdRoot.AddCommand(cmdListAudio);
            // Get audio device info command
            var cmdGetAudio = new Command("get-audio", "Get audio device information for device name");
            var optDeviceName = new Option<string>(new[] {"--device"}, "Device name");
            cmdGetAudio.AddOption(optDeviceName);
            cmdGetAudio.SetHandler(context =>
            {
                string devName = context.ParseResult.GetValueForOption(optDeviceName);
                if (devName == null)
                {
                    Log.Error("No device name specified!");
                    return;
                }
                GetAudioDeviceInfo(devName);
            });
            cmdRoot.AddCommand(cmdGetAudio);

            // Define arguments
            var optConfigFile = new Option<FileInfo>(new[] { "--config", "-c" }, "TOML daemon config file");
            var optDebug = new Option<bool>(new[] { "--debug", "-d" }, "enable debug logging");
            var optVerbose = new Option<bool>(new[] { "--verbose", "-v" }, "enable verbose logging (lots of prints)");
            var optNoReset = new Option<bool>(new[] { "--no-reset", "-nr" }, "don't reset radio on startup");
            var optCodec = new Option<string>(new[] { "--codec" }, "(debug) codec to use for WebRTC audio, default is G722");

            // Add arguments
            cmdRoot.AddOption(optConfigFile);
            cmdRoot.AddOption(optVerbose);
            cmdRoot.AddOption(optDebug);
            cmdRoot.AddOption(optNoReset);
            cmdRoot.AddOption(optCodec);

            // Main Runtime Handler
            cmdRoot.SetHandler((context) =>
            {
                // Make sure a config file was specified
                if (context.ParseResult.GetValueForOption(optConfigFile) == null)
                {
                    Log.Error("No config file specified!");
                    context.ExitCode = 1;
                } 
                else
                {
                    FileInfo configFile = context.ParseResult.GetValueForOption(optConfigFile);
                    bool debug = context.ParseResult.GetValueForOption(optDebug);
                    bool verbose = context.ParseResult.GetValueForOption(optVerbose);
                    bool noreset = context.ParseResult.GetValueForOption(optNoReset);
                    string codec = context.ParseResult.GetValueForOption(optCodec);
                    int result = Startup(configFile, debug, verbose, noreset, codec);
                    context.ExitCode = result;
                }
            });

            return await cmdRoot.InvokeAsync(args);
        }

        static int Startup(FileInfo configFile, bool debug, bool verbose, bool noreset, string codec = null)
        {
            // Add handler for SIGINT
            Console.CancelKeyPress += delegate {
                Shutdown();
            };

            // Logging setup
            if (debug)
            {
                loggerSwitch.MinimumLevel = LogEventLevel.Debug;
                Log.Debug("Debug logging enabled");
            }
            if (verbose)
            {
                loggerSwitch.MinimumLevel = LogEventLevel.Verbose;
                Log.Verbose("Verbose logging enabled");
            }

            if (codec != null)
            {
                WebRTC.Codec = codec;
            }

            // Read config from toml
            int result = ReadConfig(configFile);
            if (result != 0)
            {
                return result;
            }
            // Start websocket server
            DaemonWebsocket.StartWsServer();
            // Start radio
            radio.Start(noreset);
            // Infinite loop (with a sleep to give CPU a break
            while (!shutdown) 
            {
                Thread.Sleep(1);
            }
            return 0;
        }

        internal static int ReadConfig(FileInfo configFile)
        {
            Log.Debug("Reading config file {ConfigFilePath}", configFile);
            // Read file
            string toml = File.ReadAllText(configFile.FullName);
            // Parse TOML
            var config = Toml.ToModel(toml);
            // Daemon Info
            var info = (TomlTable)config["info"];
            Config.DaemonName = (string)info["name"];
            Config.DaemonDesc = (string)info["desc"];
            Log.Debug("         Name: {DaemonName}", Config.DaemonName);
            Log.Debug("         Desc: {DaemonDesc}", Config.DaemonDesc);
            // Daemon Network Config
            var net = (TomlTable)config["network"];
            Config.DaemonIP = IPAddress.Parse((string)net["ip"]);
            Config.DaemonPort = (int)(long)net["port"];
            Log.Debug("      Address: {IP}:{Port}", Config.DaemonIP, Config.DaemonPort);
            // Audio config
            var audio = (TomlTable)config["audio"];
            Config.TxAudioDevice = (string)audio["txDevice"];
            Config.RxAudioDevice = (string)audio["rxDevice"];
            Log.Debug("    TX device: {TxDevice}", Config.TxAudioDevice);
            Log.Debug("    RX device: {RxDevice}", Config.RxAudioDevice);
            // Lookups
            List<TextLookup> zoneLookups = new List<TextLookup>();
            List<TextLookup> chanLookups = new List<TextLookup>();
            var lookupCfg = (TomlTable)config["lookups"];
            var cfgZoneLookups = (TomlArray)lookupCfg["zoneLookup"];
            var cfgChanLookups = (TomlArray)lookupCfg["chanLookup"];
            foreach ( TomlArray lookup in cfgZoneLookups )
            {
                zoneLookups.Add(new TextLookup((string)lookup[0], (string)lookup[1]));
            }
            foreach ( TomlArray lookup in cfgChanLookups )
            {
                chanLookups.Add(new TextLookup((string)lookup[0], (string)lookup[1]));
            }
            Log.Debug("Loaded zone text lookups: {ZoneLookups}", zoneLookups);
            Log.Debug("Loaded channel text lookups: {ChannelLookups}", chanLookups);
            // Control Config
            var radioCfg = (TomlTable)config["radio"];
            string controlType = (string)radioCfg["type"];
            bool rxOnly = (bool)radioCfg["rxOnly"];
            ///
            /// None Control Type (aka non-controlled RX only radio)
            ///
            if (controlType == "none")
            {
                var noneConfig = (TomlTable)config["none"];
                string zoneName = (string)noneConfig["zone"];
                string chanName = (string)noneConfig["chan"];
                Log.Debug("      Control: Non-controlled radio");
                radio = new Radio(Config.DaemonName, Config.DaemonDesc, RadioType.ListenOnly, zoneName, chanName);
                // Update websocket radio object
                DaemonWebsocket.radio = radio;
            }
            ///
            /// Motorola SB9600 control
            ///
            else if (controlType == "sb9600")
            {
                // Parse the SB9600 config options
                var sb9600config = (TomlTable)config["sb9600"];
                SB9600.HeadType head = (SB9600.HeadType)Enum.Parse(typeof(SB9600.HeadType), (string)sb9600config["head"]);
                string port = (string)sb9600config["port"];
                Log.Debug("      Control: {HeadType}-head SB9600 radio on port {SerialPort}", head, port);
                
                // Parse softkeys and button bindings
                List<Softkey> softkeys = new List<Softkey>();
                var cfgSoftkeys = (TomlTable)config["softkeys"];
                // We convert the button bindings to a more parsable array
                var cfgButtonBinding = (TomlArray)cfgSoftkeys["buttonBinding"];
                List<string[]> btnBindings = new List<string[]>();
                foreach ( TomlArray binding in cfgButtonBinding )
                {
                    btnBindings.Add([(string)binding[0], (string)binding[1]]);
                }
                // We iterate over each softkey entry
                var cfgSoftkeyList = (TomlArray)cfgSoftkeys["softkeyList"];
                foreach ( string softkey in cfgSoftkeyList )
                {
                    Softkey key = new Softkey();
                    // Make sure the softkey name is valid
                    if (!Enum.IsDefined(typeof(SoftkeyName), softkey))
                    {
                        Log.Error("Softkey name {name} is not defined!", softkey);
                        return 1;
                    }
                    key.Name = (SoftkeyName)Enum.Parse(typeof(SoftkeyName), softkey);
                    // Make sure that there's a valid binding for this softkey
                    string btnName = btnBindings.Find(b => b[1] == key.Name.ToString())[0];
                    if (btnName == null)
                    {
                        Log.Error("Softkey name {name} not found in button binding map!", key.Name);
                        return 1;
                    }
                    // Create the button and assign it to the softkey
                    byte btnCode;
                    if (head == SB9600.HeadType.W9)
                        btnCode = ControlHeads.W9.Buttons[btnName];
                    else if (head == SB9600.HeadType.M3)
                        btnCode = ControlHeads.M3.Buttons[btnName];
                    else
                    {
                        Log.Error("Head type {Head} does not support button bindings!", head);
                        return 1;
                    }
                    key.Button = new ControlHeads.Button(btnCode, btnName);
                    // Add the softkey to the list
                    softkeys.Add(key);
                }

                // Create SB9600 radio object
                radio = new Radio(Config.DaemonName, Config.DaemonDesc, RadioType.SB9600, head, port, rxOnly, zoneLookups, chanLookups, softkeys);
                radio.StatusCallback = DaemonWebsocket.SendRadioStatus;
                
                // Update websocket radio object
                DaemonWebsocket.radio = radio;
            }
            ///
            /// CM108 single-channel PTT controlled radio
            ///
            else if (controlType == "cm108")
            {
                // TODO: Implement this lol
            }
            else
            {
                Log.Error("Unknown radio control type specified: {InvalidControlType}", controlType);
                return 1;
            }
            return 0;
        }

        static void ListAudioDeices()
        {
            Log.Information("Displaying available audio devices");

            SDL2Helper.InitSDL();

            // Enumerate
            List<string> sdlInputs = SDL2Helper.GetAudioRecordingDevices();
            List<string> sdlOutputs = SDL2Helper.GetAudioPlaybackDevices();

            if ((sdlInputs == null) || (sdlInputs.Count == 0))
            {
                Log.Error("No audio inputs detected!");
            }
            else
            {
                Log.Information("Available audio input devices:");
                for (int i = 0; i < sdlInputs.Count; i++)
                {
                    Log.Information("    {Index}: {Name}", i, sdlInputs[i]);
                }
            }

            if ((sdlOutputs == null) || (sdlOutputs.Count == 0))
            {
                Log.Error("No audio outputs detected!");
            }
            else
            {
                Log.Information("Available audio output devices");
                for (int i=0; i < sdlOutputs.Count; i++)
                {
                    Log.Information("    {Index}: {Name}", i, sdlOutputs[i]);
                }
            }

            SDL2Helper.QuitSDL();
        }

        static void GetAudioDeviceInfo(string devName)
        {
            Log.Information("Getting audio device information for {devName}", devName);
            SDL2Helper.InitSDL();
            var audioEncoder = new OpusAudioEncoder();
            var audioFormatManager = new MediaFormatManager<AudioFormat>(audioEncoder.SupportedFormats);
            AudioFormat audioFormat = audioFormatManager.SelectedFormat;
            var audioSpec = SDL2Helper.GetAudioSpec(audioFormat.ClockRate, 1);
            uint devIdx = SDL2Helper.OpenAudioPlaybackDevice(devName, ref audioSpec);
            Log.Information("    Device index: {index}", devIdx);
            Log.Information("    Suppported codecs:");
            foreach (var codec in audioEncoder.SupportedFormats)
            {
                Log.Information("        {codec}", codec.FormatName);
            }
            SDL2Helper.QuitSDL();
        }

        /// <summary>
        /// Shutdown the daemon
        /// Accessible publicly so any task can initiate shutdown
        /// </summary>
        public static void Shutdown()
        {
            Log.Warning("Caught SIGINT, shutting down");
            radio.Stop();
            DaemonWebsocket.StopWsServer();
            Log.CloseAndFlush();
            shutdown = true;
        }
    }
}