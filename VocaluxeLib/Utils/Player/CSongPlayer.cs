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
using VocaluxeLib.Draw;
using VocaluxeLib.Songs;
using VocaluxeLib.Songs.Sources;

namespace VocaluxeLib.Utils.Player
{
    public class CSongPlayer : CSoundPlayer
    {
        private readonly object _lock = new object();
        private CSong _Song;
        private bool _VideoEnabled;
        private CVideoStream _Video;
        private CFading _VideoFading;

        public ISoundSource SoundSource
        {
            get
            {
                var song = _Song;
                return song?.GetAudioSource();
            }
        }

        public bool VideoEnabled
        {
            get
            {
                lock (_lock)
                {
                    return _VideoEnabled;
                }
            }
            set
            {
                lock (_lock)
                {
                    if (_VideoEnabled == value)
                    {
                        return;
                    }

                    _VideoEnabled = value;
                    if (_VideoEnabled)
                    {
                        _LoadVideo();
                    }
                    else
                    {
                        _CloseVideo();
                    }
                }
            }
        }

        public int SongId
        {
            get
            {
                var song = _Song;
                return song?.Id ?? -1;
            }
        }

        public bool SongHasVideo
        {
            get
            {
                var song = _Song;
                return song?.HasVideo ?? false;
            }
        }

        public override string DisplayName
        {
            get
            {
                var song = _Song;
                if (song != null && !string.IsNullOrEmpty(song.Artist) && !string.IsNullOrEmpty(song.Title))
                {
                    return song.Artist + " - " + song.Title;
                }

                return base.DisplayName;
            }
        }

        public CTextureRef Cover
        {
            get
            {
                var song = _Song;
                return song == null ? CBase.Cover.GetNoCover() : song.CoverTextureBig;
            }
        }

        public CSongPlayer(bool loop = false) : base(loop) { }

        public CTextureRef GetVideoTexture()
        {
            lock (_lock)
            {
                var video = _Video;
                var song = _Song;
                if (video == null || video.IsClosed() || song == null || CBase.Video == null || CBase.Sound == null)
                {
                    return null;
                }

                if (!CBase.Video.GetFrame(video, CBase.Sound.GetPosition(_StreamId)))
                {
                    return null;
                }

                var texture = video.Texture;
                if (texture == null)
                {
                    return null;
                }

                if (_VideoFading != null)
                {
                    texture.Color.A = _VideoFading.GetValue(out var finished);
                    if (finished)
                    {
                        _VideoFading = null;
                    }
                }

                return texture;
            }
        }

        public void Load(CSong song, float position = 0f, bool autoplay = false)
        {
            if (song == null)
            {
                throw new ArgumentNullException("song");
            }

            lock (_lock)
            {
                Load(song.GetAudioSource(), position, autoplay);
                _Song = song;
                _LoadVideo();
            }
        }

        private void _LoadVideo()
        {
            lock (_lock)
            {
                var song = _Song;
                if (song == null)
                {
                    return;
                }

                if (_Video != null)
                {
                    if (!_Video.IsClosed())
                    {
                        return;
                    }

                    _Video = null;
                    _VideoFading = null;
                }

                if (CBase.Video == null)
                {
                    return;
                }

                var videoStream = song.GetVideoStream();
                if (videoStream == null)
                {
                    return;
                }

                var video = CBase.Video.LoadStream(videoStream);
                if (video == null)
                {
                    return;
                }

                _Video = video;
                _VideoFading = new CFading(0f, 1f, 3f);

                if (IsPlaying)
                {
                    CBase.Video.Skip(video, Position, song.VideoGap);
                    CBase.Video.Resume(video);
                }
                else
                {
                    CBase.Video.Skip(video, 0f, song.VideoGap);
                }
            }
        }

        public override bool Play()
        {
            lock (_lock)
            {
                if (!base.Play())
                {
                    return false;
                }

                var video = _Video;
                var song = _Song;
                if (video != null && !video.IsClosed() && song != null && CBase.Video != null)
                {
                    CBase.Video.Skip(video, Position, song.VideoGap);
                    CBase.Video.Resume(video);
                }

                return true;
            }
        }

        public override bool Pause()
        {
            lock (_lock)
            {
                if (!base.Pause())
                {
                    return false;
                }

                var video = _Video;
                if (video != null && !video.IsClosed() && CBase.Video != null)
                {
                    CBase.Video.Pause(video);
                }

                return true;
            }
        }

        public override bool Stop()
        {
            lock (_lock)
            {
                if (!base.Stop())
                {
                    return false;
                }

                var video = _Video;
                var song = _Song;
                if (video != null && !video.IsClosed() && CBase.Video != null)
                {
                    CBase.Video.Pause(video);
                    if (song != null)
                    {
                        CBase.Video.Skip(video, 0f, song.VideoGap);
                    }
                }

                return true;
            }
        }

        private void _CloseVideo()
        {
            lock (_lock)
            {
                if (_Video != null)
                {
                    CBase.Video?.Close(ref _Video);
                    _Video = null;
                    _VideoFading = null;
                }
            }
        }

        public override void Close()
        {
            lock (_lock)
            {
                base.Close();

                _Song = null;
                _CloseVideo();
            }
        }
    }
}
