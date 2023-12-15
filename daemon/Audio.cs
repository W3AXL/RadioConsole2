using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MathNet.Numerics.Statistics;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.FFmpeg;
using SIPSorceryMedia.SDL2;

namespace daemon
{
    internal class Audio
    {
        public static bool IsSDLInit = false;
        /// <summary>
        /// Inits SDL2 if necessary
        /// </summary>
        public static void InitSDL()
        {
            if (!IsSDLInit)
            {
                SDL2Helper.InitSDL();
                IsSDLInit = true;
            }
        }

        public static bool CheckInputExists(string inputName)
        {
            InitSDL();
            if (SDL2Helper.GetAudioRecordingDevices().Contains(inputName)) { return true; } else { return false; }
        }

        public static bool CheckOutputExists(string outputName)
        {
            InitSDL();
            if (SDL2Helper.GetAudioPlaybackDevices().Contains(outputName)) { return true; } else { return false; }
        }
    }
}
