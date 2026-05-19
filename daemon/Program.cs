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
using Serilog.Sinks.File;

using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorcery.Media;
using SIPSorceryMedia.SDL2;

using Org.BouncyCastle.Asn1.IsisMtt.X509;
using daemon;
using System.Runtime;
using rc2_core;
using moto_sb9600;

namespace netcore_cli
{
    internal class Daemon
    {
        // Log Level Switch
        static LoggingLevelSwitch loggerSwitch = new LoggingLevelSwitch();

        // Config Object (read in from config.yml)
        static ConfigObject Config;

        // Local audio object
        static LocalAudio localAudio;

        // Radio object
        static rc2_core.Radio radio = null;

        // Main Program Entry
        static async Task<int> Main(string[] args)
        {
            /*
             * Command Line Argument Handling
            */
            
            // Root Command
            RootCommand cmdRoot = new RootCommand();
            
            // List Audio Devices Command
            Command cmdListAudio = new Command("list-audio")
            {
                Description = "List available audio input/output devices"
            };
            cmdListAudio.SetAction((parseResult) =>
            {
                ListAudioDeices();
            });
            cmdRoot.Add(cmdListAudio);
            
            // Get audio device info command
            Command cmdGetAudio = new Command("get-audio")
            {
                Description = "Get audio device information for device name"
            };
            Option<string> optDeviceName = new Option<string>("--device")
            {
                Description = "Device name"
            };
            cmdGetAudio.Options.Add(optDeviceName);
            cmdGetAudio.SetAction(parseResult =>
            {
                string devName = parseResult.GetValue(optDeviceName);
                if (devName == null)
                {
                    Log.Error("No device name specified!");
                    return;
                }
                GetAudioDeviceInfo(devName);
            });
            cmdRoot.Add(cmdGetAudio);

            // Define root command arguments
            Option<FileInfo> optConfigFile = new Option<FileInfo>("--config", "-c")
            {
                Description = "YAML daemon config file"
            };
            Option<bool> optDebug = new Option<bool>("--debug", "-d")
            {
                Description = "enable debug logging"
            };
            Option<bool> optVerbose = new Option<bool>("--verbose", "-v")
            {
                Description = "enable verbose logging (lots of prints)"
            };
            Option<bool> optNoReset = new Option<bool>("--no-reset", "-nr")
            {
                Description = "don't reset radio on startup"
            };
            Option<bool> optLogging = new Option<bool>("--log", "-l")
            {
                Description = "log console output to file"
            };

            // Add arguments
            cmdRoot.Options.Add(optConfigFile);
            cmdRoot.Options.Add(optVerbose);
            cmdRoot.Options.Add(optDebug);
            cmdRoot.Options.Add(optNoReset);
            cmdRoot.Options.Add(optLogging);

            // Main Runtime Handler
            cmdRoot.SetAction(async (parseResult) =>
            {
                // Make sure a config file was specified
                if (parseResult.GetValue(optConfigFile) == null)
                {
                    Log.Error("No config file specified!");
                    Environment.Exit(1);
                } 
                else
                {
                    FileInfo configFile = parseResult.GetValue(optConfigFile);
                    bool debug = parseResult.GetValue(optDebug);
                    bool verbose = parseResult.GetValue(optVerbose);
                    bool noreset = parseResult.GetValue(optNoReset);
                    bool log = parseResult.GetValue(optLogging);
                    await Startup(configFile, debug, verbose, noreset, log);
                }
            });

            return await cmdRoot.Parse(args).InvokeAsync();
        }

        static async Task Startup(FileInfo configFile, bool debug, bool verbose, bool noreset, bool log)
        {
            // Logging configuration
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.ControlledBy(loggerSwitch)
                .WriteTo.Console(
                    outputTemplate: "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}"
                )
                .CreateLogger();

            // Add handler for SIGINT
            ManualResetEvent startShutdown = new ManualResetEvent(false);
            Console.CancelKeyPress += (sender, args) => {
                args.Cancel = true;
                startShutdown.Set();
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

            // Read config from toml
            ReadConfig(configFile);

            // Set up file logging (we do this after config reading)
            if (log)
            {
                // Create the logs directory if it doesn't exist
                System.IO.Directory.CreateDirectory("logs");
                // Get the timestamp
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
                Log.Information("Logging to file: {Name}_{timestamp}.log", Config.Daemon.Name, timestamp);
                // We append the file logger to the original created logger
                Log.Logger = new LoggerConfiguration()
                    .WriteTo.Logger(Log.Logger)
                    .WriteTo.File($"logs/{Config.Daemon.Name.Replace(" ", "_")}_{timestamp}.log")
                    .MinimumLevel.ControlledBy(loggerSwitch)
                    .CreateLogger();
            }

            // Setup Audio Devices
            Log.Logger.Debug("Configuring local audio");
            localAudio = new LocalAudio(Config.Audio.RxDevice, Config.Audio.TxDevice, radio, Config.Control.RxOnly);

            // Switch based on control mode
            switch(Config.Control.ControlMode)
            {
                case RadioControlMode.VOX:
                {
                    VoxRadio voxRadio = null;
                    Action<AudioFormat> rtcFormatCallback = (audioFormat) =>
                    {
                        localAudio.Start(audioFormat);
                        voxRadio?.ReArmStartupDelay("WebRTC audio negotiation");
                    };

                    voxRadio = new VoxRadio(
                        Config.Daemon.Name,
                        Config.Daemon.Desc,
                        Config.Control.RxOnly,
                        Config.Daemon.ListenAddress,
                        Config.Daemon.ListenPort,
                        Config.Control.Vox,
                        localAudio.TxAudioCallback,
                        16000,
                        rtcFormatCallback,
                        Config.Softkeys,
                        Config.TextLookups.Zone,
                        Config.TextLookups.Channel
                    );
                    radio = voxRadio;
                    localAudio.RxRawSampleCallback += voxRadio.HandleRxAudioSamples;
                    localAudio.TxPcmSampleCallback += voxRadio.HandleTxAudioSamples;
                }
                break;
                case RadioControlMode.SB9600:
                {
                    radio = new MotoSb9600Radio(
                        Config.Daemon.Name,
                        Config.Daemon.Desc,
                        Config.Control.RxOnly,
                        Config.Daemon.ListenAddress,
                        Config.Daemon.ListenPort,
                        Config.Daemon.AllowedNetworks,
                        Config.Control.Sb9600,
                        localAudio.TxAudioCallback,
                        16000,
                        localAudio.Start,
                        Config.Softkeys,
                        Config.TextLookups.Zone,
                        Config.TextLookups.Channel
                    );
                }
                break;
                default:
                {
                    Log.Error("Control mode {mode} not yet implemented!", Config.Control.ControlMode.ToString());
                    Environment.Exit((int)ERRNO.EBADCONFIG);
                }
                break;
            }

            // Setup RX audio callback
            if (Config.Control.ControlMode == RadioControlMode.VOX)
            {
                localAudio.RxEncodedSampleCallback += ((VoxRadio)radio).HandleRxEncodedSamples;
                localAudio.Start(GetDefaultAudioFormat());
            }
            else
            {
                localAudio.RxEncodedSampleCallback += radio.RxSendEncodedSamples;
            }

            // Start radio
            radio.Start(noreset);
            
            // Wait for shutdown trigger
            startShutdown.WaitOne();

            // Stop radio
            Log.Information("Shutting down...");
            radio.Stop();
            await localAudio.Stop();
            Log.CloseAndFlush();

            Environment.Exit(0);
        }

        internal static void ReadConfig(FileInfo configFile)
        {
            Log.Debug("Reading config file {ConfigFilePath}", configFile);
            
            try
            {
                using (FileStream stream = new FileStream(configFile.FullName, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    using (TextReader reader = new StreamReader(stream))
                    {
                        // Read all yaml
                        string yml = reader.ReadToEnd();

                        // Parse to object
                        IDeserializer ymlDeserializer = new DeserializerBuilder()
                            .WithNamingConvention(CamelCaseNamingConvention.Instance)
                            .Build();

                        Config = ymlDeserializer.Deserialize<ConfigObject>(yml);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to read configuration file {configFile}", configFile);
                Environment.Exit((int)rc2_core.ERRNO.ENOCONFIG);
            }
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
            AudioEncoder audioEncoder = new AudioEncoder();
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

        static AudioFormat GetDefaultAudioFormat()
        {
            AudioEncoder audioEncoder = new AudioEncoder();
            AudioFormat g722 = audioEncoder.SupportedFormats.Find(f => f.FormatName == "G722");

            if (g722.ClockRate == 0)
            {
                var audioFormatManager = new MediaFormatManager<AudioFormat>(audioEncoder.SupportedFormats);
                return audioFormatManager.SelectedFormat;
            }

            return g722;
        }
    }
}
