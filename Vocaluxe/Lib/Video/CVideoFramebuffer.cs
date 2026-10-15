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
using FFmpeg.AutoGen;

namespace Vocaluxe.Lib.Video
{
    /// <summary>
    ///     Single-Producer/Single-Consumer Ringpuffer fuer Videoframes.
    ///     Writer-Thread: Put, SetWritten. Reader-Thread: ResetStack, Pop, CFrame.SetRead, Clear.
    ///     Es sind maximal Size-1 Frames gleichzeitig speicherbar (ein Slot bleibt frei, um voll von leer zu unterscheiden).
    ///     Die Slots halten Referenzen auf FFmpeg-Frames (nicht verwalteter Speicher), die Bilddaten werden nicht kopiert.
    /// </summary>
    unsafe class CVideoFramebuffer : IDisposable
    {
        public class CFrame
        {
            private readonly CVideoFramebuffer _Parent;
            private readonly int _Index;
            public readonly AVFrame* Frame;
            public float Time = -1;

            public CFrame(CVideoFramebuffer parent, int index)
            {
                _Parent = parent;
                _Index = index;
                Frame = ffmpeg.av_frame_alloc();
                if (Frame == null)
                {
                    throw new OutOfMemoryException("Unable to alloc video frame");
                }
            }

            //Only call from reader thread
            public void SetRead()
            {
                _Parent._SetRead(_Index);
            }
        }

        private readonly CFrame[] _Frames;
        private volatile bool _Disposed;

        // _Last wird vom Writer (SetWritten) bewegt, _First vom Reader (SetRead/Clear).
        // volatile stellt sicher, dass der jeweils andere Thread aktuelle Werte sieht und die Frame-Daten
        // vor der Veroeffentlichung von _Last geschrieben sind.
        private volatile int _Last;
        private volatile int _First;
        private int _Next; // Reader only
        private volatile bool _Initialized;

        public int Size { get; }

        // Constructs a framebuffer with max. size frames
        public CVideoFramebuffer(int size)
        {
            Size = size;
            _Frames = new CFrame[size];
        }

        // Initializes the framebuffer
        // MUST be called before all others (und vor dem Start des Writer-Threads)
        public void Init()
        {
            for (var i = 0; i < Size; i++)
            {
                _Frames[i] ??= new CFrame(this, i);
            }

            _First = _Last = _Next = 0;
            _Initialized = true;
        }

        private int _GetNextIndex(int current)
        {
            current++;
            return current < Size ? current : 0;
        }

        public bool IsFull()
        {
            return _GetNextIndex(_Last) == _First;
        }

        public bool IsEmpty()
        {
            return _First == _Last;
        }

        //Only call from writer thread
        //Uebernimmt die Referenz von source (source ist danach leer), ohne die Bilddaten zu kopieren
        public bool Put(AVFrame* source, float time)
        {
            if (!_Initialized || _Disposed || IsFull())
            {
                return false;
            }

            var frame = _Frames[_Last];
            ffmpeg.av_frame_unref(frame.Frame);
            ffmpeg.av_frame_move_ref(frame.Frame, source);
            frame.Time = time;
            return true;
        }

        //Only call from writer thread after succesfull put
        public void SetWritten()
        {
            _Last = _GetNextIndex(_Last);
        }

        //Only call from reader thread
        public void ResetStack()
        {
            _Next = _First;
        }

        //Pops the next frame if available
        //You have to call ResetStack before the first call to this function and call the CFrame.SetRead to free the buffers
        //Only call from reader thread
        public CFrame Pop()
        {
            if (_Next == _Last)
            {
                return null;
            }

            var res = _Frames[_Next];
            _Next = _GetNextIndex(_Next);
            return res;
        }

        // Gibt alle Frames bis einschliesslich index frei, sofern index zu den bereits gepoppten Frames [_First, _Next) gehoert
        private void _SetRead(int index)
        {
            var first = _First;
            var popped = _Next - first; // Anzahl bereits gepoppter Frames
            if (popped < 0)
            {
                popped += Size;
            }

            var offset = index - first; // Position von index relativ zu _First
            if (offset < 0)
            {
                offset += Size;
            }

            if (offset < popped)
            {
                _First = _GetNextIndex(index);
            }
        }

        //Only call from reader thread
        public void Clear()
        {
            var last = _Last;
            _First = last;
            _Next = last;
        }

        //Gibt alle Frames frei. Erst aufrufen, wenn weder Writer noch Reader den Puffer noch benutzen.
        public void Dispose()
        {
            lock (_Frames)
            {
                if (_Disposed)
                {
                    return;
                }

                _Disposed = true;
                _Initialized = false;
                foreach (var frame in _Frames)
                {
                    if (frame == null)
                    {
                        continue;
                    }

                    var avFrame = frame.Frame;
                    ffmpeg.av_frame_free(&avFrame);
                }
            }
        }
    }
}
