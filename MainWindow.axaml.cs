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

    /*
     * Une série standard doit posséder au moins un dossier :
     *
     *   Saison 1
     *   Saison 2
     *   Season 1
     *   S1
     *
     * Kaamelott utilise :
     *
     *   Livre I
     *   Livre II
     *   Livre III
     *
     * Specials est traité comme un bonus et ne peut jamais,
     * à lui seul, constituer une série.
     */

    private static readonly Regex SeasonDirectoryRegex =
        new(
            @"^(?:saison|season)\s*(?<number>\d+)$|^s(?<short>\d+)$",
            RegexOptions.IgnoreCase |
            RegexOptions.Compiled);

    private static readonly Regex BookDirectoryRegex =
        new(
            @"^livre\s+(?<number>[IVXLCDM]+)$",
            RegexOptions.IgnoreCase |
            RegexOptions.Compiled);

    private static readonly Regex SpecialsDirectoryRegex =
        new(
            @"^specials?$",
            RegexOptions.IgnoreCase |
            RegexOptions.Compiled);

    /*
     * Formats acceptés :
     *
     * S1E1
     * S01E1
     * S1E01
     * S01E01
     *
     * L1T1
     * L01T1
     * L1T01
     * L01T01
     */
    private static readonly Regex StandardEpisodeRegex =
        new(
            @"(?<![A-Z0-9])S(?<season>\d{1,3})E(?<episode>\d{1,3})(?![A-Z0-9])",
            RegexOptions.IgnoreCase |
            RegexOptions.Compiled);

    private static readonly Regex BookEpisodeRegex =
        new(
            @"(?<![A-Z0-9])L(?<book>\d{1,3})T(?<episode>\d{1,3})(?![A-Z0-9])",
            RegexOptions.IgnoreCase |
            RegexOptions.Compiled);

    private static readonly Regex KaamelottEpisodeRegex =
        new(
            @"(?<![A-Z0-9])(?:L(?<book>\d{1,3})T(?<episode>\d{1,3})|S(?<season>\d{1,3})E(?<episode2>\d{1,3}))(?![A-Z0-9])",
            RegexOptions.IgnoreCase |
            RegexOptions.Compiled);

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
        var userProfile =
            Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile);

        return Path.Combine(
            userProfile,
            "Downloads");
    }

    private void SetInterfaceBusy(bool busy)
    {
        PasteButton.IsEnabled = !busy;
        BrowseButton.IsEnabled = !busy;
        ClearButton.IsEnabled = !busy;
        AnalyzeButton.IsEnabled = !busy;
    }

    // ============================================================
    // VALIDATION DU LIEN FREEBOX
    // ============================================================

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

        var host =
            uri.Host.TrimEnd('.');

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

    // ============================================================
    // PARCOURS DU DOSSIER
    // ============================================================

    private enum RootKind
    {
        Series,
        Season,
        Book,
        Specials
    }

    private sealed class RootContext
    {
        public RootKind Kind { get; init; }

        public string? SeriesName { get; init; }

        public string? SelectedGroup { get; init; }

        public bool IsKaamelott { get; init; }
    }

    private sealed class DetectedVideo
    {
        public Uri Uri { get; init; } = null!;

        public string Series { get; init; } =
            string.Empty;

        public string Group { get; init; } =
            string.Empty;

        public string SeasonOrBook { get; init; } =
            string.Empty;

        public string Episode { get; init; } =
            string.Empty;

        public bool IsSpecials { get; init; }

        public bool IsKaamelott { get; init; }
    }

    private static RootContext DetectRootContext(
        Uri rootUri)
    {
        var parts =
            GetUriPathParts(rootUri);

        if (parts.Length == 0)
        {
            return new RootContext
            {
                Kind = RootKind.Series
            };
        }

        var last =
            parts[^1];

        if (TryGetSeasonDirectory(
                last,
                out _))
        {
            var series =
                parts.Length >= 2
                    ? parts[^2]
                    : null;

            return new RootContext
            {
                Kind = RootKind.Season,
                SeriesName = series,
                SelectedGroup = last,
                IsKaamelott = false
            };
        }

        if (TryGetBookDirectory(
                last,
                out _))
        {
            var series =
                parts.Length >= 2
                    ? parts[^2]
                    : null;

            return new RootContext
            {
                Kind = RootKind.Book,
                SeriesName = series,
                SelectedGroup = last,
                IsKaamelott =
                    series?.Equals(
                        "Kaamelott",
                        StringComparison.OrdinalIgnoreCase)
                    == true
            };
        }

        if (IsSpecialsDirectory(last))
        {
            var series =
                parts.Length >= 2
                    ? parts[^2]
                    : null;

            return new RootContext
            {
                Kind = RootKind.Specials,
                SeriesName = series
            };
        }

        return new RootContext
        {
            Kind = RootKind.Series
        };
    }

    private async Task CrawlStructureAsync(
        Uri rootUri,
        Uri currentUri,
        RootContext context,
        HashSet<string> visited,
        List<DetectedVideo> detectedVideos,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedCurrent =
            NormalizeUri(currentUri);

        if (!visited.Add(normalizedCurrent))
        {
            return;
        }

        string html;

        try
        {
            html =
                await _httpClient.GetStringAsync(
                    currentUri,
                    cancellationToken);
        }
        catch
        {
            return;
        }

        foreach (var link in ExtractLinks(
                     html,
                     currentUri))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsInsideRoot(
                    rootUri,
                    link))
            {
                continue;
            }

            var path =
                Uri.UnescapeDataString(
                    link.AbsolutePath);

            if (LooksLikeVideo(path))
            {
                var detected =
                    TryDetectVideo(
                        rootUri,
                        context,
                        link);

                if (detected != null)
                {
                    /*
                     * IMPORTANT :
                     *
                     * Pour un lien de série complète, on contrôle
                     * immédiatement le nom de la série dès qu'une
                     * vidéo valide est rencontrée.
                     *
                     * Cela évite de parcourir tout le partage
                     * lorsqu'une deuxième série est détectée.
                     *
                     * Pour un lien Saison/Livre/Specials, la racine
                     * est déjà ciblée et ce contrôle n'est donc pas
                     * nécessaire ici.
                     */
                    if (context.Kind == RootKind.Series)
                    {
                        if (_detectedSeries.Add(
                                detected.Series))
                        {
                            if (_detectedSeries.Count > 1)
                            {
                                throw new MultipleDownloadGroupException(
                                    BuildMultipleSeriesMessage());
                            }
                        }
                    }

                    detectedVideos.Add(
                        detected);
                }

                continue;
            }

            /*
             * On continue à parcourir tous les dossiers.
             *
             * Cela permet notamment :
             *
             * Série/
             *   Saison 1/
             *      sous-dossier/
             *          episode.mkv
             *
             * ou :
             *
             * Série/
             *   Specials/
             *      Bonus/
             *          episode.mkv
             */
            if (LooksLikeDirectory(link))
            {
                await CrawlStructureAsync(
                    rootUri,
                    link,
                    context,
                    visited,
                    detectedVideos,
                    cancellationToken);
            }
        }
    }

    // ============================================================
    // DÉTECTION D'UNE VIDÉO
    // ============================================================

    private static DetectedVideo? TryDetectVideo(
        Uri rootUri,
        RootContext context,
        Uri videoUri)
    {
        var parts =
            GetUriPathParts(videoUri);

        if (parts.Length == 0)
        {
            return null;
        }

        var fileName =
            parts[^1];

        var standardMatch =
            StandardEpisodeRegex.Match(
                Path.GetFileNameWithoutExtension(
                    fileName));

        var bookMatch =
            BookEpisodeRegex.Match(
                Path.GetFileNameWithoutExtension(
                    fileName));

        var seasonIndex =
            FindSeasonDirectoryIndex(parts);

        var bookIndex =
            FindBookDirectoryIndex(parts);

        var specialsIndex =
            FindSpecialsDirectoryIndex(parts);

        // --------------------------------------------------------
        // LIEN SUR UNE SAISON
        // --------------------------------------------------------

        if (context.Kind == RootKind.Season)
        {
            if (seasonIndex < 0)
            {
                return null;
            }

            var selected =
                context.SelectedGroup;

            if (!string.Equals(
                    parts[seasonIndex],
                    selected,
                    StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (!standardMatch.Success)
            {
                return null;
            }

            var series =
                context.SeriesName;

            if (string.IsNullOrWhiteSpace(series))
            {
                return null;
            }

            var season =
                parts[seasonIndex];

            return new DetectedVideo
            {
                Uri = videoUri,
                Series = series,
                Group = season,
                SeasonOrBook = season,
                Episode = fileName,
                IsSpecials = false,
                IsKaamelott = false
            };
        }

        // --------------------------------------------------------
        // LIEN SUR UN LIVRE KAAMELOTT
        // --------------------------------------------------------

        if (context.Kind == RootKind.Book)
        {
            if (bookIndex < 0)
            {
                return null;
            }

            var selected =
                context.SelectedGroup;

            if (!string.Equals(
                    parts[bookIndex],
                    selected,
                    StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (!bookMatch.Success &&
                !standardMatch.Success)
            {
                return null;
            }

            var series =
                context.SeriesName;

            if (string.IsNullOrWhiteSpace(series))
            {
                return null;
            }

            var book =
                parts[bookIndex];

            return new DetectedVideo
            {
                Uri = videoUri,
                Series = series,
                Group = book,
                SeasonOrBook = book,
                Episode = fileName,
                IsSpecials = false,
                IsKaamelott =
                    context.IsKaamelott
            };
        }

        // --------------------------------------------------------
        // LIEN SUR SPECIALS
        // --------------------------------------------------------

        if (context.Kind == RootKind.Specials)
        {
            if (specialsIndex < 0)
            {
                return null;
            }

            if (!standardMatch.Success &&
                !bookMatch.Success)
            {
                return null;
            }

            var series =
                context.SeriesName;

            if (string.IsNullOrWhiteSpace(series))
            {
                return null;
            }

            return new DetectedVideo
            {
                Uri = videoUri,
                Series = series,
                Group = "Specials",
                SeasonOrBook = "Specials",
                Episode = fileName,
                IsSpecials = true,
                IsKaamelott =
                    series.Equals(
                        "Kaamelott",
                        StringComparison.OrdinalIgnoreCase)
            };
        }

        // --------------------------------------------------------
        // LIEN SUR LA SÉRIE COMPLÈTE
        // --------------------------------------------------------

        if (seasonIndex >= 0)
        {
            if (!standardMatch.Success)
            {
                return null;
            }

            if (seasonIndex == 0)
            {
                return null;
            }

            var series =
                parts[seasonIndex - 1];

            return new DetectedVideo
            {
                Uri = videoUri,
                Series = series,
                Group = parts[seasonIndex],
                SeasonOrBook = parts[seasonIndex],
                Episode = fileName,
                IsSpecials = false,
                IsKaamelott = false
            };
        }

        // --------------------------------------------------------
        // KAAMELOTT / LIVRE
        // --------------------------------------------------------

        if (bookIndex >= 0)
        {
            if (!bookMatch.Success &&
                !standardMatch.Success)
            {
                return null;
            }

            if (bookIndex == 0)
            {
                return null;
            }

            var series =
                parts[bookIndex - 1];

            return new DetectedVideo
            {
                Uri = videoUri,
                Series = series,
                Group = parts[bookIndex],
                SeasonOrBook = parts[bookIndex],
                Episode = fileName,
                IsSpecials = false,
                IsKaamelott =
                    series.Equals(
                        "Kaamelott",
                        StringComparison.OrdinalIgnoreCase)
            };
        }

        // --------------------------------------------------------
        // SPECIALS DANS UNE SÉRIE
        // --------------------------------------------------------

        if (specialsIndex >= 0)
        {
            if (!standardMatch.Success &&
                !bookMatch.Success)
            {
                return null;
            }

            if (specialsIndex == 0)
            {
                return null;
            }

            var series =
                parts[specialsIndex - 1];

            return new DetectedVideo
            {
                Uri = videoUri,
                Series = series,
                Group = "Specials",
                SeasonOrBook = "Specials",
                Episode = fileName,
                IsSpecials = true,
                IsKaamelott =
                    series.Equals(
                        "Kaamelott",
                        StringComparison.OrdinalIgnoreCase)
            };
        }

        return null;
    }

    // ============================================================
    // VALIDATION DU CONTEXTE SPECIALS
    // ============================================================

    private async Task<bool> ValidateSpecialsRootAsync(
        Uri specialsUri,
        RootContext context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(
                context.SeriesName))
        {
            return false;
        }

        var parentUri =
            GetParentUri(specialsUri);

        if (parentUri == null)
        {
            return false;
        }

        string html;

        try
        {
            html =
                await _httpClient.GetStringAsync(
                    parentUri,
                    cancellationToken);
        }
        catch
        {
            return false;
        }

        foreach (var link in ExtractLinks(
                     html,
                     parentUri))
        {
            var name =
                GetLastPathPart(link);

            if (TryGetSeasonDirectory(
                    name,
                    out _))
            {
                return true;
            }

            if (TryGetBookDirectory(
                    name,
                    out _))
            {
                return true;
            }
        }

        return false;
    }

    // ============================================================
    // VALIDATION DE LA SÉRIE
    // ============================================================

    private static bool IsValidDetectedSet(
        RootContext context,
        IReadOnlyCollection<DetectedVideo> videos)
    {
        if (videos.Count == 0)
        {
            return false;
        }

        if (context.Kind == RootKind.Season ||
            context.Kind == RootKind.Book)
        {
            return true;
        }

        if (context.Kind == RootKind.Specials)
        {
            return videos.Any(
                video => video.IsSpecials);
        }

        return videos.Any(
            video => !video.IsSpecials);
    }

    // ============================================================
    // AJOUT DES VIDÉOS À LA LISTE
    // ============================================================

    private void AddDetectedVideo(
        DetectedVideo detected)
    {
        if (!_detectedSeries.Add(
                detected.Series))
        {
            // Série déjà connue : OK.
        }
        else if (_detectedSeries.Count > 1)
        {
            throw new MultipleDownloadGroupException(
                BuildMultipleSeriesMessage());
        }

        if (_detectedDownloadGroups.Add(
                $"{detected.Series}\\{detected.Group}"))
        {
            /*
             * Une série complète contient naturellement
             * plusieurs saisons/livres.
             *
             * Ils sont donc autorisés lorsque le lien racine
             * est une série.
             */
        }

        if (_files.Any(
                file =>
                    file.Url.Equals(
                        detected.Uri.AbsoluteUri,
                        StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var normalizedPath =
            NormalizeDetectedPath(
                detected);

        if (string.IsNullOrWhiteSpace(
                normalizedPath))
        {
            return;
        }

        _files.Add(
            new VideoFile
            {
                Url = detected.Uri.AbsoluteUri,
                RelativePath = normalizedPath,
                Status = "En attente"
            });
    }

    private static string NormalizeDetectedPath(
        DetectedVideo detected)
    {
        return Path.Combine(
            SanitizePathPart(
                detected.Series),
            SanitizePathPart(
                detected.SeasonOrBook),
            SanitizeFileName(
                detected.Episode));
    }

    // ============================================================
    // ANALYSE
    // ============================================================

    private async void AnalyzeButton_Click(
        object? sender,
        RoutedEventArgs e)
    {
        var urlText =
            UrlBox.Text?.Trim();

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

            var context =
                DetectRootContext(rootUri);

            if (context.Kind == RootKind.Specials)
            {
                var valid =
                    await ValidateSpecialsRootAsync(
                        rootUri,
                        context,
                        _cancellationTokenSource.Token);

                if (!valid)
                {
                    StatusText.Text =
                        "Aucun épisode trouvé.";

                    await ShowMessageAsync(
                        "Le dossier « Specials » ne peut pas être " +
                        "considéré comme une série à lui seul.\n\n" +
                        "Aucune Saison ou aucun Livre correspondant " +
                        "n'a été trouvé dans le dossier parent.",
                        "Série non reconnue");

                    return;
                }
            }

            var visited =
                new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);

            var detectedVideos =
                new List<DetectedVideo>();

            await CrawlStructureAsync(
                rootUri,
                rootUri,
                context,
                visited,
                detectedVideos,
                _cancellationTokenSource.Token);

            if (context.Kind == RootKind.Series)
            {
                var hasRegularEpisodes =
                    detectedVideos.Any(
                        video => !video.IsSpecials);

                if (!hasRegularEpisodes)
                {
                    detectedVideos.Clear();
                }
            }

            if (!IsValidDetectedSet(
                    context,
                    detectedVideos))
            {
                StatusText.Text =
                    "Aucun épisode trouvé.";

                await ShowMessageAsync(
                    "Aucun fichier vidéo correspondant à une série " +
                    "ou à une saison valide n'a été trouvé.",
                    "Série non reconnue");

                return;
            }

            foreach (var detected in detectedVideos)
            {
                _cancellationTokenSource.Token
                    .ThrowIfCancellationRequested();

                AddDetectedVideo(detected);
            }

            if (_detectedSeries.Count > 1)
            {
                throw new MultipleDownloadGroupException(
                    BuildMultipleSeriesMessage());
            }

            if (_files.Count == 0)
            {
                StatusText.Text =
                    "Aucun épisode trouvé.";

                return;
            }

            if (context.Kind != RootKind.Series)
            {
                var groups =
                    detectedVideos
                        .Select(
                            video =>
                                video.Group)
                        .Distinct(
                            StringComparer.OrdinalIgnoreCase)
                        .ToList();

                if (groups.Count > 1)
                {
                    await ShowMessageAsync(
                        "Plusieurs groupes de téléchargement ont été " +
                        "détectés alors qu'un lien ciblé avait été fourni.\n\n" +
                        string.Join("\n", groups),
                        "Téléchargement bloqué");

                    _files.Clear();

                    return;
                }
            }

            StatusText.Text =
                $"{_files.Count} épisode(s) trouvé(s). " +
                "Récupération des tailles...";

            await LoadSizesAsync(
                _cancellationTokenSource.Token);

            StatusText.Text =
                $"{_files.Count} épisode(s) trouvé(s).";

            DownloadButton.IsEnabled =
                _files.Count > 0;
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

    // ============================================================
    // PARCOURS DES URL
    // ============================================================

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

            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (value.StartsWith(
                    "#",
                    StringComparison.Ordinal))
            {
                continue;
            }

            if (Uri.TryCreate(
                    baseUri,
                    value,
                    out var uri))
            {
                yield return uri;
            }
        }
    }

    private static bool IsInsideRoot(
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
            Uri.UnescapeDataString(
                rootUri.AbsolutePath)
            .TrimEnd('/');

        var candidatePath =
            Uri.UnescapeDataString(
                uri.AbsolutePath);

        if (string.Equals(
                rootPath,
                candidatePath,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return candidatePath.StartsWith(
            rootPath + "/",
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeDirectory(
        Uri uri)
    {
        if (uri.AbsolutePath.EndsWith(
                "/",
                StringComparison.Ordinal))
        {
            return true;
        }

        return !LooksLikeVideo(
            Uri.UnescapeDataString(
                uri.AbsolutePath));
    }

    private static bool LooksLikeVideo(
        string path)
    {
        return VideoExtensions.Contains(
            Path.GetExtension(path));
    }

    // ============================================================
    // ANALYSE DES CHEMINS
    // ============================================================

    private static string[] GetUriPathParts(
        Uri uri)
    {
        var path =
            Uri.UnescapeDataString(
                uri.AbsolutePath);

        return path
            .Split(
                '/',
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries);
    }

    private static string GetLastPathPart(
        Uri uri)
    {
        var parts =
            GetUriPathParts(uri);

        return parts.Length == 0
            ? string.Empty
            : parts[^1];
    }

    private static int FindSeasonDirectoryIndex(
        string[] parts)
    {
        for (var i = 0;
             i < parts.Length;
             i++)
        {
            if (TryGetSeasonDirectory(
                    parts[i],
                    out _))
            {
                return i;
            }
        }

        return -1;
    }

    private static int FindBookDirectoryIndex(
        string[] parts)
    {
        for (var i = 0;
             i < parts.Length;
             i++)
        {
            if (TryGetBookDirectory(
                    parts[i],
                    out _))
            {
                return i;
            }
        }

        return -1;
    }

    private static int FindSpecialsDirectoryIndex(
        string[] parts)
    {
        for (var i = 0;
             i < parts.Length;
             i++)
        {
            if (IsSpecialsDirectory(
                    parts[i]))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool TryGetSeasonDirectory(
        string value,
        out int number)
    {
        number = 0;

        var match =
            SeasonDirectoryRegex.Match(
                value.Trim());

        if (!match.Success)
        {
            return false;
        }

        var numberText =
            match.Groups["number"].Success
                ? match.Groups["number"].Value
                : match.Groups["short"].Value;

        return int.TryParse(
            numberText,
            out number);
    }

    private static bool TryGetBookDirectory(
        string value,
        out string roman)
    {
        roman = string.Empty;

        var match =
            BookDirectoryRegex.Match(
                value.Trim());

        if (!match.Success)
        {
            return false;
        }

        roman =
            match.Groups["number"]
                .Value
                .ToUpperInvariant();

        return IsValidRomanNumeral(
            roman);
    }

    private static bool IsSpecialsDirectory(
        string value)
    {
        return SpecialsDirectoryRegex.IsMatch(
            value.Trim());
    }

    private static bool IsValidRomanNumeral(
        string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return Regex.IsMatch(
            value,
            @"^(?=[MDCLXVI])M{0,4}(CM|CD|D?C{0,3})(XC|XL|L?X{0,3})(IX|IV|V?I{0,3})$",
            RegexOptions.IgnoreCase);
    }

    // ============================================================
    // URL PARENT
    // ============================================================

    private static Uri? GetParentUri(
        Uri uri)
    {
        var builder =
            new UriBuilder(uri);

        var path =
            builder.Path.TrimEnd('/');

        var slash =
            path.LastIndexOf('/');

        if (slash <= 0)
        {
            return null;
        }

        builder.Path =
            path[..(slash + 1)];

        return builder.Uri;
    }

    private static string NormalizeUri(
        Uri uri)
    {
        return uri.AbsoluteUri.TrimEnd('/');
    }

    // ============================================================
    // TAILLE DES FICHIERS
    // ============================================================

    private async Task LoadSizesAsync(
        CancellationToken cancellationToken)
    {
        var total =
            _files.Count;

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
                new RangeHeaderValue(
                    0,
                    0);

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

    // ============================================================
    // TÉLÉCHARGEMENT
    // ============================================================

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

        if (string.IsNullOrWhiteSpace(
                destination))
        {
            await ShowMessageAsync(
                "Choisis un dossier de destination.",
                "Erreur");

            return;
        }

        try
        {
            destination =
                Path.GetFullPath(
                    destination);

            Directory.CreateDirectory(
                destination);
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

            await Task.WhenAll(
                tasks);

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
                () =>
                    file.Status = "Annulé");

            throw;
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(
                () =>
                    file.Status = "Erreur");

            System.Diagnostics.Debug.WriteLine(
                ex);
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
            Path.GetDirectoryName(
                destination);

        if (!string.IsNullOrWhiteSpace(
                directory))
        {
            Directory.CreateDirectory(
                directory);
        }

        long existingBytes = 0;

        if (File.Exists(destination))
        {
            existingBytes =
                new FileInfo(
                    destination).Length;

            if (file.Size > 0 &&
                existingBytes >= file.Size)
            {
                await Dispatcher.UIThread.InvokeAsync(
                    () =>
                        file.Status =
                            "Déjà présent");

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
                buffer.AsMemory(
                    0,
                    bytesRead),
                cancellationToken);

            addBytes(bytesRead);
            updateProgress();
        }

        await Dispatcher.UIThread.InvokeAsync(
            () =>
                file.Status = "Terminé");
    }

    // ============================================================
    // PRESSE-PAPIERS
    // ============================================================

    private async void PasteButton_Click(
        object? sender,
        RoutedEventArgs e)
    {
        try
        {
            var clipboard =
                TopLevel.GetTopLevel(
                    this)?.Clipboard;

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
                        if (!item.Formats.Contains(
                                DataFormat.Text))
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

            text =
                text.Trim();

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

    // ============================================================
    // NAVIGATION DOSSIER LOCAL
    // ============================================================

    private async void BrowseButton_Click(
        object? sender,
        RoutedEventArgs e)
    {
        try
        {
            var currentPath =
                DestinationBox.Text?.Trim();

            IStorageFolder? suggestedStartLocation = null;

            if (!string.IsNullOrWhiteSpace(
                    currentPath) &&
                Directory.Exists(
                    currentPath))
            {
                try
                {
                    suggestedStartLocation =
                        await StorageProvider
                            .TryGetFolderFromPathAsync(
                                currentPath);
                }
                catch
                {
                }
            }

            var folders =
                await StorageProvider
                    .OpenFolderPickerAsync(
                        new FolderPickerOpenOptions
                        {
                            Title =
                                "Choisir le dossier de destination",

                            AllowMultiple = false,

                            SuggestedStartLocation =
                                suggestedStartLocation
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

    // ============================================================
    // EFFACER
    // ============================================================

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
            "Colle le lien Freebox dans le champ « Lien Freebox », " +
            "puis clique sur « Analyser ».";

        AnalyzeButton.IsEnabled = true;
        DownloadButton.IsEnabled = false;
        PasteButton.IsEnabled = true;
        BrowseButton.IsEnabled = true;
        ClearButton.IsEnabled = true;

        UrlBox.Focus();
    }

    // ============================================================
    // CHEMINS LOCAUX
    // ============================================================

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

        value =
            value.Trim();

        return string.IsNullOrWhiteSpace(
                value)
            ? "Inconnu"
            : value;
    }

    private static string SanitizeFileName(
        string value)
    {
        return SanitizePathPart(
            value);
    }

    // ============================================================
    // MESSAGES
    // ============================================================

    private string BuildMultipleSeriesMessage()
    {
        return
            "Plusieurs séries ont été détectées dans ce partage.\n\n" +
            string.Join(
                "\n",
                _detectedSeries) +
            "\n\n" +
            "Téléchargement bloqué.\n\n" +
            "Veuillez télécharger les séries une par une.";
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
                SizeToContent =
                    SizeToContent.Height,
                CanResize = false,
                WindowStartupLocation =
                    WindowStartupLocation.CenterOwner,
                Background =
                    new Avalonia.Media.SolidColorBrush(
                        Avalonia.Media.Color.Parse(
                            "#FFFFFF"))
            };

        var text =
            new TextBlock
            {
                Text = message,
                TextWrapping =
                    Avalonia.Media.TextWrapping.Wrap,
                Foreground =
                    new Avalonia.Media.SolidColorBrush(
                        Avalonia.Media.Color.Parse(
                            "#202124"))
            };

        var buttonBackground =
            new Avalonia.Media.SolidColorBrush(
                Avalonia.Media.Color.Parse(
                    "#E8EDF3"));

        var buttonHoverBackground =
            new Avalonia.Media.SolidColorBrush(
                Avalonia.Media.Color.Parse(
                    "#DCE2E9"));

        var buttonPressedBackground =
            new Avalonia.Media.SolidColorBrush(
                Avalonia.Media.Color.Parse(
                    "#CDD5DE"));

        var buttonBorder =
            new Avalonia.Media.SolidColorBrush(
                Avalonia.Media.Color.Parse(
                    "#C9CED6"));

        var buttonText =
            new TextBlock
            {
                Text = "OK",
                FontSize = 13,
                Foreground =
                    new Avalonia.Media.SolidColorBrush(
                        Avalonia.Media.Color.Parse(
                            "#202124")),
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
                Background =
                    buttonBackground,
                BorderBrush =
                    buttonBorder,
                BorderThickness =
                    new Thickness(1),
                CornerRadius =
                    new CornerRadius(4),
                HorizontalAlignment =
                    Avalonia.Layout.HorizontalAlignment.Right,
                Child = buttonText,
                Cursor =
                    new Cursor(
                        StandardCursorType.Hand)
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
                Margin =
                    new Thickness(20)
            };

        panel.Children.Add(text);
        panel.Children.Add(okButton);

        dialog.Content =
            panel;

        await dialog.ShowDialog(this);
    }

    // ============================================================
    // FORMATAGE
    // ============================================================

    private static string FormatBytes(
        long bytes)
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

        if (bytes <
            1024L * 1024 * 1024 * 1024)
        {
            return
                $"{bytes / 1024.0 / 1024.0 / 1024.0:0.##} Go";
        }

        return
            $"{bytes / 1024.0 / 1024.0 / 1024.0 / 1024.0:0.##} To";
    }
}

// ==================================================================
// EXCEPTION
// ==================================================================

internal sealed class MultipleDownloadGroupException :
    Exception
{
    public MultipleDownloadGroupException(
        string message)
        : base(message)
    {
    }
}

// ==================================================================
// VIDEO STRUCTURE
// ==================================================================

internal sealed class VideoStructure
{
    public string Series { get; init; } =
        string.Empty;

    public string DownloadGroup { get; init; } =
        string.Empty;

    public string Season { get; init; } =
        string.Empty;

    public string Episode { get; init; } =
        string.Empty;

    public bool IsKaamelott { get; init; }

    public bool IsSpecials { get; init; }
}

// ==================================================================
// VIDEO FILE
// ==================================================================

public sealed class VideoFile :
    INotifyPropertyChanged
{
    private string _relativePath =
        string.Empty;

    private long _size;

    private string _status =
        "En attente";

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
            OnPropertyChanged(
                nameof(SizeText));
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
        [CallerMemberName]
        string? propertyName = null)
    {
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(
                propertyName));
    }

    private static string FormatBytes(
        long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} o";
        }

        if (bytes < 1024L * 1024)
        {
            return $"{bytes / 1024.0:0.##} Ko";
        }

        if (bytes <
            1024L * 1024 * 1024)
        {
            return
                $"{bytes / 1024.0 / 1024.0:0.##} Mo";
        }

        if (bytes <
            1024L * 1024 * 1024 * 1024)
        {
            return
                $"{bytes / 1024.0 / 1024.0 / 1024.0:0.##} Go";
        }

        return
            $"{bytes / 1024.0 / 1024.0 / 1024.0 / 1024.0:0.##} To";
    }
}