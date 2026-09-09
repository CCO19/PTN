using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace PTN;

public partial class MainWindow : Window
{
    private readonly HttpClient _httpClient;
    private readonly ObservableCollection<VideoFile> _files = new();

    private CancellationTokenSource? _cancellationTokenSource;

    private readonly HashSet<string> _detectedSeries =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _detectedDownloadGroups =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> VideoExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4",
            ".mkv",
            ".avi",
            ".mov",
            ".m4v",
            ".wmv",
            ".webm",
            ".ts",
            ".mts",
            ".m2ts",
            ".mpg",
            ".mpeg",
            ".3gp",
            ".flv"
        };

    public MainWindow()
    {
        InitializeComponent();

        FilesList.ItemsSource = _files;

        _httpClient = new HttpClient(
            new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.All
            })
        {
            Timeout = TimeSpan.FromMinutes(30)
        };

        DestinationBox.Text = GetDefaultDownloadFolder();
    }

    private static string GetDefaultDownloadFolder()
    {
        var userProfile = Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile);

        return Path.Combine(userProfile, "Downloads");
    }

    private void SetInterfaceBusy(bool busy)
    {
        PasteButton.IsEnabled = !busy;
        BrowseButton.IsEnabled = !busy;
        ClearButton.IsEnabled = !busy;
        AnalyzeButton.IsEnabled = !busy;
    }

    private static bool IsValidFreeboxShareUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri)
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp &&
            uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            return false;
        }

        var host = uri.Host.TrimEnd('.');

        var isFreeboxHost =
            host.Equals(
                "mafreebox.freebox.fr",
                StringComparison.OrdinalIgnoreCase)
            ||
            host.EndsWith(
                ".freeboxos.fr",
                StringComparison.OrdinalIgnoreCase);

        if (!isFreeboxHost)
        {
            return false;
        }

        var path =
            Uri.UnescapeDataString(
                uri.AbsolutePath);

        var segments =
            path.Split(
                '/',
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries);

        if (segments.Length == 0)
        {
            return false;
        }

        var shareIndex =
            Array.FindIndex(
                segments,
                segment =>
                    segment.Equals(
                        "share",
                        StringComparison.OrdinalIgnoreCase));

        if (shareIndex < 0)
        {
            return false;
        }

        return shareIndex < segments.Length - 1 &&
               !string.IsNullOrWhiteSpace(
                   segments[shareIndex + 1]);
    }

    private static bool TryGetValidFreeboxShareUri(
        string text,
        out Uri uri)
    {
        uri = null!;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        text = text.Trim();

        if (!Uri.TryCreate(
                text,
                UriKind.Absolute,
                out var candidate))
        {
            return false;
        }

        if (!IsValidFreeboxShareUri(candidate))
        {
            return false;
        }

        uri = candidate;
        return true;
    }

    private async void BrowseButton_Click(
        object? sender,
        RoutedEventArgs e)
    {
        try
        {
            var currentPath = DestinationBox.Text?.Trim();

            IStorageFolder? suggestedStartLocation = null;

            if (!string.IsNullOrWhiteSpace(currentPath) &&
                Directory.Exists(currentPath))
            {
                try
                {
                    suggestedStartLocation =
                        await StorageProvider.TryGetFolderFromPathAsync(
                            currentPath);
                }
                catch
                {
                }
            }

            var folders =
                await StorageProvider.OpenFolderPickerAsync(
                    new FolderPickerOpenOptions
                    {
                        Title = "Choisir le dossier de destination",
                        AllowMultiple = false,
                        SuggestedStartLocation = suggestedStartLocation
                    });

            if (folders.Count > 0)
            {
                DestinationBox.Text =
                    folders[0].TryGetLocalPath()
                    ?? folders[0].Name;
            }
        }
        catch (Exception ex)
        {
            await ShowMessageAsync(
                "Impossible de sélectionner le dossier.\n\n" +
                ex.Message,
                "Erreur");
        }
    }

    private async void PasteButton_Click(
        object? sender,
        RoutedEventArgs e)
    {
        try
        {
            var clipboard =
                TopLevel.GetTopLevel(this)?.Clipboard;

            if (clipboard == null)
            {
                await ShowMessageAsync(
                    "Le presse-papiers n'est pas disponible.",
                    "Presse-papiers");

                return;
            }

            string? text = null;

            using (var data =
                   await clipboard.TryGetDataAsync())
            {
                if (data != null)
                {
                    foreach (var item in data.Items)
                    {
                        if (!item.Formats.Contains(DataFormat.Text))
                        {
                            continue;
                        }

                        var value =
                            await item.TryGetRawAsync(
                                DataFormat.Text);

                        if (value is string stringValue)
                        {
                            text = stringValue;
                            break;
                        }
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                await ShowMessageAsync(
                    "Le presse-papiers ne contient pas de texte.",
                    "Presse-papiers");

                return;
            }

            text = text.Trim();

            if (!TryGetValidFreeboxShareUri(
                    text,
                    out var freeboxUri))
            {
                await ShowMessageAsync(
                    "L'URL collée n'est pas un lien de partage Freebox OS valide.",
                    "Lien non autorisé");

                return;
            }

            UrlBox.Text =
                freeboxUri.AbsoluteUri;

            UrlBox.CaretIndex =
                UrlBox.Text?.Length ?? 0;

            UrlBox.Focus();
        }
        catch (Exception ex)
        {
            await ShowMessageAsync(
                "Impossible de lire le presse-papiers.\n\n" +
                ex.Message,
                "Presse-papiers");
        }
    }

    private void ClearButton_Click(
        object? sender,
        RoutedEventArgs e)
    {
        _cancellationTokenSource?.Cancel();

        _files.Clear();

        _detectedSeries.Clear();
        _detectedDownloadGroups.Clear();

        UrlBox.Clear();

        Progress.Value = 0;
        ProgressText.Text = string.Empty;

        StatusText.Text =
            "Colle le lien Freebox dans le champ « Lien Freebox », puis clique sur « Analyser ».";

        AnalyzeButton.IsEnabled = true;
        DownloadButton.IsEnabled = false;
        PasteButton.IsEnabled = true;
        BrowseButton.IsEnabled = true;
        ClearButton.IsEnabled = true;

        UrlBox.Focus();
    }

    private async void AnalyzeButton_Click(
        object? sender,
        RoutedEventArgs e)
    {
        var urlText = UrlBox.Text?.Trim();

        if (!TryGetValidFreeboxShareUri(
                urlText ?? string.Empty,
                out var rootUri))
        {
            await ShowMessageAsync(
                "L'URL collée n'est pas un lien de partage Freebox OS valide.",
                "Lien non autorisé");

            return;
        }

        _cancellationTokenSource?.Cancel();

        _cancellationTokenSource =
            new CancellationTokenSource();

        try
        {
            SetInterfaceBusy(true);
            DownloadButton.IsEnabled = false;

            _files.Clear();
            _detectedSeries.Clear();
            _detectedDownloadGroups.Clear();

            Progress.Value = 0;
            ProgressText.Text = string.Empty;

            StatusText.Text =
                "Analyse du partage Freebox...";

            var visited =
                new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);

            await CrawlStructureAsync(
                rootUri,
                rootUri,
                visited,
                _cancellationTokenSource.Token);

            if (_files.Count == 0)
            {
                StatusText.Text =
                    "Aucun épisode trouvé.";

                return;
            }

            StatusText.Text =
                $"{_files.Count} épisode(s) trouvé(s). " +
                "Récupération des tailles...";

            await LoadSizesAsync(
                _cancellationTokenSource.Token);

            StatusText.Text =
                $"{_files.Count} épisode(s) trouvé(s).";

            DownloadButton.IsEnabled = true;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text =
                "Analyse interrompue.";
        }
        catch (MultipleDownloadGroupException ex)
        {
            _files.Clear();

            StatusText.Text =
                "Téléchargement bloqué.";

            await ShowMessageAsync(
                ex.Message,
                "Téléchargement bloqué");
        }
        catch (Exception ex)
        {
            StatusText.Text =
                "Erreur pendant l'analyse.";

            await ShowMessageAsync(
                ex.Message,
                "Erreur");
        }
        finally
        {
            SetInterfaceBusy(false);
        }
    }

    private async Task CrawlStructureAsync(
        Uri rootUri,
        Uri currentUri,
        HashSet<string> visited,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!visited.Add(currentUri.AbsoluteUri))
        {
            return;
        }

        string html;

        try
        {
            html = await _httpClient.GetStringAsync(
                currentUri,
                cancellationToken);
        }
        catch
        {
            return;
        }

        foreach (var link in ExtractLinks(html, currentUri))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsInsideShare(rootUri, link))
            {
                continue;
            }

            var path =
                Uri.UnescapeDataString(
                    link.AbsolutePath);

            if (LooksLikeVideo(path))
            {
                AddVideo(link, rootUri);
                continue;
            }

            if (LooksLikeDirectory(link))
            {
                await CrawlStructureAsync(
                    rootUri,
                    link,
                    visited,
                    cancellationToken);
            }
        }
    }

    private void AddVideo(
        Uri uri,
        Uri rootUri)
    {
        var relativePath =
            GetRelativePath(rootUri, uri);

        var structure =
            DetectVideoStructure(relativePath);

        if (structure == null)
        {
            return;
        }

        if (_detectedSeries.Add(structure.Series) &&
            _detectedSeries.Count > 1)
        {
            throw new MultipleDownloadGroupException(
                BuildMultipleSeriesMessage());
        }

        if (_detectedDownloadGroups.Add(
                structure.DownloadGroup) &&
            _detectedDownloadGroups.Count > 1)
        {
            throw new MultipleDownloadGroupException(
                BuildMultipleDownloadGroupsMessage(
                    structure.Series));
        }

        if (_files.Any(file =>
                file.Url.Equals(
                    uri.AbsoluteUri,
                    StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var normalizedPath =
            NormalizeVideoPath(
                relativePath,
                structure);

        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return;
        }

        _files.Add(
            new VideoFile
            {
                Url = uri.AbsoluteUri,
                RelativePath = normalizedPath,
                Status = "En attente"
            });
    }

    private static IEnumerable<Uri> ExtractLinks(
        string html,
        Uri baseUri)
    {
        var matches =
            Regex.Matches(
                html,
                @"href\s*=\s*[""']([^""']+)[""']",
                RegexOptions.IgnoreCase);

        foreach (Match match in matches)
        {
            var value =
                WebUtility.HtmlDecode(
                    match.Groups[1].Value);

            if (Uri.TryCreate(
                    baseUri,
                    value,
                    out var uri))
            {
                yield return uri;
            }
        }
    }

    private static bool IsInsideShare(
        Uri rootUri,
        Uri uri)
    {
        if (!string.Equals(
                rootUri.Host,
                uri.Host,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rootPath =
            rootUri.AbsolutePath.TrimEnd('/') + "/";

        return uri.AbsolutePath.StartsWith(
            rootPath,
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeDirectory(Uri uri)
    {
        return uri.AbsolutePath.EndsWith(
            "/",
            StringComparison.Ordinal);
    }

    private static bool LooksLikeVideo(string path)
    {
        return VideoExtensions.Contains(
            Path.GetExtension(path));
    }

    private static string GetRelativePath(
        Uri rootUri,
        Uri fileUri)
    {
        var rootPath =
            rootUri.AbsolutePath.TrimEnd('/') + "/";

        var filePath =
            Uri.UnescapeDataString(
                fileUri.AbsolutePath);

        if (filePath.StartsWith(
                rootPath,
                StringComparison.OrdinalIgnoreCase))
        {
            filePath =
                filePath[rootPath.Length..];
        }

        return filePath.Replace(
            '/',
            Path.DirectorySeparatorChar);
    }

    private static string[] SplitPath(
        string path)
    {
        return path
            .Replace('\\', '/')
            .Split(
                '/',
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries);
    }

    private static VideoStructure? DetectVideoStructure(
        string relativePath)
    {
        var parts = SplitPath(relativePath);

        if (parts.Length < 2)
        {
            return null;
        }

        if (IsKaamelottPath(parts))
        {
            var seriesIndex =
                FindKaamelottSeriesIndex(parts);

            if (seriesIndex < 0)
            {
                return null;
            }

            var series = parts[seriesIndex];

            var bookIndex =
                FindBookIndex(
                    parts,
                    seriesIndex + 1);

            if (bookIndex < 0 ||
                bookIndex >= parts.Length - 1)
            {
                return null;
            }

            var book = parts[bookIndex];

            return new VideoStructure
            {
                Series = series,
                DownloadGroup = $"{series}\\{book}",
                Season = book,
                Episode = parts[^1],
                IsKaamelott = true
            };
        }

        var seasonIndex =
            FindSeasonIndex(parts);

        if (seasonIndex > 0 &&
            seasonIndex < parts.Length - 1)
        {
            var series = parts[seasonIndex - 1];
            var season = parts[seasonIndex];

            return new VideoStructure
            {
                Series = series,
                DownloadGroup = series,
                Season = season,
                Episode = parts[^1]
            };
        }

        var specialsIndex =
            FindSpecialsIndex(parts);

        if (specialsIndex > 0 &&
            specialsIndex < parts.Length - 1)
        {
            var series = parts[specialsIndex - 1];
            var specials = parts[specialsIndex];

            return new VideoStructure
            {
                Series = series,
                DownloadGroup = $"{series}\\{specials}",
                Season = specials,
                Episode = parts[^1],
                IsSpecials = true
            };
        }

        var seasonFromFile =
            DetectSeasonFromFileName(parts[^1]);

        if (!string.IsNullOrWhiteSpace(seasonFromFile))
        {
            return new VideoStructure
            {
                Series = parts[0],
                DownloadGroup = parts[0],
                Season = seasonFromFile,
                Episode = parts[^1]
            };
        }

        return null;
    }

    private static bool IsKaamelottPath(
        string[] parts)
    {
        return parts.Any(
            part => part.Equals(
                "Kaamelott",
                StringComparison.OrdinalIgnoreCase));
    }

    private static int FindKaamelottSeriesIndex(
        string[] parts)
    {
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Equals(
                    "Kaamelott",
                    StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static int FindBookIndex(
        string[] parts,
        int startIndex)
    {
        for (var i = startIndex;
             i < parts.Length;
             i++)
        {
            if (Regex.IsMatch(
                    parts[i].Trim(),
                    @"^Livre\s+.+$",
                    RegexOptions.IgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static int FindSpecialsIndex(
        string[] parts)
    {
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Equals(
                    "Specials",
                    StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static int FindSeasonIndex(
        string[] parts)
    {
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i].Trim();

            if (Regex.IsMatch(
                    part,
                    @"^(saison|season)\s*\d+$",
                    RegexOptions.IgnoreCase))
            {
                return i;
            }

            if (Regex.IsMatch(
                    part,
                    @"^s\d+$",
                    RegexOptions.IgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static string DetectSeasonFromFileName(
        string fileName)
    {
        var match =
            Regex.Match(
                fileName,
                @"\bS(?<season>\d{1,3})E\d{1,3}\b",
                RegexOptions.IgnoreCase);

        if (!match.Success)
        {
            return string.Empty;
        }

        var season =
            int.Parse(
                match.Groups["season"].Value);

        return $"S{season:00}";
    }

    private static string NormalizeVideoPath(
        string relativePath,
        VideoStructure structure)
    {
        return Path.Combine(
            SanitizePathPart(structure.Series),
            SanitizePathPart(structure.Season),
            SanitizeFileName(structure.Episode));
    }

    private static string SanitizePathPart(
        string value)
    {
        foreach (var invalidCharacter
                 in Path.GetInvalidFileNameChars())
        {
            value =
                value.Replace(
                    invalidCharacter.ToString(),
                    string.Empty);
        }

        value = value.Trim();

        return string.IsNullOrWhiteSpace(value)
            ? "Inconnu"
            : value;
    }

    private static string SanitizeFileName(
        string value)
    {
        return SanitizePathPart(value);
    }

    private async Task LoadSizesAsync(
        CancellationToken cancellationToken)
    {
        var total = _files.Count;
        var processed = 0;

        foreach (var file in _files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            file.Size =
                await GetSizeAsync(
                    new Uri(file.Url),
                    cancellationToken);

            processed++;

            Progress.Value =
                total == 0
                    ? 0
                    : processed * 100.0 / total;

            ProgressText.Text =
                $"Analyse des tailles : {processed} / {total}";
        }

        Progress.Value = 0;
        ProgressText.Text = string.Empty;
    }

    private async Task<long> GetSizeAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request =
                new HttpRequestMessage(
                    HttpMethod.Head,
                    uri);

            using var response =
                await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

            if (response.Content.Headers.ContentLength
                    is long length &&
                length > 0)
            {
                return length;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
        }

        try
        {
            using var request =
                new HttpRequestMessage(
                    HttpMethod.Get,
                    uri);

            request.Headers.Range =
                new RangeHeaderValue(0, 0);

            using var response =
                await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

            if (response.Content.Headers.ContentRange
                    ?.Length is long rangeLength &&
                rangeLength > 0)
            {
                return rangeLength;
            }

            if (response.Content.Headers.ContentLength
                    is long contentLength &&
                contentLength > 0)
            {
                return contentLength;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
        }

        return 0;
    }

    private async void DownloadButton_Click(
        object? sender,
        RoutedEventArgs e)
    {
        if (_files.Count == 0)
        {
            return;
        }

        var destination =
            DestinationBox.Text?.Trim();

        if (string.IsNullOrWhiteSpace(destination))
        {
            await ShowMessageAsync(
                "Choisis un dossier de destination.",
                "Erreur");

            return;
        }

        try
        {
            destination =
                Path.GetFullPath(destination);

            Directory.CreateDirectory(destination);
        }
        catch (Exception ex)
        {
            await ShowMessageAsync(
                "Impossible d'utiliser le dossier de destination.\n\n" +
                ex.Message,
                "Erreur");

            return;
        }

        _cancellationTokenSource?.Cancel();

        _cancellationTokenSource =
            new CancellationTokenSource();

        SetInterfaceBusy(true);
        DownloadButton.IsEnabled = false;

        Progress.Value = 0;
        ProgressText.Text = string.Empty;

        try
        {
            long totalBytes =
                _files.Sum(
                    file => file.Size);

            long completedBytes = 0;

            using var semaphore =
                new SemaphoreSlim(3);

            var tasks =
                _files.Select(
                    file =>
                        DownloadOneAsync(
                            file,
                            destination,
                            semaphore,
                            totalBytes,
                            () =>
                            {
                                var completed =
                                    Interlocked.Read(
                                        ref completedBytes);

                                Dispatcher.UIThread.Post(
                                    () =>
                                    {
                                        Progress.Value =
                                            totalBytes > 0
                                                ? completed * 100.0 /
                                                  totalBytes
                                                : 0;

                                        ProgressText.Text =
                                            $"{FormatBytes(completed)} / " +
                                            $"{FormatBytes(totalBytes)}";
                                    });
                            },
                            bytes =>
                            {
                                Interlocked.Add(
                                    ref completedBytes,
                                    bytes);
                            },
                            _cancellationTokenSource.Token))
                .ToArray();

            await Task.WhenAll(tasks);

            Progress.Value = 100;

            StatusText.Text =
                "Téléchargement terminé.";

            ProgressText.Text =
                $"{FormatBytes(completedBytes)} téléchargés";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text =
                "Téléchargement interrompu.";

            ProgressText.Text =
                "Téléchargement annulé.";
        }
        catch (Exception ex)
        {
            StatusText.Text =
                "Erreur pendant le téléchargement.";

            await ShowMessageAsync(
                ex.Message,
                "Erreur");
        }
        finally
        {
            SetInterfaceBusy(false);

            AnalyzeButton.IsEnabled = true;

            DownloadButton.IsEnabled =
                _files.Any(
                    file =>
                        file.Status != "Terminé" &&
                        file.Status != "Déjà présent");
        }
    }

    private async Task DownloadOneAsync(
        VideoFile file,
        string destinationRoot,
        SemaphoreSlim semaphore,
        long totalBytes,
        Action updateProgress,
        Action<long> addBytes,
        CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(
            cancellationToken);

        try
        {
            await DownloadFileAsync(
                file,
                destinationRoot,
                updateProgress,
                addBytes,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await Dispatcher.UIThread.InvokeAsync(
                () => file.Status = "Annulé");

            throw;
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(
                () => file.Status = "Erreur");

            System.Diagnostics.Debug.WriteLine(ex);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task DownloadFileAsync(
        VideoFile file,
        string destinationRoot,
        Action updateProgress,
        Action<long> addBytes,
        CancellationToken cancellationToken)
    {
        var relativePath =
            file.RelativePath.TrimStart(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);

        var destination =
            Path.GetFullPath(
                Path.Combine(
                    destinationRoot,
                    relativePath));

        var normalizedRoot =
            Path.GetFullPath(
                destinationRoot)
            .TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        if (!destination.StartsWith(
                normalizedRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Le chemin du fichier est invalide.");
        }

        var directory =
            Path.GetDirectoryName(destination);

        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        long existingBytes = 0;

        if (File.Exists(destination))
        {
            existingBytes =
                new FileInfo(destination).Length;

            if (file.Size > 0 &&
                existingBytes >= file.Size)
            {
                await Dispatcher.UIThread.InvokeAsync(
                    () => file.Status = "Déjà présent");

                return;
            }
        }

        await Dispatcher.UIThread.InvokeAsync(
            () =>
            {
                file.Status =
                    existingBytes > 0
                        ? "Reprise"
                        : "Téléchargement";
            });

        using var request =
            new HttpRequestMessage(
                HttpMethod.Get,
                file.Url);

        if (existingBytes > 0)
        {
            request.Headers.Range =
                new RangeHeaderValue(
                    existingBytes,
                    null);
        }

        using var response =
            await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

        response.EnsureSuccessStatusCode();

        var append =
            existingBytes > 0 &&
            response.StatusCode ==
            HttpStatusCode.PartialContent;

        if (!append)
        {
            existingBytes = 0;
        }

        var fileMode =
            append
                ? FileMode.Append
                : FileMode.Create;

        await using var input =
            await response.Content.ReadAsStreamAsync(
                cancellationToken);

        await using var output =
            new FileStream(
                destination,
                fileMode,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                useAsync: true);

        var buffer =
            new byte[1024 * 1024];

        int bytesRead;

        while ((bytesRead =
            await input.ReadAsync(
                buffer.AsMemory(),
                cancellationToken)) > 0)
        {
            await output.WriteAsync(
                buffer.AsMemory(0, bytesRead),
                cancellationToken);

            addBytes(bytesRead);
            updateProgress();
        }

        await Dispatcher.UIThread.InvokeAsync(
            () => file.Status = "Terminé");
    }

    private string BuildMultipleSeriesMessage()
    {
        return
            "Plusieurs séries ont été détectées dans ce partage.\n\n" +
            string.Join("\n", _detectedSeries) +
            "\n\n" +
            "Téléchargement bloqué.\n\n" +
            "Veuillez télécharger les séries une par une.";
    }

    private string BuildMultipleDownloadGroupsMessage(
        string series)
    {
        var groups =
            _detectedDownloadGroups.ToList();

        if (series.Equals(
                "Kaamelott",
                StringComparison.OrdinalIgnoreCase))
        {
            return
                "Plusieurs Livres de Kaamelott ont été détectés " +
                "dans ce partage.\n\n" +
                string.Join("\n", groups) +
                "\n\n" +
                "Téléchargement bloqué.\n\n" +
                "Pour éviter un téléchargement trop important, " +
                "Kaamelott doit être téléchargé Livre par Livre.";
        }

        return
            "Plusieurs groupes de téléchargement ont été détectés " +
            $"pour la série « {series} ».\n\n" +
            string.Join("\n", groups) +
            "\n\n" +
            "Téléchargement bloqué.";
    }

    private async Task ShowMessageAsync(
        string message,
        string title)
    {
        var dialog =
            new Window
            {
                Title = title,
                Width = 500,
                SizeToContent = SizeToContent.Height,
                CanResize = false,
                WindowStartupLocation =
                    WindowStartupLocation.CenterOwner,
                Background =
                    new Avalonia.Media.SolidColorBrush(
                        Avalonia.Media.Color.Parse("#FFFFFF"))
            };

        var text =
            new TextBlock
            {
                Text = message,
                TextWrapping =
                    Avalonia.Media.TextWrapping.Wrap,
                Foreground =
                    new Avalonia.Media.SolidColorBrush(
                        Avalonia.Media.Color.Parse("#202124"))
            };

        var buttonBackground =
            new Avalonia.Media.SolidColorBrush(
                Avalonia.Media.Color.Parse("#E8EDF3"));

        var buttonHoverBackground =
            new Avalonia.Media.SolidColorBrush(
                Avalonia.Media.Color.Parse("#DCE2E9"));

        var buttonPressedBackground =
            new Avalonia.Media.SolidColorBrush(
                Avalonia.Media.Color.Parse("#CDD5DE"));

        var buttonBorder =
            new Avalonia.Media.SolidColorBrush(
                Avalonia.Media.Color.Parse("#C9CED6"));

        var buttonText =
            new TextBlock
            {
                Text = "OK",
                FontSize = 13,
                Foreground =
                    new Avalonia.Media.SolidColorBrush(
                        Avalonia.Media.Color.Parse("#202124")),
                HorizontalAlignment =
                    Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment =
                    Avalonia.Layout.VerticalAlignment.Center
            };

        var okButton =
            new Border
            {
                Width = 90,
                Height = 38,
                Background = buttonBackground,
                BorderBrush = buttonBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                HorizontalAlignment =
                    Avalonia.Layout.HorizontalAlignment.Right,
                Child = buttonText,
                Cursor = new Cursor(StandardCursorType.Hand)
            };

        okButton.PointerEntered +=
            (_, _) =>
            {
                okButton.Background =
                    buttonHoverBackground;
            };

        okButton.PointerExited +=
            (_, _) =>
            {
                okButton.Background =
                    buttonBackground;
            };

        okButton.PointerPressed +=
            (_, _) =>
            {
                okButton.Background =
                    buttonPressedBackground;
            };

        okButton.PointerReleased +=
            (_, _) =>
            {
                okButton.Background =
                    buttonHoverBackground;

                dialog.Close();
            };

        var panel =
            new StackPanel
            {
                Spacing = 20,
                Margin = new Thickness(20)
            };

        panel.Children.Add(text);
        panel.Children.Add(okButton);

        dialog.Content = panel;

        await dialog.ShowDialog(this);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} o";
        }

        if (bytes < 1024L * 1024)
        {
            return $"{bytes / 1024.0:0.##} Ko";
        }

        if (bytes < 1024L * 1024 * 1024)
        {
            return $"{bytes / 1024.0 / 1024.0:0.##} Mo";
        }

        if (bytes < 1024L * 1024 * 1024 * 1024)
        {
            return $"{bytes / 1024.0 / 1024.0 / 1024.0:0.##} Go";
        }

        return $"{bytes / 1024.0 / 1024.0 / 1024.0 / 1024.0:0.##} To";
    }
}

internal sealed class VideoStructure
{
    public string Series { get; init; } = string.Empty;

    public string DownloadGroup { get; init; } =
        string.Empty;

    public string Season { get; init; } =
        string.Empty;

    public string Episode { get; init; } =
        string.Empty;

    public bool IsKaamelott { get; init; }

    public bool IsSpecials { get; init; }
}

internal sealed class MultipleDownloadGroupException :
    Exception
{
    public MultipleDownloadGroupException(
        string message)
        : base(message)
    {
    }
}

public sealed class VideoFile : INotifyPropertyChanged
{
    private string _relativePath = string.Empty;
    private long _size;
    private string _status = "En attente";

    public string Url { get; set; } =
        string.Empty;

    public string RelativePath
    {
        get => _relativePath;

        set
        {
            if (_relativePath == value)
            {
                return;
            }

            _relativePath = value;
            OnPropertyChanged();
        }
    }

    public long Size
    {
        get => _size;

        set
        {
            if (_size == value)
            {
                return;
            }

            _size = value;

            OnPropertyChanged();
            OnPropertyChanged(nameof(SizeText));
        }
    }

    public string SizeText =>
        Size > 0
            ? FormatBytes(Size)
            : "Inconnue";

    public string Status
    {
        get => _status;

        set
        {
            if (_status == value)
            {
                return;
            }

            _status = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler?
        PropertyChanged;

    private void OnPropertyChanged(
        [CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(propertyName));
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} o";
        }

        if (bytes < 1024L * 1024)
        {
            return $"{bytes / 1024.0:0.##} Ko";
        }

        if (bytes < 1024L * 1024 * 1024)
        {
            return $"{bytes / 1024.0 / 1024.0:0.##} Mo";
        }

        if (bytes < 1024L * 1024 * 1024 * 1024)
        {
            return $"{bytes / 1024.0 / 1024.0 / 1024.0:0.##} Go";
        }

        return $"{bytes / 1024.0 / 1024.0 / 1024.0 / 1024.0:0.##} To";
    }
}