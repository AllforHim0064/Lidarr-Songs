using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Http;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Music.TrackDownloads;

namespace Lidarr.Api.V1.Tracks
{
    [V1ApiController("trackdownload")]
    public class TrackDownloadController : Controller
    {
        private readonly ITrackDownloadService _downloads;
        private readonly ISoulseekTrackService _soulseek;

        public TrackDownloadController(ITrackDownloadService downloads, ISoulseekTrackService soulseek)
        {
            _downloads = downloads;
            _soulseek = soulseek;
        }

        [HttpGet("search")]
        public async Task<ActionResult<TrackSearchResult>> Search(int trackId, bool albumFallback = true, CancellationToken cancellationToken = default)
        {
            if (trackId <= 0)
            {
                return BadRequest("A positive trackId is required.");
            }

            return await _downloads.Search(trackId, albumFallback, cancellationToken);
        }

        [HttpPost("grab")]
        public async Task<IActionResult> Grab([FromBody] TrackGrabResource resource)
        {
            if (resource == null || resource.TrackId <= 0 || string.IsNullOrWhiteSpace(resource.CandidateId))
            {
                return BadRequest("trackId and candidateId are required.");
            }

            try
            {
                await _downloads.Grab(resource.CandidateId, resource.TrackId);
                return Accepted(new { status = "queued" });
            }
            catch (InvalidOperationException ex)
            {
                return Conflict(ex.Message);
            }
        }

        [HttpGet("soulseek")]
        public List<SoulseekTrackJob> SoulseekJobs()
        {
            return _soulseek.Jobs();
        }
    }

    public class TrackGrabResource
    {
        public int TrackId { get; set; }
        public string CandidateId { get; set; }
    }
}
