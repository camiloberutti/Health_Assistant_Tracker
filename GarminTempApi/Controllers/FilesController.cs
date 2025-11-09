using Microsoft.AspNetCore.Mvc;
using System.IO;
using System.Threading.Tasks;
using GarminTempApi.Services;
using System.Linq;

namespace GarminTempApi.Controllers
{
    [ApiController]
    [Route("api/files")]
    public class FilesController : ControllerBase
    {
        private readonly IActivityParser _xmlParser;
        private readonly FitActivityParser _fitParser;
        private readonly ActivityService _svc;

        public FilesController(IActivityParser xmlParser, FitActivityParser fitParser, ActivityService svc)
        {
            _xmlParser = xmlParser;
            _fitParser = fitParser;
            _svc = svc;
        }

        [HttpPost("upload")]
        public async Task<IActionResult> Upload([FromForm] Microsoft.AspNetCore.Http.IFormFile file)
        {
            if (file == null) return BadRequest("No file uploaded.");

            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            ms.Position = 0;

            var activities = ext switch
            {
                ".gpx" or ".tcx" or ".xml" => await _xmlParser.ParseAsync(ms, file.FileName),
                ".fit" => await _fitParser.ParseAsync(ms, file.FileName),
                _ => new System.Collections.Generic.List<Models.Activity>()
            };

            if (activities == null || !activities.Any())
                return BadRequest("No activities parsed from file.");

            foreach (var a in activities)
                await _svc.SaveActivityAsync(a);

            return Ok(new { imported = activities.Count, file = file.FileName });
        }
    }
}
