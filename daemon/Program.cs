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

using daemon;
using System.Runtime;
using rc2_core;
using moto_sb9600;
using RadioConsole.Protocol;

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
            // Minimal logger we create at startup so that list-audio (and any future convenience commands) can properly print
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.ControlledBy(loggerSwitch)
                .WriteTo.Console(
                    outputTemplate: "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}"
                )
                .CreateLogger();

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

            // Parse out softkeys into a true list
            List<SoftkeyName> consoleSoftkeys = new List<SoftkeyName>();
            foreach (string name in Config.Softkeys)
            {
                consoleSoftkeys.Add(ConfigMapping.GetSoftkeyName(name));
            }

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
            localAudio = new LocalAudio(Config.Audio.RxDevice, Config.Audio.TxDevice, Config.Audio.SampleRate, Config.Control.RxOnly);

            // Switch based on control mode
            switch(Config.Control.ControlMode)
            {
                case RadioControlMode.SB9600:
                {
                    // Create the radio
                    radio = new MotoSb9600Radio(
                        Config.Daemon.Name,
                        Config.Daemon.Desc,
                        Config.Control.RxOnly,
                        Config.Daemon.ListenAddress,
                        Config.Daemon.ListenPort,
                        Config.Daemon.AllowedNetworks,
                        Config.Control.Sb9600,
                        Config.Audio.SampleRate,
                        consoleSoftkeys,
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

            // Setup audio callbacks
            localAudio.RxAudioAvailable += radio.SendRxPCM16Samples;
            if (!Config.Control.RxOnly)
            {
                radio.OnTxAudio += localAudio.PlayTxSamples;
            }

            // Start local audio
            localAudio.Start();

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

        /// <summary>
        /// List the available PortAudio devices on the machine running the daemon
        /// </summary>
        static void ListAudioDeices()
        {
            Log.Logger.Information("Displaying available audio devices");

            // Enumerate
            List<string> inputs = Audio.GetInputDeviceNames();
            List<string> outputs = Audio.GetOutputDeviceNames();

            if ((inputs == null) || (inputs.Count == 0))
            {
                Log.Logger.Error("No audio inputs detected!");
            }
            else
            {
                Log.Logger.Information("Available audio input devices:");
                for (int i = 0; i < inputs.Count; i++)
                {
                    Log.Logger.Information("    {Index}: {Name}", i, inputs[i]);
                }
            }

            if ((outputs == null) || (outputs.Count == 0))
            {
                Log.Logger.Error("No audio outputs detected!");
            }
            else
            {
                Log.Logger.Information("Available audio output devices");
                for (int i=0; i < outputs.Count; i++)
                {
                    Log.Logger.Information("    {Index}: {Name}", i, outputs[i]);
                }
            }
        }
    }
}