using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Linq;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.TrackImport;
using NzbDrone.Core.Music;
using NzbDrone.Core.Music.TrackDownloads;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MusicTests.TrackDownloads
{
    [TestFixture]
    public class SelectedTrackImportFixture : CoreTest<SelectedTrackImportService>
    {
        private Track _track;
        private List<ImportDecision<LocalTrack>> _decisions;

        [SetUp]
        public void Setup()
        {
            var artist = new Artist
            {
                Id = 1,
                Name = "Artist",
                Path = "/music/Artist",
                QualityProfile = new QualityProfile
                {
                    Items = new List<QualityProfileQualityItem>
                    {
                        new QualityProfileQualityItem { Quality = Quality.FLAC, Allowed = true }
                    }
                }
            };
            _track = new Track
            {
                Id = 10,
                Title = "Wanted",
                Artist = artist,
                AlbumRelease = new AlbumRelease { Id = 2, Album = new Album { Id = 3, Title = "Album" } }
            };
            Mocker.GetMock<ITrackService>().Setup(s => s.GetTracks(It.IsAny<IEnumerable<int>>())).Returns(new List<Track> { _track });
            Mocker.GetMock<IDiskProvider>().Setup(d => d.FolderExists("/download")).Returns(true);
            Mocker.GetMock<IRootFolderService>().Setup(s => s.GetBestRootFolder(artist.Path)).Returns(new RootFolder());
            Mocker.GetMock<IImportApprovedTracks>().Setup(s => s.Import(It.IsAny<List<ImportDecision<LocalTrack>>>(), false,
                It.IsAny<DownloadClientItem>(), ImportMode.Copy)).Callback<List<ImportDecision<LocalTrack>>, bool, DownloadClientItem, ImportMode>((d, r, i, m) => _decisions = d)
                .Returns(new List<ImportResult>());
            Files("Wanted", "Extra");
        }

        private void Files(params string[] titles)
        {
            var files = new List<IFileInfo>();
            for (var i = 0; i < titles.Length; i++)
            {
                var path = "/download/" + i + ".flac";
                var info = new Mock<IFileInfo>();
                info.SetupGet(f => f.FullName).Returns(path);
                info.SetupGet(f => f.Length).Returns(1000);
                info.SetupGet(f => f.LastWriteTimeUtc).Returns(DateTime.UtcNow);
                files.Add(info.Object);
                Mocker.GetMock<IDiskProvider>().Setup(d => d.GetFileInfo(path)).Returns(info.Object);
                Mocker.GetMock<IAudioTagService>().Setup(t => t.ReadTags(path)).Returns(new ParsedTrackInfo
                {
                    Title = titles[i], ArtistTitle = "Artist", AlbumTitle = "Album", Quality = new QualityModel(Quality.FLAC)
                });
            }

            Mocker.GetMock<IDiskScanService>().Setup(s => s.GetAudioFiles("/download", true)).Returns(files.ToArray());
        }

        [Test]
        public void imports_only_requested_song_without_replacing_album_or_moving_source()
        {
            Subject.Import("/download", new List<int> { 10 });
            Assert.That(_decisions.Count, Is.EqualTo(1));
            Assert.That(_decisions.Single().Item.Tracks.Single().Id, Is.EqualTo(10));
            Mocker.GetMock<IImportApprovedTracks>().Verify(s => s.Import(It.IsAny<List<ImportDecision<LocalTrack>>>(),
                false, null, ImportMode.Copy), Times.Once());
        }

        [Test]
        public void ambiguous_duplicates_are_not_imported()
        {
            Files("Wanted", "Wanted");
            Subject.Import("/download", new List<int> { 10 });
            Assert.That(_decisions, Is.Empty);
        }

        [Test]
        public void missing_song_is_not_replaced_with_an_extra()
        {
            Files("Extra", "Another Extra");
            Subject.Import("/download", new List<int> { 10 });
            Assert.That(_decisions, Is.Empty);
        }

        [Test]
        public void existing_song_is_not_overwritten()
        {
            _track.TrackFileId = 99;
            Subject.Import("/download", new List<int> { 10 });
            Assert.That(_decisions, Is.Empty);
        }

        [Test]
        public void rejects_removed_request_identity()
        {
            Assert.Throws<InvalidOperationException>(() => Subject.Import("/download", new List<int> { 10, 11 }));
        }
    }
}
