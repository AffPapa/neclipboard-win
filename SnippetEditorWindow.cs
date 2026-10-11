using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using MediaFontFamily = System.Windows.Media.FontFamily;
using WpfControls = System.Windows.Controls;

namespace NeClipboardWin;

public sealed class SnippetEditorWindow : Window
{
    private readonly WpfControls.TextBox _titleBox = new();
    private readonly WpfControls.TextBox _contentBox = new();
    private readonly WpfControls.TextBlock _errorText = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
    private readonly Func<Snippet, string?>? _validate;
    public Snippet Result { get; }

    public SnippetEditorWindow(Snippet? source = null, Func<Snippet, string?>? validate = null)
    {
        _validate = validate;
        Result = new Snippet
        {
            Id = source?.Id ?? Guid.NewGuid(),
            Title = source?.Title ?? string.Empty,
            Content = source?.Content ?? string.Empty,
            UpdatedUtc = DateTime.UtcNow
        };
        Title = source is null ? "Новый сниппет" : "Изменить сниппет";
        Width = 520;
        Height = 390;
        MinWidth = 420;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ThemeManager.Bind(this, BackgroundProperty, "AppBackgroundBrush");
        ThemeManager.Bind(this, ForegroundProperty, "TextBrush");
        FontFamily = new MediaFontFamily("Segoe UI Variable, Segoe UI");
        SourceInitialized += (_, _) => ThemeManager.ApplyWindowTheme(this);

        _titleBox.Text = Result.Title;
        _titleBox.MaxLength = 200;
        AutomationProperties.SetName(_titleBox, "Название сниппета");
        _contentBox.Text = Result.Content;
        _contentBox.MaxLength = StorageSafety.MaximumTextCharacters;
        AutomationProperties.SetName(_contentBox, "Текст сниппета");
        _contentBox.AcceptsReturn = true;
        _contentBox.TextWrapping = TextWrapping.Wrap;
        _contentBox.VerticalScrollBarVisibility = WpfControls.ScrollBarVisibility.Auto;
        _titleBox.TextChanged += (_, _) => RefreshBlankFieldError();
        _contentBox.TextChanged += (_, _) => RefreshBlankFieldError();

        ThemeManager.Bind(_titleBox, WpfControls.TextBox.BackgroundProperty, "SurfaceRaisedBrush");
        ThemeManager.Bind(_titleBox, WpfControls.TextBox.ForegroundProperty, "TextBrush");
        ThemeManager.Bind(_titleBox, WpfControls.TextBox.BorderBrushProperty, "BorderBrush");
        _titleBox.Padding = new Thickness(9, 7, 9, 7);
        ThemeManager.Bind(_contentBox, WpfControls.TextBox.BackgroundProperty, "SurfaceRaisedBrush");
        ThemeManager.Bind(_contentBox, WpfControls.TextBox.ForegroundProperty, "TextBrush");
        ThemeManager.Bind(_contentBox, WpfControls.TextBox.BorderBrushProperty, "BorderBrush");
        _contentBox.Padding = new Thickness(9, 7, 9, 7);

        var grid = new WpfControls.Grid { Margin = new Thickness(20) };
        grid.RowDefinitions.Add(new WpfControls.RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new WpfControls.RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new WpfControls.RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new WpfControls.RowDefinition());
        grid.RowDefinitions.Add(new WpfControls.RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new WpfControls.RowDefinition { Height = GridLength.Auto });
        var titleLabel = new WpfControls.TextBlock { Text = "Название", FontSize = 12 };
        ThemeManager.Bind(titleLabel, WpfControls.TextBlock.ForegroundProperty, "MutedBrush");
        grid.Children.Add(titleLabel);
        WpfControls.Grid.SetRow(_titleBox, 1);
        grid.Children.Add(_titleBox);
        var label = new WpfControls.TextBlock { Text = "Текст сниппета", FontSize = 12, Margin = new Thickness(0, 12, 0, 5) };
        ThemeManager.Bind(label, WpfControls.TextBlock.ForegroundProperty, "MutedBrush");
        WpfControls.Grid.SetRow(label, 2);
        grid.Children.Add(label);
        WpfControls.Grid.SetRow(_contentBox, 3);
        grid.Children.Add(_contentBox);
        var buttons = new WpfControls.StackPanel { Orientation = WpfControls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = CreateButton("Отмена", false);
        cancel.IsCancel = true;
        var save = CreateButton("Сохранить", true);
        save.IsDefault = true;
        save.Click += (_, _) => Save();
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        ThemeManager.Bind(_errorText, WpfControls.TextBlock.ForegroundProperty, "TextBrush");
        AutomationProperties.SetName(_errorText, "Ошибка заполнения сниппета");
        AutomationProperties.SetLiveSetting(_errorText, AutomationLiveSetting.Assertive);
        WpfControls.Grid.SetRow(_errorText, 4);
        grid.Children.Add(_errorText);
        WpfControls.Grid.SetRow(buttons, 5);
        grid.Children.Add(buttons);
        Content = grid;
        ContentRendered += (_, _) => _titleBox.Focus();
    }

    private static WpfControls.Button CreateButton(string text, bool primary)
    {
        var button = new WpfControls.Button
        {
            Content = text,
            Margin = new Thickness(4),
            Padding = new Thickness(12, 7, 12, 7),
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand,
            FontSize = 12
        };
        ThemeManager.Bind(button, WpfControls.Button.BackgroundProperty, primary ? "AccentBrush" : "SurfaceRaisedBrush");
        ThemeManager.Bind(button, WpfControls.Button.ForegroundProperty, primary ? "AccentTextBrush" : "TextBrush");
        ThemeManager.Bind(button, WpfControls.Button.BorderBrushProperty, primary ? "AccentBrush" : "BorderStrongBrush");
        return button;
    }

    private void Save()
    {
        if (string.IsNullOrWhiteSpace(_titleBox.Text) || string.IsNullOrWhiteSpace(_contentBox.Text))
        {
            ShowError(string.IsNullOrWhiteSpace(_titleBox.Text) ? "Укажите название заготовки." : "Добавьте текст заготовки.");
            (string.IsNullOrWhiteSpace(_titleBox.Text) ? _titleBox : _contentBox).Focus();
            return;
        }
        var proposed = Result.DeepClone();
        proposed.Title = _titleBox.Text.Trim();
        proposed.Content = _contentBox.Text;
        var error = _validate?.Invoke(proposed);
        if (error is not null) { ShowError(error); return; }
        Result.Title = proposed.Title;
        Result.Content = proposed.Content;
        Result.UpdatedUtc = DateTime.UtcNow;
        DialogResult = true;
    }

    private void ShowError(string message)
    {
        _errorText.Text = message;
        AutomationProperties.SetName(_errorText, message);
        _errorText.Visibility = Visibility.Visible;
        FrameworkElementAutomationPeer.CreatePeerForElement(_errorText)?
            .RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void RefreshBlankFieldError()
    {
        if (_errorText.Visibility != Visibility.Visible
            || (_errorText.Text != "Укажите название заготовки." && _errorText.Text != "Добавьте текст заготовки."))
            return;

        var message = string.IsNullOrWhiteSpace(_titleBox.Text)
            ? "Укажите название заготовки."
            : string.IsNullOrWhiteSpace(_contentBox.Text)
                ? "Добавьте текст заготовки."
                : null;
        if (message is null)
        {
            _errorText.Visibility = Visibility.Collapsed;
            _errorText.Text = string.Empty;
        }
        else if (_errorText.Text != message)
        {
            ShowError(message);
        }
    }
}
