using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Music.TrackDownloads
{
    public class TrackDownloadCandidate
    {
        public string Id { get; set; }
        public int TrackId { get; set; }
        public string Title { get; set; }
        public string Source { get; set; }
        public long Size { get; set; }
        public bool AlbumFallback { get; set; }
        public string Quality { get; set; }
        public string Rejection { get; set; }
    }

    public class TrackSearchResult
    {
        public List<TrackDownloadCandidate> Candidates { get; set; } = new List<TrackDownloadCandidate>();
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public interface ITrackDownloadService
    {
        Task<TrackSearchResult> Search(int trackId, bool albumFallback, CancellationToken cancellationToken);
        Task Grab(string candidateId, int trackId);
    }

    public class TrackDownloadService : ITrackDownloadService
    {
        private sealed class Candidate
        {
            public TrackDownloadCandidate Resource { get; set; }
            public RemoteAlbum Album { get; set; }
            public SoulseekFile File { get; set; }
            public DateTime Expires { get; set; }
        }

        private readonly ITrackService _tracks;
        private readonly IIndexerFactory _indexers;
        private readonly ISearchForReleases _search;
        private readonly IDownloadService _download;
        private readonly ISoulseekTrackService _soulseek;
        private readonly ConcurrentDictionary<string, Candidate> _candidates = new ConcurrentDictionary<string, Candidate>();
        private readonly SemaphoreSlim _grabLock = new SemaphoreSlim(1, 1);

        public TrackDownloadService(ITrackService tracks, IIndexerFactory indexers, ISearchForReleases search,
            IDownloadService download, ISoulseekTrackService soulseek)
        {
            _tracks = tracks;
            _indexers = indexers;
            _search = search;
            _download = download;
            _soulseek = soulseek;
        }

        public async Task<TrackSearchResult> Search(int trackId, bool albumFallback, CancellationToken cancellationToken)
        {
            var track = _tracks.GetTrack(trackId);
            var artist = track.Artist.Value;
            var album = track.AlbumRelease.Value.Album.Value;
            var result = new TrackSearchResult();
            foreach (var expired in _candidates.Where(c => c.Value.Expires < DateTime.UtcNow))
            {
                _candidates.TryRemove(expired.Key, out _);
            }

            if (_soulseek.Configured)
            {
                try
                {
                    foreach (var file in await _soulseek.Search(track, cancellationToken))
                    {
                        Add(result, new Candidate
                        {
                            File = file,
                            Resource = new TrackDownloadCandidate
                            {
                                TrackId = trackId,
                                Title = file.Filename,
                                Source = "Soulseek / " + file.Username,
                                Size = file.Size,
                                Quality = System.IO.Path.GetExtension(file.Filename).TrimStart('.'),
                            }
                        });
                    }
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    result.Warnings.Add("Soulseek search failed. Check slskd connectivity and API permissions.");
                }
            }
            else
            {
                result.Warnings.Add("Soulseek is not configured. Torrent and Usenet sources remain available.");
            }

            var criteria = new AlbumSearchCriteria
            {
                Artist = artist,
                Albums = new List<Album> { album },
                TrackTitle = track.Title,
                AlbumTitle = album.Title,
                InteractiveSearch = true,
                UserInvokedSearch = true
            };
            foreach (var indexer in _indexers.InteractiveSearchEnabled().Where(i =>
                (i is Indexers.Newznab.Newznab || i is Indexers.Torznab.Torznab) &&
                (i.Definition.Tags.Count == 0 || i.Definition.Tags.Intersect(artist.Tags).Any())))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    foreach (var release in await indexer.Fetch(criteria))
                    {
                        if (!TrackMatch.ContainsTitle(release.Title, artist.Name) || !TrackMatch.ContainsTitle(release.Title, track.Title))
                        {
                            continue;
                        }

                        var quality = QualityParser.ParseQuality(release.Title, null, 0);
                        var allowed = artist.QualityProfile.Value.Items.Where(i => i.Allowed)
                            .SelectMany(i => i.GetQualities()).Any(q => q == quality.Quality);
                        Add(result, new Candidate
                        {
                            Album = new RemoteAlbum
                            {
                                Artist = artist,
                                Albums = new List<Album> { album },
                                Release = release,
                                ParsedAlbumInfo = new ParsedAlbumInfo { ArtistName = artist.Name, AlbumTitle = album.Title, Quality = quality },
                                DownloadAllowed = true,
                                ReleaseSource = ReleaseSourceType.InteractiveSearch
                            },
                            Resource = new TrackDownloadCandidate
                            {
                                TrackId = trackId,
                                Title = release.Title,
                                Source = release.Indexer,
                                Size = release.Size,
                                Quality = quality.ToString(),
                                Rejection = allowed ? null : "Quality is not allowed by the artist profile"
                            }
                        });
                    }
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    result.Warnings.Add(indexer.Definition.Name + ": track search failed");
                }
            }

            if (albumFallback)
            {
                try
                {
                    foreach (var decision in await _search.AlbumSearch(album.Id, false, true, true))
                    {
                        Add(result, new Candidate
                        {
                            Album = decision.RemoteAlbum,
                            Resource = new TrackDownloadCandidate
                            {
                                TrackId = trackId,
                                Title = decision.RemoteAlbum.Release.Title,
                                Source = decision.RemoteAlbum.Release.Indexer,
                                Size = decision.RemoteAlbum.Release.Size,
                                Quality = decision.RemoteAlbum.ParsedAlbumInfo?.Quality?.ToString(),
                                AlbumFallback = true,
                                Rejection = decision.Approved ? null : string.Join("; ", decision.Rejections.Select(r => r.Reason))
                            }
                        });
                    }
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    result.Warnings.Add("Album fallback search failed.");
                }
            }

            result.Candidates = result.Candidates.OrderBy(c => c.AlbumFallback).ThenBy(c => c.Rejection != null).ThenBy(c => c.Size).ToList();
            return result;
        }

        private void Add(TrackSearchResult result, Candidate candidate)
        {
            candidate.Resource.Id = Guid.NewGuid().ToString("N");
            candidate.Expires = DateTime.UtcNow.AddMinutes(30);
            _candidates[candidate.Resource.Id] = candidate;
            result.Candidates.Add(candidate.Resource);
        }

        public async Task Grab(string candidateId, int trackId)
        {
            await _grabLock.WaitAsync();
            try
            {
                if (!_candidates.TryGetValue(candidateId ?? string.Empty, out var candidate) || candidate.Expires < DateTime.UtcNow ||
                    candidate.Resource.TrackId != trackId || candidate.Resource.Rejection != null)
                {
                    throw new InvalidOperationException("Search result is invalid, rejected or expired. Search again.");
                }

                var track = _tracks.GetTrack(trackId);
                if (track.HasFile)
                {
                    throw new InvalidOperationException("This song already has a file. Remove it before downloading another copy.");
                }

                if (candidate.File != null)
                {
                    await _soulseek.Grab(track, candidate.File);
                }
                else
                {
                    candidate.Album.RequestedTrackIds = new List<int> { trackId };
                    candidate.Album.RequestedForeignTrackIds = new List<string> { track.ForeignTrackId };
                    await _download.DownloadReport(candidate.Album, null);
                }

                _candidates.TryRemove(candidateId, out _);
            }
            finally
            {
                _grabLock.Release();
            }
        }
    }
}
