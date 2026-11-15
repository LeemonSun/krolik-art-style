using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Bestiary;

internal sealed class Comparison : Window
{
    private readonly ScaleTransform scale = new(1, 1);
    private readonly TranslateTransform shift = new();
    private readonly CancellationTokenSource stopped = new();

    public Comparison(Window owner, Library library, Photo first, Photo second)
    {
        Owner = owner;
        Title = "Bestiary · Сравнение";
        Background = new SolidColorBrush(Color.FromRgb(20, 19, 26));
        WindowState = WindowState.Maximized;
        var layout = new DockPanel { Margin = new Thickness(14) };
        Content = layout;
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(controls, Dock.Top);
        var close = new Button { Content = "Закрыть" };
        close.Click += (_, _) => Close();
        var zoom = new Slider { Minimum = 1, Maximum = 4, Value = 1, Width = 150, Margin = new Thickness(16, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        zoom.ValueChanged += (_, _) =>
        {
            scale.ScaleX = scale.ScaleY = zoom.Value;
            if (zoom.Value == 1) shift.X = shift.Y = 0;
        };
        controls.Children.Add(close);
        controls.Children.Add(zoom);
        controls.Children.Add(new TextBlock { Text = "Общий масштаб", VerticalAlignment = VerticalAlignment.Center });
        layout.Children.Add(controls);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new());
        grid.ColumnDefinitions.Add(new());
        layout.Children.Add(grid);
        var photos = new[] { first, second };
        for (int i = 0; i < photos.Length; i++)
        {
            Photo photo = photos[i];
            var image = new Image { Stretch = Stretch.Uniform, RenderTransformOrigin = new Point(0.5, 0.5) };
            var transforms = new TransformGroup();
            transforms.Children.Add(scale);
            transforms.Children.Add(shift);
            image.RenderTransform = transforms;
            var stage = new Grid { Background = Brushes.Black, ClipToBounds = true };
            stage.Children.Add(image);
            Point? dragging = null;
            stage.MouseLeftButtonDown += (_, e) =>
            {
                if (zoom.Value <= 1) return;
                dragging = e.GetPosition(stage);
                stage.CaptureMouse();
            };
            stage.MouseMove += (_, e) =>
            {
                if (dragging is not Point start) return;
                Point current = e.GetPosition(stage);
                shift.X += current.X - start.X;
                shift.Y += current.Y - start.Y;
                dragging = current;
            };
            stage.MouseLeftButtonUp += (_, _) => stage.ReleaseMouseCapture();
            stage.LostMouseCapture += (_, _) => dragging = null;
            stage.MouseWheel += (_, e) => { zoom.Value = Math.Clamp(zoom.Value * (e.Delta > 0 ? 1.2 : 1 / 1.2), 1, 4); };
            var pane = new DockPanel { Margin = new Thickness(4) };
            var button = new Button { Content = "Выбрать обложкой", HorizontalAlignment = HorizontalAlignment.Right };
            button.Click += (_, _) =>
            {
                try
                {
                    library.State.Covers[photo.Album] = photo.Key;
                    library.Save();
                    button.Content = "Обложка сохранена";
                }
                catch (Exception error) { MessageBox.Show(this, error.Message, "Не удалось сохранить каталог"); }
            };
            DockPanel.SetDock(button, Dock.Bottom);
            pane.Children.Add(button);
            pane.Children.Add(stage);
            Grid.SetColumn(pane, i);
            grid.Children.Add(pane);
            Loaded += async (_, _) =>
            {
                try { image.Source = await library.Preview(photo, false, stopped.Token); }
                catch (OperationCanceledException) { }
            };
        }
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Closed += (_, _) => stopped.Cancel();
    }
}
