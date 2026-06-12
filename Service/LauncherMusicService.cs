using AsetLauncher.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;

namespace AsetLauncher.Services
{
    public sealed class LauncherMusicTrack
    {
        public string Id { get; set; }

        public string Name { get; set; }

        public string FilePath { get; set; }
    }

    public static class LauncherMusicService
    {
        private const string SoundsFolderRelative = "Assets\\Sounds";

        private static readonly object Sync = new object();
        private static readonly MediaPlayer Player = CreatePlayer();
        private static List<LauncherMusicTrack> _tracks = new List<LauncherMusicTrack>();
        private static string _currentTrackId = string.Empty;
        private static string _currentTrackPath = string.Empty;
        private static bool _musicEnabled = true;

        public static IReadOnlyList<LauncherMusicTrack> GetAvailableTracks()
        {
            lock (Sync)
            {
                RefreshTracks();
                return _tracks.ToList();
            }
        }

        public static void ApplySettings(LauncherSettings settings)
        {
            if (settings == null)
            {
                Stop();
                return;
            }

            lock (Sync)
            {
                RefreshTracks();

                _musicEnabled = settings.MusicEnabled;
                Player.Volume = ClampVolume(settings.MusicVolume) / 100.0;

                var selectedTrack = ResolveTrack(settings.MusicTrackId);
                if (!_musicEnabled || selectedTrack == null)
                {
                    StopInternal();
                    return;
                }

                if (!string.Equals(_currentTrackPath, selectedTrack.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        Player.Open(new Uri(selectedTrack.FilePath, UriKind.Absolute));
                        _currentTrackId = selectedTrack.Id;
                        _currentTrackPath = selectedTrack.FilePath;
                        LauncherLogService.Info("Р’С‹Р±СЂР°РЅ РјСѓР·С‹РєР°Р»СЊРЅС‹Р№ С‚СЂРµРє: " + selectedTrack.Name);
                    }
                    catch (Exception ex)
                    {
                        LauncherLogService.Warn("РќРµ СѓРґР°Р»РѕСЃСЊ РѕС‚РєСЂС‹С‚СЊ РјСѓР·С‹РєР°Р»СЊРЅС‹Р№ С‚СЂРµРє: " + ex.Message);
                        StopInternal();
                        return;
                    }
                }

                try
                {
                    Player.Play();
                }
                catch (Exception ex)
                {
                    LauncherLogService.Warn("РќРµ СѓРґР°Р»РѕСЃСЊ Р·Р°РїСѓСЃС‚РёС‚СЊ РјСѓР·С‹РєР°Р»СЊРЅС‹Р№ С‚СЂРµРє: " + ex.Message);
                }
            }
        }

        public static void Stop()
        {
            lock (Sync)
            {
                StopInternal();
            }
        }

        private static MediaPlayer CreatePlayer()
        {
            var player = new MediaPlayer();
            player.MediaEnded += Player_MediaEnded;
            player.MediaFailed += Player_MediaFailed;
            return player;
        }

        private static void Player_MediaEnded(object sender, EventArgs e)
        {
            lock (Sync)
            {
                if (!_musicEnabled || string.IsNullOrWhiteSpace(_currentTrackPath) || !File.Exists(_currentTrackPath))
                {
                    return;
                }

                try
                {
                    Player.Position = TimeSpan.Zero;
                    Player.Play();
                }
                catch (Exception ex)
                {
                    LauncherLogService.Warn("РќРµ СѓРґР°Р»РѕСЃСЊ Р·Р°С†РёРєР»РёС‚СЊ РјСѓР·С‹РєР°Р»СЊРЅС‹Р№ С‚СЂРµРє: " + ex.Message);
                }
            }
        }

        private static void Player_MediaFailed(object sender, System.Windows.Media.ExceptionEventArgs e)
        {
            LauncherLogService.Warn("РћС€РёР±РєР° РІРѕСЃРїСЂРѕРёР·РІРµРґРµРЅРёСЏ РјСѓР·С‹РєРё: " + e.ErrorException.Message);
        }

        private static void StopInternal()
        {
            try
            {
                Player.Stop();
            }
            catch
            {
            }
        }

        private static LauncherMusicTrack ResolveTrack(string preferredTrackId)
        {
            if (_tracks.Count == 0)
            {
                _currentTrackId = string.Empty;
                _currentTrackPath = string.Empty;
                return null;
            }

            var normalizedId = NormalizeTrackId(preferredTrackId);
            if (!string.IsNullOrWhiteSpace(normalizedId))
            {
                var selected = _tracks.FirstOrDefault(t =>
                    string.Equals(t.Id, normalizedId, StringComparison.OrdinalIgnoreCase));
                if (selected != null)
                {
                    return selected;
                }
            }

            return _tracks[0];
        }

        private static void RefreshTracks()
        {
            var soundsRoot = ResolveSoundsRoot();
            if (string.IsNullOrWhiteSpace(soundsRoot) || !Directory.Exists(soundsRoot))
            {
                _tracks = new List<LauncherMusicTrack>();
                return;
            }

            var tracks = Directory.GetFiles(soundsRoot, "*.mp3", SearchOption.AllDirectories)
                .Select(path =>
                {
                    var relative = GetRelativePath(soundsRoot, path)
                        .Replace(Path.DirectorySeparatorChar, '/')
                        .Replace(Path.AltDirectorySeparatorChar, '/');

                    return new LauncherMusicTrack
                    {
                        Id = NormalizeTrackId(relative),
                        Name = Path.GetFileNameWithoutExtension(path),
                        FilePath = path
                    };
                })
                .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _tracks = tracks;
        }

        private static string ResolveSoundsRoot()
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var candidatePaths = new[]
            {
                Path.Combine(baseDir, SoundsFolderRelative),
                Path.Combine(baseDir, "..", SoundsFolderRelative),
                Path.Combine(baseDir, "..", "..", SoundsFolderRelative),
                Path.Combine(baseDir, "..", "..", "..", SoundsFolderRelative),
            };

            return candidatePaths
                .Select(Path.GetFullPath)
                .FirstOrDefault(Directory.Exists);
        }

        private static string GetRelativePath(string root, string fullPath)
        {
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(fullPath))
            {
                return string.Empty;
            }

            var rootUri = new Uri(AppendDirectorySeparatorChar(Path.GetFullPath(root)));
            var fullUri = new Uri(Path.GetFullPath(fullPath));
            return Uri.UnescapeDataString(rootUri.MakeRelativeUri(fullUri).ToString());
        }

        private static string AppendDirectorySeparatorChar(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return path;
            }

            if (path[path.Length - 1] != Path.DirectorySeparatorChar && path[path.Length - 1] != Path.AltDirectorySeparatorChar)
            {
                return path + Path.DirectorySeparatorChar;
            }

            return path;
        }

        private static string NormalizeTrackId(string trackId)
        {
            return (trackId ?? string.Empty)
                .Trim()
                .Replace('\\', '/');
        }

        private static int ClampVolume(int volume)
        {
            if (volume < 0)
            {
                return 0;
            }

            if (volume > 100)
            {
                return 100;
            }

            return volume;
        }
    }
}

