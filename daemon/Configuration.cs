using moto_sb9600;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace daemon
{
    /// <summary>
    /// Base daemon config
    /// </summary>
    public class DaemonConfig
    {
        /// <summary>
        /// Name of this daemon
        /// </summary>
        public string Name = "";
        /// <summary>
        /// Description of this daemon
        /// </summary>
        public string Desc = "";
        /// <summary>
        /// Listen address the console client connects to
        /// </summary>
        public IPAddress ListenAddress = IPAddress.Parse("127.0.0.1");
        /// <summary>
        /// Listen port the console client connects to
        /// </summary>
        public int ListenPort = 8801;
    }

    /// <summary>
    /// Valid radio control types
    /// </summary>
    public enum RadioControlMode
    {
        VOX = 0,
        TRC = 1,
        SB9600 = 2,
        XCMP_SER = 3,
        XCMP_USB = 4
    }

    /// <summary>
    /// Radio control config
    /// </summary>
    public class ControlConfig
    {
        /// <summary>
        /// Control mode for this daemon
        /// </summary>
        public RadioControlMode ControlMode = RadioControlMode.SB9600;
        /// <summary>
        /// Whether the radio is RX only (TX disabled)
        /// </summary>
        public bool RxOnly = false;
        /// <summary>
        /// Config for motorola SB9600
        /// </summary>
        public MotoSb9600Config Sb9600 = new MotoSb9600Config();
    }

    /// <summary>
    /// Radio audio config
    /// </summary>
    public class AudioConfig
    {
        /// <summary>
        /// TX audio device for radio (speakers)
        /// </summary>
        public string TxDevice = "";
        /// <summary>
        /// RX audio device for radio (microphone)
        /// </summary>
        public string RxDevice = "";
    }

    public class TextLookupConfig
    {
        public List<rc2_core.TextLookup> Zone = new List<rc2_core.TextLookup>();

        public List<rc2_core.TextLookup> Channel = new List<rc2_core.TextLookup>();
    }

    public class ConfigObject
    {
        /// <summary>
        /// Daemon config
        /// </summary>
        public DaemonConfig Daemon = new DaemonConfig();
        /// <summary>
        /// Control config
        /// </summary>
        public ControlConfig Control = new ControlConfig();
        /// <summary>
        /// Audio config
        /// </summary>
        public AudioConfig Audio = new AudioConfig();
        /// <summary>
        /// Text lookup configuration
        /// </summary>
        public TextLookupConfig TextLookups = new TextLookupConfig();
        /// <summary>
        /// Softkey list
        /// </summary>
        public List<rc2_core.SoftkeyName> Softkeys = new List<rc2_core.SoftkeyName>();
    }
}
