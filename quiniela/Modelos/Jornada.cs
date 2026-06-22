namespace quiniela.Modelos;

public class Jornada
{
    public Guid IdJornada { get; set; }
    public int Numero { get; set; }
    public string Nombre { get; set; } = string.Empty;
    public bool BancaActualizada { get; set; }
}
