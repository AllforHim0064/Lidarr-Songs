using System;
using System.IO;
using System.Text.RegularExpressions;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Music.TrackDownloads
{
    public static class TrackMatch
    {
        public static string Normalize(string text)
        {
            return Regex.Replace((text ?? string.Empty).Normalize().ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();
        }

        public static bool ContainsTitle(string text, string title)
        {
            var normalized = Normalize(title);
            return normalized.Length > 0 && (" " + Normalize(text) + " ").Contains(" " + normalized + " ", StringComparison.Ordinal);
        }

        public static bool IsAudio(string path)
        {
            return Regex.IsMatch(path ?? string.Empty, @"\.(flac|mp3|m4a|aac|ogg|opus|wav|wv|ape|aiff)$", RegexOptions.IgnoreCase);
        }

        public static bool Matches(Track track, ParsedTrackInfo tags, string path)
        {
            if (tags == null)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(tags.TrackMBId))
            {
                return tags.TrackMBId == track.ForeignTrackId || track.OldForeignTrackIds.Contains(tags.TrackMBId);
            }

            var recordingMatches = !string.IsNullOrWhiteSpace(tags.RecordingMBId) &&
                (tags.RecordingMBId == track.ForeignRecordingId || track.OldForeignRecordingIds.Contains(tags.RecordingMBId));
            if (!string.IsNullOrWhiteSpace(tags.RecordingMBId) && !string.IsNullOrWhiteSpace(track.ForeignRecordingId) && !recordingMatches)
            {
                return false;
            }
            var durationMatches = track.Duration <= 0 || tags.Duration.TotalMilliseconds <= 0 ||
                Math.Abs(track.Duration - tags.Duration.TotalMilliseconds) <= 8000;
            var title = string.IsNullOrWhiteSpace(tags.Title) ? Path.GetFileNameWithoutExtension(path) : tags.Title;
            var filenameTitle = Regex.Replace(Normalize(title), @"^\d+\s+", string.Empty);
            var artist = track.Artist.Value;
            var artistPrefix = Normalize(artist.Name) + " ";
            if (filenameTitle.StartsWith(artistPrefix, StringComparison.Ordinal))
            {
                filenameTitle = filenameTitle.Substring(artistPrefix.Length);
            }

            var titleMatches = Normalize(title) == Normalize(track.Title) ||
                (string.IsNullOrWhiteSpace(tags.Title) && filenameTitle == Normalize(track.Title));
            var artistMatches = !string.IsNullOrWhiteSpace(tags.ArtistTitle) ?
                ContainsTitle(tags.ArtistTitle, artist.Name) : ContainsTitle(path, artist.Name);

            var albumMatches = string.IsNullOrWhiteSpace(tags.AlbumTitle) ||
                Normalize(tags.AlbumTitle) == Normalize(track.AlbumRelease.Value.Album.Value.Title);
            return durationMatches && (recordingMatches || (titleMatches && artistMatches && albumMatches));
        }
    }
}
