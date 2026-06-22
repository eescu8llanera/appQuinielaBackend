namespace quiniela.Modelos
{
    public class Pronostico
    {
        public Guid IdPronostico { get; set; }
        public Guid IdPartido { get; set; }
        public string Jugador { get; set; } = string.Empty;
        public int GolesLocal { get; set; }
        public int GolesVisitante { get; set; }
        public string Signo { get; set; } = string.Empty;
    }
}
