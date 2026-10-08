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
using System.Collections.Generic;
using System.Threading;
using NAudio.Wave;

namespace Vocaluxe.Lib.Sound.Record.DirectSound
{
    public class CSoundCardSource : IDisposable
    {
        private WaveInEvent _WaveIn;
        private readonly string _Guid;      // enthält jetzt den Geräteindex
        private readonly short _Channels;
        private double _SampleRate = 44.1;
        private bool _Running;

        public CSoundCardSource(string guid, short channels)
        {
            _Guid = guid;
            _Channels = channels;
        }

        public event EventHandler<CSampleDataEventArgs> SampleDataReady = delegate { };

        public double SampleRateKhz
        {
            get { return _SampleRate; }
            set
            {
                _SampleRate = value;
                if (_Running)
                    Restart();
            }
        }

        public void Start()
        {
            if (_Running)
                throw new InvalidOperationException();

            _WaveIn = new WaveInEvent
            {
                DeviceNumber = int.Parse(_Guid),
                WaveFormat = new WaveFormat((int)(SampleRateKhz * 1000D), 16, _Channels),
                BufferMilliseconds = 46,   // ca. 2048 Samples bei 44,1 kHz
                NumberOfBuffers = 2
            };
            _WaveIn.DataAvailable += _OnDataAvailable;
            _Running = true;
            _WaveIn.StartRecording();
        }

        private void _OnDataAvailable(object sender, WaveInEventArgs e)
        {
            var data = new byte[e.BytesRecorded];
            Buffer.BlockCopy(e.Buffer, 0, data, 0, e.BytesRecorded);
            SampleDataReady(this, new CSampleDataEventArgs(data, _Guid));
        }

        public void Stop()
        {
            _Running = false;
            if (_WaveIn == null)
                return;
            _WaveIn.DataAvailable -= _OnDataAvailable;
            _WaveIn.StopRecording();
            _WaveIn.Dispose();
            _WaveIn = null;
        }

        public void Restart() { Stop(); Start(); }

        public void Dispose()
        {
            Stop();
            GC.SuppressFinalize(this);
        }
    }
}
