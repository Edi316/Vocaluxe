using System;
using System.IO;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using Vocaluxe.Base;
using VocaluxeLib.Log;

namespace Vocaluxe.Lib.FFmpeg
{
    internal static class FFmpegHelper
    {
        public const int IOBufferSize = 32768;

        internal static void PrepareFFmpegBinaries()
        {
            var ffmpegPath = CConfig.Config.FFmpeg.FFmpegPath;
            if (string.IsNullOrEmpty(ffmpegPath))
            {
                throw new ArgumentException("FFmpeg path was not provided in configuration");
            }

            ffmpegPath = _ResolvePath(ffmpegPath);
            ffmpeg.RootPath = ffmpegPath;
            var version = _TryGetVersion();
            if (string.IsNullOrEmpty(version))
            {
                throw new ArgumentException($"Unable to find FFmpeg binaries in {ffmpegPath}");
            }

            CLog.Information($"FFmpeg binaries version {version} found in {ffmpegPath}");
        }

        private static string _ResolvePath(string path)
        {
            if (Path.IsPathRooted(path))
            {
                return path;
            }

            var candidate = Path.Combine(AppContext.BaseDirectory, path);
            return Directory.Exists(candidate) ? candidate : Path.Combine(Directory.GetCurrentDirectory(), path);
        }

        private static string _TryGetVersion()
        {
            try
            {
                return ffmpeg.av_version_info();
            }
            catch (Exception)
            {
                return null;
            }
        }

        internal static unsafe string ErrorToString(int errorCode)
        {
            const int bufferSize = 1024;
            var buffer = stackalloc byte[bufferSize];
            try
            {
                ffmpeg.av_strerror(errorCode, buffer, bufferSize);
                return Marshal.PtrToStringAnsi((IntPtr)buffer) ?? "unknown error";
            }
            catch (Exception)
            {
                return "unknown error";
            }
        }
    }
}
