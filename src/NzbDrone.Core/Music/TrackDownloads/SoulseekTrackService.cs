using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.MediaFiles.TrackImport;
using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.Music.TrackDownloads
{
    public class SoulseekFile
    {
        public string Username { get; set; }
        public string Filename { get; set; }
        public long Size { get; set; }
    }

    public class SoulseekTrackJob
    {
        public string Id { get; set; }
        public int TrackId { get; set; }
        public string ForeignTrackId { get; set; }
        public string Username { get; set; }
        public string Filename { get; set; }
        public long Size { get; set; }
        public string Status { get; set; }
        public string Error { get; set; }
    }

    public class ProcessSoulseekTracksCommand : Command
    {
        public override bool RequiresDiskAccess => true;
    }

    public interface ISoulseekTrackService
    {
        bool Configured { get; }
        Task<List<SoulseekFile>> Search(Track track, CancellationToken cancellationToken);
        Task Grab(Track track, SoulseekFile file);
        List<SoulseekTrackJob> Jobs();
    }

    public class SoulseekTrackService : ISoulseekTrackService, IExecute<ProcessSoulseekTracksCommand>
    {
        private readonly ISelectedTrackImportService _import;
        private readonly ITrackService _tracks;
        private readonly string _jobPath;
        private readonly string _completedRoot;
        private readonly string _url;
        private readonly string _key;
        private readonly SemaphoreSlim _jobLock = new SemaphoreSlim(1, 1);
        private readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(40) };

        public SoulseekTrackService(ISelectedTrackImportService import, ITrackService tracks, IAppFolderInfo folders)
        {
            _import = import;
            _tracks = tracks;
            _jobPath = Path.Combine(folders.AppDataFolder, "soulseek-track-jobs.json");
            _url = Environment.GetEnvironmentVariable("LIDARR_SLSKD_URL")?.TrimEnd('/');
            _key = Environment.GetEnvironmentVariable("LIDARR_SLSKD_API_KEY");
            _completedRoot = Environment.GetEnvironmentVariable("LIDARR_SLSKD_DOWNLOAD_ROOT");
        }

        public bool Configured => !string.IsNullOrWhiteSpace(_url) && !string.IsNullOrWhiteSpace(_key) && !string.IsNullOrWhiteSpace(_completedRoot);

        private async Task<JsonElement> Request(HttpMethod method, string route, object body = null, CancellationToken cancellationToken = default)
        {
            using var request = new HttpRequestMessage(method, _url + "/api/v0/" + route);
            request.Headers.Add("X-API-Key", _key);
            if (body != null)
            {
                request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            }

            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException("slskd returned HTTP " + (int)response.StatusCode);
            }

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return json.RootElement.Clone();
        }

        public async Task<List<SoulseekFile>> Search(Track track, CancellationToken cancellationToken)
        {
            var search = await Request(HttpMethod.Post, "searches", new
            {
                searchText = track.Artist.Value.Name + " " + track.Title,
                searchTimeout = 5,
                responseLimit = 100,
                fileLimit = 1000
            }, cancellationToken);
            var id = search.GetProperty("id").GetString();
            // A bounded wait: slskd searches continue asynchronously after POST returns.
            await Task.Delay(TimeSpan.FromSeconds(7), cancellationToken);
            var responses = await Request(HttpMethod.Get, "searches/" + id + "/responses", cancellationToken: cancellationToken);
            var results = new List<SoulseekFile>();
            foreach (var response in responses.EnumerateArray())
            {
                foreach (var file in response.GetProperty("files").EnumerateArray())
                {
                    var filename = file.GetProperty("filename").GetString();
                    if (TrackMatch.IsAudio(filename) && TrackMatch.ContainsTitle(filename, track.Title) &&
                        TrackMatch.ContainsTitle(filename, track.Artist.Value.Name) &&
                        !filename.Replace('\\', '/').Split('/').Any(s => s == ".."))
                    {
                        results.Add(new SoulseekFile
                        {
                            Username = response.GetProperty("username").GetString(),
                            Filename = filename,
                            Size = file.GetProperty("size").GetInt64()
                        });
                    }
                }
            }

            return results.OrderBy(f => f.Size).Take(100).ToList();
        }

        public List<SoulseekTrackJob> Jobs()
        {
            _jobLock.Wait();
            try
            {
                return Load();
            }
            finally
            {
                _jobLock.Release();
            }
        }

        private List<SoulseekTrackJob> Load()
        {
            return File.Exists(_jobPath) ? JsonSerializer.Deserialize<List<SoulseekTrackJob>>(File.ReadAllText(_jobPath)) : new List<SoulseekTrackJob>();
        }

        private void Save(List<SoulseekTrackJob> jobs)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_jobPath));
            File.WriteAllText(_jobPath + ".tmp", JsonSerializer.Serialize(jobs));
            File.Move(_jobPath + ".tmp", _jobPath, true);
        }

        public async Task Grab(Track track, SoulseekFile file)
        {
            await _jobLock.WaitAsync();
            try
            {
                var jobs = Load();
                if (jobs.Any(j => j.TrackId == track.Id && j.Status != "Imported" && j.Status != "Failed"))
                {
                    throw new InvalidOperationException("This song already has an active Soulseek download.");
                }

                var job = new SoulseekTrackJob
                {
                    Id = Guid.NewGuid().ToString(),
                    TrackId = track.Id,
                    ForeignTrackId = track.ForeignTrackId,
                    Username = file.Username,
                    Filename = file.Filename,
                    Size = file.Size,
                    Status = "Queued"
                };
                // Save before contacting slskd so a crash cannot lose the requested identity.
                jobs.Add(job);
                Save(jobs);
                try
                {
                    var response = await Request(HttpMethod.Post, "transfers/downloads/batches", new
                    {
                        id = job.Id,
                        username = file.Username,
                        files = new[] { new { filename = file.Filename, size = file.Size } },
                        options = new { destination = "lidarr-tracks/" + job.Id, externalId = "lidarr-track-" + track.Id }
                    });
                    if (response.GetProperty("failures").GetArrayLength() > 0)
                    {
                        throw new InvalidOperationException("slskd rejected this file. Check its transfer queue.");
                    }
                }
                catch
                {
                    job.Status = "Failed";
                    job.Error = "Queue request failed. Check slskd before retrying.";
                    Save(jobs);
                    throw;
                }
            }
            finally
            {
                _jobLock.Release();
            }
        }

        public void Execute(ProcessSoulseekTracksCommand message)
        {
            if (!Configured)
            {
                return;
            }

            _jobLock.Wait();
            try
            {
                var jobs = Load();
                foreach (var job in jobs.Where(j => j.Status == "Queued" || j.Status == "ImportBlocked"))
                {
                    try
                    {
                        var batch = Request(HttpMethod.Get, "transfers/downloads/batches/" + job.Id).GetAwaiter().GetResult();
                        var transfers = batch.GetProperty("transfers").EnumerateArray().ToList();
                        if (transfers.Count != 1 || !transfers.Single().GetProperty("state").GetString().Contains("Succeeded", StringComparison.OrdinalIgnoreCase))
                        {
                            job.Error = "Waiting for a successful slskd transfer. Failed transfers can be retried in slskd.";
                            continue;
                        }

                        var track = _tracks.GetTrack(job.TrackId);
                        if (track.ForeignTrackId != job.ForeignTrackId)
                        {
                            throw new InvalidOperationException("Track identity changed. Search again before importing.");
                        }

                        var folder = Path.Combine(_completedRoot, "lidarr-tracks", job.Id);
                        var results = _import.Import(folder, new List<int> { job.TrackId });
                        if (!results.Any(r => r.Result == ImportResultType.Imported))
                        {
                            job.Status = "ImportBlocked";
                            job.Error = "No unambiguous matching audio file was imported. Check tags and the shared download path.";
                            continue;
                        }

                        // The only folder removed is the dedicated UUID folder created for this job.
                        job.Status = "Imported";
                        job.Error = null;
                        Save(jobs);
                        try
                        {
                            Directory.Delete(folder, true);
                        }
                        catch (IOException)
                        {
                            job.Error = "Song imported successfully; its download folder needs manual cleanup.";
                        }
                        catch (UnauthorizedAccessException)
                        {
                            job.Error = "Song imported successfully; its download folder needs manual cleanup.";
                        }
                    }
                    catch (Exception ex)
                    {
                        job.Status = "ImportBlocked";
                        job.Error = ex is InvalidOperationException ? ex.Message : "Transfer or import failed. Check shared paths and slskd.";
                    }
                }

                Save(jobs);
            }
            finally
            {
                _jobLock.Release();
            }
        }
    }
}
