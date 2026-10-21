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

using VocaluxeLib.Songs.Sources;

namespace VocaluxeLib.Utils.Player
{
    public class CSoundPlayer
    {
        private readonly object _soundLock = new object();

        protected volatile int _StreamId = -1;
        protected readonly float _FadeTime = CBase.Settings.GetSoundPlayerFadeTime();

        private volatile bool _IsPlaying;

        public bool Loop;

        /// <summary>
        ///     Gets the current stream position or sets it
        /// </summary>
        public float Position
        {
            set
            {
                lock (_soundLock)
                {
                    if (!SoundLoaded || CBase.Sound == null)
                    {
                        return;
                    }

                    CBase.Sound.SetPosition(_StreamId, value);
                }
            }
            get
            {
                lock (_soundLock)
                {
                    return !SoundLoaded || CBase.Sound == null ? -1 : CBase.Sound.GetPosition(_StreamId);
                }
            }
        }

        public float Length
        {
            get
            {
                lock (_soundLock)
                {
                    return !SoundLoaded || CBase.Sound == null ? -1 : CBase.Sound.GetLength(_StreamId);
                }
            }
        }

        public bool IsPlaying
        {
            get { return _IsPlaying; }
        }

        public bool IsFinished
        {
            get
            {
                if (Loop)
                {
                    return false;
                }

                lock (_soundLock)
                {
                    if (!_IsPlaying || !SoundLoaded || CBase.Sound == null)
                    {
                        return true;
                    }

                    return CBase.Sound.IsFinished(_StreamId);
                }
            }
        }

        public bool SoundLoaded
        {
            get { return _StreamId != -1; }
        }

        public virtual string DisplayName { get; private set; } = string.Empty;

        public CSoundPlayer(bool loop = false)
        {
            Loop = loop;
        }

        public void Load(ISoundSource source, float position = -1f, bool autoplay = false)
        {
            Close();

            if (source == null)
            {
                return;
            }

            lock (_soundLock)
            {
                if (CBase.Sound == null)
                {
                    return;
                }

                var streamId = CBase.Sound.Load(source, false, true);
                if (streamId < 0)
                {
                    return;
                }

                _StreamId = streamId;
                DisplayName = source.DisplayName;
            }

            if (position > 0f)
            {
                Position = position;
            }

            if (autoplay)
            {
                Play();
            }
        }

        /// <summary>
        ///     Starts or resumes the player
        /// </summary>
        /// <returns>True if state changed, false if nothing loaded or already playing</returns>
        public virtual bool Play()
        {
            lock (_soundLock)
            {
                if (!SoundLoaded || _IsPlaying || CBase.Sound == null)
                {
                    return false;
                }

                CBase.Sound.SetStreamVolume(_StreamId, 0);
                CBase.Sound.Fade(_StreamId, 100, _FadeTime);
                CBase.Sound.Play(_StreamId);
                _IsPlaying = true;
                return true;
            }
        }

        /// <summary>
        ///     Pauses the player
        /// </summary>
        /// <returns>True if state changed, false if nothing loaded or already paused</returns>
        public virtual bool Pause()
        {
            lock (_soundLock)
            {
                if (!SoundLoaded || CBase.Sound == null || CBase.Sound.IsPaused(_StreamId))
                {
                    return false;
                }

                CBase.Sound.Fade(_StreamId, 0, _FadeTime, EStreamAction.Pause);
                _IsPlaying = false;
                return true;
            }
        }

        /// <summary>
        ///     Stops the player (no playback and position is set to start)
        /// </summary>
        /// <returns>True if playback was stopped</returns>
        public virtual bool Stop()
        {
            lock (_soundLock)
            {
                if (!SoundLoaded || CBase.Sound == null)
                {
                    return false;
                }

                CBase.Sound.Fade(_StreamId, 0, _FadeTime, EStreamAction.Stop);
                _IsPlaying = false;
                return true;
            }
        }

        public virtual void Close()
        {
            lock (_soundLock)
            {
                if (!SoundLoaded)
                {
                    return;
                }

                var streamId = _StreamId;

                _StreamId = -1;
                DisplayName = string.Empty;
                _IsPlaying = false;

                CBase.Sound?.Fade(streamId, 0, _FadeTime, EStreamAction.Close);
            }
        }

        public void Update()
        {
            var stop = false;
            var restart = false;

            lock (_soundLock)
            {
                if (!_IsPlaying || !SoundLoaded || CBase.Sound == null)
                {
                    return;
                }

                var finished = CBase.Sound.IsFinished(_StreamId);
                if (Loop)
                {
                    // Restart
                    restart = finished;
                }
                else
                {
                    var len = CBase.Sound.GetLength(_StreamId);
                    var timeToPlay = len > 0f ? len - CBase.Sound.GetPosition(_StreamId) : _FadeTime + 1f;

                    stop = timeToPlay <= _FadeTime || finished;
                }
            }

            if (restart)
            {
                Stop();
                Play();
            }
            else if (stop)
            {
                Stop();
            }
        }
    }
}
