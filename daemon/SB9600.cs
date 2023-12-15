using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO.Ports;
using FFmpeg.AutoGen;
using System.Collections.Concurrent;
using System.Threading.Tasks.Dataflow;
using Serilog;
using Microsoft.VisualBasic;
using Serilog.Debugging;
using Org.BouncyCastle.Utilities;
using WebSocketSharp;
using Org.BouncyCastle.Crypto.Digests;
using netcore_cli;

namespace daemon
{
    internal static class ControlHeads
    {
        public static class M3
        {
            public static readonly Dictionary<string, byte> Buttons = new Dictionary<string, byte>()
            {
                { "ptt", 0x01 },
                { "knob_vol", 0x02 },
                { "btn_left_top", 0x60 },
                { "btn_left_mid", 0x61 },
                { "btn_left_bot", 0x62 },
                { "btn_bot_1", 0x63 },
                { "btn_bot_2", 0x64 },
                { "btn_bot_3", 0x65 },
                { "btn_bot_4", 0x66 },
                { "btn_bot_5", 0x67 },
                { "btn_bot_6", 0x68 },
                { "btn_kp_1", 0x31 },
                { "btn_kp_2", 0x32 },
                { "btn_kp_3", 0x33 },
                { "btn_kp_4", 0x34 },
                { "btn_kp_5", 0x35 },
                { "btn_kp_6", 0x36 },
                { "btn_kp_7", 0x37 },
                { "btn_kp_8", 0x38 },
                { "btn_kp_9", 0x39 },
                { "btn_kp_*", 0x3A },
                { "btn_kp_0", 0x30 },
                { "btn_kp_#", 0x3B },
                { "btn_kp_a", 0x69 },
                { "btn_kp_b", 0x6A },
                { "btn_kp_c", 0x6B },
                { "btn_kp_d", 0x6D },
            };

            public static readonly Dictionary<string, byte> Indicators = new Dictionary<string, byte>()
            {
                { "monitor", 0x01 },
                { "scan", 0x04 },
                { "scan_pri", 0x05 },
                { "direct", 0x07 },
                { "led_amber", 0x0D },
                { "led_red", 0x0B },
                { "ind_bot_1", 0x14 },
                { "ind_bot_2", 0x15 },
                { "ind_bot_3", 0x16 },
                { "ind_bot_4", 0x17 },
                { "ind_bot_5", 0x18 },
                { "ind_bot_6", 0x19 },
            };

            public static readonly List<string> IgnoredStrings = new List<string>()
            {
                "SELF TEST",
                "LAST RCVD/XMIT"
            };
        }

        public static class W9
        {
            public static readonly Dictionary<string, byte> Buttons = new Dictionary<string, byte>()
            {
                { "ptt", 0x01 },
                { "mode_down", 0x50 },
                { "mode_up", 0x51 },
                { "vol_down", 0x52 },
                { "vol_up", 0x53 },
                { "btn_top_1", 0x63 },
                { "btn_top_2", 0x64 },
                { "btn_top_3", 0x65 },
                { "btn_top_4", 0x66 },
                { "btn_top_5", 0x67 },
                { "btn_top_6", 0x68 },
                { "btn_kp_1", 0x31 },
                { "btn_kp_2", 0x32 },
                { "btn_kp_3", 0x33 },
                { "btn_kp_4", 0x34 },
                { "btn_kp_5", 0x35 },
                { "btn_kp_6", 0x36 },
                { "btn_kp_7", 0x37 },
                { "btn_kp_8", 0x38 },
                { "btn_kp_9", 0x39 },
                { "btn_kp_*", 0x3A },
                { "btn_kp_0", 0x30 },
                { "btn_kp_#", 0x3B },
                { "btn_home", 0x61 },
                { "btn_sel", 0x60 },
                { "btn_dim", 0x62 },
            };

            public static readonly Dictionary<string, byte> Indicators = new Dictionary<string, byte>()
            {
                { "ind_top_1", 0x07 },
                { "ind_top_2", 0x08 },
                { "ind_top_3", 0x09 },
                { "ind_top_4", 0x0A },
                { "ind_top_5", 0x0B },
                { "ind_top_6", 0x0C },
                { "ind_pri", 0x0D },
                { "ind_nonpri", 0x0E },
                { "ind_busy", 0x0F },
                { "ind_xmit", 0x10 },
            };
        }

        /// <summary>
        /// Get the name of a button based on the control head type and button code
        /// </summary>
        /// <param name="head"></param>
        /// <param name="code"></param>
        /// <returns></returns>
        public static string GetButton(SB9600.HeadType head, byte code)
        {
            if (head == SB9600.HeadType.M3)
            {
                foreach (KeyValuePair<string, byte> button in M3.Buttons)
                {
                    if (button.Value == code)
                    {
                        return button.Key;
                    }
                }
                return null;
            }
            else if (head == SB9600.HeadType.W9)
            {
                foreach (KeyValuePair<string, byte> button in W9.Buttons)
                {
                    if (button.Value == code)
                    {
                        return button.Key;
                    }
                }
                return null;
            }
            else { return null; }
        }
    }
    internal class SB9600
    {
        // Class Variables
        public SerialPort Port { get; set; }

        // Cancellation token for serial listener task
        private CancellationTokenSource ts;
        private CancellationToken ct;

        // Flags for SB9600
        private bool inSbep = false;
        private bool waiting = false;

        // Queue for TX messages to send in infinite loop when free
        private ConcurrentQueue<QueueMessage> msgQueue = new ConcurrentQueue<QueueMessage>();

        // Delayed Message List
        private List<DelayedMessage> delayedMessages = new List<DelayedMessage>();

        // Head type
        public HeadType Head { get; set; }

        // Delegate callback to indicate new received status
        public delegate void Callback();
        public Callback StatusCallback { get; set; }

        private bool newStatus = false;

        private bool noReset = false;

        // Reference to Radio object for updating parameters
        public RadioStatus radioStatus { get; set; }

        private string displayText1 { get; set; }
        private string displayText2 { get; set; }
        private List<TextLookup> ZoneLookups { get; set; }
        private List<TextLookup> ChanLookups { get; set; }
        
        public enum HeadType
        {
            W9,
            M3,
            O5
        }

        private static readonly byte[] sb9600CrcTable =
        {
            0x00, 0x99, 0xad, 0x34, 0xc5, 0x5c, 0x68, 0xf1, 0x15, 0x8c, 0xb8, 0x21, 0xd0, 0x49, 0x7d, 0xe4,
            0x2a, 0xb3, 0x87, 0x1e, 0xef, 0x76, 0x42, 0xdb, 0x3f, 0xa6, 0x92, 0x0b, 0xfa, 0x63, 0x57, 0xce,
            0x54, 0xcd, 0xf9, 0x60, 0x91, 0x08, 0x3c, 0xa5, 0x41, 0xd8, 0xec, 0x75, 0x84, 0x1d, 0x29, 0xb0,
            0x7e, 0xe7, 0xd3, 0x4a, 0xbb, 0x22, 0x16, 0x8f, 0x6b, 0xf2, 0xc6, 0x5f, 0xae, 0x37, 0x03, 0x9a,
            0xa8, 0x31, 0x05, 0x9c, 0x6d, 0xf4, 0xc0, 0x59, 0xbd, 0x24, 0x10, 0x89, 0x78, 0xe1, 0xd5, 0x4c,
            0x82, 0x1b, 0x2f, 0xb6, 0x47, 0xde, 0xea, 0x73, 0x97, 0x0e, 0x3a, 0xa3, 0x52, 0xcb, 0xff, 0x66,
            0xfc, 0x65, 0x51, 0xc8, 0x39, 0xa0, 0x94, 0x0d, 0xe9, 0x70, 0x44, 0xdd, 0x2c, 0xb5, 0x81, 0x18,
            0xd6, 0x4f, 0x7b, 0xe2, 0x13, 0x8a, 0xbe, 0x27, 0xc3, 0x5a, 0x6e, 0xf7, 0x06, 0x9f, 0xab, 0x32,
            0xcf, 0x56, 0x62, 0xfb, 0x0a, 0x93, 0xa7, 0x3e, 0xda, 0x43, 0x77, 0xee, 0x1f, 0x86, 0xb2, 0x2b,
            0xe5, 0x7c, 0x48, 0xd1, 0x20, 0xb9, 0x8d, 0x14, 0xf0, 0x69, 0x5d, 0xc4, 0x35, 0xac, 0x98, 0x01,
            0x9b, 0x02, 0x36, 0xaf, 0x5e, 0xc7, 0xf3, 0x6a, 0x8e, 0x17, 0x23, 0xba, 0x4b, 0xd2, 0xe6, 0x7f,
            0xb1, 0x28, 0x1c, 0x85, 0x74, 0xed, 0xd9, 0x40, 0xa4, 0x3d, 0x09, 0x90, 0x61, 0xf8, 0xcc, 0x55,
            0x67, 0xfe, 0xca, 0x53, 0xa2, 0x3b, 0x0f, 0x96, 0x72, 0xeb, 0xdf, 0x46, 0xb7, 0x2e, 0x1a, 0x83,
            0x4d, 0xd4, 0xe0, 0x79, 0x88, 0x11, 0x25, 0xbc, 0x58, 0xc1, 0xf5, 0x6c, 0x9d, 0x04, 0x30, 0xa9,
            0x33, 0xaa, 0x9e, 0x07, 0xf6, 0x6f, 0x5b, 0xc2, 0x26, 0xbf, 0x8b, 0x12, 0xe3, 0x7a, 0x4e, 0xd7,
            0x19, 0x80, 0xb4, 0x2d, 0xdc, 0x45, 0x71, 0xe8, 0x0c, 0x95, 0xa1, 0x38, 0xc9, 0x50, 0x64, 0xfd
        };

        private enum SB9600Addresses
        {
            BROADCAST   = 0x00,
            RADIO       = 0x01,
            DSP         = 0x02,
            MPL         = 0x03,
            INTOPTIONS  = 0x04,
            FRONTPANEL  = 0x05,
            REARPANEL   = 0x06,
            EXTPANEL    = 0x07,
            SIREN_PA    = 0x08,
            SECURENET   = 0x09,
            EMGCY_STAT  = 0x0A,
            MSG_SELCALL = 0x0B,
            MDC600CALL  = 0x0C,
            MVS         = 0x0D,
            PHONE       = 0x0E,
            DTMF        = 0x0F,
            TRNK_SYS    = 0x10,
            TRNK_OPT    = 0x11,
            VRS         = 0x12,
            SP_RPT      = 0x13,
            SINGLETONE  = 0x14,
            VEHICLE_LOC = 0x16,
            KDT_TERM    = 0x17,
            TRNK_DESK   = 0x18,
            METROCOM    = 0x19,
            CTRL_HOST   = 0x1A,
            VEHICLE_ADP = 0x1B
        }

        private enum SB9600Opcodes
        {
            EPREQ  = 0x06,  // Expanded protocol request (enter SBEP)
            SETBUT = 0x0A,  // Set button
            RADRDY = 0x15,  // Radio Ready
            RADKEY = 0x19,  // Radio Key
            RXAUD  = 0x1A,  // RX audio routing
            TXAUD  = 0x1B,  // TX audio routing
            AUDMUT = 0x1D,  // Audio muting
            SQLDET = 0x1E,  // Squelch detect
            ACTMDU = 0x1F,  // Active mode update
            PLDECT = 0x23,  // PL detect
            PRUPST = 0x3B,  // Power-up Self-Test Result
            DISPLY = 0x3C,  // Display
            BUTCTL = 0x57,  // Button control
            LUMCTL = 0x58   // Illumination Control
        }

        private class SB9600Msg
        {
            public byte Address { get; set; }
            public byte[] Data { get; set; }
            public byte Opcode { get; set; }

            public SB9600Msg()
            {
            }

            public SB9600Msg(byte _address, byte[] _data, byte _opcode)
            {
                Address = _address;
                Data = _data;
                Opcode = _opcode;
            }

            public byte CalcCrc()
            {
                byte crc = 0;
                // Run the crc on the 4 message bytes
                crc = sb9600CrcTable[(crc ^ Address) & 0xFF];
                crc = sb9600CrcTable[(crc ^ Data[0]) & 0xFF];
                crc = sb9600CrcTable[(crc ^ Data[1]) & 0xFF];
                crc = sb9600CrcTable[(crc ^ Opcode) & 0xFF];
                // Return
                return crc;
            }
            /// <summary>
            /// Decodes a byte array to the SB9600 message object
            /// </summary>
            /// <param name="data"></param>
            /// <returns></returns>
            public bool Decode(byte[] data)
            {
                // Verify data length
                if (data.Length > 5)
                {
                    Log.Error($"Tried to parse SB9600 message longer than 5! ({data.Length})");
                    return false;
                }

                // Get params
                Address = data[0];
                Data[0] = data[1];
                Data[1] = data[2];
                Opcode = data[3];
                    
                // Verify CRC
                if (data[4] != CalcCrc())
                {
                    Log.Error($"Invalid CRC received. Expected {CalcCrc()}, got {data[4]}");
                    return false;
                }

                // Return true if everything is OK
                return true;
            }

            /// <summary>
            /// Encodes the SB9600 message object to a byte array
            /// </summary>
            /// <param name="data"></param>
            public byte[] Encode()
            {
                byte[] data = new byte[5];
                data[0] = Address;
                data[1] = Data[0];
                data[2] = Data[1];
                data[3] = Opcode;
                data[4] = CalcCrc();
                return data;
            }
        }

        private enum SBEPModules
        {
            BROADCAST = 0x00,
            RADIO = 0x01,
            PANEL = 0x05,
        }

        private enum SBEPOpcodes
        {
            RESERVED = 0x00,
            DISPLAY = 0x01,
            RFTEST = 0x02,
            VIRTUAL = 0x03,
            ACK = 0x05,
            NACK = 0x06,
            EXTENDED = 0x0F,
        }

        private class SBEPMsg
        {
            public byte Opcode { get; set; }
            public byte[] Data { get; set; }

            private static byte CalcCrc(byte[] data)
            {
                byte crc = 0x00;
                foreach (byte b in data)
                {
                    crc = (byte)((crc + b) & 0xFF);
                }
                crc ^= 0xFF;
                return crc;
            }

            public int Decode(byte[] data)
            {
                // Starting length
                int length = 0;

                // Data array start index
                int dataidx = 1;

                // Get msn and lsn of first byte
                byte msn = (byte)(data[0] >> 4);
                byte lsn = (byte)(data[0] & 0x0F);

                // Get extended opcode first
                byte opcode = 0x00;
                if (msn == 0x0F)
                {
                    opcode = data[1];
                    length += 1;
                    dataidx += 1;
                }
                else
                {
                    opcode = msn;
                }

                // Get length next
                if (lsn == 0x0F)
                {
                    if (opcode >= 0x0F)
                    {
                        // we start at index 2 if we have a preceeding extended opcode byte
                        length += BitConverter.ToInt16(data, 2);
                    }
                    else
                    {
                        // start at index 1 if there's no extended opcode byte
                        length += BitConverter.ToInt16(data, 1);
                    }
                    dataidx += 2;
                }
                else
                {
                    length += lsn;
                }

                // Perform CRC check now
                byte recvCrc = data[length - 1];                // the crc should be the last byte (i.e. at position length - 1)
                byte calcCrc = CalcCrc(data[0..(length-2)]);    // we caculate CRC on all bytes except the CRC so it's length - 2
                if (recvCrc != calcCrc)
                {
                    Log.Error($"SBEP CRC check failed! Got {recvCrc} but expected {calcCrc}");
                    return 0;
                }

                // Set variables
                Opcode = opcode;
                Data = data[dataidx..(length - 2)];

                return length;
            }

            /// <summary>
            /// Encodes the SBEP message object to a byte array
            /// </summary>
            /// <returns></returns>
            public byte[] Encode()
            {
                // init output list (converted to an array on return)
                List<byte> msg = new List<byte> { 0x00 };

                // Length is the length of the data plus the CRC byte (unless we have no data in which case it's 0)
                int length = 0;
                if (Data.Length > 0)
                    length = Data.Length + 1;

                // Check for extended opcode first
                if (Opcode > 0x0F)
                {
                    msg[0] |= 0b11110000;   // msn of the first byte becomes 0xF
                    msg.Add(Opcode);        // the next byte is the extended opcode
                }
                else
                {
                    msg[0] |= (byte)(Opcode << 4);
                }

                // Check for extended size
                if (length > 0x0E)
                {
                    msg[0] |= 0b00001111;                                   // lsn of the first byte becomes 0xF
                    msg.AddRange(BitConverter.GetBytes((short)length));     // next two bytes are the extended size
                }
                else
                {
                    msg[0] |= (byte)(Data.Length);
                }

                // Add the data
                msg.AddRange(Data);

                // Calculate the CRC (using all byets per the v05.13 spec) and add it to the message
                msg.Add(CalcCrc(msg.ToArray()));

                // We're done, so return the array from the list
                return msg.ToArray();
            }
        }

        private class QueueMessage
        {
            public SB9600Msg sb9600msg { get; set; } = null;
            public SBEPMsg SBEPMsg { get; set; } = null;

            public QueueMessage(SB9600Msg msg)
            {
                sb9600msg = msg;
            }

            public QueueMessage(SBEPMsg msg)
            {
                SBEPMsg = msg;
            }
        }

        /// <summary>
        /// Class for holding a message which is delayed until a specific time
        /// </summary>
        private class DelayedMessage
        {
            public long ExecTime { get; set; }
            public SB9600Msg SB9600msg { get; set; }
            public SBEPMsg SbepMsg { get; set; }

            public DelayedMessage(long execTime, SB9600Msg msg)
            {
                ExecTime = execTime;
                SB9600msg = msg;
            }
            public DelayedMessage(long execTime, SBEPMsg msg)
            {
                ExecTime = execTime;
                SbepMsg = msg;
            }
        }

        public SB9600(SerialPort port, HeadType _head, List<TextLookup> zoneLookups, List<TextLookup> chanLookups)
        {
            Port = port;
            Port.BaudRate = 9600;
            Head = _head;
            ZoneLookups = zoneLookups;
            ChanLookups = chanLookups;
        }

        public SB9600(string portName, HeadType _head, List<TextLookup> zoneLookups, List<TextLookup> chanLookups)
        {
            Port = new SerialPort(portName);
            Port.BaudRate = 9600;
            Head = _head;
            ZoneLookups = zoneLookups;
            ChanLookups = chanLookups;
        }

        public void Start(bool noreset)
        {
            noReset = noreset;
            // Check if serial port exists first
            if (!SerialPort.GetPortNames().Contains(Port.PortName))
            {
                throw new Exception("Specified serial port does not exist!");
            }
            Log.Debug($"Starting SB9600 service on serial port {Port.PortName}");
            ts = new CancellationTokenSource();
            ct = ts.Token;
            Task.Factory.StartNew(serialLoop, ct);
            Log.Verbose("Started!");
        }

        public void Stop()
        {
            Log.Debug($"Stopping SB9600 service on serial port {Port.PortName}");
            if (ts != null)
            {
                Log.Verbose("Cancelling service token");
                ts.Cancel();
                ts.Dispose();
                ts = null;
            }
            if (Port.IsOpen)
            {
                Log.Verbose("Closing and disposing port");
                Port.Close();
                Port.Dispose();
            }
            Log.Verbose("Done");
        }

        /// <summary>
        /// Set the state of the BUSY line (controlled by DTR)
        /// </summary>
        /// <param name="busy"></param>
        private void setBusy(bool busy)
        {
            Port.DtrEnable = busy;
        }

        /// <summary>
        /// Gets the current state of the BUSY line (detected by CTS)
        /// </summary>
        /// <returns></returns>
        private bool getBusy()
        {
            return Port.CtsHolding;
        }

        private bool Reset()
        {
            SB9600Msg msg = new SB9600Msg((byte)SB9600Addresses.BROADCAST, [0x00, 0x01], 0x08);
            return sendSb9600(msg);
        }

        private bool sendSb9600(SB9600Msg msg, int attempts = 3)
        {
            byte[] data = msg.Encode();
            // Wait for busy to drop
            while (getBusy()) { }
            // Grab busy
            setBusy(true);
            // flag for successful send
            bool sent = false;
            while (!sent && attempts > 0)
            {
                // flush the buffers
                Port.DiscardInBuffer();
                Port.DiscardOutBuffer();
                // Send the msg
                Port.Write(data, 0, data.Length);
                attempts--;
                // Verify sent
                byte[] rx = new byte[data.Length];
                Port.Read(rx, 0, rx.Length);
                if (rx != data)
                {
                    sent = false;
                    Log.Error($"Failed to verify SB9600 message TX. Sent {Util.Hex(data)}, Recieved {Util.Hex(rx)}. ({attempts} attempts left)");
                }
                else
                {
                    sent = true;
                }
            }
            // return success or failure
            return sent;
        }

        private bool processSB9600(byte[] msgBytes)
        {
            // Verify message length
            if (msgBytes.Length > 5)
            {
                throw new ArgumentException($"SB9600 message must be 5 bytes long. Got {msgBytes.Length} bytes");
            }

            Log.Debug($"Got SB9600 message {msgBytes}");

            // Decode message
            SB9600Msg msg = new SB9600Msg();
            if (msg.Decode(msgBytes) == false)
            {
                Log.Error($"Failed to decode SB9600 message {msgBytes}");
                return false;
            }

            // Proccess
            switch (msg.Address)
            {
                ///
                /// Broadcast Module
                ///
                case (byte)SB9600Addresses.BROADCAST:
                    // These are used by several opcodes
                    byte group = (byte)((msg.Data[1] & 0b11100000) >> 5);       // group is defined by upper 3 bits
                    byte address = (byte)(msg.Data[1] & 0b00011111);            // address is defined by lower 5 bytes
                    // Switch on the opcode
                    switch (msg.Opcode)
                    {
                        ///
                        /// EPREQ Request
                        /// Used to enter SBEP mode, among other things
                        ///
                        case (byte)SB9600Opcodes.EPREQ:
                            // Break out msg components
                            byte protocol = (byte)((msg.Data[0] & 0b00110000) >> 4);    // protocol is defined by bits 4 & 5
                            byte baudrate = (byte)(msg.Data[0] & 0b00001111);           // baudrate is defined by lowest 4 bits
                            if (protocol == 0x01)
                            {
                                inSbep = true;
                                if (baudrate != 0x02)
                                {
                                    Log.Error($"Got command to switch to unsupported SBEP baudrate {baudrate}");
                                }
                                else
                                {
                                    inSbep = true;
                                    Log.Debug($"Entering SBEP at 9600 baud");
                                }
                            } else
                            {
                                Log.Warning($"Got EPREQ command for unknon protocol {protocol}");
                            }
                            break;
                        ///
                        /// Set Button Command
                        /// These are not "buttons" necessarily but more like radio control commands
                        ///
                        case (byte)SB9600Opcodes.SETBUT:
                            byte register = msg.Data[0];
                            byte command = msg.Data[1];
                            // switch on button type
                            switch (register)
                            {
                                // Monitor mode
                                case 0x01:
                                    if (command == 0x01)
                                    {
                                        radioStatus.Monitor = true;
                                        Log.Information("Radio monitor on");
                                    }
                                    else
                                    {
                                        radioStatus.Monitor = false;
                                        Log.Information("Radio monitor off");
                                    }
                                    newStatus = true;
                                    break;
                                // TX mode
                                case 0x03:
                                    if (command == 0x01)
                                    {
                                        if (radioStatus.State != RadioState.Transmitting)
                                        {
                                            radioStatus.State = RadioState.Transmitting;
                                            newStatus = true;
                                            Log.Information("Radio now transmitting");
                                        }
                                        else if (radioStatus.State != RadioState.Receiving && radioStatus.State != RadioState.Idle)
                                        {
                                            radioStatus.State = RadioState.Idle;
                                            newStatus = true;
                                            Log.Information("Radio no longer transmitting");
                                        }
                                    }
                                    break;
                                default:
                                    Log.Warning($"Unhandled SETBUT button register {msg.Data[0]}");
                                    break;
                            }
                            break;
                        ///
                        /// Power-up Self Test Result
                        /// Happens on power-up to verify all components are working
                        ///
                        case (byte)SB9600Opcodes.PRUPST:
                            // If any bits are 1, those indicate errors
                            if (msg.Data[0] != 0x00)
                            {
                                Log.Error($"Detected PRUPST errors from group {group}, address {address}: {msg.Data[0]}");
                            }
                            else
                            {
                                Log.Information($"Got normal PRUPST from group {group}, address {address}");
                            }
                            break;
                        ///
                        /// 
                        /// 
                        
                        ///
                        /// Handle any unknown codes with a warning
                        ///
                        default:
                            Log.Warning($"Unhandled SB9600 broadcast opcode {msg.Opcode}");
                            break;
                    }
                    break;
                ///
                /// Radio Module
                ///
                case (byte)SB9600Addresses.RADIO:
                    switch (msg.Opcode)
                    {
                        ///
                        /// Radio Ready Opcode
                        ///
                        case (byte)SB9600Opcodes.RADRDY:
                            Log.Debug($"Got RADRDY opcode. Data: {msg.Data}");
                            break;
                        ///
                        /// Radio Key
                        /// 
                        case (byte)SB9600Opcodes.RADKEY:
                            if (msg.Data[1] == 0x01)
                            {
                                if (radioStatus.State != RadioState.Transmitting)
                                {
                                    Log.Information("Radio now transmitting");
                                    radioStatus.State = RadioState.Transmitting;
                                    newStatus = true;
                                }
                            }
                            else
                            {
                                if (radioStatus.State != RadioState.Receiving && radioStatus.State != RadioState.Idle)
                                {
                                    Log.Information("Radio no longer transmitting");
                                    radioStatus.State = RadioState.Idle;
                                    newStatus = true;
                                }
                            }
                            break;
                        ///
                        /// RX Audio Routing
                        ///
                        case (byte)SB9600Opcodes.RXAUD:
                            Log.Debug($"Got new audio routing: {msg.Data}");
                            break;
                        ///
                        /// Audio Muting
                        ///
                        case (byte)SB9600Opcodes.AUDMUT:
                            if (msg.Data[1] == 0x01)
                                Log.Debug("Radio audio unmuted");
                            else
                                Log.Debug("Radio audio muted");
                            break;
                        ///
                        /// Squelch Detect
                        /// Used for RX state detection
                        /// 
                        case (byte)SB9600Opcodes.SQLDET:
                            // Channel Idle
                            if (msg.Data[1] == 0x00)
                            {
                                if (radioStatus.State != RadioState.Idle && radioStatus.State != RadioState.Transmitting)
                                {
                                    radioStatus.State = RadioState.Idle;
                                    newStatus = true;
                                }
                            }
                            // Channel RX
                            else if (msg.Data[1] == 0x03)
                            {
                                if (radioStatus.State != RadioState.Receiving && radioStatus.State != RadioState.Transmitting)
                                {
                                    radioStatus.State = RadioState.Receiving;
                                    newStatus = true;
                                }
                            }
                            // This one comes up when we get activity on a channel while scanning, we ignore it for now
                            else if (msg.Data[1] == 0x01) { }
                            // Throw a warning on any unknown states
                            else
                            {
                                Log.Warning($"Got unknown SQLDET status {msg.Data[1]}");
                            }
                            break;
                        ///
                        /// Active Mode Update Message
                        /// 
                        case (byte)SB9600Opcodes.ACTMDU:
                            Log.Information($"Got ACTMDU for mode number {msg.Data[1]} with options {msg.Data[0]}");
                            break;
                        ///
                        /// PL Detect
                        /// 
                        case (byte)SB9600Opcodes.PLDECT:
                            switch (msg.Data[1])
                            {
                                case 0x00:
                                    Log.Debug("No PL, channel unqualified");
                                    break;
                                case 0x01:
                                    Log.Debug("No PL, channel qualified");
                                    break;
                                case 0x02:
                                    Log.Debug("Valid PL, channel unqualified");
                                    break;
                                case 0x03:
                                    Log.Debug("Valid PL, channel qualified");
                                    break;
                            }
                            break;
                        ///
                        /// Display
                        /// 
                        case (byte)SB9600Opcodes.DISPLY:
                            Log.Debug($"Got DISPLY update for field {msg.Data[0]}, data: {msg.Data[1]}");
                            break;
                        ///
                        /// Unknown opcode that pops up on XTLs for some reason
                        /// 
                        case 0x60:
                            break;
                        ///
                        /// Throw warning for any unhandled opcodes
                        ///
                        default:
                            Log.Warning($"Unhandled SB9600 radio opcode {msg.Opcode}");
                            break;
                    }
                    break;
                ///
                /// Front Panel Module
                ///
                case (byte)SB9600Addresses.FRONTPANEL:
                    switch (msg.Opcode)
                    {
                        ///
                        /// Button Control Opcode
                        ///
                        case (byte)SB9600Opcodes.BUTCTL:
                            // Lookup the button
                            string buttonName = ControlHeads.GetButton(Head, msg.Data[0]);
                            // Ignore knobs for now
                            if (buttonName.Contains("knob")) { }
                            else
                            {
                                if (msg.Data[1] == 0x01)
                                {
                                    Log.Information($"Button {buttonName} pressed");
                                }
                                else
                                {
                                    Log.Information($"Button {buttonName} released");
                                }
                            }
                            break;
                        ///
                        /// Backlight/Illumination
                        /// 
                        case (byte)SB9600Opcodes.LUMCTL:
                            break;
                        ///
                        /// Deafult handler for unknown opcodes
                        ///
                        default:
                            Log.Warning($"Unhandled SB9600 front panel opcode {msg.Opcode}");
                            break;
                    }
                    break;
                default:
                    Log.Warning($"Unhandled SB9600 address: {msg.Address}");
                    break;
            }

            return true;
        }

        private int processSBEP(byte[] msgBytes)
        {
            Log.Debug($"Processing SBEP message {msgBytes}");

            int totalBytes = 0;
            byte[] origMsg = msgBytes;

            // Ignore the 0x50 ACK if there is one
            if (msgBytes[0] == 0x50)
            {
                // Get rid of the byte
                msgBytes = msgBytes[1..];
                totalBytes += 1;
                Log.Debug($"Ignoring SBEP 0x50 ACK");
            }

            // Try to decode from the msg buffer
            SBEPMsg msg = new SBEPMsg();
            int msgLength = msg.Decode(msgBytes);

            // If we got a valid message, handle it
            if (msgLength > 0)
            {
                switch (msg.Opcode)
                {
                    ///
                    /// SBEP Display update
                    /// We handle all zone/channel text updates here
                    ///
                    case (byte)SBEPOpcodes.DISPLAY:
                        Log.Debug("Got SBEP display update");
                        // Get display update params
                        byte row = msg.Data[0];
                        byte col = msg.Data[1];
                        byte chars = msg.Data[2];
                        byte srow = msg.Data[3];
                        byte scol = msg.Data[4];
                        // Extract display characters
                        string text = Encoding.ASCII.GetString(msg.Data, 5, chars);
                        Log.Verbose($"Got text string ({chars}) from SBEP: {text}");
                        // Update head parameters depending on head type
                        switch (Head)
                        {
                            ///
                            /// W9 is a single-line display
                            /// so we extract both zone & channel text lookups from one display line
                            ///
                            case HeadType.W9:
                                Log.Verbose($"Got W9 display update for row/col {row}/{col}");
                                string newDisplay = displayText1.Substring(0, scol) + text + displayText1.Substring(scol);
                                Log.Debug($"Got new display text: {newDisplay}");
                                if (newDisplay != displayText1)
                                {
                                    // Update our display text
                                    displayText1 = newDisplay;
                                    // By default we use the full display as the radio's channel text
                                    radioStatus.ChannelName = displayText1;
                                    // Lookup zone text match, if any
                                    if (ZoneLookups.Count > 0)
                                    {
                                        foreach (TextLookup lookup in ZoneLookups)
                                        {
                                            if (displayText1.Contains(lookup.Match))
                                            {
                                                radioStatus.ZoneName = lookup.Replacement;
                                                Log.Debug($"Found zone text {radioStatus.ZoneName} from {lookup.Match} in display text {displayText1}");
                                                // Replace the channel text with the display text minus the matched characters
                                                radioStatus.ChannelName = displayText1.Replace(lookup.Match, "");
                                            }
                                        }
                                    }
                                    // Lookup channel text match, if any
                                    if (ChanLookups.Count > 0)
                                    {
                                        foreach (TextLookup lookup in ChanLookups)
                                        {
                                            if (radioStatus.ChannelName.Contains(lookup.Match))
                                            {
                                                radioStatus.ChannelName = lookup.Replacement;
                                                Log.Debug($"Found channel text {radioStatus.ChannelName} from {lookup.Match} in original text {radioStatus.ChannelName}");
                                            }
                                        }
                                    }
                                    // Flag that we've got a new status
                                    newStatus = true;
                                }
                                break;
                            ///
                            /// M3 is a two-line display
                            /// so we extract the zone/channel information from each line accordingly
                            ///
                            case HeadType.M3:
                                // Top row is zone text
                                if (srow == 0)
                                {
                                    // Replace the specified indexes based on starting column value
                                    displayText1 = displayText1.Substring(0, scol) + text + displayText1.Substring(scol);
                                    // Verify that the new text is not an ignored string
                                    if (ControlHeads.M3.IgnoredStrings.IndexOf(displayText1) == -1)
                                    {
                                        radioStatus.ZoneName = displayText1;
                                        Log.Debug($"Got new zone text for radio: {radioStatus.ZoneName}");
                                        // Perform lookup
                                        if (ZoneLookups.Count > 0)
                                        {
                                            foreach (TextLookup lookup in ZoneLookups)
                                            {
                                                if (radioStatus.ZoneName.Contains(lookup.Match))
                                                {
                                                    radioStatus.ZoneName = lookup.Replacement;
                                                    Log.Debug($"Found zone text {radioStatus.ZoneName} from {lookup.Match} in original text {displayText1}");
                                                }
                                            }
                                        }
                                        // Set flag
                                        newStatus = true;
                                    }
                                }
                                // Bottom row is channel text
                                else if (srow == 1)
                                {
                                    // Replace updated characters
                                    displayText2 = displayText2.Substring(0, scol) + text + displayText2.Substring(scol);
                                    // Verify that it's not an ignored string
                                    if (ControlHeads.M3.IgnoredStrings.IndexOf(displayText2) == -1)
                                    {
                                        radioStatus.ChannelName = displayText2;
                                        Log.Debug($"Got new channel text for radio: {radioStatus.ChannelName}");
                                        // Perform lookup
                                        if (ChanLookups.Count > 0)
                                        {
                                            foreach (TextLookup lookup in ChanLookups)
                                            {
                                                if (radioStatus.ChannelName.Contains(lookup.Match))
                                                {
                                                    radioStatus.ChannelName = lookup.Replacement;
                                                    Log.Debug($"Found channel text {radioStatus.ChannelName} from {lookup.Match} in original text {displayText2}");
                                                }
                                            }
                                        }
                                        // Set flag
                                        newStatus = true;
                                    }
                                }
                                break;
                        }
                        break;
                    ///
                    /// RF hardware test
                    /// 
                    case (byte)SBEPOpcodes.RFTEST:
                        Log.Debug("Got SBEP RF hardware test");
                        break;
                    ///
                    /// Default warning for unhandled SBEP codes
                    ///
                    default:
                        Log.Warning($"Got unhandled SBEP opcode {msg.Opcode}");
                        break;
                }
            }

            // Return the total number of bytes we read
            return totalBytes + msgLength;
        }

        private void processData(byte[] data)
        {
            // Handle SBEP if we're in SBEP
            if (inSbep)
            {
                // Decode SBEP message
                int processed = processSBEP(data);
                inSbep = false;
                Log.Debug("Exiting SBEP");
                if (processed == 0)
                {
                    Log.Error("Failed to process SBEP message!");
                }
                // Handle any remaining messages
                if (data.Length > processed)
                {
                    data = data[processed..];
                    Log.Debug($"Processing remaining SB9600 data: {data}");
                    processData(data);
                }
            }
            // Handle SB9600 otherwise
            else
            {
                // Handle the buffer until we don't have 5 bytes left
                while (data.Length >= 5)
                {
                    byte[] curMsg = data[0..5];
                    processSB9600(curMsg);
                    data = data[5..];
                    // if we were commanded to go into SBEP by the last message
                    // process the next message as SBEP
                    if (inSbep && data.Length > 5)
                    {
                        inSbep = false;
                        int processed = processSBEP(data);
                        data = data[processed..];
                    }
                }
            }
        }

        /// <summary>
        /// Infinite loop task to read/write to serial port
        /// </summary>
        /// <param name="_token"></param>
        private void serialLoop(object _token)
        {
            var token = (CancellationToken) _token;

            // Open the serial port.
            Log.Verbose("Opening port...");
            Port.Open();
            Log.Verbose("Port opened");

            // If we have an initial BUSY on startup, wait for that to clear and then flush the buffer
            while (getBusy() && !token.IsCancellationRequested) { }
            Port.DiscardInBuffer();
            Port.DiscardOutBuffer();
            Log.Verbose("Buffers cleared");

            // Reset the radio
            if (!noReset)
            {
                Log.Debug("Resetting radio");
                if (!Reset())
                {
                    Log.Error("Failed to reset the radio! Exiting...");
                    Daemon.Shutdown();
                    return;
                }
            }

            Log.Debug("SB9600 service running");
            while (!token.IsCancellationRequested)
            {
                // Wait for BUSY to clear
                while (getBusy() && !token.IsCancellationRequested) { }

                // Receive first
                // Check if we're actively receiving
                if (getBusy() && waiting) { }
                // Check if we just started receicing
                else if (getBusy() && !waiting)
                    waiting = true;
                // Message is done, so process it
                else if (waiting && !getBusy())
                {
                    waiting = false;
                    // Read message
                    byte[] rxMsg = new byte[Port.BytesToRead];
                    Port.Read(rxMsg, 0, Port.BytesToRead);
                    // Process
                    processData(rxMsg);
                    // Clear buffers
                    Port.DiscardInBuffer();
                    Port.DiscardOutBuffer();
                }

                // Transmit next
                // Send a message from the queue if we're not waiting on RX and not busy
                if (!waiting && !getBusy())
                {
                    // Try and get a message and send it
                    QueueMessage msg = null;
                    if (msgQueue.TryDequeue(out msg))
                    {
                        // SB9600
                        if (msg.sb9600msg != null)
                        {
                            sendSb9600(msg.sb9600msg);
                        }
                    }
                }

                // Update radio status if new status is received
                if (newStatus)
                {
                    newStatus = false;
                    StatusCallback();
                }

                // Check for any delayed commands that need to be sent
                long curTimeMs = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;
                foreach (DelayedMessage msg in delayedMessages)
                {
                    if (curTimeMs > msg.ExecTime)
                    {
                        if (msg.SB9600msg != null)
                        {
                            sendSb9600(msg.SB9600msg);
                        }
                    }
                }
            }
        }
    }
}
