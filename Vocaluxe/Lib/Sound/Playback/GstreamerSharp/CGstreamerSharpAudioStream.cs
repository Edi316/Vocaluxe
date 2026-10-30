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
using GLib;
using Gst;
using Vocaluxe.Base;
using VocaluxeLib.Log;

namespace Vocaluxe.Lib.Sound.Playback.GstreamerSharp
{
    class CGstreamerSharpAudioStream : CAudioStreamBase
    {
        // GstPlayFlags of playbin
        private const int _PlayFlagAudio = 1 << 1;
        private const int _PlayFlagSoftVolume = 1 << 4;

        // With prescan, wait at most this long for the stream to initialize, afterwards duration queries are done asynchronously
        private static readonly ulong _PrescanTimeout = 500UL * (ulong)Constants.MSECOND;

        // Streams that are still being terminated in the background (see _Dispose)
        private static readonly object _TerminationLock = new object();
        private static int _PendingTerminations;

        private readonly string _MediaUri;
        private volatile Element _Element;
        private Bus _Bus;
        private bool _FileOpened;

        private volatile bool _IsFinished;

        private volatile float _Position;

        //Hold this if you set _Element/_Bus=null, when accessing _Bus or when accessing _Element from outside the main thread
        private readonly object _ElementLock = new object();

        public override float Volume
        {
            get => base.Volume;
            set
            {
                base.Volume = value;
                _SetElementVolume();
            }
        }

        public override float VolumeMax
        {
            get => base.VolumeMax;
            set
            {
                base.VolumeMax = value;
                _SetElementVolume();
            }
        }

        public override float Duration
        {
            get
            {
                // Required in cases where the length is not known at startup
                if (base.Duration < 0 && _Element != null)
                {
                    _UpdateDuration();
                }

                return base.Duration >= 0f ? base.Duration : 0f;
            }
        }

        public override float Position
        {
            get
            {
                // Copy to a local, _Element might be set to null by another thread at any time
                var element = _Element;
                if (element == null)
                {
                    return _Position;
                }

                // No position available while changing state or when not prerolled (Null/Ready)
                var currentState = element.CurrentState;
                if (currentState < State.Paused || (currentState == State.Paused && element.PendingState != State.VoidPending))
                {
                    return _Position;
                }

                // Query may fail temporarily (e.g. while seeking), so don't spam the log, just keep the last known position
                if (element.QueryPosition(Format.Time, out var position))
                {
                    _Position = (float)position / Constants.SECOND;
                }

                return _Position;
            }
            set
            {
                var element = _Element;
                if (element == null)
                {
                    return;
                }

                _Position = value;
                _IsFinished = false;
                element.SeekSimple(Format.Time, SeekFlags.Accurate | SeekFlags.Flush, (long)(value * Constants.SECOND));
            }
        }

        public override bool IsFinished => _Element == null || _IsFinished;

        public override bool IsPaused
        {
            get
            {
                var element = _Element;
                return element == null || element.TargetState == State.Paused;
            }
            set => _Element?.SetState(value ? State.Paused : State.Playing);
        }

        public CGstreamerSharpAudioStream(int id, string mediaUri, bool loop, EAudioEffect effect = EAudioEffect.None) : base(id, loop, effect)
        {
            _MediaUri = mediaUri;
        }

        public override bool Open(bool prescan)
        {
            System.Diagnostics.Debug.Assert(!_FileOpened);
            if (_FileOpened)
            {
                return false;
            }

            Duration = -1;
            _Position = 0;
            _IsFinished = false;

            if (!Uri.TryCreate(_MediaUri, UriKind.Absolute, out var uri))
            {
                CLog.Error("Invalid media uri: " + _MediaUri);
                return false;
            }

            var audioSinkBin = _CreateAudioSinkBin();
            if (audioSinkBin == null)
            {
                return false;
            }

            var element = ElementFactory.Make("playbin", "playbin");
            if (element == null)
            {
                CLog.Error("Could not create playbin");
                audioSinkBin.Dispose();
                return false;
            }

            element["audio-sink"] = audioSinkBin;
            element["flags"] = _PlayFlagAudio | _PlayFlagSoftVolume;
            element["uri"] = uri.AbsoluteUri;
            element.Connect("about-to-finish", _OnAboutToFinish);

            lock (_ElementLock)
            {
                _Bus = element.Bus; // Get it only once, each access creates a new reference
                _Element = element;
            }

            if (element.SetState(State.Paused) == StateChangeReturn.Failure)
            {
                // Details (if any) are posted as error message on the bus
                _OnMessage(_Bus.TimedPopFiltered(0UL, MessageType.Error));
                CLog.Error("Could not open " + _MediaUri);
                _Dispose(true);
                return false;
            }

            // Passing CLOCK_TIME_NONE here causes the pipeline to block for a long time so with
            // prescan enabled the pipeline will wait 500ms for stream to initialize and then continue
            // if it takes more than 500ms, duration queries will be performed asynchronously
            var msg = _Bus.TimedPopFiltered(prescan ? _PrescanTimeout : 0UL, MessageType.AsyncDone | MessageType.Error);
            if (!_OnMessage(msg))
            {
                _Dispose(true);
                return false;
            }

            _FileOpened = true;
            return true;
        }

        /// <summary>
        /// Creates the bin: audioconvert [-> audiokaraoke] -> autoaudiosink
        /// </summary>
        private Bin _CreateAudioSinkBin()
        {
            var convert = ElementFactory.Make("audioconvert", "convert");
            var audiosink = ElementFactory.Make("autoaudiosink", "audiosink");

            if (convert == null || audiosink == null)
            {
                CLog.Error("Could not create pipeline");
                _DisposeAll(convert, audiosink);
                return null;
            }

            Element audioKaraoke = null;
            if (_Effect.HasFlag(EAudioEffect.Karaoke))
            {
                audioKaraoke = ElementFactory.Make("audiokaraoke", "karaoke");
                if (audioKaraoke == null)
                {
                    CLog.Error("Could not create karaoke effect (gst-plugins-good missing?), playing without effect");
                }
            }

            var audioSinkBin = new Bin("Audiosink");
            audioSinkBin.Add(convert);
            audioSinkBin.Add(audiosink);

            bool linked;
            if (audioKaraoke != null)
            {
                audioSinkBin.Add(audioKaraoke);
                audioKaraoke["level"] = CConfig.Config.Sound.KaraokeEffectLevel;
                audioKaraoke["mono-level"] = CConfig.Config.Sound.KaraokeEffectLevel;
                linked = convert.Link(audioKaraoke) && audioKaraoke.Link(audiosink);
            }
            else
            {
                linked = convert.Link(audiosink);
            }

            if (!linked)
            {
                CLog.Error("Could not link pipeline elements");
                _DisposeAll(convert, audiosink, audioKaraoke, audioSinkBin);
                return null;
            }

            var pad = convert.GetStaticPad("sink");
            if (pad == null)
            {
                CLog.Error("Could not create pads");
                _DisposeAll(convert, audiosink, audioKaraoke, audioSinkBin);
                return null;
            }

            var ghostpad = new GhostPad("sink", pad);
            pad.Dispose();

            if (!ghostpad.SetActive(true))
            {
                CLog.Error("Could not link pads");
                _DisposeAll(ghostpad, convert, audiosink, audioKaraoke, audioSinkBin);
                return null;
            }

            if (!audioSinkBin.AddPad(ghostpad))
            {
                CLog.Error("Could not add pad");
                _DisposeAll(ghostpad, convert, audiosink, audioKaraoke, audioSinkBin);
                return null;
            }

            return audioSinkBin;
        }

        private static void _DisposeAll(params IDisposable[] items)
        {
            foreach (var item in items)
            {
                item?.Dispose();
            }
        }

        protected override void _Dispose(bool disposing)
        {
            Element element;
            Bus bus;
            lock (_ElementLock)
            {
                //Atomic get and set
                element = _Element;
                if (element == null)
                {
                    return;
                }

                bus = _Bus;
                _Element = null; //Now everything "seems" closed
                _Bus = null;
            }

            if (element.TargetState == State.Playing)
            {
                element.SetState(State.Paused); //Stop output
            }

            _CloseStreamListener?.OnCloseStream(this);

            //Now really close it in the background
            lock (_TerminationLock)
            {
                _PendingTerminations++;
            }

            System.Threading.Tasks.Task.Run(() => _TerminateStream(element, bus));
        }

        private static void _TerminateStream(Element element, Bus bus)
        {
            try
            {
                //One of these might take a bit, so better do this in the background to avoid lags
                element.SetState(State.Null);
                element.Dispose();
                bus?.Dispose();
            }
            catch (Exception e)
            {
                CLog.Error("Error while terminating Gstreamer stream: " + e.Message);
            }
            finally
            {
                lock (_TerminationLock)
                {
                    _PendingTerminations--;
                    System.Threading.Monitor.PulseAll(_TerminationLock);
                }
            }
        }

        /// <summary>
        /// Waits until all streams that are closed in the background are terminated.
        /// </summary>
        /// <returns>true if there are no pending terminations anymore, false on timeout</returns>
        internal static bool WaitForPendingTerminations(int timeoutMs)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            lock (_TerminationLock)
            {
                while (_PendingTerminations > 0)
                {
                    var remaining = timeoutMs - (int)stopwatch.ElapsedMilliseconds;
                    if (remaining <= 0)
                    {
                        return false;
                    }

                    System.Threading.Monitor.Wait(_TerminationLock, remaining);
                }

                return true;
            }
        }

        public override void Play()
        {
            var element = _Element;
            if (element == null)
            {
                return;
            }

            // Starting from Ready/Null restarts the stream from the beginning
            if (element.CurrentState <= State.Ready)
            {
                _IsFinished = false;
            }

            element.SetState(State.Playing);
        }

        public override void Stop()
        {
            // Seeking is not possible in Ready state, but going back to Paused/Playing from there starts at 0 anyway
            _Element?.SetState(State.Ready);
            _Position = 0;
        }

        private void _SetElementVolume()
        {
            var element = _Element;
            if (element != null)
            {
                // playbin's volume property is a gdouble
                element["volume"] = (double)(Volume * VolumeMax);
            }
        }

        private void _OnAboutToFinish(object sender, GLib.SignalArgs args)
        {
            if (!_Loop)
            {
                return;
            }

            lock (_ElementLock)
            {
                if (_Element != null)
                {
                    _Element["uri"] = new Uri(_MediaUri).AbsoluteUri;
                }
            }
        }

        /// <returns>false if an error occured</returns>
        private bool _OnMessage(Message msg)
        {
            if (msg == null || msg.Handle == IntPtr.Zero)
            {
                return true;
            }

            try
            {
                switch (msg.Type)
                {
                    case MessageType.Eos:
                        if (_Loop)
                        {
                            Position = 0;
                        }
                        else
                        {
                            _IsFinished = true;
                        }

                        break;
                    case MessageType.Error:
                        msg.ParseError(out GException error, out string debug);
                        CLog.Error($"Gstreamer error: message '{error?.Message}', code {error?.Code}, debug information: {debug}");
                        _IsFinished = true;
                        return false;
                    case MessageType.AsyncDone:
                    case MessageType.DurationChanged:
                        _UpdateDuration();
                        break;
                }

                return true;
            }
            finally
            {
                msg.Dispose();
            }
        }

        public override void Update()
        {
            base.Update();

            // Hold the lock so the bus can't be disposed by _TerminateStream while we use it
            lock (_ElementLock)
            {
                var bus = _Bus;
                while (bus != null && _Element != null && bus.HavePending())
                {
                    _OnMessage(bus.Pop());
                }
            }
        }

        private void _UpdateDuration()
        {
            lock (_ElementLock)
            {
                var element = _Element;
                if (element == null)
                {
                    return;
                }

                if (element.QueryDuration(Format.Time, out long duration))
                {
                    Duration = duration / (float)Constants.SECOND;
                }
            }
        }
    }
}
