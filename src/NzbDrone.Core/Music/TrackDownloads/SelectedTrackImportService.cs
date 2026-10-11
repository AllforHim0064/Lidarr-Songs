using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NzbDrone.Common.Disk;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.TrackImport;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.Music.TrackDownloads
{
    public interface ISelectedTrackImportService
    {
        List<ImportResult> Import(string path, List<int> trackIds, DownloadClientItem downloadItem = null);
    }

    public class SelectedTrackImportService : ISelectedTrackImportService
    {
        private readonly ITrackService _tracks;
        private readonly IAudioTagService _tags;
        private readonly IDiskScanService _scan;
        private readonly IDiskProvider _disk;
        private readonly IImportApprovedTracks _import;
        private readonly IRootFolderService _roots;

        public SelectedTrackImportService(ITrackService tracks, IAudioTagService tags, IDiskScanService scan,
            IDiskProvider disk, IImportApprovedTracks import, IRootFolderService roots)
        {
            _tracks = tracks;
            _tags = tags;
            _scan = scan;
            _disk = disk;
            _import = import;
            _roots = roots;
        }

        public List<ImportResult> Import(string path, List<int> trackIds, DownloadClientItem downloadItem = null)
        {
            var requested = _tracks.GetTracks(trackIds);
            if (requested.Count != trackIds.Distinct().Count())
            {
                throw new InvalidOperationException("A requested track no longer exists. Search again before importing.");
            }

            var files = _disk.FolderExists(path) ? _scan.GetAudioFiles(path).Select(f => f.FullName).ToList() : new List<string> { path };
            var candidates = new List<LocalTrack>();
            foreach (var file in files.Where(TrackMatch.IsAudio))
            {
                var tags = _tags.ReadTags(file);
                var matches = requested.Where(t => TrackMatch.Matches(t, tags, file)).ToList();
                if (matches.Count != 1)
                {
                    continue;
                }

                var track = matches.Single();
                if (track.HasFile)
                {
                    continue;
                }

                if (_roots.GetBestRootFolder(track.Artist.Value.Path) == null)
                {
                    throw new InvalidOperationException("Artist destination must be inside a configured root folder.");
                }

                var info = _disk.GetFileInfo(file);
                var quality = tags.Quality ?? Parser.QualityParser.ParseQuality(file, null, 0);
                var allowed = track.Artist.Value.QualityProfile.Value.Items.Where(i => i.Allowed)
                    .SelectMany(i => i.GetQualities()).Any(q => q == quality.Quality);
                if (!allowed)
                {
                    continue;
                }

                candidates.Add(new LocalTrack
                {
                    Path = file,
                    Size = info.Length,
                    Modified = info.LastWriteTimeUtc,
                    FileTrackInfo = tags,
                    Artist = track.Artist.Value,
                    Album = track.AlbumRelease.Value.Album.Value,
                    Release = track.AlbumRelease.Value,
                    Tracks = new List<Track> { track },
                    Quality = quality,
                    ExistingFile = false
                });
            }

            // An ambiguous file never wins merely because it is the first directory entry.
            var unambiguous = candidates.GroupBy(c => c.Tracks.Single().Id).Where(g => g.Count() == 1)
                .Select(g => new ImportDecision<LocalTrack>(g.Single())).ToList();
            // Preserve other library songs: replaceExisting=true removes all existing album files.
            return _import.Import(unambiguous, false, downloadItem, ImportMode.Copy);
        }
    }
}
