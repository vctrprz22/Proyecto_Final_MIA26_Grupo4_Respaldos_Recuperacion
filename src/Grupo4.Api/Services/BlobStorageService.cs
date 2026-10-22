using System.Security.Cryptography;
using System.Text;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using MIA_Grupo4_Blob_Avance.Models;

namespace MIA_Grupo4_Blob_Avance.Services;

public class BlobStorageService
{
    private readonly BlobContainerClient _container;
    private readonly ILogger<BlobStorageService> _logger;
    private readonly byte[] _key;

    public BlobStorageService(BlobServiceClient service, IConfiguration config, ILogger<BlobStorageService> logger)
    {
        _container = service.GetBlobContainerClient("archivos");
        _logger = logger;
        var keyValue = config["FILE_ENCRYPTION_KEY"];
        if (string.IsNullOrWhiteSpace(keyValue))
            throw new InvalidOperationException("Falta FILE_ENCRYPTION_KEY.");
        try { _key = Convert.FromBase64String(keyValue); }
        catch (FormatException) { throw new InvalidOperationException("FILE_ENCRYPTION_KEY debe ser Base64."); }
        if (_key.Length != 32)
            throw new InvalidOperationException("FILE_ENCRYPTION_KEY debe contener 32 bytes.");
    }

    public async Task<FileMetadata> SubirAsync(IFormFile file)
    {
        var id = Guid.NewGuid().ToString("N");
        try
        {
            if (file.Length == 0) throw new ArgumentException("El archivo esta vacio.");
            if (file.Length > 10 * 1024 * 1024) throw new ArgumentException("Maximo permitido: 10 MB.");

            using var input = new MemoryStream();
            await file.CopyToAsync(input);
            byte[] original = input.ToArray();
            string hash = Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant();

            // Formato cifrado: nonce (12 bytes) + etiqueta (16 bytes) + contenido cifrado.
            byte[] nonce = RandomNumberGenerator.GetBytes(12);
            byte[] tag = new byte[16];
            byte[] ciphertext = new byte[original.Length];
            using (var aes = new AesGcm(_key, 16))
                aes.Encrypt(nonce, original, ciphertext, tag);

            byte[] payload = new byte[nonce.Length + tag.Length + ciphertext.Length];
            Buffer.BlockCopy(nonce, 0, payload, 0, nonce.Length);
            Buffer.BlockCopy(tag, 0, payload, nonce.Length, tag.Length);
            Buffer.BlockCopy(ciphertext, 0, payload, nonce.Length + tag.Length, ciphertext.Length);

            await _container.CreateIfNotExistsAsync(PublicAccessType.None);
            var blob = _container.GetBlobClient(id);
            var fecha = DateTime.UtcNow;
            var nombre = Path.GetFileName(file.FileName);
            var tipo = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType;
            var metadata = new Dictionary<string, string>
            {
                ["nombrebase64"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(nombre)),
                ["tipo"] = tipo,
                ["tamano"] = original.Length.ToString(),
                ["fecha"] = fecha.ToString("O"),
                ["sha256"] = hash
            };
            using var encrypted = new MemoryStream(payload);
            await blob.UploadAsync(encrypted, new BlobUploadOptions
            {
                Metadata = metadata,
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/octet-stream" }
            });
            _logger.LogInformation("Operacion=SUBIR Resultado=OK ArchivoId={ArchivoId} FechaUtc={FechaUtc}", id, fecha);
            return new FileMetadata { Id = id, NombreOriginal = nombre, TipoContenido = tipo,
                TamanoOriginal = original.Length, FechaCargaUtc = fecha, RutaBlob = $"archivos/{id}", Sha256Original = hash };
        }
        catch (Exception ex)
        {
            _logger.LogError("Operacion=SUBIR Resultado=ERROR ArchivoId={ArchivoId} FechaUtc={FechaUtc} TipoError={TipoError}", id, DateTime.UtcNow, ex.GetType().Name);
            throw;
        }
    }

    public async Task<List<FileMetadata>> ListarAsync()
    {
        try
        {
            var lista = new List<FileMetadata>();
            await foreach (var item in _container.GetBlobsAsync(BlobTraits.Metadata))
                lista.Add(Convertir(item.Name, item.Metadata));
            _logger.LogInformation("Operacion=LISTAR Resultado=OK Cantidad={Cantidad} FechaUtc={FechaUtc}", lista.Count, DateTime.UtcNow);
            return lista;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            _logger.LogInformation("Operacion=LISTAR Resultado=OK Cantidad=0 FechaUtc={FechaUtc}", DateTime.UtcNow);
            return new List<FileMetadata>();
        }
        catch (Exception ex)
        {
            _logger.LogError("Operacion=LISTAR Resultado=ERROR FechaUtc={FechaUtc} TipoError={TipoError}", DateTime.UtcNow, ex.GetType().Name);
            throw;
        }
    }

    public async Task<FileMetadata?> ConsultarAsync(string id)
    {
        try
        {
            var blob = _container.GetBlobClient(id);
            var props = await blob.GetPropertiesAsync();
            _logger.LogInformation("Operacion=CONSULTAR Resultado=OK ArchivoId={ArchivoId} FechaUtc={FechaUtc}", id, DateTime.UtcNow);
            return Convertir(id, props.Value.Metadata);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            _logger.LogWarning("Operacion=CONSULTAR Resultado=NO_ENCONTRADO ArchivoId={ArchivoId} FechaUtc={FechaUtc}", id, DateTime.UtcNow);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError("Operacion=CONSULTAR Resultado=ERROR ArchivoId={ArchivoId} FechaUtc={FechaUtc} TipoError={TipoError}", id, DateTime.UtcNow, ex.GetType().Name);
            throw;
        }
    }

    public async Task<bool> EliminarAsync(string id)
    {
        try
        {
            // Solo elimina del contenedor principal, nunca del de respaldos.
            var response = await _container.GetBlobClient(id).DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots);
            bool eliminado = response.Value;
            _logger.LogInformation("Operacion=ELIMINAR Resultado={Resultado} ArchivoId={ArchivoId} FechaUtc={FechaUtc}", eliminado ? "OK" : "NO_ENCONTRADO", id, DateTime.UtcNow);
            return eliminado;
        }
        catch (Exception ex)
        {
            _logger.LogError("Operacion=ELIMINAR Resultado=ERROR ArchivoId={ArchivoId} FechaUtc={FechaUtc} TipoError={TipoError}", id, DateTime.UtcNow, ex.GetType().Name);
            throw;
        }
    }

    private static FileMetadata Convertir(string id, IDictionary<string, string> datos)
    {
        string Get(string campo) => datos.TryGetValue(campo, out var valor) ? valor : "";
        var nombre64 = Get("nombrebase64");
        return new FileMetadata
        {
            Id = id,
            NombreOriginal = string.IsNullOrEmpty(nombre64) ? "" : Encoding.UTF8.GetString(Convert.FromBase64String(nombre64)),
            TipoContenido = Get("tipo"),
            TamanoOriginal = long.TryParse(Get("tamano"), out var n) ? n : 0,
            FechaCargaUtc = DateTime.TryParse(Get("fecha"), out var f) ? f : default,
            RutaBlob = $"archivos/{id}",
            Sha256Original = Get("sha256")
        };
    }
}
