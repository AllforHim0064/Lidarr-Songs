using System;
using NUnit.Framework;
using NzbDrone.Core.Music;
using NzbDrone.Core.Music.TrackDownloads;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Test.MusicTests.TrackDownloads
{
    [TestFixture]
    public class TrackMatchFixture
    {
        private Track _track;
        private ParsedTrackInfo _tags;

        [SetUp]
        public void Setup()
        {
            _track = new Track
            {
                Title = "Wanted Song",
                Duration = 180000,
                ForeignTrackId = "track-one",
                ForeignRecordingId = "recording-one",
                Artist = new Artist { Name = "Example Artist" },
                AlbumRelease = new AlbumRelease { Album = new Album { Title = "Example Album" } }
            };
            _tags = new ParsedTrackInfo
            {
                Title = "Wanted Song",
                ArtistTitle = "Example Artist",
                AlbumTitle = "Example Album",
                Duration = TimeSpan.FromSeconds(180)
            };
        }

        [Test]
        public void matches_tagged_song()
        {
            Assert.That(TrackMatch.Matches(_track, _tags, "/downloads/file.flac"), Is.True);
        }

        [TestCase("Wanted Song (Live)")]
        [TestCase("Wanted Song Remix")]
        [TestCase("An Unwanted Song")]
        public void rejects_different_versions_and_titles(string title)
        {
            _tags.Title = title;
            Assert.That(TrackMatch.Matches(_track, _tags, "/downloads/file.flac"), Is.False);
        }

        [Test]
        public void rejects_other_artist()
        {
            _tags.ArtistTitle = "Someone Else";
            Assert.That(TrackMatch.Matches(_track, _tags, "/downloads/file.flac"), Is.False);
        }

        [Test]
        public void rejects_wrong_recording_even_when_title_matches()
        {
            _tags.RecordingMBId = "another-recording";
            Assert.That(TrackMatch.Matches(_track, _tags, "/downloads/file.flac"), Is.False);
        }

        [Test]
        public void rejects_duration_mismatch()
        {
            _tags.Duration = TimeSpan.FromSeconds(240);
            Assert.That(TrackMatch.Matches(_track, _tags, "/downloads/file.flac"), Is.False);
        }

        [Test]
        public void recording_id_allows_compilation_album()
        {
            _tags.RecordingMBId = "recording-one";
            _tags.AlbumTitle = "A Compilation";
            Assert.That(TrackMatch.Matches(_track, _tags, "/downloads/file.flac"), Is.True);
        }

        [Test]
        public void rejects_conflicting_track_id()
        {
            _tags.TrackMBId = "another-track";
            Assert.That(TrackMatch.Matches(_track, _tags, "/downloads/file.flac"), Is.False);
        }

        [Test]
        public void filename_fallback_requires_an_exact_version()
        {
            _tags.Title = null;
            Assert.That(TrackMatch.Matches(_track, _tags, "/downloads/01 Wanted Song.flac"), Is.True);
            Assert.That(TrackMatch.Matches(_track, _tags, "/downloads/01 Wanted Song Live.flac"), Is.False);
        }

        [TestCase("Unwanted", "Wanted", false)]
        [TestCase("Artist - Wanted [FLAC]", "Wanted", true)]
        [TestCase("Anything", "", false)]
        public void search_matches_whole_words(string text, string title, bool expected)
        {
            Assert.That(TrackMatch.ContainsTitle(text, title), Is.EqualTo(expected));
        }
    }
}
