using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SoundBox.Models;

namespace SoundBox.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class SoundBoxController : ControllerBase
    {
        private static readonly ConcurrentDictionary<string, List<Format>> FormatsCache = new();

        [HttpPost("get-formats")]
        public async Task<ActionResult> GetFormats([FromBody] DownloadRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Url))
                return BadRequest(new { message = "URL is required" });

            if (FormatsCache.TryGetValue(request.Url, out var cachedFormats))
                return Ok(cachedFormats);

            var startInfo = new ProcessStartInfo
            {
                FileName = "yt-dlp",
                Arguments = $"-F {request.Url}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            string output = await process.StandardOutput.ReadToEndAsync();
            string error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
                return BadRequest(new { message = "yt-dlp failed", error });

            var formats = ParseFormats(output);
            FormatsCache[request.Url] = formats;
            return Ok(formats);
        }



        [HttpGet("download")]
        public async Task<IActionResult> Download(string url, string formatId, bool audioOnly = false)
        {
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(formatId))
                return BadRequest(new { message = "URL and formatId are required" });

            // Get proper filename from yt-dlp
            var fileName = await GetFileName(url, formatId, audioOnly);

            string args = audioOnly
                ? $"-f {formatId} -o - {url}"
                : $"-f {formatId}+bestaudio -o - {url}";

            var startInfo = new ProcessStartInfo
            {
                FileName = "yt-dlp",
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var process = new Process { StartInfo = startInfo };

            try
            {
                process.Start();

                // log stderr to console
                _ = Task.Run(async () =>
                {
                    string? line;
                    while ((line = await process.StandardError.ReadLineAsync()) != null)
                    {
                        Console.WriteLine($"yt-dlp: {line}");
                    }
                });

                var contentType = audioOnly ? "audio/mpeg" : "video/mp4";

                HttpContext.RequestAborted.Register(() =>
                {
                    try { if (!process.HasExited) process.Kill(); } catch { }
                });

                return new FileStreamResult(process.StandardOutput.BaseStream, contentType)
                {
                    FileDownloadName = fileName
                };
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Download failed", error = ex.Message });
            }
        }

        private async Task<string> GetFileName(string url, string formatId, bool audioOnly)
        {
            var ext = audioOnly ? "mp3" : "mp4";

            var startInfo = new ProcessStartInfo
            {
                FileName = "yt-dlp",
                Arguments = $"-f {formatId} --print \"%(title)s.{ext}\" {url}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var process = new Process { StartInfo = startInfo };
            process.Start();

            string output = (await process.StandardOutput.ReadToEndAsync()).Trim();
            await process.WaitForExitAsync();

            if (string.IsNullOrWhiteSpace(output))
                return $"download.{ext}";

            // Remove invalid chars for filenames
            foreach (var c in Path.GetInvalidFileNameChars())
                output = output.Replace(c, '_');

            return output;
        }


        private List<Format> ParseFormats(string ytDlpOutput)
        {
            var formats = new List<Format>();
            var lines = ytDlpOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                var match = Regex.Match(line, @"^(?<id>\d+)\s+(?<ext>\w+)\s+(?<res>[\w\d+x]+)");
                if (match.Success)
                {
                    formats.Add(new Format
                    {
                        FormatId = match.Groups["id"].Value,
                        Extension = match.Groups["ext"].Value,
                        Resolution = match.Groups["res"].Value
                    });
                }
            }

            return formats;
        }

        [HttpPost("convert-to-audio")]
        public async Task<IActionResult> ConvertToAudio(IFormFile videoFile, [FromForm] string bitrate = "192k")
        {
            if (videoFile == null || videoFile.Length == 0)
                return BadRequest(new { message = "No video uploaded" });

            var tempFolder = Path.Combine(Path.GetTempPath(), "SoundBoxUploads", Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempFolder);

            var videoPath = Path.Combine(tempFolder, videoFile.FileName);
            await using (var stream = new FileStream(videoPath, FileMode.Create))
            {
                await videoFile.CopyToAsync(stream);
            }

            var outputPath = Path.Combine(tempFolder, Path.GetFileNameWithoutExtension(videoFile.FileName) + ".mp3");

            var startInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = $"-i \"{videoPath}\" -b:a {bitrate} -vn \"{outputPath}\" -y",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            try
            {
                using (var process = Process.Start(startInfo))
                {
                    if (process == null)
                        return StatusCode(500, new { message = "Could not start ffmpeg" });

                    await process.WaitForExitAsync();

                    if (!System.IO.File.Exists(outputPath))
                        return StatusCode(500, new { message = "Conversion failed, no output file created" });

                    var outputName = Path.GetFileName(outputPath);
                    var stream = new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.Read);

                    return File(stream, "audio/mpeg", outputName);
                }
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Conversion failed", error = ex.Message });
            }
            finally
            {
                _ = Task.Run(() =>
                {
                    try { if (Directory.Exists(tempFolder)) Directory.Delete(tempFolder, true); } catch { }
                });
            }
        }

    }
}
