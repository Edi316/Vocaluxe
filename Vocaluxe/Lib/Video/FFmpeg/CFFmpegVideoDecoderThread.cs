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
using System.Threading;
using Vocaluxe.Base;
using VocaluxeLib.Draw;
using VocaluxeLib.Log;

namespace Vocaluxe.Lib.Video.FFmpeg
{
    //This class describes a thread decoding a video
    //All public methods are meant to be called from "reader" thread only
    //Most others are to be called by this thread  (_Thread instance) only!
    unsafe class CFFmpegVideoDecoderThread
    {
        private CFFmpegVideoDecoderContext _DecoderContext;

        private Thread _Thread;
        // Schuetzt: Framebuffer-Clear/-SetWritten, _RequestTime, _LoopWrapped, _RequestSkip (Schreibzugriffe)
        private readonly object _BufferMutex = new();

        private readonly CVideoFramebuffer _VideoFramebuffer;
        private float _LastDecodedTime; // time of last decoded frame (decoder thread only)
        private float _LastShownTime = -1f; // time if the last shown frame in s IMPORTANT: Write only in context of reader

        // time of last requested frame aka current should-be position (_LastDecodedTime should be >=_RequestTime)
        //HAS to be < Length
        private volatile float _RequestTime;
        public float RequestTime => _RequestTime;

        // Ersetzt die frühere Magic-Konstante: true, wenn der Decoder selbststaendig am Ende der Datei (Loop) auf 0 zurueckgesprungen ist,
        // der Reader aber noch Frames vom Ende des Videos anfragt. Wird bei jeder neuen Anfrage des Readers zurueckgesetzt.
        private volatile bool _LoopWrapped;
        private bool _Paused; // reader only

        private float _FrameDuration; // frame time in s (wird vor dem Start des Threads gesetzt)
        private TimeSpan _Duration = TimeSpan.Zero;

        private volatile bool _RequestSkip;
        private volatile bool _Terminated;
        private bool _FrameAvailable; // decoder thread only
        private volatile bool _NoMoreFrames;
        private volatile bool _Loop;
        private bool _DecodedAnyFrame; // decoder thread only: seit dem letzten Loop-Wrap wurde mindestens ein Frame decodiert
        private readonly AutoResetEvent _EvWakeUp = new(false); // Reader hat Platz im Puffer geschaffen / Skip / Stop
        private readonly AutoResetEvent _EvNoMoreFrames = new(false);
        private bool _DropSeekEnabled = true; // Used to fallback to frame skipping if seek is failing once on this file

        public TimeSpan Duration => _Duration;

        public bool Loop
        {
            get => _Loop;
            set => _Loop = value;
        }

        public CFFmpegVideoDecoderThread()
        {
            _VideoFramebuffer = new CVideoFramebuffer(10);
            _FrameDuration = 0.02f; // Set a reasonable standard till correct value is set
        }

        // Open the stream and get the length.
        // We now use stream because it can also handle http files ;)
        public bool LoadStream(Stream sourceStream)
        {
            if (_DecoderContext != null)
            {
                CLog.Error("A stream is already opened in the thread");
                return false;
            }

            _DecoderContext = new CFFmpegVideoDecoderContext();
            if (!_DecoderContext.Initialize(sourceStream))
            {
                _Free();
                _DecoderContext = null;
                return false;
            }

            _Duration = _DecoderContext.Duration;

            var framePerSeconds = _DecoderContext.FrameRate;
            if (framePerSeconds > 0)
            {
                _FrameDuration = 1 / framePerSeconds;
            }

            // Hier (synchron) statt im Decoder-Thread, damit der Reader nie einen uninitialisierten Puffer sieht
            _VideoFramebuffer.Init(_DecoderContext.FrameBufferSize);
            _FrameAvailable = false;
            return true;
        }

        public bool Start()
        {
            if (_DecoderContext == null)
            {
                CLog.Error("No decoder was init for for this thread");
                return false;
            }

            if (_Thread != null)
            {
                CLog.Error("Tried to start a video file that is already started");
                return false;
            }

            _RequestTime = 0f;
            _Thread = new Thread(_Execute)
            {
                Priority = ThreadPriority.Normal,
                IsBackground = true,
                Name = "VideoDecoder"
            };
            _Thread.Start();
            return true;
        }

        public void Stop()
        {
            _Terminated = true;
            _EvWakeUp.Set();
            _EvNoMoreFrames.Set();

            if (_Thread == null)
            {
                // Thread wurde nie gestartet -> niemand sonst gibt den Context frei
                _Free();
            }
        }

        public void Pause()
        {
            _Paused = true;
        }

        public void Resume()
        {
            _Paused = false;
        }

        //Sets the position to the given time discarding all decoded frames
        public void Skip(float time)
        {
            //Problem: Buffer is cleared from the reader thread while the decoder may be in _Decode or _Copy
            //Solution: Clear, RequestTime and _RequestSkip are changed atomically under the mutex and the decoder
            //checks _RequestSkip under the same mutex before it publishes a frame
            lock (_BufferMutex)
            {
                _VideoFramebuffer.Clear();
                _RequestTime = time;
                _LoopWrapped = false;
                _LastShownTime = time - _FrameDuration; //Set this to time to detect overflow of time in FindFrame but subtract FrameDuration so GetFrame will get the first frame
                _RequestSkip = true;
            }

            _EvNoMoreFrames.Set();
            _EvWakeUp.Set();
        }

        private CVideoFramebuffer.CFrame _FindFrame(float now)
        {
            CVideoFramebuffer.CFrame result = null;
            var loop = _Loop;
            var loopWrapped = _LoopWrapped;
            _VideoFramebuffer.ResetStack();
            while (_VideoFramebuffer.Pop() is { } frame)
            {
                var frameTime = frame.Time;

                if (frameTime > now)
                {
                    //2 Cases: all following frames are after this one, or we have a loop and 'now' wrapped over
                    //First case is if we have no loop or we did not wrap or frame is before last one (last is the case if frame is already one of the new iterations, e.g. Last=19 now=1 frame=2)
                    if (!loop || _LastShownTime <= now || frameTime < _LastShownTime)
                    {
                        break; //Following frames (incl this one) are after now, so do not consider any of them
                    }
                }
                else if (loop && loopWrapped)
                {
                    //Frame time might have wrapped but now did not
                    if (frameTime < _LastShownTime && _LastShownTime <= now)
                    {
                        break; //Following frames (incl this one) are after now, so do not consider any of them
                    }
                }

                //Get the last(newest) possible frame and skip the rest
                //Frame is to old -> Discard
                result?.SetRead();

                result = frame;
                if (_Paused)
                {
                    break; //Just get 1 frame if paused otherwise a paused movie could move a bit
                }
            }

            return result;
        }

        /// <summary>
        ///     Gets a frame
        /// </summary>
        /// <param name="frame">Referenz to texture where frame should be put in (can be null, then texture is created)</param>
        /// <param name="time">Maximum start time for frame</param>
        /// <param name="finished">Set to whether there are no more frames (stream finished, no loop, future calls will always return false)</param>
        /// <returns>True if a new frame was gotten</returns>
        public bool GetFrame(ref CTextureRef frame, ref float time, out bool finished)
        {
            bool result;
            if (Math.Abs(_LastShownTime - time) < _FrameDuration && frame != null) //Check 1 frame difference
            {
                time = _LastShownTime;
                result = false;
            }
            else
            {
                var curFrame = _FindFrame(time);
                if (curFrame != null)
                {
                    if (frame == null)
                    {
                        frame = CDraw.AddTexture(_DecoderContext.FrameWidth, _DecoderContext.FrameHeight, curFrame.Data);
                    }
                    else
                    {
                        CDraw.UpdateTexture(frame, _DecoderContext.FrameWidth, _DecoderContext.FrameHeight, curFrame.Data);
                    }

                    if (!_Paused)
                    {
                        curFrame.SetRead();
                    }

                    time = curFrame.Time;
                    _LastShownTime = time;
                    result = frame != null;
                }
                else
                {
                    result = false;
                }

                // Platz im Puffer geschaffen -> Decoder aufwecken (falls er auf einen vollen Puffer wartet)
                _EvWakeUp.Set();
            }

            finished = _NoMoreFrames && _VideoFramebuffer.IsEmpty();

            return result;
        }

        //Time should be < Length
        public void SyncTime(float time)
        {
            if (_Thread == null)
            {
                return; //Not initialized
            }

            // Unter dem Mutex, damit RequestTime/_LoopWrapped konsistent zu den Aenderungen des Decoder-Threads sind (Lock ist reentrant fuer Skip)
            lock (_BufferMutex)
            {
                var requestTime = _RequestTime;
                if (requestTime - time >= _FrameDuration)
                {
                    //Jump back more than 1 frame. To guarantee the order in the buffer use Skip() which clears the buffer
                    Skip(time);
                }
                else if (time - requestTime >= _FrameDuration)
                {
                    //we were more than 1 frame to slow -> Jump forward (This is save, as the decoder will skip frames if necessary)
                    if (_Loop && _LoopWrapped)
                    {
                        //In a loop our decoder may have reset RequestTime to 0 but we want a frame from the end of the video
                        //Skipping forward is fatal as it resets the decoder to decode already decoded frames causing lags
                        //So first check if we have a valid frame in our buffer
                        _VideoFramebuffer.ResetStack();
                        while (_VideoFramebuffer.Pop() is { } frame)
                        {
                            if (frame.Time + _FrameDuration >= time)
                            {
                                return;
                            }
                        }

                        //If we don't the Length might be inaccurate (e.g. last frame ends at 19.98 but Length=20)
                        if (time >= _Duration.TotalSeconds - 2 * _FrameDuration)
                        {
                            return;
                        }
                    }

                    _RequestTime = time;
                    _LoopWrapped = false;
                }
            }
        }

        //Just call this if thread is not alive
        private void _Free()
        {
            _DecoderContext?.Dispose();
        }

        // Skip to a given time (in s)
        private void _Skip()
        {
            var skipTime = _RequestTime; //Copy to variable to have consistent checks
            if (skipTime < 0 || (_Duration > TimeSpan.Zero && skipTime >= _Duration.TotalSeconds))
            {
                skipTime = 0;
            }

            try
            {
                if (!_DecoderContext.Seek(true, TimeSpan.FromSeconds(skipTime)))
                {
                    CLog.Error("Unable to skip video to " + skipTime);
                }
            }
            catch (Exception e)
            {
                CLog.Error("Error skipping video: " + e.Message);
            }

            _LastDecodedTime = skipTime;
            _FrameAvailable = false;
        }

        private void _Decode()
        {
            const int minFrameDropCount = 4;
            // With => seekThreshold frames to drop use seek instead of skip
            const int seekThreshold = 25; // 25 frames = 0.5 second with _FrameDuration = 0.02f

            if (_NoMoreFrames)
            {
                return;
            }

            var videoTime = _RequestTime;
            var timeDifference = videoTime - _LastDecodedTime;

            var dropFrame = timeDifference >= (minFrameDropCount - 1) * _FrameDuration;

            var hasFrameDecoded = false;
            if (dropFrame)
            {
                var frameDropCount = (int)Math.Ceiling(timeDifference / _FrameDuration);
                if (!_DropSeekEnabled || frameDropCount < seekThreshold)
                {
                    hasFrameDecoded = _DecoderContext.DropWithSkip(frameDropCount);
                }
                else
                {
                    hasFrameDecoded = _DropWithSeek(TimeSpan.FromSeconds(videoTime), frameDropCount);
                }
            }

            if (!hasFrameDecoded)
            {
                try
                {
                    hasFrameDecoded = _DecoderContext.GetFrame();
                }
                catch (Exception ex)
                {
                    CLog.Error("Unable to get frame " + ex);
                }
            }

            if (hasFrameDecoded)
            {
                _FrameAvailable = true;
                _DecodedAnyFrame = true;
            }
            else if (_Loop && _DecodedAnyFrame)
            {
                // Ende erreicht -> von vorne beginnen. Ein Skip des Readers hat Vorrang.
                lock (_BufferMutex)
                {
                    if (_RequestSkip)
                    {
                        return;
                    }

                    _RequestTime = 0f;
                    _LoopWrapped = true;
                }

                _DecodedAnyFrame = false;
                _Skip();
            }
            else
            {
                // Kein Loop, oder nach dem letzten Neustart kam kein einziger Frame (defekte Datei) -> kein Endlos-Loop
                _NoMoreFrames = true;
            }
        }

        private bool _DropWithSeek(TimeSpan videoTime, int frameDropCount)
        {
            var seekOk = false;
            try
            {
                seekOk = _DecoderContext.Seek(true, videoTime);
            }
            catch (Exception e)
            {
                CLog.Error("Error seeking frame: " + e.Message);
            }

            if (!seekOk)
            {
                // Fallback to frame skipping
                _DropSeekEnabled = false;
                return _DecoderContext.DropWithSkip(frameDropCount);
            }

            // Seek landet auf dem Keyframe VOR dem Ziel -> bis kurz vor die Zielzeit decodieren (ohne Konvertierung)
            var target = TimeSpan.FromSeconds(Math.Max(0, videoTime.TotalSeconds - _FrameDuration));
            return _DecoderContext.DecodeUntil(target);
        }

        //Copies a frame to the buffer but does not set it as 'written'
        //Returns true if frame data is now in buffer
        private bool _CopyDecodedFrameToBuffer()
        {
            _LastDecodedTime = (float)_DecoderContext.Position.TotalSeconds;
            var result = _VideoFramebuffer.Put(_DecoderContext.DecodedFrameBuffer, _LastDecodedTime);
            _FrameAvailable = false;
            return result;
        }

        private void _Execute()
        {
            try
            {
                while (!_Terminated)
                {
                    if (_NoMoreFrames && !_RequestSkip)
                    {
                        _EvNoMoreFrames.WaitOne();
                        if (_Terminated)
                        {
                            break;
                        }
                    }

                    if (_RequestSkip)
                    {
                        _RequestSkip = false;
                        _NoMoreFrames = false;
                        _DecodedAnyFrame = true; // Nach explizitem Skip darf ein Loop-Wrap wieder stattfinden
                        _Skip();
                    }

                    if (!_FrameAvailable)
                    {
                        _Decode();
                    }

                    //Bail out if we want to skip
                    if (!_RequestSkip && _FrameAvailable)
                    {
                        if (!_VideoFramebuffer.IsFull())
                        {
                            if (_CopyDecodedFrameToBuffer())
                            {
                                //Do not write to buffer if we want to skip. So check and write have to be done atomicly
                                lock (_BufferMutex)
                                {
                                    if (_RequestSkip)
                                    {
                                        continue; //Frame is invalid if we want to skip
                                    }

                                    _VideoFramebuffer.SetWritten();
                                }
                            }
                        }
                        else
                        {
                            // Puffer voll: warten bis der Reader einen Frame verbraucht hat (mit Timeout, um Stop/Skip sicher zu bemerken)
                            _EvWakeUp.WaitOne(50);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                CLog.Error("Video decoder thread terminated unexpectedly: " + e);
                _NoMoreFrames = true; // Reader soll das Video als beendet sehen
            }
            finally
            {
                _Free();
            }
        }
    }
}
