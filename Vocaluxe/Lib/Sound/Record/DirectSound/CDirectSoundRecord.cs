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

using System.Collections.Generic;
using System.Linq;
using NAudio.Wave;

namespace Vocaluxe.Lib.Sound.Record.DirectSound
{
    class CDirectSoundRecord : CRecordBase, IRecord
    {
        private bool _Initialized;

        private List<CSoundCardSource> _Sources;

        public override bool Init()
        {
            if (!base.Init())
            {
                return false;
            }

            _Sources = new List<CSoundCardSource>();

            for (int i = 0; i < WaveInEvent.DeviceCount; i++)
            {
                var caps = WaveInEvent.GetCapabilities(i);
                _Devices.Add(new CRecordDevice(_Devices.Count, caps.ProductName, i.ToString(), caps.Channels));
            }
            _Initialized = true;

            return true;
        }

        public override void Close()
        {
            Stop();
            _Initialized = false;
            base.Close();
        }

        public bool Start()
        {
            if (!_Initialized)
            {
                return false;
            }

            foreach (var buffer in _Buffer)
            {
                buffer.Reset();
            }

            foreach (var device in _Devices)
            {
                var usingDevice = false;
                for (var ch = 0; ch < device.Channels; ++ch)
                {
                    if (device.PlayerChannel[ch] > 0)
                    {
                        usingDevice = true;
                    }
                }

                if (usingDevice)
                {
                    var source = new CSoundCardSource(device.Driver, (short)device.Channels) { SampleRateKhz = 44.1 };
                    source.SampleDataReady += _OnDataReady;
                    source.Start();

                    _Sources.Add(source);
                }
            }

            return true;
        }

        public bool Stop()
        {
            if (!_Initialized)
            {
                return false;
            }

            foreach (var source in _Sources)
            {
                source.Stop();
                source.Dispose();
            }

            _Sources.Clear();

            return true;
        }

        private void _OnDataReady(object sender, CSampleDataEventArgs e)
        {
            if (!_Initialized)
            {
                return;
            }

            var dev = _Devices.FirstOrDefault(device => device.Driver == e.Guid);
            if (dev == null)
            {
                return;
            }

            _HandleData(dev, e.Data);
        }
    }
}
