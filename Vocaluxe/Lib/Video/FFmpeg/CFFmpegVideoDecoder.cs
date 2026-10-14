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

using System.Diagnostics;
using System.IO;
using VocaluxeLib.Draw;

namespace Vocaluxe.Lib.Video.FFmpeg
{
    class CFFmpegVideoDecoder
    {
        private readonly Stopwatch _LoopTimer = new();

        private float _Gap;
        private bool _Paused;
        private float _LoopTime;

        private CFFmpegVideoDecoderThread _Thread;
        public float Duration { get; private set; }
        public bool Finished { get; private set; }

        public CFFmpegVideoDecoder()
        {
            Duration = 0;
            Finished = true;
        }

        public bool Loop
        {
            get => _Thread != null && _Thread.Loop;
            set
            {
                if (_Thread == null)
                {
                    return;
                }

                _Thread.Loop = value;
                if (value)
                {
                    _LoopTime = _Thread.RequestTime;
                    // Timer nur laufen lassen, wenn nicht pausiert
                    _LoopTimer.Reset();
                    if (!_Paused)
                    {
                        _LoopTimer.Start();
                    }
                }
            }
        }

        public bool Paused
        {
            set
            {
                if (_Paused == value)
                {
                    return;
                }

                _Paused = value;
                if (_Paused)
                {
                    _LoopTimer.Stop();
                    _Thread?.Pause();
                }
                else
                {
                    if (Loop)
                    {
                        _LoopTimer.Start();
                    }

                    _Thread?.Resume();
                }
            }
        }

        public bool LoadStream(Stream stream)
        {
            if (_Thread != null)
            {
                return false;
            }

            var thread = new CFFmpegVideoDecoderThread();
            if (thread.LoadStream(stream))
            {
                _Thread = thread;
                Duration = (float)_Thread.Duration.TotalSeconds;
                Finished = false;
                if (_Thread.Start())
                {
                    return true;
                }

                thread.Stop();
            }

            _Thread = null;
            return false;
        }

        public void Close()
        {
            _Thread?.Stop();
            _Thread = null; // Der Thread beendet sich selbst und gibt FFmpeg-Ressourcen frei; dieses Objekt kann neu geladen werden
            _LoopTimer.Reset();
            _LoopTime = 0;
            _Gap = 0;
            _Paused = false;
            Duration = 0;
            Finished = true;
        }

        public bool GetFrame(ref CTextureRef frame, float time, out float videoTime)
        {
            if (Finished || _Thread == null)
            {
                videoTime = Duration - _Gap;
                return false;
            }

            if (Loop)
            {
                time = _LoopTime + _LoopTimer.ElapsedMilliseconds / 1000f;
                // Duration <= 0 (unbekannte Laenge) darf nie zu einer Endlosschleife fuehren
                if (Duration > 0 && time >= Duration)
                {
                    time %= Duration;
                    _LoopTime = time;
                    _LoopTimer.Restart();
                }
            }
            else
            {
                time += _Gap;
            }

            _Thread.SyncTime(time);

            bool finished;
            _Thread.GetFrame(ref frame, ref time, out finished);
            videoTime = time - _Gap;

            if (finished) //Only set, not reset
            {
                Finished = true;
            }

            return frame != null;
        }

        public bool Skip(float start, float gap)
        {
            if (_Thread == null)
            {
                return false;
            }

            Finished = false;
            _Gap = gap;
            _Thread.Skip(start + gap);
            if (Loop)
            {
                Loop = true; //Reset loop (timer)
            }

            return true;
        }
    }
}
