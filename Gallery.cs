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
    private bool importing;
    private bool newest;
    private bool onlyAlbumFavorites;
    private List<Photo> galleryPhotos = [];
    private StackPanel? visibleRows;
    private Border? beforeRows;
    private Border? afterRows;
    private int firstRow = -1, lastRow = -1, columns;
    private double rowHeight;
    private CancellationTokenSource rowLoading = new();

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
            Render(resetScroll: true);
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
        scroll.ScrollChanged += (_, _) => ShowVisibleRows();
        SizeChanged += (_, _) =>
        {
            if (visibleRows is not null)
                Dispatcher.BeginInvoke(() => { if (visibleRows is not null) ShowVisibleRows(); });
        };
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
        Closed += (_, _) => { loading.Cancel(); rowLoading.Cancel(); };
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
        onlyAlbumFavorites = false;
        if (search.Text.Length > 0)
            search.Clear();
        else
            Render(resetScroll: true);
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

    private void Render(bool resetScroll = false)
    {
        double position = resetScroll ? 0 : scroll.VerticalOffset;
        visibleRows = null;
        rowLoading.Cancel();
        loading.Cancel();
        loading.Dispose();
        loading = new CancellationTokenSource();
        nav.Children.Clear();
        content.Children.Clear();
        if (!importing)
        status.Text = $"{photos.Count:N0} файлов\nЛокальная библиотека";
        var albums = photos.GroupBy(p => p.Album).OrderBy(g => Rank(g.Key)).ThenBy(g => g.Key).ToList();
        AddNav("home", "Коллекция");
        AddNav("all", "Все изображения");
        AddNav("favorites", "Избранное", photos.Count(p => library.State.Favorites.Contains(p.Key)));
        AddNav("inbox", "Входящие", photos.Count(IsInbox));
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
            if (query.Length == 0 && series is null && albums.Any(g => g.Key == section) && !library.State.PhotoOnly)
                AlbumCard(filtered);
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
                    if (!library.State.PhotoOnly && !onlyAlbumFavorites)
                        filtered = filtered.Where(p => !library.State.Series.ContainsKey(p.Key))
                            .Concat(stacks.Select(g => g.First())).ToList();
                }
            }
            GalleryPage(filtered);
        }
        scroll.UpdateLayout();
        scroll.ScrollToVerticalOffset(position);
        ShowVisibleRows();
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
        title.Children.Add(Label($"{photos.Count:N0} файлов в коллекции", 15, Gold, inset: new Thickness(3, 6, 0, 0)));
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
            result = result.Where(p => p.Key.Contains(query, StringComparison.OrdinalIgnoreCase) || p.Album.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (library.State.Series.GetValueOrDefault(p.Key)?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));
        }
        else if (section == "favorites")
            result = result.Where(p => library.State.Favorites.Contains(p.Key));
        else if (section == "inbox")
            result = result.Where(IsInbox);
        else if (section is not ("recent" or "all"))
            result = result.Where(p => p.Album == section);
        if (onlyAlbumFavorites) result = result.Where(p => library.State.Favorites.Contains(p.Key));
        if (series is not null && query.Length == 0)
            result = result.Where(p => library.State.Series.GetValueOrDefault(p.Key) == series);
        return section is "recent" or "inbox" || newest ? result.OrderByDescending(p => p.Added).ThenBy(p => p.Key) : result.OrderBy(p => p.Key, StringComparer.CurrentCultureIgnoreCase);
    }

    private bool IsInbox(Photo p) => p.Key.StartsWith("Imports/", StringComparison.OrdinalIgnoreCase) && !library.State.Albums.ContainsKey(p.Key);

    private void AlbumCard(List<Photo> items)
    {
        if (items.Count == 0) return;
        var cover = items.FirstOrDefault(p => p.Key == library.State.Covers.GetValueOrDefault(section))
            ?? items.OrderBy(CoverRank).ThenBy(p => p.Key).First();
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var art = Tile(cover, "Обложка", "", () => OpenViewer(items, items.IndexOf(cover)), Menu(cover));
        ((FrameworkElement)art).Width = 226;
        panel.Children.Add(art);
        var description = new StackPanel { Margin = new Thickness(15, 15, 0, 10) };
        description.Children.Add(Label(section.Split('/')[^1], 26, Gold, true));
        description.Children.Add(Label($"{items.Count:N0} кадров  ·  {items.Count(p => library.State.Favorites.Contains(p.Key))} в избранном", 13, Muted));
        description.Children.Add(Action(onlyAlbumFavorites ? "Все кадры" : "Избранное альбома", () => { onlyAlbumFavorites = !onlyAlbumFavorites; Render(true); }));
        panel.Children.Add(description);
        content.Children.Add(panel);
    }

    private void ContentHeader(string query, int count)
    {
        var header = new DockPanel { Margin = new Thickness(0, 4, 0, 14), LastChildFill = true };
        var command = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(command, Dock.Right);
        if (series is not null)
            command.Children.Add(Action("Все серии", () => Go(section)));
        command.Children.Add(Action(library.State.PhotoOnly ? "Только фото ✓" : "Только фото", () => Mutate(() => library.State.PhotoOnly = !library.State.PhotoOnly)));
        command.Children.Add(Action(library.State.SingleColumn ? "В один столбец ✓" : "В один столбец",
            () => Mutate(() => library.State.SingleColumn = !library.State.SingleColumn)));
        var size = new Slider { Minimum = 200, Maximum = 760, Width = 95, Margin = new Thickness(8, 0, 4, 0),
            Value = library.State.TileHeight, ToolTip = "Размер кадра", VerticalAlignment = VerticalAlignment.Center };
        size.ValueChanged += (_, _) =>
        {
            library.State.TileHeight = size.Value;
            ShowVisibleRows();
        };
        size.PreviewMouseLeftButtonUp += (_, _) => Mutate(() => { }, false);
        size.PreviewKeyUp += (_, _) => Mutate(() => { }, false);
        command.Children.Add(size);
        command.Children.Add(Action(newest ? "По дате ↓" : "По имени", () => { newest = !newest; Render(true); }));
        if (section == "favorites" && count > 0)
            command.Children.Add(Action("Случайно", () => OpenViewer(Filter(query).OrderBy(_ => Random.Shared.Next()).ToList(), 0)));
        if (library.State.LastAssignment.Count > 0 && section == "inbox")
            command.Children.Add(Action("Отменить сортировку", UndoAssignment));
        if (selected.Count > 0)
        {
            command.Children.Add(Action($"В серию · {selected.Count}", () => SetSeries(photos.Where(p => selected.Contains(p.Key)).ToList())));
            command.Children.Add(Action("В альбом", AssignAlbum));
            if (selected.Count == 2)
                command.Children.Add(Action("Сравнить", CompareSelection));
            command.Children.Add(Action("Снять выделение", () => { selected.Clear(); Render(); }));
        }
        header.Children.Add(command);
        var titles = new StackPanel();
        titles.Children.Add(Label(query.Length > 0 ? "Поиск" : series ?? (section switch
        {
            "all" => "Все изображения", "recent" => "Недавно добавленные", "favorites" => "Избранное", "inbox" => "Входящие", _ => section.Split('/')[^1]
        }), 34, Ink, true));
        titles.Children.Add(Label($"{count:N0} фото", 13, Muted));
        header.Children.Add(titles);
        content.Children.Add(header);
    }

    private void GalleryPage(List<Photo> results)
    {
        if (results.Count == 0)
        {
            content.Children.Add(Label("Здесь пока нет изображений", 17, Muted, inset: new Thickness(0, 20, 0, 0)));
            return;
        }
        galleryPhotos = results;
        firstRow = lastRow = -1;
        beforeRows = new Border();
        afterRows = new Border();
        visibleRows = new StackPanel();
        content.Children.Add(beforeRows);
        content.Children.Add(visibleRows);
        content.Children.Add(afterRows);
        ShowVisibleRows();
    }

    private void ShowVisibleRows()
    {
        if (visibleRows is null || beforeRows is null || afterRows is null) return;
        double width = scroll.ViewportWidth > 100 ? scroll.ViewportWidth : Math.Max(550, ActualWidth - 280);
        double height = library.State.TileHeight;
        int columnCount = library.State.SingleColumn ? 1 : Math.Clamp((int)(width / (height * 0.72)), 1, 6);
        double stride = height + (library.State.PhotoOnly ? 12 : 60);
        var gridOrigin = beforeRows.TranslatePoint(new Point(0, 0), content).Y;
        int rowCount = (galleryPhotos.Count + columnCount - 1) / columnCount;
        int start = Math.Clamp((int)Math.Floor((scroll.VerticalOffset - gridOrigin) / stride) - 1, 0, rowCount - 1);
        int end = Math.Min(rowCount, start + Math.Max(3, (int)Math.Ceiling(scroll.ViewportHeight / stride) + 3));
        if (start == firstRow && end == lastRow && columns == columnCount && rowHeight == height) return;
        firstRow = start;
        lastRow = end;
        columns = columnCount;
        rowHeight = height;
        rowLoading.Cancel();
        rowLoading.Dispose();
        rowLoading = new CancellationTokenSource();
        beforeRows.Height = start * stride;
        afterRows.Height = (rowCount - end) * stride;
        visibleRows.Children.Clear();
        for (int row = start; row < end; row++)
        {
            var grid = new UniformGrid { Columns = columnCount, Rows = 1, Height = stride };
            for (int column = 0; column < columnCount; column++)
            {
                int index = row * columnCount + column;
                if (index >= galleryPhotos.Count) break;
                var photo = galleryPhotos[index];
                string? stack = library.State.Series.GetValueOrDefault(photo.Key);
                bool grouped = !library.State.PhotoOnly && stack is not null && !onlyAlbumFavorites && section == photo.Album && series is null && search.Text.Length == 0;
                string subtitle = grouped ? $"{photos.Count(p => p.Album == section && library.State.Series.GetValueOrDefault(p.Key) == stack)} фото в серии" : photo.Album.Split('/')[^1];
                grid.Children.Add(Tile(photo, grouped ? stack! : photo.Title, subtitle, () =>
                {
                    if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && !grouped)
                    {
                        Toggle(selected, photo.Key);
                        Render();
                    }
                    else if (grouped) Go(section, stack);
                    else OpenViewer(galleryPhotos, index);
                }, Menu(photo), true));
            }
            visibleRows.Children.Add(grid);
        }
    }

    private UIElement Tile(Photo photo, string title, string subtitle, Action open, ContextMenu? menu, bool inGallery = false)
    {
        bool only = inGallery && library.State.PhotoOnly;
        bool hidden = library.State.HideMarked && library.State.Hidden.Contains(photo.Key);
        var art = new Grid { Height = inGallery ? library.State.TileHeight : 177, ClipToBounds = true, Background = Paint("#191721") };
        var image = new Image { Stretch = only ? Stretch.Uniform : Stretch.UniformToFill };
        var placeholder = Label(hidden ? "Превью скрыто" : photo.IsVideo ? "▶  Видео" : "", 13, Muted);
        placeholder.HorizontalAlignment = HorizontalAlignment.Center;
        placeholder.VerticalAlignment = VerticalAlignment.Center;
        art.Children.Add(image);
        art.Children.Add(placeholder);
        if (!hidden && !photo.IsVideo)
            _ = LoadImage(photo, image, placeholder, inGallery ? rowLoading.Token : loading.Token);
        if (!only && library.State.Favorites.Contains(photo.Key))
        {
            var badge = Label("♥", 16, Gold, true, new Thickness(9));
            badge.HorizontalAlignment = HorizontalAlignment.Right;
            art.Children.Add(badge);
        }
        var stack = new StackPanel();
        stack.Children.Add(art);
        if (!only)
        {
            stack.Children.Add(Label(title, 15, Ink, true, new Thickness(11, 8, 11, 2)));
            stack.Children.Add(Label(subtitle, 12, Muted, inset: new Thickness(11, 0, 11, 9)));
        }
        var button = Action("", open);
        button.Content = stack;
        button.Padding = new Thickness(0);
        button.Margin = new Thickness(0, 0, 10, 12);
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        if (!only) button.ToolTip = title;
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

    private void AssignAlbum()
    {
        var dialog = new Window { Owner = this, Title = "Назначить альбом", Width = 400, Height = 165,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Paint("#191720") };
        var panel = new StackPanel { Margin = new Thickness(15) };
        panel.Children.Add(Label($"Кадров выбрано: {selected.Count}"));
        var choices = new ComboBox { Margin = new Thickness(0, 10, 0, 10), IsEditable = true };
        choices.ItemsSource = photos.Select(p => p.Album).Concat(["Henkai", "Other References", "Random References", "Off Topic"])
            .Where(name => name != "Imports").Distinct().Order().ToList();
        choices.Text = "Other References";
        panel.Children.Add(choices);
        panel.Children.Add(Action("Сохранить", () => dialog.DialogResult = true));
        dialog.Content = panel;
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(choices.Text)) return;
        string album = choices.Text.Trim();
        Mutate(() =>
        {
            library.State.LastAssignment.Clear();
            foreach (string key in selected)
            {
                library.State.LastAssignment[key] = library.State.Albums.GetValueOrDefault(key);
                library.State.Albums[key] = album;
            }
            selected.Clear();
            photos = library.Scan();
        });
    }

    private void UndoAssignment() => Mutate(() =>
    {
        foreach (var item in library.State.LastAssignment)
            if (item.Value is null) library.State.Albums.Remove(item.Key);
            else library.State.Albums[item.Key] = item.Value;
        library.State.LastAssignment.Clear();
        photos = library.Scan();
    });

    private void CompareSelection()
    {
        var pair = photos.Where(p => selected.Contains(p.Key) && !p.IsVideo).Take(2).ToList();
        if (pair.Count == 2)
        {
            new Comparison(this, library, pair[0], pair[1]).ShowDialog();
            Render();
        }
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
            Go("inbox");
            MessageBox.Show(this, $"Добавлено: {result.Added:N0}\nТочных дублей: {result.Duplicates:N0}\nОшибок: {result.Failed:N0}", "Импорт завершён");
        }
        catch (Exception error) { MessageBox.Show(this, error.Message, "Импорт остановлен"); }
        finally { importing = false; Render(); }
    }

    internal async Task Capture(string file, bool gallery = false)
    {
        if (gallery)
        {
            Go("all");
            await Task.Delay(450);
            scroll.ScrollToVerticalOffset(1300);
            await Task.Delay(350);
            if (galleryPhotos.Count > 30 && firstRow <= 0)
                throw new InvalidOperationException("Прокрутка галереи не загрузила следующие строки.");
        }
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
