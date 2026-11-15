using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Bestiary;

internal sealed class Gallery : Window
{
    private static readonly Brush Ink = Paint("#EFE8DA");
    private static readonly Brush Muted = Paint("#ADA6B4");
    private static readonly Brush Gold = Paint("#DABA91");
    private static readonly Brush Panel = Paint("#201E28");
    private static readonly Brush Line = Paint("#3A3442");
    private const int PageSize = 24;
    private readonly Library library;
    private readonly Grid shell = new();
    private readonly StackPanel nav = new();
    private readonly StackPanel content = new();
    private readonly TextBlock status = Label("");
    private readonly TextBox search = new() { Width = 230, ToolTip = "Поиск по имени, папке и серии" };
    private readonly ScrollViewer scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly HashSet<string> selected = [];
    private List<Photo> photos = [];
    private CancellationTokenSource loading = new();
    private string section = "home";
    private string? series;
    private int page;
    private bool importing;
    private bool newest;

    public Gallery(Library library)
    {
        this.library = library;
        Title = "Bestiary · Архив Кролика";
        Width = 1320;
        Height = 880;
        MinWidth = 880;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Paint("#14131A");
        shell.Background = Background;
        shell.ColumnDefinitions.Add(new() { Width = new GridLength(216) });
        shell.ColumnDefinitions.Add(new());
        Content = shell;

        var sidebar = new DockPanel { Background = Paint("#191720"), LastChildFill = true };
        var brand = new StackPanel { Margin = new Thickness(23, 27, 20, 26) };
        brand.Children.Add(Label("◇   BESTIARY", 22, Gold, true));
        brand.Children.Add(Label("Архив Кролика", 13, Muted, inset: new Thickness(0, 6, 0, 0)));
        DockPanel.SetDock(brand, Dock.Top);
        sidebar.Children.Add(brand);
        status.Margin = new Thickness(23, 12, 18, 20);
        status.Foreground = Muted;
        status.TextWrapping = TextWrapping.Wrap;
        DockPanel.SetDock(status, Dock.Bottom);
        sidebar.Children.Add(status);
        sidebar.Children.Add(new ScrollViewer { Content = nav, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        shell.Children.Add(sidebar);

        var body = new DockPanel { LastChildFill = true, Margin = new Thickness(26, 16, 24, 14) };
        Grid.SetColumn(body, 1);
        shell.Children.Add(body);
        var toolbar = new DockPanel { Margin = new Thickness(0, 1, 0, 14), LastChildFill = false };
        var searchField = new Grid();
        searchField.Children.Add(search);
        var hint = Label("Поиск по коллекции", 13, Muted, inset: new Thickness(10, 0, 0, 0));
        hint.IsHitTestVisible = false;
        hint.VerticalAlignment = VerticalAlignment.Center;
        searchField.Children.Add(hint);
        toolbar.Children.Add(searchField);
        search.TextChanged += (_, _) =>
        {
            hint.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            page = 0;
            Render();
        };
        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(tools, Dock.Right);
        tools.Children.Add(Action("Обновить", () => { photos = library.Scan(); Render(); }));
        tools.Children.Add(Action("Видимость", () => Mutate(() => library.State.HideMarked = !library.State.HideMarked), "Показать или скрыть помеченные превью"));
        tools.Children.Add(Action("+  Импорт", Import, "Копировать изображения из другой папки", true));
        toolbar.Children.Add(tools);
        DockPanel.SetDock(toolbar, Dock.Top);
        body.Children.Add(toolbar);
        scroll.Content = content;
        body.Children.Add(scroll);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            {
                search.Focus();
                search.SelectAll();
                e.Handled = true;
            }
        };
        Closing += (_, e) => { if (importing) e.Cancel = true; };
        Closed += (_, _) => loading.Cancel();
        photos = library.Scan();
        Render();
    }

    private static Brush Paint(string value) => (Brush)new BrushConverter().ConvertFromString(value)!;

    private static TextBlock Label(string text, int size = 13, Brush? color = null, bool bold = false, Thickness? inset = null) =>
        new() { Text = text, FontSize = size, Foreground = color ?? Ink, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Margin = inset ?? new Thickness(), TextTrimming = TextTrimming.CharacterEllipsis };

    private static Button Action(string text, Action action, string? tooltip = null, bool primary = false)
    {
        var button = new Button { Content = text, ToolTip = tooltip, Margin = new Thickness(6, 0, 0, 0) };
        if (primary)
        {
            button.Background = Gold;
            button.Foreground = Paint("#211810");
        }
        button.Click += (_, _) => action();
        return button;
    }

    private static Border Frame(UIElement child) => new() { Child = child, CornerRadius = new CornerRadius(11), Background = Panel, BorderBrush = Line, BorderThickness = new Thickness(1), ClipToBounds = true };

    private void Go(string destination, string? selectedSeries = null)
    {
        selected.Clear();
        section = destination;
        series = selectedSeries;
        page = 0;
        if (search.Text.Length > 0)
            search.Clear();
        else
            Render();
    }

    private void AddNav(string key, string text, int? number = null, bool indent = false)
    {
        var row = Action(number is int count ? $"{text}  ·  {count:N0}" : text, () => Go(key));
        row.HorizontalContentAlignment = HorizontalAlignment.Left;
        row.Margin = new Thickness(indent ? 23 : 12, 2, 12, 2);
        row.Padding = new Thickness(10, 7, 7, 7);
        row.Background = key == section ? Paint("#3A2D3A") : Brushes.Transparent;
        row.BorderBrush = key == section ? Paint("#75596A") : Brushes.Transparent;
        nav.Children.Add(row);
    }

    private void Render()
    {
        loading.Cancel();
        loading.Dispose();
        loading = new CancellationTokenSource();
        nav.Children.Clear();
        content.Children.Clear();
        if (!importing)
            status.Text = $"{photos.Count:N0} изображений\nЛокальная библиотека";
        var albums = photos.GroupBy(p => p.Album).OrderBy(g => Rank(g.Key)).ThenBy(g => g.Key).ToList();
        AddNav("home", "Коллекция");
        AddNav("all", "Все изображения");
        AddNav("favorites", "Избранное", photos.Count(p => library.State.Favorites.Contains(p.Key)));
        AddNav("recent", "Недавно добавленные");
        nav.Children.Add(Label("АЛЬБОМЫ", 10, Muted, true, new Thickness(24, 20, 10, 6)));
        foreach (var album in albums)
            AddNav(album.Key, album.Key.Split('/')[^1], album.Count(), album.Key.StartsWith("Dere/"));

        string query = search.Text.Trim();
        if (query.Length == 0 && section == "home")
        {
            Hero();
            var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 15) };
            row.Children.Add(Action("♡  Избранное", () => Go("favorites")));
            row.Children.Add(Action("Недавние фото", () => Go("recent")));
            content.Children.Add(row);
            content.Children.Add(Label("Альбомы", 30, Ink, true, new Thickness(0, 3, 0, 12)));
            var albumGrid = new UniformGrid { Columns = 4 };
            foreach (var album in albums)
            {
                Photo cover = album.FirstOrDefault(p => p.Key == library.State.Covers.GetValueOrDefault(album.Key))
                    ?? album.OrderBy(CoverRank).ThenBy(p => p.Key).First();
                string key = album.Key;
                albumGrid.Children.Add(Tile(cover, key.Split('/')[^1], $"{album.Count():N0} фото", () => Go(key), null));
            }
            content.Children.Add(albumGrid);
        }
        else
        {
            var filtered = Filter(query).ToList();
            ContentHeader(query, filtered.Count);
            if (query.Length == 0 && series is null && albums.Any(g => g.Key == section))
            {
                var stacks = filtered.Where(p => library.State.Series.ContainsKey(p.Key))
                    .GroupBy(p => library.State.Series[p.Key]).OrderBy(g => g.Key).ToList();
                if (stacks.Count > 0)
                {
                    var chips = new WrapPanel { Margin = new Thickness(0, 0, 0, 14) };
                    foreach (var stack in stacks)
                    {
                        string name = stack.Key;
                        chips.Children.Add(Action($"{name}  ·  {stack.Count()}", () => Go(section, name)));
                    }
                    content.Children.Add(chips);
                    filtered = filtered.Where(p => !library.State.Series.ContainsKey(p.Key))
                        .Concat(stacks.Select(g => g.First())).ToList();
                }
            }
            GalleryPage(filtered);
        }
        scroll.ScrollToTop();
    }

    private static int Rank(string key) => key switch
    {
        "Dere/Yandere" => 0, "Dere/Mazodere" => 1, "Henkai" => 2,
        _ when key.StartsWith("Dere/") => 3, _ => 4
    };

    private static int CoverRank(Photo p) =>
        p.Title.Contains("original", StringComparison.OrdinalIgnoreCase) || p.Title.Contains("Reference 01") ? 0 :
        p.Title.Contains("эталон", StringComparison.OrdinalIgnoreCase) || p.Title.Contains("портрет", StringComparison.OrdinalIgnoreCase) ? 1 : 2;

    private void Hero()
    {
        var stage = new Grid { Height = 216 };
        if (File.Exists(library.Citadel))
        {
            try
            {
                var art = new ImageBrush(Library.LoadBitmap(library.Citadel)) { Stretch = Stretch.UniformToFill, AlignmentY = AlignmentY.Center };
                stage.Background = art;
            }
            catch (Exception error) when (error is IOException or FormatException or NotSupportedException) { }
        }
        var gradient = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        gradient.GradientStops.Add(new(Color.FromArgb(255, 25, 22, 33), 0));
        gradient.GradientStops.Add(new(Color.FromArgb(230, 25, 22, 33), 0.36));
        gradient.GradientStops.Add(new(Color.FromArgb(22, 25, 22, 33), 1));
        stage.Children.Add(new Border { Background = gradient, CornerRadius = new CornerRadius(10) });
        var title = new StackPanel { Margin = new Thickness(30, 0, 18, 0), VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(Label("Бестиарий", 56, Ink, true));
        title.Children.Add(Label($"{photos.Count:N0} изображений в коллекции", 15, Gold, inset: new Thickness(3, 6, 0, 0)));
        stage.Children.Add(title);
        var panel = Frame(stage);
        panel.Margin = new Thickness(0, 0, 0, 15);
        content.Children.Add(panel);
    }

    private IEnumerable<Photo> Filter(string query)
    {
        IEnumerable<Photo> result = photos;
        if (query.Length > 0)
        {
            result = result.Where(p => p.Key.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (library.State.Series.GetValueOrDefault(p.Key)?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));
        }
        else if (section == "favorites")
            result = result.Where(p => library.State.Favorites.Contains(p.Key));
        else if (section is not ("recent" or "all"))
            result = result.Where(p => p.Album == section);
        if (series is not null && query.Length == 0)
            result = result.Where(p => library.State.Series.GetValueOrDefault(p.Key) == series);
        return section == "recent" || newest ? result.OrderByDescending(p => p.Added).ThenBy(p => p.Key) : result.OrderBy(p => p.Key, StringComparer.CurrentCultureIgnoreCase);
    }

    private void ContentHeader(string query, int count)
    {
        var header = new DockPanel { Margin = new Thickness(0, 4, 0, 14), LastChildFill = true };
        var command = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(command, Dock.Right);
        if (series is not null)
            command.Children.Add(Action("Все серии", () => Go(section)));
        command.Children.Add(Action(newest ? "По дате ↓" : "По имени", () => { newest = !newest; page = 0; Render(); }));
        if (selected.Count > 0)
        {
            command.Children.Add(Action($"В серию · {selected.Count}", () => SetSeries(photos.Where(p => selected.Contains(p.Key)).ToList())));
            command.Children.Add(Action("Снять выделение", () => { selected.Clear(); Render(); }));
        }
        header.Children.Add(command);
        var titles = new StackPanel();
        titles.Children.Add(Label(query.Length > 0 ? "Поиск" : series ?? (section switch
        {
            "all" => "Все изображения", "recent" => "Недавно добавленные", "favorites" => "Избранное", _ => section.Split('/')[^1]
        }), 34, Ink, true));
        titles.Children.Add(Label($"{count:N0} фото", 13, Muted));
        header.Children.Add(titles);
        content.Children.Add(header);
    }

    private void GalleryPage(List<Photo> results)
    {
        int pages = Math.Max(1, (results.Count + PageSize - 1) / PageSize);
        page = Math.Clamp(page, 0, pages - 1);
        if (results.Count == 0)
        {
            content.Children.Add(Label("Здесь пока нет изображений", 17, Muted, inset: new Thickness(0, 20, 0, 0)));
            return;
        }
        if (pages > 1) content.Children.Add(Pager(pages));
        var grid = new UniformGrid { Columns = 4 };
        foreach (Photo photo in results.Skip(page * PageSize).Take(PageSize))
        {
            string? stack = library.State.Series.GetValueOrDefault(photo.Key);
            bool grouped = stack is not null && section == photo.Album && series is null && search.Text.Length == 0;
            int index = results.IndexOf(photo);
            string caption = grouped ? stack! : photo.Title;
            string subtitle = grouped ? $"{photos.Count(p => p.Album == section && library.State.Series.GetValueOrDefault(p.Key) == stack)} фото в серии" : photo.Album.Split('/')[^1];
            grid.Children.Add(Tile(photo, caption, subtitle, () =>
            {
                if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && !grouped)
                {
                    Toggle(selected, photo.Key);
                    Render();
                }
                else if (grouped) Go(section, stack);
                else OpenViewer(results, index);
            }, Menu(photo)));
        }
        content.Children.Add(grid);
        if (pages > 1) content.Children.Add(Pager(pages));
    }

    private UIElement Pager(int pages)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 10, 12) };
        var previous = Action("‹", () => { page--; Render(); }, "Предыдущая страница");
        previous.IsEnabled = page > 0;
        var next = Action("›", () => { page++; Render(); }, "Следующая страница");
        next.IsEnabled = page + 1 < pages;
        row.Children.Add(previous);
        row.Children.Add(Label($"{page + 1} / {pages}", 13, Muted, inset: new Thickness(12, 7, 6, 0)));
        row.Children.Add(next);
        return row;
    }

    private UIElement Tile(Photo photo, string title, string subtitle, Action open, ContextMenu? menu)
    {
        bool hidden = library.State.HideMarked && library.State.Hidden.Contains(photo.Key);
        var art = new Grid { Height = 177, ClipToBounds = true, Background = Paint("#191721") };
        var image = new Image { Stretch = Stretch.UniformToFill };
        var placeholder = Label(hidden ? "Превью скрыто" : "", 13, Muted);
        placeholder.HorizontalAlignment = HorizontalAlignment.Center;
        placeholder.VerticalAlignment = VerticalAlignment.Center;
        art.Children.Add(image);
        art.Children.Add(placeholder);
        if (!hidden)
            _ = LoadImage(photo, image, placeholder, loading.Token);
        if (library.State.Favorites.Contains(photo.Key))
        {
            var badge = Label("♥", 16, Gold, true, new Thickness(9));
            badge.HorizontalAlignment = HorizontalAlignment.Right;
            art.Children.Add(badge);
        }
        var stack = new StackPanel();
        stack.Children.Add(art);
        stack.Children.Add(Label(title, 15, Ink, true, new Thickness(11, 8, 11, 2)));
        stack.Children.Add(Label(subtitle, 12, Muted, inset: new Thickness(11, 0, 11, 9)));
        var button = Action("", open);
        button.Content = stack;
        button.Padding = new Thickness(0);
        button.Margin = new Thickness(0, 0, 10, 12);
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        button.ToolTip = title;
        button.ContextMenu = menu;
        if (menu is not null && selected.Contains(photo.Key))
            button.BorderBrush = Gold;
        return button;
    }

    private async Task LoadImage(Photo photo, Image image, TextBlock placeholder, CancellationToken token)
    {
        try
        {
            image.Source = await library.Preview(photo, true, token);
            if (image.Source is null) placeholder.Text = "Не удалось открыть";
        }
        catch (OperationCanceledException) { }
    }

    private ContextMenu Menu(Photo photo)
    {
        var menu = new ContextMenu();
        void Item(string title, Action action)
        {
            var item = new MenuItem { Header = title };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        Item(library.State.Favorites.Contains(photo.Key) ? "Убрать из избранного" : "Добавить в избранное", () => Mutate(() => Toggle(library.State.Favorites, photo.Key)));
        Item("Сделать обложкой альбома", () => Mutate(() => library.State.Covers[photo.Album] = photo.Key));
        Item("Назначить серию…", () => SetSeries([photo]));
        Item(library.State.Hidden.Contains(photo.Key) ? "Снять отметку скрытия" : "Пометить для скрытия", () => Mutate(() => Toggle(library.State.Hidden, photo.Key)));
        return menu;
    }

    private static void Toggle(HashSet<string> set, string key)
    {
        if (!set.Remove(key)) set.Add(key);
    }

    private void Mutate(Action action, bool refresh = true)
    {
        try
        {
            action();
            library.Save();
            if (refresh) Render();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, e.Message, "Не удалось сохранить каталог");
        }
    }

    private void SetSeries(IReadOnlyList<Photo> selection)
    {
        var dialog = new Window { Owner = this, Title = "Серия", Width = 420, Height = 175,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Paint("#191720") };
        var box = new StackPanel { Margin = new Thickness(16) };
        box.Children.Add(Label("Имя серии. Пустое поле убирает фото из серии."));
        var input = new TextBox { Text = selection.Count == 1 ? library.State.Series.GetValueOrDefault(selection[0].Key) ?? "" : "", Margin = new Thickness(0, 10, 0, 10) };
        box.Children.Add(input);
        var save = Action("Сохранить", () => { dialog.DialogResult = true; });
        save.IsDefault = true;
        box.Children.Add(save);
        dialog.Content = box;
        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        if (dialog.ShowDialog() == true)
            Mutate(() =>
            {
                string name = input.Text.Trim();
                foreach (var photo in selection)
                {
                    if (name.Length == 0) library.State.Series.Remove(photo.Key);
                    else library.State.Series[photo.Key] = name;
                }
                selected.Clear();
            });
    }

    private void OpenViewer(IReadOnlyList<Photo> list, int index)
    {
        var viewer = new Viewer(this, library, list, index);
        viewer.ShowDialog();
        Render();
    }

    private async void Import()
    {
        if (importing) return;
        var picker = new OpenFolderDialog { Title = "Выбери папку с изображениями" };
        if (picker.ShowDialog(this) != true) return;
        importing = true;
        status.Text = "Проверяю точные дубли…";
        try
        {
            var result = await library.Import(picker.FolderName, photos, new Progress<string>(value => status.Text = value));
            photos = library.Scan();
            Go("recent");
            MessageBox.Show(this, $"Добавлено: {result.Added:N0}\nТочных дублей: {result.Duplicates:N0}\nОшибок: {result.Failed:N0}", "Импорт завершён");
        }
        catch (Exception error) { MessageBox.Show(this, error.Message, "Импорт остановлен"); }
        finally { importing = false; Render(); }
    }

    internal async Task Capture(string file)
    {
        await Task.Delay(2300);
        var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(shell);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(file);
        encoder.Save(output);
        Close();
    }
}
