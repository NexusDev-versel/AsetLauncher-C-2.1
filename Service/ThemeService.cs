using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AsetLauncher.Services
{
    public sealed class LauncherTheme
    {
        public string Id { get; set; }

        public string Name { get; set; }

        public string DirectoryPath { get; set; }

        public string MainImagePath { get; set; }

        public string LogoImagePath { get; set; }
    }

    public static class ThemeService
    {
        private const string DefaultThemeId = "default";
        private const string ThemesFolderRelative = "Assets\\Tems";

        private static readonly object Sync = new object();
        private static List<LauncherTheme> _themes = new List<LauncherTheme>();

        public static event Action<LauncherTheme> ThemeChanged;

        public static LauncherTheme CurrentTheme { get; private set; }

        public static IReadOnlyList<LauncherTheme> GetAvailableThemes()
        {
            lock (Sync)
            {
                EnsureThemesLoaded();
                return _themes.ToList();
            }
        }

        public static void ApplyTheme(string themeId)
        {
            LauncherTheme selected;
            lock (Sync)
            {
                EnsureThemesLoaded();
                selected = ResolveTheme(themeId);
                CurrentTheme = selected;
            }

            LauncherLogService.Info("Тема применена: " + selected.Name + " (" + selected.Id + ")");
            ThemeChanged?.Invoke(selected);
        }

        public static ImageSource CreateImageSourceOrFallback(string candidateFilePath, string fallbackResourcePath)
        {
            if (!string.IsNullOrWhiteSpace(candidateFilePath) && File.Exists(candidateFilePath))
            {
                try
                {
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.UriSource = new Uri(candidateFilePath, UriKind.Absolute);
                    image.EndInit();
                    image.Freeze();
                    return image;
                }
                catch
                {
                }
            }

            var normalizedResource = (fallbackResourcePath ?? string.Empty).Replace('\\', '/').TrimStart('/');
            var packUri = new Uri("pack://application:,,,/" + normalizedResource, UriKind.Absolute);
            var fallback = new BitmapImage(packUri);
            fallback.Freeze();
            return fallback;
        }

        private static void EnsureThemesLoaded()
        {
            if (_themes.Count > 0)
            {
                return;
            }

            RefreshThemes();
        }

        private static void RefreshThemes()
        {
            var list = new List<LauncherTheme>
            {
                new LauncherTheme
                {
                    Id = DefaultThemeId,
                    Name = "Стандартная",
                    DirectoryPath = null,
                    MainImagePath = null,
                    LogoImagePath = null
                }
            };

            var root = ResolveThemesRoot();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                _themes = list;
                return;
            }

            foreach (var dir in Directory.GetDirectories(root, "*", SearchOption.AllDirectories))
            {
                var mainImage = TryFindFile(dir, "Main.png");
                if (string.IsNullOrWhiteSpace(mainImage))
                {
                    continue;
                }

                var relativeId = GetRelativePath(root, dir)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Replace(Path.DirectorySeparatorChar, '/')
                    .Replace(Path.AltDirectorySeparatorChar, '/');

                var logoImage = TryFindFile(
                    dir,
                    "Аватарка лаунчера.png",
                    "Profile.png",
                    "Friends.png",
                    "logo.png");

                list.Add(new LauncherTheme
                {
                    Id = relativeId,
                    Name = BuildThemeName(relativeId),
                    DirectoryPath = dir,
                    MainImagePath = mainImage,
                    LogoImagePath = logoImage
                });
            }

            _themes = list
                .OrderBy(t => t.Id == DefaultThemeId ? 0 : 1)
                .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static LauncherTheme ResolveTheme(string themeId)
        {
            if (string.IsNullOrWhiteSpace(themeId))
            {
                themeId = DefaultThemeId;
            }

            var selected = _themes.FirstOrDefault(t => t.Id.Equals(themeId, StringComparison.OrdinalIgnoreCase));
            if (selected != null)
            {
                return selected;
            }

            return _themes.FirstOrDefault(t => t.Id == DefaultThemeId) ?? _themes.First();
        }

        private static string ResolveThemesRoot()
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var candidatePaths = new[]
            {
                Path.Combine(baseDir, ThemesFolderRelative),
                Path.Combine(baseDir, "..", "..", ThemesFolderRelative),
                Path.Combine(baseDir, "..", "..", "..", ThemesFolderRelative),
            };

            return candidatePaths
                .Select(Path.GetFullPath)
                .FirstOrDefault(Directory.Exists);
        }

        private static string TryFindFile(string directoryPath, params string[] fileNames)
        {
            if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
            {
                return null;
            }

            var files = Directory.GetFiles(directoryPath, "*", SearchOption.TopDirectoryOnly);
            foreach (var expectedName in fileNames)
            {
                var found = files.FirstOrDefault(f =>
                    string.Equals(Path.GetFileName(f), expectedName, StringComparison.OrdinalIgnoreCase));

                if (!string.IsNullOrWhiteSpace(found))
                {
                    return found;
                }
            }

            return null;
        }
        private static string BuildThemeName(string relativeId)
        {
            if (string.IsNullOrWhiteSpace(relativeId))
            {
                return "Без названия";
            }

            var normalized = relativeId
                .Replace('\\', '/')
                .Trim('/')
                .Trim();

            if (string.IsNullOrWhiteSpace(normalized))
            {
                return "Без названия";
            }

            var parts = normalized.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            var leaf = parts.Length > 0 ? parts[parts.Length - 1] : normalized;
            leaf = leaf.Trim();

            if (string.IsNullOrWhiteSpace(leaf))
            {
                return "Без названия";
            }

            return leaf;
        }
        private static string GetRelativePath(string root, string fullPath)
        {
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(fullPath))
            {
                return fullPath ?? string.Empty;
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
    }
}

