using System.Text.RegularExpressions;
using Jellyfin.Plugin.TorBoxSync.Models;

namespace Jellyfin.Plugin.TorBoxSync.Services;

public static partial class StrmPathBuilder
{
    public static ManagedFileRecord ToManagedRecord(TorBoxFileCandidate candidate, string libraryRootPath, DateTimeOffset now, int? fallbackYear = null)
    {
        var parsed = ParseCandidate(candidate, fallbackYear);

        var relativePath = parsed.MediaKind switch
        {
            "episode" => Path.Combine(
                "series",
                SanitizePathSegment(parsed.DisplayName),
                $"Season {parsed.SeasonNumber:00}",
                $"{SanitizePathSegment(parsed.FileBaseName)}.strm"),
            "showextra" => Path.Combine(
                "series",
                SanitizePathSegment(parsed.DisplayName),
                parsed.ExtrasFolder!,
                $"{SanitizePathSegment(parsed.FileBaseName)}.strm"),
            "movieextra" => Path.Combine(
                "movies",
                SanitizePathSegment(parsed.DisplayName),
                parsed.ExtrasFolder!,
                $"{SanitizePathSegment(parsed.FileBaseName)}.strm"),
            _ => Path.Combine(
                "movies",
                SanitizePathSegment(parsed.DisplayName),
                $"{SanitizePathSegment(parsed.FileBaseName)}.strm"),
        };

        return new ManagedFileRecord
        {
            TorBoxType = candidate.TorBoxType,
            TorBoxItemId = candidate.ItemId,
            TorBoxFileId = candidate.FileId,
            TorBoxItemName = candidate.ItemName,
            TorBoxFileName = candidate.FileName,
            TorBoxPath = candidate.Path,
            MediaKind = parsed.MediaKind,
            ShowName = parsed.MediaKind is "episode" or "showextra" ? parsed.DisplayName : string.Empty,
            SeasonNumber = parsed.SeasonNumber,
            EpisodeNumber = parsed.EpisodeNumber,
            RelativeStrmPath = relativePath,
            StrmPath = Path.GetFullPath(Path.Combine(libraryRootPath, relativePath)),
            DownloadLink = candidate.DownloadLink,
            FirstSeenUtc = now,
            LastSeenUtc = now
        };
    }

    /// <summary>
    /// Returns the first 4-digit year (1900-2099) found in <paramref name="text"/>, or null.
    /// Used by TorBoxSyncManager to mine the earliest year across all files in a torrent.
    /// </summary>
    public static int? ExtractFirstYear(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = YearPattern().Match(text);
        return m.Success ? int.Parse(m.Groups["year"].Value) : null;
    }

    public static bool IsUnderRoot(string path, string rootPath)
    {
        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizePath(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static ParsedMedia ParseCandidate(TorBoxFileCandidate candidate, int? fallbackYear = null)
    {
        var episodeMatch = EpisodePattern().Match(candidate.FileName);
        if (!episodeMatch.Success)
            episodeMatch = EpisodePattern().Match(candidate.Path);

        if (episodeMatch.Success)
            return ParseEpisode(candidate, episodeMatch, fallbackYear);

        var extras = TryParseExtras(candidate, fallbackYear);
        if (extras is not null)
            return extras;

        return ParseMovie(candidate, fallbackYear);
    }

    // ── Extras / specials detection ──────────────────────────────────────────

    // Named extras folders with a specific Jellyfin target name.
    // For TV show torrents, ANY unrecognised non-season intermediate folder also
    // falls back to "extras" — so "Bonus Bits", "Making Of", etc. are covered
    // automatically without needing to be listed here.
    private static readonly Dictionary<string, string> _extrasFolderMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["extras"] = "extras",          ["extra"] = "extras",
        ["bonus"] = "extras",           ["bonus features"] = "extras",
        ["bonusfeatures"] = "extras",   ["bonus content"] = "extras",
        ["clips"] = "extras",           ["clip"] = "extras",
        ["scenes"] = "extras",
        ["featurettes"] = "featurettes", ["featurette"] = "featurettes",
        ["behind the scenes"] = "behind the scenes",
        ["behindthescenes"] = "behind the scenes",
        ["behind-the-scenes"] = "behind the scenes",
        ["deleted scenes"] = "deleted scenes",
        ["deletedscenes"] = "deleted scenes",
        ["deleted"] = "deleted scenes",
        ["trailers"] = "trailers",      ["trailer"] = "trailers",
        ["interviews"] = "interviews",  ["interview"] = "interviews",
        ["shorts"] = "shorts",          ["short"] = "shorts",
        // specials / season 0 / pilots → Season 00 subfolder (Jellyfin S00Exx)
        ["specials"] = "Season 00",     ["special"] = "Season 00",
        ["season 0"] = "Season 00",     ["season 00"] = "Season 00",
        ["s00"] = "Season 00",
        ["pilot"] = "Season 00",        ["pilots"] = "Season 00",
    };

    private static ParsedMedia? TryParseExtras(TorBoxFileCandidate candidate, int? fallbackYear = null)
    {
        if (string.IsNullOrWhiteSpace(candidate.Path))
            return null;

        var parts = candidate.Path.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        // Need at least root / some-folder / file.ext
        if (parts.Length < 3)
            return null;

        var rootFolder = parts[0];
        var fileName   = parts[^1];
        var isTvShow   = LooksLikeTvShowFolder(rootFolder);

        string? jellyfinFolder = null;
        var hasSeasonIntermediate = false;

        for (var i = 1; i < parts.Length - 1; i++)
        {
            var part = parts[i].Trim();

            // Named extras/specials folder — works for movies and TV shows alike.
            if (_extrasFolderMap.TryGetValue(part, out var mapped))
            {
                jellyfinFolder = mapped;
                break;
            }

            // Season folder (e.g. "Season 02", "S02") — not extras, keep scanning.
            if (TvSeasonKeywordPattern().IsMatch(part) || TvSeasonNumberPattern().IsMatch(part))
            {
                hasSeasonIntermediate = true;
                continue;
            }

            // For TV show torrents, ANY intermediate folder that isn't a season folder
            // is treated as generic extras. This handles arbitrary folder names like
            // "Bonus Bits", "Making Of", "Outtakes", etc. without enumerating them.
            if (isTvShow || hasSeasonIntermediate)
            {
                jellyfinFolder = "extras";
                break;
            }
        }

        if (jellyfinFolder is null)
            return null;

        var fileBaseName    = Path.GetFileNameWithoutExtension(fileName);
        var effectiveIsTv   = isTvShow || hasSeasonIntermediate;
        var parentTitle     = ExtractTitleFromFolder(rootFolder, fallbackYear);

        return new ParsedMedia(
            effectiveIsTv ? "showextra" : "movieextra",
            parentTitle,
            fileBaseName,
            null,
            null,
            jellyfinFolder);
    }

    private static bool LooksLikeTvShowFolder(string name)
    {
        // Season range:  S01-03, S01-03C, S01-S03
        if (TvSeasonRangePattern().IsMatch(name))   return true;
        // Standalone season:  S01, S02 (not part of SxxExx or a range)
        if (TvSeasonNumberPattern().IsMatch(name))  return true;
        // "Season N" / "Seasons N-M"
        if (TvSeasonKeywordPattern().IsMatch(name)) return true;
        // "Complete Series" / "Complete Collection"
        if (TvCompleteSeriesPattern().IsMatch(name)) return true;
        // Full SxxExx episode tag somewhere in the folder name
        if (EpisodePattern().IsMatch(name))         return true;
        return false;
    }

    private static string ExtractTitleFromFolder(string folderName, int? fallbackYear = null)
    {
        // Strip from the season/quality marker onward, then run through the
        // normal title + year extractor so we get "(YYYY)" when a year is present.
        var stripped = SeasonAndBeyondPattern().Replace(folderName, string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(stripped))
            stripped = folderName;

        var (title, year) = ParseTitleAndYear(stripped);
        if (string.IsNullOrWhiteSpace(title))
            title = CleanTitleText(stripped);

        // If the folder name carries no year, use the earliest year found across the
        // torrent's media files (supplied by the caller from BuildEarliestYearMap).
        return string.IsNullOrWhiteSpace(title) ? "Unknown" : FormatTitle(title, year ?? fallbackYear);
    }

    private static ParsedMedia ParseEpisode(TorBoxFileCandidate candidate, Match episodeMatch, int? fallbackYear = null)
    {
        var seasonNumber = int.Parse(episodeMatch.Groups["season"].Value);
        var episodeNumber = int.Parse(episodeMatch.Groups["episode"].Value);
        var showSource = ChooseShowNameSource(candidate, episodeMatch);
        var (showTitle, showYear) = ParseTitleAndYear(showSource);
        if (string.IsNullOrWhiteSpace(showTitle))
        {
            (showTitle, showYear) = ParseTitleAndYear(candidate.ItemName);
        }

        if (string.IsNullOrWhiteSpace(showTitle))
        {
            showTitle = "Unknown";
        }

        var displayName = FormatTitle(showTitle, showYear ?? fallbackYear);
        var episodeCode = $"S{seasonNumber:00}E{episodeNumber:00}";
        var extraEpisodeNumbers = ParseExtraEpisodeNumbers(episodeMatch.Groups["extra"].Value, episodeNumber);
        if (extraEpisodeNumbers.Count > 0)
        {
            episodeCode = $"{episodeCode}-E{extraEpisodeNumbers[^1]:00}";
        }

        var fileBaseName = $"{displayName} - {episodeCode}";
        var episodeTitle = CleanEpisodeTitle(episodeMatch.Groups["tail"].Value);
        if (!string.IsNullOrWhiteSpace(episodeTitle))
        {
            fileBaseName = $"{fileBaseName} - {episodeTitle}";
        }

        return new ParsedMedia("episode", displayName, fileBaseName, seasonNumber, episodeNumber);
    }

    private static ParsedMedia ParseMovie(TorBoxFileCandidate candidate, int? fallbackYear = null)
    {
        var (title, year) = ParseTitleAndYear(candidate.ItemName);
        if (string.IsNullOrWhiteSpace(title) || LooksLikeHash(title))
        {
            (title, year) = ParseTitleAndYear(candidate.FileName);
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            title = "Unknown";
        }

        var displayName = FormatTitle(title, year ?? fallbackYear);
        var versionSuffix = ExtractVersionSuffix(candidate.FileName);
        var fileBaseName = string.IsNullOrWhiteSpace(versionSuffix)
            ? displayName
            : $"{displayName} - {versionSuffix}";

        return new ParsedMedia("movie", displayName, fileBaseName, null, null);
    }

    private static string ChooseShowNameSource(TorBoxFileCandidate candidate, Match fileEpisodeMatch)
    {
        var prefix = fileEpisodeMatch.Groups["prefix"].Value;
        if (!string.IsNullOrWhiteSpace(prefix) && HasLetters(prefix))
        {
            return prefix;
        }

        var showDirectory = fileEpisodeMatch.Groups["showdir"].Value;
        if (!string.IsNullOrWhiteSpace(showDirectory) && HasLetters(showDirectory))
        {
            return showDirectory;
        }

        var itemEpisodeMatch = EpisodePattern().Match(candidate.ItemName);
        if (itemEpisodeMatch.Success)
        {
            var itemPrefix = itemEpisodeMatch.Groups["prefix"].Value;
            if (!string.IsNullOrWhiteSpace(itemPrefix) && HasLetters(itemPrefix))
            {
                return itemPrefix;
            }
        }

        var seasonPackMatch = SeasonPackPattern().Match(candidate.ItemName);
        if (seasonPackMatch.Success)
        {
            var seasonPackPrefix = seasonPackMatch.Groups["prefix"].Value;
            if (!string.IsNullOrWhiteSpace(seasonPackPrefix) && HasLetters(seasonPackPrefix))
            {
                return seasonPackPrefix;
            }
        }

        return candidate.ItemName;
    }

    private static (string Title, int? Year) ParseTitleAndYear(string value)
    {
        var cleaned = PrecleanName(value);
        var yearMatch = YearPattern().Match(cleaned);
        if (yearMatch.Success)
        {
            var titleBeforeYear = cleaned[..yearMatch.Index];
            var title = CleanTitleText(titleBeforeYear);
            var year = int.Parse(yearMatch.Groups["year"].Value);
            if (!string.IsNullOrWhiteSpace(title))
            {
                return (title, year);
            }
        }

        return (CleanTitleText(ReleaseNoisePattern().Replace(cleaned, string.Empty)), null);
    }

    private static string CleanTitleText(string value)
    {
        var withoutExtension = Path.GetFileNameWithoutExtension(value);
        var cleaned = SeparatorPattern().Replace(withoutExtension, " ");
        cleaned = WebsitePrefixPattern().Replace(cleaned, string.Empty);
        cleaned = WebsitePostfixPattern().Replace(cleaned, string.Empty);
        cleaned = BracketedTokenPattern().Replace(cleaned, " ");
        cleaned = TrailingGroupPattern().Replace(cleaned, string.Empty);
        cleaned = DanglingSeparatorsPattern().Replace(cleaned, " ");
        cleaned = WhitespacePattern().Replace(cleaned, " ").Trim();
        // Strip any trailing unclosed bracket/paren left after the above passes
        // (happens when e.g. "(2160p)" is the last token before the year and the
        // open paren survives after BracketedTokenPattern strips the closed pair).
        cleaned = TrailingUnclosedBracketPattern().Replace(cleaned, string.Empty).TrimEnd();
        return cleaned;
    }

    private static string PrecleanName(string value)
    {
        var withoutExtension = Path.GetFileNameWithoutExtension(value);
        var cleaned = SeparatorPattern().Replace(withoutExtension, " ");
        cleaned = WebsitePrefixPattern().Replace(cleaned, string.Empty);
        cleaned = WebsitePostfixPattern().Replace(cleaned, string.Empty);
        return WhitespacePattern().Replace(cleaned, " ").Trim();
    }

    private static string FormatTitle(string title, int? year)
        => year.HasValue ? $"{title} ({year.Value})" : title;

    private static string CleanEpisodeTitle(string value)
    {
        var cleaned = CleanTitleText(ReleaseNoisePattern().Replace(value, string.Empty));
        if (string.IsNullOrWhiteSpace(cleaned)
            || QualityTokenOnlyPattern().IsMatch(cleaned)
            || EpisodeTitleNoiseOnlyPattern().IsMatch(cleaned)
            || cleaned.Length < 3)
        {
            return string.Empty;
        }

        return cleaned;
    }

    private static string ExtractVersionSuffix(string fileName)
    {
        var cleaned = PrecleanName(fileName);
        var suffixParts = VersionTokenPattern()
            .Matches(cleaned)
            .Select(match => NormalizeVersionToken(match.Value))
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToArray();

        return suffixParts.Length == 0 ? string.Empty : string.Join(" ", suffixParts);
    }

    private static string SanitizePathSegment(string value)
    {
        var chars = value.Select(c => IsInvalidPathSegmentChar(c) ? ' ' : c).ToArray();
        var sanitized = WhitespacePattern().Replace(new string(chars), " ").Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "Unknown" : sanitized;
    }

    private static bool IsInvalidPathSegmentChar(char value)
        => value is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|'
           || char.IsControl(value);

    private static bool HasLetters(string value)
        => value.Any(char.IsLetter);

    private static bool LooksLikeHash(string value)
    {
        var cleaned = WhitespacePattern().Replace(value, string.Empty);
        return HashPattern().IsMatch(cleaned);
    }

    private static List<int> ParseExtraEpisodeNumbers(string value, int firstEpisode)
    {
        var episodes = new List<int>();
        foreach (Match match in ExtraEpisodeNumberPattern().Matches(value))
        {
            if (!int.TryParse(match.Groups["episode"].Value, out var episode) || episode <= firstEpisode)
            {
                continue;
            }

            if (episodes.Count == 0 && episode - firstEpisode > 1)
            {
                for (var current = firstEpisode + 1; current <= episode; current++)
                {
                    episodes.Add(current);
                }

                continue;
            }

            episodes.Add(episode);
        }

        return episodes.Distinct().OrderBy(i => i).ToList();
    }

    private static string NormalizeVersionToken(string value)
    {
        var token = value.Replace("-", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        if (ResolutionTokenPattern().IsMatch(token))
        {
            return token.ToLowerInvariant();
        }

        return token switch
        {
            "WEBDL" => "WEB-DL",
            "WEBRIP" => "WEBRip",
            "BLURAY" => "BluRay",
            "REMUX" => "REMUX",
            "UHD" => "UHD",
            "HDR" => "HDR",
            "DV" => "DV",
            "HEVC" => "HEVC",
            "H265" => "H265",
            "X265" => "x265",
            "X264" => "x264",
            _ => token
        };
    }

    private sealed record ParsedMedia(
        string MediaKind,
        string DisplayName,
        string FileBaseName,
        int? SeasonNumber,
        int? EpisodeNumber,
        string? ExtrasFolder = null);

    // NxM alternative uses (?<!\w) + no separator around 'x' so that codec tags like
    // x265 and x264 (e.g. "MP4.x265", "7.1.x265") are never mistaken for episode markers.
    // Real NxM filenames write the pattern directly adjacent: "Show.4x01.mkv", never "4.x.265".
    [GeneratedRegex(
        @"(?i)(?<prefix>.*?)" +
        @"(?:" +
            // Standard SxxExx / TxxExx
            @"(?:[SsTt](?<season>\d{1,4})[ ._\-\[\(]*(?:[Ee][Pp]?)[ ._-]*(?<episode>\d{1,4})(?<extra>(?:[ ._-]*(?:[Ee][Pp]?|x|-)[ ._-]*\d{1,4})*))" +
            // NxM (e.g. 4x01) — season and episode must be directly adjacent to 'x' with no
            // separator, and the season must not be preceded by another word character so that
            // codec suffixes like MP4.x265 or 7.1.x265 are excluded.
            @"|(?:(?<!\w)(?<season>\d{1,2})x(?<episode>\d{1,3})(?<extra>(?:[ ._-]*(?:x|-)[ ._-]*\d{1,4})*))" +
            // "Season N Episode M" word form
            @"|(?:Season[ ._-]*(?<season>\d{1,4})[ ._-]*Episode[ ._-]*(?<episode>\d{1,4})(?<extra>(?:[ ._-]*(?:Episode|-)[ ._-]*\d{1,4})*))" +
        @")(?<tail>.*)$",
        RegexOptions.Compiled)]
    private static partial Regex EpisodePattern();

    [GeneratedRegex(@"(?i)(?<prefix>.*?)(?:\b[Ss](?<season>\d{1,4})\b|\bSeason[ ._-]*(?<season>\d{1,4})\b).*$", RegexOptions.Compiled)]
    private static partial Regex SeasonPackPattern();

    [GeneratedRegex(@"\b(?<year>19\d{2}|20\d{2})\b", RegexOptions.Compiled)]
    private static partial Regex YearPattern();

    [GeneratedRegex(@"[._-]+", RegexOptions.Compiled)]
    private static partial Regex SeparatorPattern();

    [GeneratedRegex(@"\b(2160p|1080p|720p|576p|540p|480p|WEB[-_. ]?DL|WEBRip|WEB[-_. ]?Mux|Blu[-_. ]?Ray|BDMux|BDRemux|BRRip|BDRip|DVD[-_. ]?Rip|REMUX|UHD|HDR10?\+?|DV|DoVi|HEVC|H\.?265|x265|H\.?264|x264|AV1|XviD|DivX|AMZN|ATVP|DSNP|NF|HMAX|MAX|Hulu|PCOK|iTunes|DDP?5\.1|DDP?7\.1|EAC3|AC3|DDP|DTS|TrueHD|Atmos|AAC[25]?\.1?|FLAC|Opus)\b.*$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ReleaseNoisePattern();

    [GeneratedRegex(@"\b(2160p|1080p|720p|576p|540p|480p|WEB[-_. ]?DL|WEBRip|WEB[-_. ]?Mux|Blu[-_. ]?Ray|BDMux|BDRemux|BRRip|BDRip|DVD[-_. ]?Rip|REMUX|UHD|HDR10?\+?|DV|DoVi|HEVC|H\.?265|x265|H\.?264|x264|AV1|AMZN|ATVP|DSNP|NF|HMAX|MAX|Hulu|PCOK|iTunes)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex VersionTokenPattern();

    [GeneratedRegex(@"^\s*(2160p|1080p|720p|576p|540p|480p|WEB[-_. ]?DL|WEBRip|Blu[-_. ]?Ray|REMUX|UHD|HDR|DV|HEVC|H\.?265|x265|H\.?264|x264)(\s+|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex QualityTokenOnlyPattern();

    [GeneratedRegex(@"^\s*(?:(?:FRENCH|TRUEFRENCH|VOSTFR|VO|VF|GERMAN|SPANISH|LATINO|ITALIAN|DUTCH|SWEDISH|SWE|DANISH|NORWEGIAN|FINNISH|POLISH|PORTUGUESE|BRAZILIAN|JAPANESE|KOREAN|CHINESE|ENGLISH|MULTI|MULTISUBS?|SUBBED|DUBBED|DUAL\s+AUDIO|DL)\s*)+$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex EpisodeTitleNoiseOnlyPattern();

    [GeneratedRegex(@"^(?:(?:\[|\()\s*)?(?:www\.)?[-a-z0-9-]{1,256}\.(?:[a-z]{2,6}\.[a-z]{2,6}|xn--[a-z0-9-]{4,}|[a-z]{2,})(?:\s*(?:\]|\))|[ -]{2,})[ -]*", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex WebsitePrefixPattern();

    [GeneratedRegex(@"(?:\[\s*)?(?:www\.)?[-a-z0-9-]{1,256}\.(?:xn--[a-z0-9-]{4,}|[a-z]{2,6})(?:\s*\])?$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex WebsitePostfixPattern();

    [GeneratedRegex(@"\[[^\]]{1,40}\]|\([^\)]{1,40}\)$", RegexOptions.Compiled)]
    private static partial Regex BracketedTokenPattern();

    // Scene group suffixes are ALL-CAPS or all-digits (e.g. -YIFY, -RARBG, -NTB).
    // Titlecase words like "-Planets" are part of the title, not a group name.
    [GeneratedRegex(@"[-–—]\s*[A-Z0-9]{2,8}$", RegexOptions.Compiled)]
    private static partial Regex TrailingGroupPattern();

    [GeneratedRegex(@"[\s\-–—]+$", RegexOptions.Compiled)]
    private static partial Regex DanglingSeparatorsPattern();

    [GeneratedRegex(@"^(?:[a-fA-F0-9]{24,}|[0-9a-zA-Z]{32}|[A-Z]{11}\d{3}|[a-z]{12}\d{3}|Backup_\d{5,}S\d{2}-\d{2}|abc(?:[-_. ]xyz)?|b00bs|123)$", RegexOptions.Compiled)]
    private static partial Regex HashPattern();

    [GeneratedRegex(@"^(2160|1080|720|576|540|480)P$", RegexOptions.Compiled)]
    private static partial Regex ResolutionTokenPattern();

    [GeneratedRegex(@"(?:[Ee][Pp]?|x|-)?[ ._-]*(?<episode>\d{1,4})", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ExtraEpisodeNumberPattern();

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex WhitespacePattern();

    [GeneratedRegex(@"[\(\[]\s*$", RegexOptions.Compiled)]
    private static partial Regex TrailingUnclosedBracketPattern();

    // TV-show folder detection
    [GeneratedRegex(@"\bS\d{1,2}[-–]S?\d{1,2}C?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex TvSeasonRangePattern();

    [GeneratedRegex(@"\bS\d{2}(?![-–E\d])\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex TvSeasonNumberPattern();

    [GeneratedRegex(@"\bSeasons?\s+\d", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex TvSeasonKeywordPattern();

    [GeneratedRegex(@"\b(?:Complete\s+Series|Complete\s+Collection|The\s+Complete\s+Series)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex TvCompleteSeriesPattern();

    // Strip season marker and everything after it so only the show/movie title remains.
    // Handles: "Bluey S01-03C …", "Breaking Bad S01-S05 …", "Walking Dead Season 10 …",
    //          "GOT Complete Series", "Show.Name.S01.BluRay"
    [GeneratedRegex(
        @"\s*\bS(?:easons?\s*)?\d{1,2}(?:[-–]S?\d{1,2}C?)?\b.*$" +
        @"|\s*\bComplete\b.*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex SeasonAndBeyondPattern();
}
