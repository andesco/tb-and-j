using System.Text.RegularExpressions;
using Jellyfin.Plugin.TorBoxSync.Models;

namespace Jellyfin.Plugin.TorBoxSync.Services;

public static partial class StrmPathBuilder
{
    public static ManagedFileRecord ToManagedRecord(TorBoxFileCandidate candidate, string libraryRootPath, DateTimeOffset now)
    {
        var parsed = ParseCandidate(candidate);
        var relativePath = parsed.MediaKind == "episode"
            ? Path.Combine(
                "series",
                SanitizePathSegment(parsed.DisplayName),
                $"Season {parsed.SeasonNumber}",
                $"{SanitizePathSegment(parsed.FileBaseName)}.strm")
            : Path.Combine(
                "movies",
                SanitizePathSegment(parsed.DisplayName),
                $"{SanitizePathSegment(parsed.FileBaseName)}.strm");

        return new ManagedFileRecord
        {
            TorBoxType = candidate.TorBoxType,
            TorBoxItemId = candidate.ItemId,
            TorBoxFileId = candidate.FileId,
            TorBoxItemName = candidate.ItemName,
            TorBoxFileName = candidate.FileName,
            TorBoxPath = candidate.Path,
            MediaKind = parsed.MediaKind,
            ShowName = parsed.MediaKind == "episode" ? parsed.DisplayName : string.Empty,
            SeasonNumber = parsed.SeasonNumber,
            EpisodeNumber = parsed.EpisodeNumber,
            RelativeStrmPath = relativePath,
            StrmPath = Path.GetFullPath(Path.Combine(libraryRootPath, relativePath)),
            DownloadLink = candidate.DownloadLink,
            FirstSeenUtc = now,
            LastSeenUtc = now
        };
    }

    public static bool IsUnderRoot(string path, string rootPath)
    {
        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizePath(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static ParsedMedia ParseCandidate(TorBoxFileCandidate candidate)
    {
        var episodeMatch = EpisodePattern().Match(candidate.FileName);
        if (!episodeMatch.Success)
        {
            episodeMatch = EpisodePattern().Match(candidate.Path);
        }

        if (episodeMatch.Success)
        {
            return ParseEpisode(candidate, episodeMatch);
        }

        return ParseMovie(candidate);
    }

    private static ParsedMedia ParseEpisode(TorBoxFileCandidate candidate, Match episodeMatch)
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

        var displayName = FormatTitle(showTitle, showYear);
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

    private static ParsedMedia ParseMovie(TorBoxFileCandidate candidate)
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

        var displayName = FormatTitle(title, year);
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
        int? EpisodeNumber);

    [GeneratedRegex(@"(?i)(?<prefix>.*?)(?:(?:[SsTt](?<season>\d{1,4})[ ._\-\[\(]*(?:[Ee][Pp]?)[ ._-]*(?<episode>\d{1,4})(?<extra>(?:[ ._-]*(?:[Ee][Pp]?|x|-)[ ._-]*\d{1,4})*))|(?:(?<season>\d{1,4})[ ._-]*x[ ._-]*(?<episode>\d{1,4})(?<extra>(?:[ ._-]*(?:x|-)[ ._-]*\d{1,4})*))|(?:Season[ ._-]*(?<season>\d{1,4})[ ._-]*Episode[ ._-]*(?<episode>\d{1,4})(?<extra>(?:[ ._-]*(?:Episode|-)[ ._-]*\d{1,4})*)))(?<tail>.*)$", RegexOptions.Compiled)]
    private static partial Regex EpisodePattern();

    [GeneratedRegex(@"(?i)(?<prefix>.*?)(?:\b[Ss](?<season>\d{1,4})\b|\bSeason[ ._-]*(?<season>\d{1,4})\b).*$", RegexOptions.Compiled)]
    private static partial Regex SeasonPackPattern();

    [GeneratedRegex(@"\b(?<year>19\d{2}|20\d{2})\b", RegexOptions.Compiled)]
    private static partial Regex YearPattern();

    [GeneratedRegex(@"[._]+", RegexOptions.Compiled)]
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

    [GeneratedRegex(@"[-–—]\s*[A-Za-z0-9]{2,20}$", RegexOptions.Compiled)]
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
}
