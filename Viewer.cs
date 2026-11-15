using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

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
        var bar = new DockPanel { Margin = new Thickness(20, 14, 20, 14) };
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(controls, Dock.Right);
        controls.Children.Add(Command("‹", () => Step(-1), "Предыдущий кадр"));
        controls.Children.Add(Command("›", () => Step(1), "Следующий кадр"));
        favorite.Margin = new Thickness(8, 0, 0, 0);
        favorite.Click += (_, _) => Favorite();
        controls.Children.Add(favorite);
        controls.Children.Add(Command("Обложка", SetCover, "Сделать обложкой альбома"));
        controls.Children.Add(Command("×", Close, "Закрыть просмотр · Esc"));
        bar.Children.Add(controls);
        bar.Children.Add(caption);
        DockPanel.SetDock(bar, Dock.Top);
        layout.Children.Add(bar);
        art.RenderTransformOrigin = new Point(0.5, 0.5);
        art.RenderTransform = scale;
        stage.Children.Add(art);
        stage.Children.Add(message);
        layout.Children.Add(stage);
        stage.MouseWheel += (_, e) => Zoom(e.Delta > 0 ? 1.18 : 1 / 1.18);
        stage.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) Zoom(scale.ScaleX > 1 ? 1 / scale.ScaleX : 2); };
        PreviewKeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Escape: Close(); break;
                case Key.Left: Step(-1); break;
                case Key.Right: Step(1); break;
                case Key.F: Favorite(); break;
                case Key.Add: case Key.OemPlus: Zoom(1.18); break;
                case Key.Subtract: case Key.OemMinus: Zoom(1 / 1.18); break;
                case Key.D0: Zoom(1 / scale.ScaleX); break;
                default: return;
            }
            e.Handled = true;
        };
        Loaded += (_, _) => _ = ShowPhoto();
        Closed += (_, _) => loading.Cancel();
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
        art.Source = null;
        message.Text = "Открываю фото…";
        try
        {
            var bitmap = await library.Preview(photo, false, token);
            if (token.IsCancellationRequested) return;
            art.Source = bitmap;
            message.Text = bitmap is null ? "Не удалось открыть изображение" : "";
        }
        catch (OperationCanceledException) { }
    }
}
