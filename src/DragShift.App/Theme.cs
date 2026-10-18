using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;

namespace DragShift.App;

/// <summary>Warm, light look. Styles are XAML parsed at runtime, so the build doesn't need the XAML compiler.</summary>
static class Theme
{
    public static readonly Color Accent = Color.FromRgb(0xFF, 0x5A, 0x1F);
    public static readonly Color Ink = Color.FromRgb(0x2B, 0x1D, 0x16);
    public static readonly Color Muted = Color.FromRgb(0x8A, 0x6E, 0x60);
    public static readonly Color Panel = Color.FromRgb(0xFF, 0xF4, 0xEC);
    public static readonly Color PanelDeep = Color.FromRgb(0xFF, 0xE4, 0xD3);

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
          <SolidColorBrush x:Key="Accent" Color="#FF5A1F"/>
          <SolidColorBrush x:Key="Ink" Color="#2B1D16"/>

          <Style TargetType="TextBlock">
            <Setter Property="Foreground" Value="#2B1D16"/>
            <Setter Property="FontFamily" Value="Segoe UI Variable Text, Segoe UI"/>
          </Style>

          <Style x:Key="Pill" TargetType="ButtonBase">
            <Setter Property="Foreground" Value="#2B1D16"/>
            <Setter Property="Background" Value="#80FFFFFF"/>
            <Setter Property="BorderBrush" Value="#30A0522D"/>
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
                      <Setter TargetName="B" Property="Background" Value="#FFFFE0CC"/>
                    </Trigger>
                    <Trigger Property="ToggleButton.IsChecked" Value="True">
                      <Setter TargetName="B" Property="Background" Value="#FF5A1F"/>
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
            <Setter Property="Background" Value="#FF5A1F"/>
            <Setter Property="Foreground" Value="White"/>
            <Setter Property="BorderBrush" Value="#FF5A1F"/>
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
                      <Setter TargetName="Ring" Property="BorderBrush" Value="#FF5A1F"/>
                    </Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>

          <Style TargetType="Slider">
            <Setter Property="Foreground" Value="#FF5A1F"/>
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
