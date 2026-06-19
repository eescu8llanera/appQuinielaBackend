namespace quiniela.Modelos
{
    public class Partidos
    {
        public Guid IdPartido { get; set; }
        public string Local { get; set; } = string.Empty;
        public string Visitante { get; set; } = string.Empty;
        public int? GolesLocal { get; set; }
        public int? GolesVisitante { get; set; }
    }
}
