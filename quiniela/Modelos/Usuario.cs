namespace quiniela.Modelos
{
    public class Usuario
    {
        public Guid IdUsuario { get; set; }
        public string Nombre { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Rol { get; set; } = "User";
    }
}
