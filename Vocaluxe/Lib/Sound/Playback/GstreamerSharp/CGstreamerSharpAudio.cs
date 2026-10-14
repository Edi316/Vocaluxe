#region license
// This file is part of Vocaluxe.
//
// Vocaluxe is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// Vocaluxe is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with Vocaluxe. If not, see <http://www.gnu.org/licenses/>.
#endregion

using System;
using System.IO;
using Gst;
using VocaluxeLib.Log;
using VocaluxeLib.Songs.Sources;
using VocaluxeLib.Utils.Player;

namespace Vocaluxe.Lib.Sound.Playback.GstreamerSharp
{
    public class CGstreamerSharpAudio : CPlaybackBase
    {
        // Max time to wait for streams that are still shutting down in the background before deinitializing GStreamer
        private const int _TerminateTimeoutMs = 2000;

        public override bool Init()
        {
            if (_Initialized)
            {
                return true;
            }
#if WIN
#if ARCH_X86
            const string varName = "GSTREAMER_1_0_ROOT_X86";
            const string localDir = "gstreamer86";
#elif ARCH_X64
            const string varName = "GSTREAMER_1_0_ROOT_X86_64";
            const string localDir = "gstreamer64";
#else
#error Either ARCH_X86 or ARCH_X64 has to be defined
#endif
            string dllDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "libs", "unmanaged", localDir, "bin");

            if (!Directory.Exists(dllDirectory))
            {
                // Process environment contains user and machine variables (the installer may set either of them)
                var gstreamerEnvVar = Environment.GetEnvironmentVariable(varName);

                if (string.IsNullOrEmpty(gstreamerEnvVar) || !Directory.Exists(gstreamerEnvVar))
                {
                    CLog.Error("Gstreamer not found! Make sure you installed it correctly and that it set the environment variable '" + varName + "'!", true);
                    return false;
                }

                dllDirectory = Path.Combine(gstreamerEnvVar, "bin");
            }

            COSFunctions.AddEnvironmentPath(dllDirectory);
#endif
            Application.Init();

            _Initialized = Application.IsInitialized;
            return _Initialized;
        }

        public override void Close()
        {
            if (!_Initialized)
            {
                return;
            }

            base.Close();

            // Streams are terminated in the background. Deinit while they are still running might crash.
            if (CGstreamerSharpAudioStream.WaitForPendingTerminations(_TerminateTimeoutMs))
            {
                Application.Deinit();
            }
            else
            {
                CLog.Error("Some Gstreamer streams did not terminate in time, skipping Gstreamer deinitialization");
            }

            _Initialized = false;
        }

        protected override IAudioStream _CreateStream(int id, ISoundSource source, bool loop, EAudioEffect effect = EAudioEffect.None)
        {
            return new CGstreamerSharpAudioStream(id, source.GetUri(), loop, effect);
        }
    }
}
