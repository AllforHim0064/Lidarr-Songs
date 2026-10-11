using System.Collections.Generic;
using System.Data;
using System.Linq;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Datastore.Converters;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Music;
using NzbDrone.Core.Music.Events;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Download.TrackedDownloads
{
    [TestFixture]
    public class TrackedDownloadServiceFixture : CoreTest<TrackedDownloadService>
    {
        [TestCase(false)]
        [TestCase(true)]
        public void should_restore_selected_track_identity_after_history_serialization(bool roundTrip)
        {
            var data = new Dictionary<string, string>
            {
                { "RequestedTrackIds", "12" },
                { "RequestedForeignTrackIds", "selected-track" }
            };
            if (roundTrip)
            {
                var parameter = new Mock<IDbDataParameter>();
                parameter.SetupProperty(p => p.Value);
                var converter = new EmbeddedDocumentConverter<Dictionary<string, string>>();
                converter.SetValue(parameter.Object, data);
                data = converter.Parse(parameter.Object.Value);
                data.Should().ContainKey("requestedTrackIds");
            }

            var artist = new Artist { Id = 5 };
            var album = new Album { Id = 4, ArtistId = 5 };
            Mocker.GetMock<IHistoryService>()
                .Setup(s => s.FindByDownloadId("35238"))
                .Returns(new List<EntityHistory>
                {
                    new EntityHistory
                    {
                        EventType = EntityHistoryEventType.Grabbed,
                        Artist = artist,
                        Album = album,
                        AlbumId = album.Id,
                        SourceTitle = "Audio Artist - Individual Song MP3",
                        Data = data
                    }
                });
            // Metadata refresh changed the local ID; persisted foreign identity is authoritative.
            Mocker.GetMock<ITrackService>()
                .Setup(s => s.GetTracksByAlbum(album.Id))
                .Returns(new List<Track> { new Track { Id = 42, ForeignTrackId = "selected-track" } });
            var client = new DownloadClientDefinition { Id = 1, Protocol = nameof(TorrentDownloadProtocol) };
            var item = new DownloadClientItem
            {
                DownloadId = "35238",
                Title = "Audio Artist - Individual Song MP3",
                DownloadClientInfo = new DownloadClientItemClientInfo { Id = 1, Protocol = client.Protocol }
            };

            var tracked = Subject.TrackDownload(client, item);

            tracked.RemoteAlbum.Should().NotBeNull();
            tracked.RemoteAlbum.RequestedTrackIds.Should().Equal(42);
            tracked.RemoteAlbum.Albums.Should().ContainSingle().Which.Id.Should().Be(4);
        }

        private void GivenDownloadHistory()
        {
            Mocker.GetMock<IHistoryService>()
                .Setup(s => s.FindByDownloadId(It.Is<string>(sr => sr == "35238")))
                .Returns(new List<EntityHistory>()
                {
                 new EntityHistory()
                {
                     DownloadId = "35238",
                     SourceTitle = "Audio Artist - Audio Album [2018 - FLAC]",
                     ArtistId = 5,
                     AlbumId = 4,
                }
                });
        }

        [Test]
        public void should_track_downloads_using_the_source_title_if_it_cannot_be_found_using_the_download_title()
        {
            GivenDownloadHistory();

            var remoteAlbum = new RemoteAlbum
            {
                Artist = new Artist() { Id = 5 },
                Albums = new List<Album> { new Album { Id = 4 } },
                ParsedAlbumInfo = new ParsedAlbumInfo()
                {
                    AlbumTitle = "Audio Album",
                    ArtistName = "Audio Artist"
                }
            };

            Mocker.GetMock<IParsingService>()
                  .Setup(s => s.Map(It.Is<ParsedAlbumInfo>(i => i.AlbumTitle == "Audio Album" && i.ArtistName == "Audio Artist"), It.IsAny<int>(), It.IsAny<IEnumerable<int>>()))
                  .Returns(remoteAlbum);

            var client = new DownloadClientDefinition()
            {
                Id = 1,
                Protocol = nameof(TorrentDownloadProtocol)
            };

            var item = new DownloadClientItem()
            {
                Title = "The torrent release folder",
                DownloadId = "35238",
                DownloadClientInfo = new DownloadClientItemClientInfo
                {
                    Protocol = client.Protocol,
                    Id = client.Id,
                    Name = client.Name
                }
            };

            var trackedDownload = Subject.TrackDownload(client, item);

            trackedDownload.Should().NotBeNull();
            trackedDownload.RemoteAlbum.Should().NotBeNull();
            trackedDownload.RemoteAlbum.Artist.Should().NotBeNull();
            trackedDownload.RemoteAlbum.Artist.Id.Should().Be(5);
            trackedDownload.RemoteAlbum.Albums.First().Id.Should().Be(4);
        }

        [Test]
        public void should_unmap_tracked_download_if_album_deleted()
        {
            GivenDownloadHistory();

            var remoteAlbum = new RemoteAlbum
            {
                Artist = new Artist() { Id = 5 },
                Albums = new List<Album> { new Album { Id = 4 } },
                ParsedAlbumInfo = new ParsedAlbumInfo()
                {
                    AlbumTitle = "Audio Album",
                    ArtistName = "Audio Artist"
                }
            };

            Mocker.GetMock<IParsingService>()
                  .Setup(s => s.Map(It.Is<ParsedAlbumInfo>(i => i.AlbumTitle == "Audio Album" && i.ArtistName == "Audio Artist"), It.IsAny<int>(), It.IsAny<IEnumerable<int>>()))
                  .Returns(remoteAlbum);

            var client = new DownloadClientDefinition()
            {
                Id = 1,
                Protocol = nameof(TorrentDownloadProtocol)
            };

            var item = new DownloadClientItem()
            {
                Title = "Audio Artist - Audio Album [2018 - FLAC]",
                DownloadId = "35238",
                DownloadClientInfo = new DownloadClientItemClientInfo
                {
                    Protocol = client.Protocol,
                    Id = client.Id,
                    Name = client.Name
                }
            };

            // get a tracked download in place
            var trackedDownload = Subject.TrackDownload(client, item);
            Subject.GetTrackedDownloads().Should().HaveCount(1);

            // simulate deletion - album no longer maps
            Mocker.GetMock<IParsingService>()
                .Setup(s => s.Map(It.Is<ParsedAlbumInfo>(i => i.AlbumTitle == "Audio Album" && i.ArtistName == "Audio Artist"), It.IsAny<int>(), It.IsAny<IEnumerable<int>>()))
                .Returns(default(RemoteAlbum));

            // handle deletion event
            Subject.Handle(new AlbumDeletedEvent(remoteAlbum.Albums.First(), false, false));

            // verify download has null remote album
            var trackedDownloads = Subject.GetTrackedDownloads();
            trackedDownloads.Should().HaveCount(1);
            trackedDownloads.First().RemoteAlbum.Should().BeNull();
        }

        [Test]
        public void should_unmap_tracked_download_if_album_removed()
        {
            GivenDownloadHistory();

            var remoteAlbum = new RemoteAlbum
            {
                Artist = new Artist() { Id = 5 },
                Albums = new List<Album> { new Album { Id = 4 } },
                ParsedAlbumInfo = new ParsedAlbumInfo()
                {
                    AlbumTitle = "Audio Album",
                    ArtistName = "Audio Artist"
                }
            };

            Mocker.GetMock<IParsingService>()
                  .Setup(s => s.Map(It.Is<ParsedAlbumInfo>(i => i.AlbumTitle == "Audio Album" && i.ArtistName == "Audio Artist"), It.IsAny<int>(), It.IsAny<IEnumerable<int>>()))
                  .Returns(remoteAlbum);

            var client = new DownloadClientDefinition()
            {
                Id = 1,
                Protocol = nameof(TorrentDownloadProtocol)
            };

            var item = new DownloadClientItem()
            {
                Title = "Audio Artist - Audio Album [2018 - FLAC]",
                DownloadId = "35238",
                DownloadClientInfo = new DownloadClientItemClientInfo
                {
                    Protocol = client.Protocol,
                    Id = client.Id,
                    Name = client.Name
                }
            };

            Subject.TrackDownload(client, item);
            Subject.GetTrackedDownloads().Should().HaveCount(1);

            // simulate deletion - album no longer maps
            Mocker.GetMock<IParsingService>()
                .Setup(s => s.Map(It.Is<ParsedAlbumInfo>(i => i.AlbumTitle == "Audio Album" && i.ArtistName == "Audio Artist"), It.IsAny<int>(), It.IsAny<IEnumerable<int>>()))
                .Returns(default(RemoteAlbum));

            Subject.Handle(new AlbumInfoRefreshedEvent(remoteAlbum.Artist, new List<Album>(), new List<Album>(), remoteAlbum.Albums));

            var trackedDownloads = Subject.GetTrackedDownloads();
            trackedDownloads.Should().HaveCount(1);
            trackedDownloads.First().RemoteAlbum.Should().BeNull();
        }

        [Test]
        public void should_not_throw_when_processing_deleted_albums()
        {
            GivenDownloadHistory();

            var remoteAlbum = new RemoteAlbum
            {
                Artist = new Artist() { Id = 5 },
                Albums = new List<Album> { new Album { Id = 4 } },
                ParsedAlbumInfo = new ParsedAlbumInfo()
                {
                    AlbumTitle = "Audio Album",
                    ArtistName = "Audio Artist"
                }
            };

            Mocker.GetMock<IParsingService>()
                  .Setup(s => s.Map(It.Is<ParsedAlbumInfo>(i => i.AlbumTitle == "Audio Album" && i.ArtistName == "Audio Artist"), It.IsAny<int>(), It.IsAny<IEnumerable<int>>()))
                  .Returns(remoteAlbum);

            var client = new DownloadClientDefinition()
            {
                Id = 1,
                Protocol = nameof(TorrentDownloadProtocol)
            };

            var item = new DownloadClientItem()
            {
                Title = "Audio Artist - Audio Album [2018 - FLAC]",
                DownloadId = "35238",
                DownloadClientInfo = new DownloadClientItemClientInfo
                {
                    Protocol = client.Protocol,
                    Id = client.Id,
                    Name = client.Name
                }
            };

            Subject.TrackDownload(client, item);
            Subject.GetTrackedDownloads().Should().HaveCount(1);

            // simulate deletion - album no longer maps
            Mocker.GetMock<IParsingService>()
                .Setup(s => s.Map(It.Is<ParsedAlbumInfo>(i => i.AlbumTitle == "Audio Album" && i.ArtistName == "Audio Artist"), It.IsAny<int>(), It.IsAny<IEnumerable<int>>()))
                .Returns(default(RemoteAlbum));

            Subject.Handle(new AlbumInfoRefreshedEvent(remoteAlbum.Artist, new List<Album>(), new List<Album>(), remoteAlbum.Albums));

            var trackedDownloads = Subject.GetTrackedDownloads();
            trackedDownloads.Should().HaveCount(1);
            trackedDownloads.First().RemoteAlbum.Should().BeNull();
        }

        [Test]
        public void should_not_throw_when_processing_deleted_artist()
        {
            GivenDownloadHistory();

            var remoteAlbum = new RemoteAlbum
            {
                Artist = new Artist() { Id = 5 },
                Albums = new List<Album> { new Album { Id = 4 } },
                ParsedAlbumInfo = new ParsedAlbumInfo()
                {
                    AlbumTitle = "Audio Album",
                    ArtistName = "Audio Artist"
                }
            };

            Mocker.GetMock<IParsingService>()
                  .Setup(s => s.Map(It.Is<ParsedAlbumInfo>(i => i.AlbumTitle == "Audio Album" && i.ArtistName == "Audio Artist"), It.IsAny<int>(), It.IsAny<IEnumerable<int>>()))
                  .Returns(remoteAlbum);

            var client = new DownloadClientDefinition()
            {
                Id = 1,
                Protocol = nameof(TorrentDownloadProtocol)
            };

            var item = new DownloadClientItem()
            {
                Title = "Audio Artist - Audio Album [2018 - FLAC]",
                DownloadId = "35238",
                DownloadClientInfo = new DownloadClientItemClientInfo
                {
                    Protocol = client.Protocol,
                    Id = client.Id,
                    Name = client.Name
                }
            };

            Subject.TrackDownload(client, item);
            Subject.GetTrackedDownloads().Should().HaveCount(1);

            // simulate deletion - album no longer maps
            Mocker.GetMock<IParsingService>()
                .Setup(s => s.Map(It.Is<ParsedAlbumInfo>(i => i.AlbumTitle == "Audio Album" && i.ArtistName == "Audio Artist"), It.IsAny<int>(), It.IsAny<IEnumerable<int>>()))
                .Returns(default(RemoteAlbum));

            Subject.Handle(new ArtistsDeletedEvent(new List<Artist> { remoteAlbum.Artist }, true, true));

            var trackedDownloads = Subject.GetTrackedDownloads();
            trackedDownloads.Should().HaveCount(1);
            trackedDownloads.First().RemoteAlbum.Should().BeNull();
        }
    }
}
