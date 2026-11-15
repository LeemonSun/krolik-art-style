using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.IO;

namespace Bestiary;

internal sealed class Viewer : Window
{
    private readonly Library library;
    private readonly IReadOnlyList<Photo> photos;
    private readonly Image art = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock caption = new() { FontSize = 15, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button favorite = new();
    private readonly TextBlock message = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 20 };
    private readonly Grid stage = new() { ClipToBounds = true };
    private readonly ScaleTransform scale = new(1, 1);
    private readonly TranslateTransform shift = new();
    private readonly MediaElement video = new() { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, Stretch = Stretch.Uniform };
    private readonly StackPanel film = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly DispatcherTimer slideshow = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly DispatcherTimer animation = new();
    private readonly Button play;
    private readonly DockPanel bar = new();
    private IReadOnlyList<BitmapFrame> gif = [];
    private int gifIndex;
    private bool playingVideo;
    private Point? drag;
    private CancellationTokenSource loading = new();
    private int index;

    public Viewer(Window owner, Library library, IReadOnlyList<Photo> photos, int index)
    {
        Owner = owner;
        this.library = library;
        this.photos = photos;
        this.index = index;
        Title = "Bestiary · Просмотр";
        WindowStyle = WindowStyle.None;
        WindowState = WindowState.Maximized;
        Background = new SolidColorBrush(Color.FromRgb(17, 16, 23));
        var layout = new DockPanel();
        Content = layout;
        bar.Margin = new Thickness(20, 14, 20, 14);
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(controls, Dock.Right);
        controls.Children.Add(Command("‹", () => Step(-1), "Предыдущий кадр"));
        controls.Children.Add(Command("›", () => Step(1), "Следующий кадр"));
        favorite.Margin = new Thickness(8, 0, 0, 0);
        favorite.Click += (_, _) => Favorite();
        controls.Children.Add(favorite);
        controls.Children.Add(Command("Обложка", SetCover, "Сделать обложкой альбома"));
        play = Command("Слайд-шоу", TogglePlayback, "Пробел · Слайд-шоу или пауза видео");
        controls.Children.Add(play);
        controls.Children.Add(Command("×", Close, "Закрыть просмотр · Esc"));
        bar.Children.Add(controls);
        bar.Children.Add(caption);
        DockPanel.SetDock(bar, Dock.Top);
        layout.Children.Add(bar);
        film.Margin = new Thickness(12, 8, 12, 12);
        DockPanel.SetDock(film, Dock.Bottom);
        layout.Children.Add(film);
        art.RenderTransformOrigin = new Point(0.5, 0.5);
        var transforms = new TransformGroup();
        transforms.Children.Add(scale);
        transforms.Children.Add(shift);
        art.RenderTransform = transforms;
        stage.Children.Add(art);
        stage.Children.Add(video);
        stage.Children.Add(message);
        layout.Children.Add(stage);
        video.Visibility = Visibility.Collapsed;
        video.MediaOpened += (_, _) => message.Text = "";
        video.MediaFailed += (_, _) => message.Text = "Windows не смог открыть это видео";
        video.MediaEnded += (_, _) =>
        {
            video.Stop();
            playingVideo = false;
            play.Content = "Воспроизвести";
        };
        slideshow.Tick += (_, _) =>
        {
            if (photos[index].IsVideo) return;
            if (index == photos.Count - 1) { index = 0; _ = ShowPhoto(); }
            else Step(1);
        };
        animation.Tick += (_, _) =>
        {
            if (gif.Count <= 1) return;
            gifIndex = (gifIndex + 1) % gif.Count;
            ShowGifFrame();
        };
        stage.MouseWheel += (_, e) => Zoom(e.Delta > 0 ? 1.18 : 1 / 1.18);
        stage.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) { Zoom(scale.ScaleX > 1 ? 1 / scale.ScaleX : 2); return; }
            if (scale.ScaleX <= 1) return;
            drag = e.GetPosition(stage);
            stage.CaptureMouse();
        };
        stage.MouseMove += (_, e) =>
        {
            if (drag is not Point previous) return;
            Point now = e.GetPosition(stage);
            shift.X += now.X - previous.X;
            shift.Y += now.Y - previous.Y;
            drag = now;
        };
        stage.MouseLeftButtonUp += (_, _) => { drag = null; stage.ReleaseMouseCapture(); };
        stage.LostMouseCapture += (_, _) => drag = null;
        PreviewKeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Escape: Close(); break;
                case Key.Left: Step(-1); break;
                case Key.Right: Step(1); break;
                case Key.F: Favorite(); break;
                case Key.Space: TogglePlayback(); break;
                case Key.Tab:
                    bar.Visibility = film.Visibility = bar.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
                    break;
                case Key.Add: case Key.OemPlus: Zoom(1.18); break;
                case Key.Subtract: case Key.OemMinus: Zoom(1 / 1.18); break;
                case Key.D0: Zoom(1 / scale.ScaleX); break;
                default: return;
            }
            e.Handled = true;
        };
        Loaded += (_, _) => _ = ShowPhoto();
        Closed += (_, _) => { loading.Cancel(); slideshow.Stop(); animation.Stop(); video.Close(); };
    }

    private static Button Command(string title, Action execute, string tooltip)
    {
        var button = new Button { Content = title, ToolTip = tooltip, Margin = new Thickness(8, 0, 0, 0) };
        button.Click += (_, _) => execute();
        return button;
    }

    private void Zoom(double factor)
    {
        scale.ScaleY = scale.ScaleX = Math.Clamp(scale.ScaleX * factor, 1, 4);
        if (scale.ScaleX == 1) shift.X = shift.Y = 0;
    }

    private void TogglePlayback()
    {
        if (photos[index].IsVideo)
        {
            if (playingVideo) video.Pause(); else video.Play();
            playingVideo = !playingVideo;
            play.Content = playingVideo ? "Пауза" : "Воспроизвести";
            return;
        }
        if (slideshow.IsEnabled) slideshow.Stop(); else slideshow.Start();
        play.Content = slideshow.IsEnabled ? "Остановить" : "Слайд-шоу";
    }

    private void ShowGifFrame()
    {
        var frame = gif[gifIndex];
        art.Source = frame;
        try
        {
            var metadata = frame.Metadata as BitmapMetadata;
            var delay = metadata?.GetQuery("/grctlext/Delay");
            animation.Interval = TimeSpan.FromMilliseconds(delay is ushort value ? Math.Max(25, value * 10) : 100);
        }
        catch (NotSupportedException) { animation.Interval = TimeSpan.FromMilliseconds(100); }
    }

    private async Task LoadFilm(Photo photo, Image thumb, CancellationToken token)
    {
        try { thumb.Source = await library.Preview(photo, true, token); }
        catch (OperationCanceledException) { }
    }

    private void Filmstrip(CancellationToken token)
    {
        film.Children.Clear();
        for (int i = Math.Max(0, index - 4); i < Math.Min(index + 5, photos.Count); i++)
        {
            int target = i;
            var photo = photos[i];
            var thumb = new Image { Stretch = Stretch.Uniform, Width = 70, Height = 54 };
            var button = Command("", () => { index = target; _ = ShowPhoto(); }, photo.Title);
            button.Padding = new Thickness(2);
            button.Content = photo.IsVideo || library.State.HideMarked && library.State.Hidden.Contains(photo.Key)
                ? new TextBlock { Text = photo.IsVideo ? "▶" : "—", Width = 70, Height = 54, TextAlignment = TextAlignment.Center } : thumb;
            if (i == index) button.BorderBrush = new SolidColorBrush(Color.FromRgb(218, 186, 145));
            film.Children.Add(button);
            if (button.Content == thumb) _ = LoadFilm(photo, thumb, token);
        }
    }

    private void Step(int offset)
    {
        int next = Math.Clamp(index + offset, 0, photos.Count - 1);
        if (index == next) return;
        index = next;
        _ = ShowPhoto();
    }

    private void Save()
    {
        try { library.Save(); }
        catch (Exception error) { MessageBox.Show(this, error.Message, "Не удалось сохранить каталог"); }
    }

    private void SetCover()
    {
        var photo = photos[index];
        library.State.Covers[photo.Album] = photo.Key;
        Save();
        caption.Text = $"{index + 1} / {photos.Count}   ·   {photo.Title}   ·   Обложка альбома";
    }

    private void Favorite()
    {
        string key = photos[index].Key;
        if (!library.State.Favorites.Remove(key)) library.State.Favorites.Add(key);
        Save();
        FavoriteLabel();
    }

    private void FavoriteLabel() => favorite.Content = library.State.Favorites.Contains(photos[index].Key) ? "♥  Избранное" : "♡  В избранное";

    private async Task ShowPhoto()
    {
        loading.Cancel();
        loading.Dispose();
        loading = new CancellationTokenSource();
        var token = loading.Token;
        var photo = photos[index];
        caption.Text = $"{index + 1} / {photos.Count}   ·   {photo.Title}";
        FavoriteLabel();
        Zoom(1 / scale.ScaleX);
        animation.Stop();
        gif = [];
        video.Close();
        video.Visibility = Visibility.Collapsed;
        playingVideo = false;
        play.Content = slideshow.IsEnabled ? "Остановить" : "Слайд-шоу";
        art.Source = null;
        message.Text = "Открываю фото…";
        Filmstrip(token);
        try
        {
            if (photo.IsVideo)
            {
                slideshow.Stop();
                video.Source = new Uri(photo.Path, UriKind.Absolute);
                video.Visibility = Visibility.Visible;
                video.Play();
                playingVideo = true;
                play.Content = "Пауза";
                return;
            }
            if (photo.IsGif)
            {
                var frames = await Task.Run(() =>
                {
                    using var input = File.OpenRead(photo.Path);
                    return new GifBitmapDecoder(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames
                        .Select(frame => { frame.Freeze(); return frame; }).ToArray();
                }, token);
                if (token.IsCancellationRequested) return;
                if (frames.Length > 1)
                {
                    gif = frames;
                    gifIndex = 0;
                    ShowGifFrame();
                    animation.Start();
                    message.Text = "";
                    return;
                }
            }
            var bitmap = await library.Preview(photo, false, token);
            if (token.IsCancellationRequested) return;
            art.Source = bitmap;
            message.Text = bitmap is null ? "Не удалось открыть изображение" : "";
        }
        catch (OperationCanceledException) { }
        catch (Exception) { message.Text = "Не удалось открыть файл"; }
    }
}
