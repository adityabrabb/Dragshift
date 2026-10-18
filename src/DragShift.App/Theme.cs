using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;

namespace DragShift.App;

/// <summary>Fresh green, light look. Styles are XAML parsed at runtime, so the build doesn't need the XAML compiler.</summary>
static class Theme
{
    public static readonly Color Accent = Color.FromRgb(0x1F, 0x9D, 0x55);
    public static readonly Color Ink = Color.FromRgb(0x12, 0x26, 0x1B);
    public static readonly Color Muted = Color.FromRgb(0x5A, 0x76, 0x66);
    public static readonly Color Panel = Color.FromRgb(0xF1, 0xFA, 0xF4);
    public static readonly Color PanelDeep = Color.FromRgb(0xDC, 0xF1, 0xE3);

    public static readonly SolidColorBrush AccentBrush = Freeze(new SolidColorBrush(Accent));
    public static readonly SolidColorBrush InkBrush = Freeze(new SolidColorBrush(Ink));
    public static readonly SolidColorBrush MutedBrush = Freeze(new SolidColorBrush(Muted));

    public static Brush WindowBackground => Freeze(new LinearGradientBrush(Panel, PanelDeep, 90));

    static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }

    const string Styles = """
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <SolidColorBrush x:Key="Accent" Color="#1F9D55"/>
          <SolidColorBrush x:Key="Ink" Color="#12261B"/>

          <Style TargetType="TextBlock">
            <Setter Property="Foreground" Value="#12261B"/>
            <Setter Property="FontFamily" Value="Segoe UI Variable Text, Segoe UI"/>
          </Style>

          <Style x:Key="Pill" TargetType="ButtonBase">
            <Setter Property="Foreground" Value="#12261B"/>
            <Setter Property="Background" Value="#80FFFFFF"/>
            <Setter Property="BorderBrush" Value="#302E7D4F"/>
            <Setter Property="BorderThickness" Value="1"/>
            <Setter Property="Padding" Value="14,6"/>
            <Setter Property="Margin" Value="3"/>
            <Setter Property="FontSize" Value="13"/>
            <Setter Property="Cursor" Value="Hand"/>
            <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="ButtonBase">
                  <Border x:Name="B" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                          BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="8" Padding="{TemplateBinding Padding}">
                    <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True">
                      <Setter TargetName="B" Property="Background" Value="#C0FFFFFF"/>
                    </Trigger>
                    <Trigger Property="IsPressed" Value="True">
                      <Setter TargetName="B" Property="Background" Value="#FFDCF1E3"/>
                    </Trigger>
                    <Trigger Property="ToggleButton.IsChecked" Value="True">
                      <Setter TargetName="B" Property="Background" Value="#1F9D55"/>
                      <Setter Property="Foreground" Value="White"/>
                    </Trigger>
                    <Trigger Property="IsEnabled" Value="False">
                      <Setter Property="Opacity" Value="0.45"/>
                    </Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>
          <Style TargetType="Button" BasedOn="{StaticResource Pill}"/>
          <Style TargetType="ToggleButton" BasedOn="{StaticResource Pill}"/>
          <Style TargetType="RadioButton" BasedOn="{StaticResource Pill}"/>

          <Style x:Key="Primary" TargetType="Button" BasedOn="{StaticResource Pill}">
            <Setter Property="Background" Value="#1F9D55"/>
            <Setter Property="Foreground" Value="White"/>
            <Setter Property="BorderBrush" Value="#1F9D55"/>
            <Setter Property="FontWeight" Value="SemiBold"/>
          </Style>

          <Style x:Key="Swatch" TargetType="RadioButton">
            <Setter Property="Width" Value="30"/>
            <Setter Property="Height" Value="30"/>
            <Setter Property="Margin" Value="3"/>
            <Setter Property="Cursor" Value="Hand"/>
            <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="RadioButton">
                  <Border x:Name="Ring" CornerRadius="8" BorderThickness="2" BorderBrush="Transparent" Padding="2">
                    <Border CornerRadius="6" Background="{TemplateBinding Background}" BorderBrush="#20000000" BorderThickness="1">
                      <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                    </Border>
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsChecked" Value="True">
                      <Setter TargetName="Ring" Property="BorderBrush" Value="#1F9D55"/>
                    </Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>

          <Style TargetType="Slider">
            <Setter Property="Foreground" Value="#1F9D55"/>
            <Setter Property="VerticalAlignment" Value="Center"/>
            <Setter Property="IsMoveToPointEnabled" Value="True"/>
          </Style>
        </ResourceDictionary>
        """;

    public static void Apply(Application app)
    {
        app.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(Styles));
    }

    public static Style Style(string key) => (Style)Application.Current.FindResource(key);
}
