namespace quiniela.Modelos
{
    public class Clasificacion
    {
        public Guid IdClasificacion { get; set; }
        public List<JugadorClasificacion> Jugadores { get; set; } = [];
    }

    public class JugadorClasificacion
    {
        public string Nombre { get; set; } = string.Empty;
        public int Puntos { get; set; }
        public int Aciertos { get; set; }
        public decimal Banca { get; set; }
        public bool PlenoAcertado { get; set; }
        public int AciertosElige8 { get; set; }
    }
}
