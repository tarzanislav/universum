using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Universum.Core.Library;

namespace Universum.App.Pages;

/// <summary>
/// A poster shown in a list: loaded from the poster cache only when something first shows it (so a long list downloads
/// only what is on the screen), as a frozen bitmap decoded at the size it is drawn.
/// </summary>
public sealed class PosterThumb(AppServices services, string? path, PosterSize size) : ObservableObject
{
    private ImageSource? _image;
    private bool _asked;

    /// <summary>The picture; null while it loads, without a poster or with posters switched off.</summary>
    public ImageSource? Image
    {
        get
        {
            if (!_asked)
            {
                _asked = true;
                _ = LoadAsync();
            }

            return _image;
        }
    }

    /// <summary>Asks for the picture again (posters switched on, another server).</summary>
    public void Reset()
    {
        _asked = false;
        _image = null;
        OnPropertyChanged(nameof(Image));
    }

    private async Task LoadAsync()
    {
        if (services.Posters is not { } posters || path is null || await posters.GetAsync(path, size) is not { } file)
        {
            return;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = size == PosterSize.Large ? 342 : 154;
            bitmap.UriSource = new Uri(file);
            bitmap.EndInit();
            bitmap.Freeze();
            _image = bitmap;
            OnPropertyChanged(nameof(Image));
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException or ArgumentException or FileFormatException)
        {
            // a picture WPF cannot read stays a placeholder
        }
    }
}

/// <summary>Opens the pages of titles (TMDB, HISTRA, IMDb, ČSFD) in the user's browser; the self-test only records them.</summary>
public static class Browser
{
    public static Action<Uri> Open { get; set; } = address =>
    {
        if (address.Scheme == Uri.UriSchemeHttps)
        {
            Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true })?.Dispose();
        }
    };
}
