using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;
using Microsoft.VisualBasic.FileIO;

namespace Bestiary;

internal sealed class Catalog
{
    public HashSet<string> Favorites { get; set; } = [];
    public HashSet<string> Hidden { get; set; } = [];
    public Dictionary<string, string> Covers { get; set; } = [];
    public Dictionary<string, string> Series { get; set; } = [];
    public Dictionary<string, DateTime> Added { get; set; } = [];
    public Dictionary<string, string> Albums { get; set; } = [];
    public Dictionary<string, string?> LastAssignment { get; set; } = [];
    public bool PhotoOnly { get; set; } = true;
    public bool SingleColumn { get; set; }
    public double TileHeight { get; set; } = 400;
    public bool HideMarked { get; set; } = true;
}

internal sealed record Photo(string Key, string Path, string Album, string Title, DateTime Added)
{
    public bool IsVideo => System.IO.Path.GetExtension(Path).Equals(".mp4", StringComparison.OrdinalIgnoreCase);
    public bool IsGif => System.IO.Path.GetExtension(Path).Equals(".gif", StringComparison.OrdinalIgnoreCase);
}
internal sealed record ImportResult(int Added, int Duplicates, int Failed);

internal sealed class Library
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".tif", ".tiff", ".mp4" };
    private readonly string root;
    private readonly string catalogPath;
    private readonly string thumbnailDirectory;
    private readonly SemaphoreSlim decoders = new(3);
    private readonly Dictionary<string, BitmapSource> recentThumbnails = [];
    private readonly Queue<string> thumbnailOrder = new();
    public string PhotosDirectory { get; }
    public Catalog State { get; }

    public Library(string root)
    {
        this.root = root;
        PhotosDirectory = Path.Combine(root, "Photos");
        catalogPath = Path.Combine(root, "Data", "catalog.json");
        thumbnailDirectory = Path.Combine(root, "Data", "Thumbnails");
        Directory.CreateDirectory(PhotosDirectory);
        Directory.CreateDirectory(thumbnailDirectory);
        var source = File.Exists(catalogPath) ? catalogPath : catalogPath + ".bak";
        State = File.Exists(source)
            ? JsonSerializer.Deserialize<Catalog>(File.ReadAllText(source)) ?? throw new IOException("Каталог пуст.")
            : new Catalog();
    }

    public string Citadel => Path.Combine(root, "Data", "Citadel.png");

    public void Save()
    {
        var pending = catalogPath + ".tmp";
        File.WriteAllText(pending, JsonSerializer.Serialize(State, new JsonSerializerOptions { WriteIndented = true }));
        if (File.Exists(catalogPath))
            File.Replace(pending, catalogPath, catalogPath + ".bak", true);
        else
            File.Move(pending, catalogPath);
    }

    private static IEnumerable<string> Files(string path) => Directory.EnumerateFiles(path, "*", new EnumerationOptions
    {
        RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint
    }).Where(p => Extensions.Contains(Path.GetExtension(p)));

    public List<Photo> Scan()
    {
        recentThumbnails.Clear();
        thumbnailOrder.Clear();
        return Files(PhotosDirectory).Select(p =>
        {
            string key = Path.GetRelativePath(PhotosDirectory, p).Replace('\\', '/');
            string[] parts = key.Split('/');
            string album = parts.Length == 1 ? "References" : parts[0];
            if (album.Equals("Dere", StringComparison.OrdinalIgnoreCase) && parts.Length > 2)
                album += "/" + parts[1];
            return new Photo(key, p, State.Albums.GetValueOrDefault(key, album), Path.GetFileNameWithoutExtension(p),
                State.Added.GetValueOrDefault(key, File.GetCreationTimeUtc(p)));
        }).ToList();
    }

    public void Recycle(Photo photo)
    {
        string relative = Path.GetRelativePath(PhotosDirectory, Path.GetFullPath(photo.Path));
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar))
            throw new IOException("Файл находится вне фототеки Bestiary.");
        FileSystem.DeleteFile(photo.Path, UIOption.AllDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
    }

    public async Task<BitmapSource?> Preview(Photo photo, bool thumbnail, CancellationToken token = default, int maxSize = 4096)
    {
        if (photo.IsVideo) return null;
        token.ThrowIfCancellationRequested();
        if (thumbnail && recentThumbnails.TryGetValue(photo.Key, out var existing)) return existing;
        await decoders.WaitAsync(token);
        try
        {
            var result = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                var info = new FileInfo(photo.Path);
                var key = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
                    $"v2|{photo.Key}|{info.Length}|{info.LastWriteTimeUtc.Ticks}")));
                string cache = Path.Combine(thumbnailDirectory, key + ".jpg");
                if (thumbnail && File.Exists(cache))
                {
                    try { return LoadBitmap(cache); }
                    catch (Exception error) when (error is IOException or NotSupportedException or FormatException) { }
                }
                int size = thumbnail ? 820 : maxSize;
                BitmapSource bitmap = Path.GetExtension(photo.Path).Equals(".webp", StringComparison.OrdinalIgnoreCase)
                    ? DecodeWebp(photo.Path, size)
                    : DecodeWindows(photo.Path, size);
                token.ThrowIfCancellationRequested();
                if (thumbnail)
                {
                    var encoder = new JpegBitmapEncoder { QualityLevel = 84 };
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    try
                    {
                        using var encoded = new MemoryStream();
                        encoder.Save(encoded);
                        File.WriteAllBytes(cache, encoded.ToArray());
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                return bitmap;
            }, token);
            token.ThrowIfCancellationRequested();
            if (thumbnail && recentThumbnails.TryAdd(photo.Key, result))
            {
                thumbnailOrder.Enqueue(photo.Key);
                if (thumbnailOrder.Count > 52) recentThumbnails.Remove(thumbnailOrder.Dequeue());
            }
            return result;
        }
        catch (Exception error) when (error is not OperationCanceledException) { return null; }
        finally { decoders.Release(); }
    }

    public static BitmapSource LoadBitmap(string path)
    {
        using var file = File.OpenRead(path);
        return LoadBitmap(file);
    }

    private static BitmapSource DecodeWindows(string path, int size)
    {
        using var stream = File.OpenRead(path);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        stream.Position = 0;
        bitmap.StreamSource = stream;
        if (frame.PixelWidth >= frame.PixelHeight && frame.PixelWidth > size)
            bitmap.DecodePixelWidth = size;
        else if (frame.PixelHeight > size)
            bitmap.DecodePixelHeight = size;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource DecodeWebp(string path, int size)
    {
        using var original = SKBitmap.Decode(path) ?? throw new IOException("Не удалось прочитать WebP.");
        double factor = Math.Min(1.0, size / (double)Math.Max(original.Width, original.Height));
        var info = new SKImageInfo(Math.Max(1, (int)(original.Width * factor)),
            Math.Max(1, (int)(original.Height * factor)), SKColorType.Bgra8888, SKAlphaType.Premul);
        using var scaled = new SKBitmap(info);
        if (!original.ScalePixels(scaled, new SKSamplingOptions(SKFilterMode.Linear)))
            throw new IOException("Не удалось масштабировать WebP.");
        using var pixmap = scaled.PeekPixels();
        byte[] bytes = pixmap.GetPixelSpan().ToArray();
        var image = BitmapSource.Create(info.Width, info.Height, 96, 96, PixelFormats.Pbgra32,
            null, bytes, scaled.RowBytes);
        image.Freeze();
        return image;
    }

    private static BitmapSource LoadBitmap(Stream stream)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    public async Task<ImportResult> Import(string source, IReadOnlyList<Photo> photos, IProgress<string> progress)
    {
        string full = Path.GetFullPath(source);
        string photosRoot = Path.GetFullPath(PhotosDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if (full.TrimEnd(Path.DirectorySeparatorChar).Equals(photosRoot, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(photosRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || photosRoot.StartsWith(full.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Выбери папку вне библиотеки Bestiary.");

        var result = await Task.Run(() =>
        {
            var hashes = new HashSet<string>(photos.Select(p => Hash(p.Path)));
            var added = new List<string>();
            int duplicate = 0, failed = 0, index = 0;
            var files = Files(full).ToList();
            foreach (var file in files)
            {
                if (index++ % 10 == 0)
                    progress.Report($"Импорт {index:N0} / {files.Count:N0}");
                string? pending = null;
                try
                {
                    string checksum = Hash(file);
                    if (hashes.Contains(checksum))
                    {
                        duplicate++;
                        continue;
                    }
                    string relative = Path.GetRelativePath(full, file);
                    string output = Path.Combine(PhotosDirectory, "Imports", relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                    if (File.Exists(output))
                    {
                        string name = Path.GetFileNameWithoutExtension(file);
                        output = Path.Combine(Path.GetDirectoryName(output)!,
                            name[..Math.Min(name.Length, 100)] + "_" + checksum[..16] + Path.GetExtension(file));
                    }
                    pending = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    File.Copy(file, pending);
                    if (Hash(pending) != checksum)
                        throw new IOException("Файл изменился во время копирования.");
                    File.Move(pending, output);
                    hashes.Add(checksum);
                    added.Add(Path.GetRelativePath(PhotosDirectory, output).Replace('\\', '/'));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { failed++; }
                finally { if (pending is not null && File.Exists(pending)) File.Delete(pending); }
            }
            return (added, duplicate, failed);
        });
        foreach (string key in result.added)
            State.Added[key] = DateTime.UtcNow;
        Save();
        return new ImportResult(result.added.Count, result.duplicate, result.failed);
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public async Task Verify(string report)
    {
        var photos = Scan();
        var examples = photos.Where(p => !p.IsVideo).GroupBy(p => Path.GetExtension(p.Path).ToLowerInvariant()).Select(g => g.First()).ToList();
        foreach (var photo in examples)
            if (await Preview(photo, true) is null || await Preview(photo, false) is null)
                throw new IOException($"Не удалось открыть {photo.Key}");
        var movies = photos.Where(p => p.IsVideo).ToList();
        bool? videoOpened = null;
        if (movies.Count > 0)
        {
            var player = new MediaPlayer { IsMuted = true };
            var opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            player.MediaOpened += (_, _) => opened.TrySetResult(player.HasVideo);
            player.MediaFailed += (_, _) => opened.TrySetResult(false);
            player.Open(new Uri(movies[0].Path));
            player.Play();
            try { videoOpened = await opened.Task.WaitAsync(TimeSpan.FromSeconds(8)); }
            catch (TimeoutException) { videoOpened = false; }
            finally { player.Close(); }
        }
        File.WriteAllText(report, JsonSerializer.Serialize(new
        {
            Photos = photos.Count - movies.Count, Videos = movies.Count, VideoOpened = videoOpened,
            Formats = examples.Select(p => Path.GetExtension(p.Path)),
            Albums = photos.Select(p => p.Album).Distinct().Count(), Favorites = State.Favorites.Count
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
