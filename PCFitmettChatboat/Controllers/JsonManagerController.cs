using Microsoft.AspNetCore.Mvc;
using PCFitmettChatboat.Models;
using System.Text.Json;

namespace PCFitmettChatboat.Controllers
{
    public class JsonManagerController : Controller
    {
        private readonly string _dataFolder;
        private readonly string _pdfFolder;

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

            _pdfFolder = configuration["DataSettings:PdfFolder"]
                ?? throw new InvalidOperationException(
                    "PdfFolder is missing from appsettings.json.");

            Directory.CreateDirectory(_dataFolder);
            Directory.CreateDirectory(_pdfFolder);
        }

        public IActionResult Index() => View();

        private bool TryGetFilePath(string? source, out string path)
        {
            string fileName = source?.ToLowerInvariant() switch
            {
                "articles" => "articles.json",
                "pdf" => "pdfs.json",
                _ => string.Empty
            };

            path = string.IsNullOrEmpty(fileName)
                ? string.Empty
                : Path.Combine(_dataFolder, fileName);

            return !string.IsNullOrEmpty(fileName);
        }

        private async Task<List<PdfRecord>> ReadPdfsAsync()
        {
            string path = Path.Combine(_dataFolder, "pdfs.json");

            if (!System.IO.File.Exists(path))
                return new List<PdfRecord>();

            string json = await System.IO.File.ReadAllTextAsync(path);

            return JsonSerializer.Deserialize<List<PdfRecord>>(
                json, JsonOptions) ?? new List<PdfRecord>();
        }

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

                if (source.Equals("pdf", StringComparison.OrdinalIgnoreCase))
                {
                    var pdfs = JsonSerializer.Deserialize<List<PdfRecord>>(
                        json, JsonOptions) ?? new List<PdfRecord>();

                    return Ok(new
                    {
                        records = pdfs,
                        fileName = Path.GetFileName(path),
                        filePath = path
                    });
                }

                var articles = JsonSerializer.Deserialize<List<JsonRecord>>(
                    json, JsonOptions) ?? new List<JsonRecord>();

                return Ok(new
                {
                    records = articles,
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

        [HttpPost]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> Save(
            string source,
            [FromBody] JsonElement records)
        {
            if (!TryGetFilePath(source, out string path))
                return BadRequest(new { message = "Invalid source." });

            if (records.ValueKind != JsonValueKind.Array)
                return BadRequest(new { message = "Records must be a JSON array." });

            await FileLock.WaitAsync();

            string tempPath = path + ".tmp";

            try
            {
                string json;

                if (source.Equals("pdf", StringComparison.OrdinalIgnoreCase))
                {
                    var pdfs = records.Deserialize<List<PdfRecord>>(JsonOptions)
                               ?? new List<PdfRecord>();

                    if (pdfs.Any(x => x.Id < 1) ||
                        pdfs.Select(x => x.Id).Distinct().Count() != pdfs.Count)
                    {
                        return BadRequest(new
                        {
                            message = "PDF IDs must be positive and unique."
                        });
                    }

                    foreach (var pdf in pdfs)
                    {
                        if (!IsSafePdfFileName(pdf.FileName) ||
                            !IsSafePdfFileName(pdf.PreviousFileName, allowEmpty: true))
                        {
                            return BadRequest(new
                            {
                                message = $"Invalid PDF filename for ID {pdf.Id}."
                            });
                        }
                    }

                    json = JsonSerializer.Serialize(pdfs, JsonOptions);
                }
                else if (source.Equals("articles", StringComparison.OrdinalIgnoreCase))
                {
                    var articles = records.Deserialize<List<JsonRecord>>(JsonOptions)
                                   ?? new List<JsonRecord>();

                    if (articles.Any(x => x.Id < 1) ||
                        articles.Select(x => x.Id).Distinct().Count() != articles.Count)
                    {
                        return BadRequest(new
                        {
                            message = "Article IDs must be positive and unique."
                        });
                    }

                    foreach (var article in articles)
                    {
                        article.CurrentUrl ??= string.Empty;
                        article.PreviousUrl ??= string.Empty;

                        if (!IsValidWebUrl(article.CurrentUrl) ||
                            !IsValidWebUrl(article.PreviousUrl))
                        {
                            return BadRequest(new
                            {
                                message = $"Invalid HTTP/HTTPS URL for ID {article.Id}."
                            });
                        }
                    }

                    json = JsonSerializer.Serialize(articles, JsonOptions);
                }
                else
                {
                    return BadRequest(new { message = "Invalid source." });
                }

                await System.IO.File.WriteAllTextAsync(tempPath, json);
                System.IO.File.Move(tempPath, path, true);

                return Ok(new { message = $"Saved {Path.GetFileName(path)} successfully." });
            }
            catch (JsonException ex)
            {
                return BadRequest(new { message = "Invalid JSON: " + ex.Message });
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
                    try { System.IO.File.Delete(tempPath); }
                    catch (IOException) { }
                }

                FileLock.Release();
            }
        }

        // Upload a new PDF or replace an existing PDF by supplying its ID.
        [HttpPost]
        [IgnoreAntiforgeryToken]
        [RequestSizeLimit(25 * 1024 * 1024)]
        public async Task<IActionResult> UploadPdf(IFormFile? file, int? id = null)
        {
            if (file == null || file.Length == 0)
                return BadRequest(new { message = "Please select a PDF file." });

            if (file.Length > 25 * 1024 * 1024)
                return BadRequest(new { message = "Maximum PDF size is 25 MB." });

            if (!string.Equals(
                    Path.GetExtension(file.FileName), ".pdf",
                    StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest(new { message = "Only PDF files are allowed." });
            }

            // Check the PDF signature as well as the extension.
            byte[] signature = new byte[5];

            await using (var input = file.OpenReadStream())
            {
                int read = await input.ReadAsync(signature.AsMemory(0, 5));

                if (read != 5 ||
                    System.Text.Encoding.ASCII.GetString(signature) != "%PDF-")
                {
                    return BadRequest(new
                    {
                        message = "The uploaded file is not a valid PDF."
                    });
                }
            }

            string originalName = Path.GetFileName(file.FileName);

            // Keep the original filename, but remove characters that are
            // unsafe or invalid for a stored filename.
            string safeName = string.Concat(
                originalName.Where(c =>
                    char.IsLetterOrDigit(c) ||
                    c == '.' || c == '-' || c == '_' || c == ' '));

            if (!IsSafePdfFileName(safeName))
                return BadRequest(new { message = "Invalid PDF filename." });

            await FileLock.WaitAsync();

            try
            {
                var pdfs = await ReadPdfsAsync();
                PdfRecord? existing = id.HasValue
                    ? pdfs.FirstOrDefault(x => x.Id == id.Value)
                    : null;

                if (id.HasValue && existing == null)
                    return NotFound(new { message = "PDF record not found." });

                string targetName = existing == null
                    ? safeName
                    : existing.FileName;

                // Avoid overwriting another PDF when adding a new record.
                if (existing == null &&
                    pdfs.Any(x => !x.Delete &&
                        x.FileName.Equals(targetName,
                            StringComparison.OrdinalIgnoreCase)))
                {
                    return BadRequest(new
                    {
                        message = "A PDF with this filename already exists. Rename the file first."
                    });
                }

                string targetPath = Path.Combine(_pdfFolder, targetName);

                if (existing != null)
                {
                    // Preserve the old PDF under a unique archived filename.
                    string archiveName = $"{Guid.NewGuid():N}_{existing.FileName}";
                    string archivePath = Path.Combine(_pdfFolder, archiveName);

                    if (System.IO.File.Exists(targetPath))
                        System.IO.File.Move(targetPath, archivePath);

                    existing.PreviousFileName = archiveName;
                    existing.Flagupdate = true;
                    existing.Delete = false;
                }

                try
                {
                    await using var output = new FileStream(
                        targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);

                    await file.CopyToAsync(output);
                }
                catch
                {
                    // If replacing a PDF fails, restore its archived file.
                    if (existing != null)
                    {
                        string archivePath = Path.Combine(
                            _pdfFolder, existing.PreviousFileName);

                        if (System.IO.File.Exists(archivePath))
                            System.IO.File.Move(archivePath, targetPath);

                        existing.PreviousFileName = string.Empty;
                        existing.Flagupdate = false;
                    }

                    throw;
                }

                if (existing == null)
                {
                    int nextId = pdfs.Count == 0
                        ? 1
                        : pdfs.Max(x => x.Id) + 1;

                    pdfs.Add(new PdfRecord
                    {
                        Id = nextId,
                        FileName = safeName,
                        PreviousFileName = string.Empty,
                        Flagupdate = false,
                        Delete = false
                    });
                }

                string json = JsonSerializer.Serialize(pdfs, JsonOptions);
                string jsonPath = Path.Combine(_dataFolder, "pdfs.json");
                string tempPath = jsonPath + ".tmp";

                await System.IO.File.WriteAllTextAsync(tempPath, json);
                System.IO.File.Move(tempPath, jsonPath, true);

                return Ok(new
                {
                    message = existing == null
                        ? "PDF uploaded successfully."
                        : "PDF replaced successfully.",
                    fileName = targetName
                });
            }
            catch (IOException ex)
            {
                return StatusCode(500, new
                {
                    message = "Could not upload or save the PDF: " + ex.Message
                });
            }
            finally
            {
                FileLock.Release();
            }
        }

        // Open the PDF in the browser. The ID must refer to an active record.
        [HttpGet]
        public async Task<IActionResult> OpenPdf(int id)
        {
            var pdfs = await ReadPdfsAsync();

            var pdf = pdfs.FirstOrDefault(x => x.Id == id && !x.Delete);

            if (pdf == null || !IsSafePdfFileName(pdf.FileName))
                return NotFound("PDF not found.");

            string path = Path.Combine(_pdfFolder, pdf.FileName);

            if (!System.IO.File.Exists(path))
                return NotFound("The PDF file does not exist on the server.");

            Response.Headers["Content-Disposition"] =
                $"inline; filename=\"{pdf.FileName}\"";

            return PhysicalFile(path, "application/pdf", enableRangeProcessing: true);
        }

        private static bool IsSafePdfFileName(
            string? fileName, bool allowEmpty = false)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return allowEmpty;

            return fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(fileName) == fileName
                && !fileName.Contains("..")
                && fileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
        }

        private static bool IsValidWebUrl(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return true;

            return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
                && (uri.Scheme == Uri.UriSchemeHttp ||
                    uri.Scheme == Uri.UriSchemeHttps);
        }
    }
}