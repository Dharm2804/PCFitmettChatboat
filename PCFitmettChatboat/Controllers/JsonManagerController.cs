using Microsoft.AspNetCore.Mvc;
using PCFitmettChatboat.Models;
using System.Text.Json;

namespace PCFitmettChatboat.Controllers
{
    public class JsonManagerController : Controller
    {

        private readonly string _dataFolder;

        private static readonly SemaphoreSlim FileLock = new(1, 1);

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };

        public JsonManagerController(IConfiguration configuration)
        {
            _dataFolder = configuration["DataSettings:DataFolder"]
                ?? throw new InvalidOperationException(
                    "DataFolder is missing from appsettings.json.");
        }


        public IActionResult Index()
        {
            return View();
        }

        
        // GET: /JsonManager/Load?source=articles
        [HttpGet]
        public async Task<IActionResult> Load(string source = "articles")
        {
            if (!TryGetFilePath(source, out string path))
            {
                return BadRequest(new
                {
                    message = "Invalid source. Use articles or pdf."
                });
            }

            try
            {
                if (!System.IO.File.Exists(path))
                {
                    return NotFound(new
                    {
                        message = $"File not found: {path}"
                    });
                }

                string json = await System.IO.File.ReadAllTextAsync(path);

                var records =
                    JsonSerializer.Deserialize<List<JsonRecord>>(
                        json,
                        JsonOptions
                    ) ?? new List<JsonRecord>();

                return Ok(new
                {
                    records = records,
                    fileName = Path.GetFileName(path),
                    filePath = path
                });
            }
            catch (JsonException ex)
            {
                return BadRequest(new
                {
                    message = "Invalid JSON format: " + ex.Message
                });
            }
            catch (IOException ex)
            {
                return StatusCode(500, new
                {
                    message = "Could not read JSON file: " + ex.Message
                });
            }
        }


        // POST: /JsonManager/Save?source=articles
        [HttpPost]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> Save(
            string source,
            [FromBody] List<JsonRecord>? records)
        {
            if (!TryGetFilePath(source, out string path))
            {
                return BadRequest(new
                {
                    message = "Invalid source. Use articles or pdf."
                });
            }

            if (records == null)
            {
                return BadRequest(new
                {
                    message = "No JSON records were supplied."
                });
            }

            // Validate positive, unique IDs.
            if (records.Any(x => x.Id < 1) ||
                records.Select(x => x.Id).Distinct().Count() != records.Count)
            {
                return BadRequest(new
                {
                    message = "Record IDs must be positive and unique."
                });
            }

            // Validate URL fields.
            foreach (var record in records)
            {
                record.CurrentUrl ??= string.Empty;
                record.PreviousUrl ??= string.Empty;

                if (!IsValidWebUrl(record.CurrentUrl))
                {
                    return BadRequest(new
                    {
                        message = $"Invalid HTTP/HTTPS URL for ID {record.Id}."
                    });
                }

                if (!IsValidWebUrl(record.PreviousUrl))
                {
                    return BadRequest(new
                    {
                        message = $"Invalid previous URL for ID {record.Id}."
                    });
                }
            }

            await FileLock.WaitAsync();

            string tempPath = path + ".tmp";

            try
            {
                string json = JsonSerializer.Serialize(records, JsonOptions);

                await System.IO.File.WriteAllTextAsync(tempPath, json);

                // Replace the destination file after the temporary file is ready.
                System.IO.File.Move(tempPath, path, true);

                return Ok(new
                {
                    message = $"Saved {records.Count} records to {Path.GetFileName(path)}."
                });
            }
            catch (IOException ex)
            {
                return StatusCode(500, new
                {
                    message = "Could not save JSON file: " + ex.Message
                });
            }
            finally
            {
                if (System.IO.File.Exists(tempPath))
                {
                    try
                    {
                        System.IO.File.Delete(tempPath);
                    }
                    catch (IOException)
                    {
                        // Ignore temporary-file cleanup errors.
                    }
                }

                FileLock.Release();
            }
        }

        private bool TryGetFilePath(string? source, out string path)
        {
            string fileName = source?.ToLowerInvariant() switch
            {
                "articles" => "articles.json",
                "pdf" => "pdf.json",
                _ => string.Empty
            };

            path = string.IsNullOrEmpty(fileName)
                ? string.Empty
                : Path.Combine(_dataFolder, fileName);

            return !string.IsNullOrEmpty(fileName);
        }

        private static bool IsValidWebUrl(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return true;
            }

            return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
                && (uri.Scheme == Uri.UriSchemeHttp
                    || uri.Scheme == Uri.UriSchemeHttps);
        }
    }
}
