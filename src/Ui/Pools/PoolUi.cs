using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace Counterplay;

/// <summary>Единый тёмный стиль окон пулов: кнопки, поля ввода, верхушка окна.</summary>
static class PoolUi
{
    private static ResourceDictionary? _rd;
    public static Style ButtonStyle { get; private set; } = null!;
    public static Style GoldStyle   { get; private set; } = null!;
    public static DataTemplate RoleItemTemplate { get { Ensure(); return _roleItem!; } }
    private static DataTemplate? _roleItem;

    private const string Xaml = @"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                    xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Style x:Key='CpButton' TargetType='Button'>
    <Setter Property='Foreground' Value='#C9D2DC'/>
    <Setter Property='Background' Value='#1A2430'/>
    <Setter Property='BorderBrush' Value='#35485A'/>
    <Setter Property='BorderThickness' Value='1'/>
    <Setter Property='Padding' Value='12,5'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='SnapsToDevicePixels' Value='True'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='Button'>
          <Border x:Name='b' CornerRadius='5' Background='{TemplateBinding Background}'
                  BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}'>
            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center' Margin='{TemplateBinding Padding}'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='b' Property='Background' Value='#22364F'/>
              <Setter TargetName='b' Property='BorderBrush' Value='#5A8AC8'/>
            </Trigger>
            <Trigger Property='IsPressed' Value='True'>
              <Setter TargetName='b' Property='Background' Value='#2C445F'/>
            </Trigger>
            <Trigger Property='IsEnabled' Value='False'>
              <Setter Property='Opacity' Value='0.5'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- Кнопка-акцент, золото hextech. Для действия, которое стоит В СТОРОНЕ от
       хода формы (выгрузить пул файлом), — среди одинаковых тёмных кнопок
       «Назад/Сброс/Сохранить» такую не находили глазами. -->
  <Style x:Key='CpButtonGold' TargetType='Button'>
    <Setter Property='Foreground' Value='#EBD6A8'/>
    <Setter Property='Background' Value='#2A2418'/>
    <Setter Property='BorderBrush' Value='#C8AA6E'/>
    <Setter Property='BorderThickness' Value='1'/>
    <Setter Property='Padding' Value='12,5'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='SnapsToDevicePixels' Value='True'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='Button'>
          <Border x:Name='b' CornerRadius='5' Background='{TemplateBinding Background}'
                  BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}'>
            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center' Margin='{TemplateBinding Padding}'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='b' Property='Background' Value='#3B3220'/>
              <Setter TargetName='b' Property='BorderBrush' Value='#E4C98A'/>
              <Setter Property='Foreground' Value='#F6E8C6'/>
            </Trigger>
            <Trigger Property='IsPressed' Value='True'>
              <Setter TargetName='b' Property='Background' Value='#4A3E27'/>
            </Trigger>
            <Trigger Property='IsEnabled' Value='False'>
              <Setter Property='Opacity' Value='0.5'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- Пункт списка роли: иконка роли + полное название мелким шрифтом -->
  <DataTemplate x:Key='RoleItem'>
    <StackPanel Orientation='Horizontal'>
      <Image Source='{Binding Icon}' Width='14' Height='14' Margin='0,0,5,0' VerticalAlignment='Center'/>
      <TextBlock Text='{Binding Name}' FontSize='10' VerticalAlignment='Center'/>
    </StackPanel>
  </DataTemplate>

  <Style TargetType='ComboBoxItem'>
    <Setter Property='Foreground' Value='#D7DEE6'/>
    <Setter Property='Padding' Value='7,4'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ComboBoxItem'>
          <Border x:Name='ib' Background='Transparent' CornerRadius='3' Padding='{TemplateBinding Padding}'>
            <ContentPresenter/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='ib' Property='Background' Value='#22364F'/>
            </Trigger>
            <Trigger Property='IsSelected' Value='True'>
              <Setter TargetName='ib' Property='Background' Value='#2C445F'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType='ComboBox'>
    <Setter Property='Foreground' Value='#E6EDF3'/>
    <Setter Property='Background' Value='#0F1822'/>
    <Setter Property='BorderBrush' Value='#35485A'/>
    <Setter Property='BorderThickness' Value='1'/>
    <Setter Property='SnapsToDevicePixels' Value='True'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ComboBox'>
          <Grid>
            <ToggleButton x:Name='tb' Focusable='False' ClickMode='Press'
                Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}'
                IsChecked='{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}'>
              <ToggleButton.Template>
                <ControlTemplate TargetType='ToggleButton'>
                  <Border x:Name='cb' CornerRadius='5' Background='{TemplateBinding Background}'
                          BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1'>
                    <Path HorizontalAlignment='Right' VerticalAlignment='Center' Margin='0,0,7,0'
                          Data='M0,0 L4,4 L8,0 Z' Fill='#9FB3C8'/>
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property='IsMouseOver' Value='True'>
                      <Setter TargetName='cb' Property='BorderBrush' Value='#5A8AC8'/>
                      <Setter TargetName='cb' Property='Background' Value='#16202C'/>
                    </Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </ToggleButton.Template>
            </ToggleButton>
            <ContentPresenter Content='{TemplateBinding SelectionBoxItem}'
                ContentTemplate='{TemplateBinding SelectionBoxItemTemplate}'
                Margin='7,3,20,3' VerticalAlignment='Center' HorizontalAlignment='Left'
                IsHitTestVisible='False'/>
            <Popup x:Name='PART_Popup' Placement='Bottom' AllowsTransparency='True' Focusable='False'
                   PopupAnimation='Slide'
                   IsOpen='{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}}'>
              <Border Background='#121A24' BorderBrush='#35485A' BorderThickness='1' CornerRadius='5'
                      Margin='0,2,0,0'
                      MinWidth='{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}'>
                <ScrollViewer MaxHeight='240'>
                  <ItemsPresenter/>
                </ScrollViewer>
              </Border>
            </Popup>
          </Grid>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- Полоса прокрутки: тонкая, без стрелок, в тон окну -->
  <Style x:Key='CpScrollThumb' TargetType='Thumb'>
    <Setter Property='OverridesDefaultStyle' Value='True'/>
    <Setter Property='IsTabStop' Value='False'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='Thumb'>
          <Border x:Name='th' Background='#35485A' CornerRadius='4' Margin='2'/>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='th' Property='Background' Value='#5A8AC8'/>
            </Trigger>
            <Trigger Property='IsDragging' Value='True'>
              <Setter TargetName='th' Property='Background' Value='#6FA0DC'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key='CpScrollPage' TargetType='RepeatButton'>
    <Setter Property='OverridesDefaultStyle' Value='True'/>
    <Setter Property='Focusable' Value='False'/>
    <Setter Property='IsTabStop' Value='False'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='RepeatButton'>
          <Border Background='Transparent'/>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType='ScrollBar'>
    <Setter Property='Background' Value='Transparent'/>
    <Setter Property='Width' Value='11'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ScrollBar'>
          <Grid Background='Transparent'>
            <Border Background='#14FFFFFF' CornerRadius='4' Margin='3'/>
            <Track x:Name='PART_Track' IsDirectionReversed='True'>
              <Track.DecreaseRepeatButton>
                <RepeatButton Style='{StaticResource CpScrollPage}' Command='ScrollBar.PageUpCommand'/>
              </Track.DecreaseRepeatButton>
              <Track.Thumb>
                <Thumb Style='{StaticResource CpScrollThumb}'/>
              </Track.Thumb>
              <Track.IncreaseRepeatButton>
                <RepeatButton Style='{StaticResource CpScrollPage}' Command='ScrollBar.PageDownCommand'/>
              </Track.IncreaseRepeatButton>
            </Track>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property='Orientation' Value='Horizontal'>
              <Setter TargetName='PART_Track' Property='IsDirectionReversed' Value='False'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
    <Style.Triggers>
      <Trigger Property='Orientation' Value='Horizontal'>
        <Setter Property='Width'  Value='Auto'/>
        <Setter Property='Height' Value='11'/>
      </Trigger>
    </Style.Triggers>
  </Style>

  <Style TargetType='TextBox'>
    <Setter Property='Foreground' Value='#E6EDF3'/>
    <Setter Property='CaretBrush' Value='#E6EDF3'/>
    <Setter Property='Background' Value='#0F1822'/>
    <Setter Property='BorderBrush' Value='#35485A'/>
    <Setter Property='BorderThickness' Value='1'/>
    <Setter Property='Padding' Value='7,5'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='TextBox'>
          <Border CornerRadius='5' Background='{TemplateBinding Background}'
                  BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}'>
            <ScrollViewer x:Name='PART_ContentHost' Margin='{TemplateBinding Padding}'/>
          </Border>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
</ResourceDictionary>";

    private static void Ensure()
    {
        if (_rd != null) return;
        _rd = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(Xaml);
        ButtonStyle = (Style)_rd["CpButton"];
        GoldStyle   = (Style)_rd["CpButtonGold"];
        _roleItem   = (DataTemplate)_rd["RoleItem"];
    }

    // Тёмные поля ввода (неявный стиль) + доступ к стилю кнопок.
    public static void Apply(Window w)
    {
        Ensure();
        w.Resources.MergedDictionaries.Add(_rd!);
    }

    public static Button Btn(string text) { Ensure(); return new Button { Content = text, Style = ButtonStyle }; }

    /// Кнопка-акцент золотом — для действия в сторону от хода формы.
    public static Button GoldBtn(string text) { Ensure(); return new Button { Content = text, Style = GoldStyle }; }

    // Стрелки ⭱/⭳ есть ровно в одном системном шрифте (Segoe UI Symbol), а
    // интерфейс набран Geist — просим шрифт для значка явно, чтобы не зависеть
    // от того, как система подберёт замену.
    public static readonly System.Windows.Media.FontFamily SymbolFont = new("Segoe UI Symbol, Segoe UI");

    /// Значок + подпись одной строкой: значок своим шрифтом, текст — шрифтом окна.
    public static FrameworkElement IconLabel(string glyph, string text)
    {
        var t = new TextBlock();
        t.Inlines.Add(new System.Windows.Documents.Run(glyph + "  ") { FontFamily = SymbolFont, FontSize = 13 });
        t.Inlines.Add(new System.Windows.Documents.Run(text));
        return t;
    }

    // Кастомная тёмная верхушка окна: заголовок + крестик, перетаскивание, рамка.
    public static FrameworkElement Chrome(Window w, string title, FrameworkElement inner, bool resizable = false)
    {
        w.WindowStyle = WindowStyle.None;
        w.ResizeMode = resizable ? ResizeMode.CanResize : ResizeMode.NoResize;

        // WindowStyle.None + CanResize сам по себе оставляет сверху белую полосу:
        // системная рамка никуда не девается, её просто нечем закрыть. WindowChrome
        // убирает её целиком (GlassFrameThickness = 0), оставляя от системы только
        // полосу захвата по краям — за неё и тянут. CaptionHeight = 0, иначе Windows
        // считает верх окна заголовком и перехватывает наше перетаскивание.
        if (resizable)
            System.Windows.Shell.WindowChrome.SetWindowChrome(w, new System.Windows.Shell.WindowChrome
            {
                CaptionHeight = 0,
                GlassFrameThickness = new Thickness(0),
                ResizeBorderThickness = new Thickness(6),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false,
            });

        var root = new DockPanel();
        var bar = new Border { Height = 36, Background = new SolidColorBrush(Color.FromRgb(0x12, 0x1A, 0x24)) };
        bar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) w.DragMove(); };
        var barDock = new DockPanel { LastChildFill = true };

        var close = new Button
        {
            Content = "✕", Width = 44, FontSize = 13, Cursor = System.Windows.Input.Cursors.Hand,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(Color.FromRgb(0x9F, 0xB3, 0xC8))
        };
        Ensure();
        close.Style = CloseStyle();
        close.Click += (_, _) => w.Close();
        DockPanel.SetDock(close, Dock.Right);
        barDock.Children.Add(close);
        barDock.Children.Add(new TextBlock
        {
            Text = title, Foreground = new SolidColorBrush(Color.FromRgb(0xC9, 0xD2, 0xDC)),
            FontWeight = FontWeights.Bold, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(13, 0, 0, 0)
        });
        bar.Child = barDock;
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(inner);

        FrameworkElement body = root;
        if (resizable)
        {
            var layers = new Grid();
            layers.Children.Add(root);
            layers.Children.Add(Grip(w));
            body = layers;
        }

        return new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x3A, 0x4A)), BorderThickness = new Thickness(1),
            Child = body
        };
    }

    // Уголок в правом нижнем углу: за края тянуть можно, но их не видно —
    // уголок показывает, что окно вообще растягивается. Считаем в
    // координатах окна (не экрана) — тогда одинаково работает при любом масштабе
    // экрана, а не только при 100%.
    private static FrameworkElement Grip(Window w)
    {
        var grip = new Canvas
        {
            Width = 15, Height = 15, Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Cursor = System.Windows.Input.Cursors.SizeNWSE
        };
        var ink = new SolidColorBrush(Color.FromRgb(0x6B, 0x80, 0x96));
        for (var i = 0; i < 3; i++)
        {
            var o = 3.5 + i * 3.5;
            grip.Children.Add(new System.Windows.Shapes.Line
            { X1 = 13.5, Y1 = o, X2 = o, Y2 = 13.5, Stroke = ink, StrokeThickness = 1.2 });
        }

        var dragging = false;
        double dx = 0, dy = 0;   // сколько от курсора до края окна — чтобы край не прыгал под курсор
        grip.MouseLeftButtonDown += (_, e) =>
        {
            var p = e.GetPosition(w);
            dx = w.ActualWidth - p.X; dy = w.ActualHeight - p.Y;
            dragging = grip.CaptureMouse();
            e.Handled = true;
        };
        grip.MouseMove += (_, e) =>
        {
            if (!dragging) return;
            var p = e.GetPosition(w);
            w.Width  = Math.Max(w.MinWidth,  p.X + dx);
            w.Height = Math.Max(w.MinHeight, p.Y + dy);
        };
        grip.MouseLeftButtonUp += (_, _) => { dragging = false; grip.ReleaseMouseCapture(); };
        return grip;
    }

    // Крестик закрытия: прозрачный, красный при наведении.
    private static Style CloseStyle()
    {
        var s = new Style(typeof(Button));
        var t = new ControlTemplate(typeof(Button));
        var b = new System.Windows.FrameworkElementFactory(typeof(Border));
        b.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        b.Name = "cb";
        var cp = new System.Windows.FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        b.AppendChild(cp);
        t.VisualTree = b;
        var tr = new Trigger { Property = System.Windows.Controls.Control.IsMouseOverProperty, Value = true };
        tr.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0xC0, 0x41, 0x3B)), "cb"));
        tr.Setters.Add(new Setter(System.Windows.Controls.Control.ForegroundProperty, Brushes.White));
        t.Triggers.Add(tr);
        s.Setters.Add(new Setter(System.Windows.Controls.Control.TemplateProperty, t));
        return s;
    }
}
