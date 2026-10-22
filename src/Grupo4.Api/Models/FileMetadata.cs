namespace MIA_Grupo4_Blob_Avance.Models;

public class FileMetadata
{
    public string Id { get; set; } = "";
    public string NombreOriginal { get; set; } = "";
    public string TipoContenido { get; set; } = "";
    public long TamanoOriginal { get; set; }
    public DateTime FechaCargaUtc { get; set; }
    public string RutaBlob { get; set; } = "";
    public string Sha256Original { get; set; } = "";
}
