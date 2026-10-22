using Azure;
using Microsoft.AspNetCore.Mvc;
using MIA_Grupo4_Blob_Avance.Services;

namespace MIA_Grupo4_Blob_Avance.Controllers;

[ApiController]
[Route("api/files")]
public class FilesController : ControllerBase
{
    private readonly BlobStorageService _storage;
    private readonly ILogger<FilesController> _logger;

    public FilesController(BlobStorageService storage, ILogger<FilesController> logger)
    {
        _storage = storage;
        _logger = logger;
    }

    [HttpPost]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> Subir(IFormFile file)
    {
        if (file == null || file.Length == 0) return BadRequest(new { mensaje = "Selecciona un archivo no vacio." });
        if (file.Length > 10 * 1024 * 1024) return BadRequest(new { mensaje = "El archivo excede 10 MB." });
        try
        {
            var resultado = await _storage.SubirAsync(file);
            return CreatedAtAction(nameof(Consultar), new { id = resultado.Id }, resultado);
        }
        catch (RequestFailedException) { return StatusCode(503, new { mensaje = "No se pudo acceder a Azure Storage." }); }
        catch (Exception) { return StatusCode(500, new { mensaje = "Error al subir archivo." }); }
    }

    [HttpGet]
    public async Task<IActionResult> Listar()
    {
        try { return Ok(await _storage.ListarAsync()); }
        catch (Exception ex) { _logger.LogError("Operacion=LISTAR_API Resultado=ERROR TipoError={TipoError}", ex.GetType().Name); return StatusCode(503, new { mensaje = "No se pudo consultar el almacenamiento." }); }
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Consultar(string id)
    {
        try
        {
            var archivo = await _storage.ConsultarAsync(id);
            return archivo == null ? NotFound(new { mensaje = "Archivo no encontrado." }) : Ok(archivo);
        }
        catch (Exception) { return StatusCode(503, new { mensaje = "No se pudo consultar el archivo." }); }
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Eliminar(string id)
    {
        try
        {
            bool eliminado = await _storage.EliminarAsync(id);
            return eliminado ? Ok(new { mensaje = "Archivo eliminado del contenedor principal.", id }) : NotFound(new { mensaje = "Archivo no encontrado." });
        }
        catch (Exception) { return StatusCode(503, new { mensaje = "No se pudo eliminar el archivo." }); }
    }
}
