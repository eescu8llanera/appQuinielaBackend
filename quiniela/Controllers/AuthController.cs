using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using quiniela.Modelos;

namespace quiniela.Controllers
{
    [ApiController]
    [Route("[controller]/[action]")]
    public class AuthController(IConfiguration configuration) : ControllerBase
    {
        private readonly string _connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("No existe la cadena de conexion 'DefaultConnection'.");

        [HttpPost]
        public async Task<ActionResult<AuthResponse>> Registro([FromBody] RegistroRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Nombre)
                || string.IsNullOrWhiteSpace(request.Email)
                || string.IsNullOrWhiteSpace(request.Password))
            {
                return BadRequest("Nombre, email y password son obligatorios.");
            }

            var rol = NormalizarRol(request.Rol);
            if (rol is null)
            {
                return BadRequest("El rol debe ser 'Admin' o 'User'.");
            }

            await EnsureSchemaAsync();

            var usuario = new Usuario
            {
                IdUsuario = Guid.NewGuid(),
                Nombre = request.Nombre.Trim(),
                Email = request.Email.Trim().ToLowerInvariant(),
                Rol = rol
            };

            var passwordHash = HashPassword(request.Password);

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            await using var command = new NpgsqlCommand(
                """
                INSERT INTO usuarios (id_usuario, nombre, email, password_hash, rol)
                VALUES (@id_usuario, @nombre, @email, @password_hash, @rol)
                RETURNING id_usuario, nombre, email, rol;
                """,
                connection);

            command.Parameters.AddWithValue("id_usuario", usuario.IdUsuario);
            command.Parameters.AddWithValue("nombre", usuario.Nombre);
            command.Parameters.AddWithValue("email", usuario.Email);
            command.Parameters.AddWithValue("password_hash", passwordHash);
            command.Parameters.AddWithValue("rol", usuario.Rol);

            try
            {
                await using var reader = await command.ExecuteReaderAsync();
                await reader.ReadAsync();

                usuario = new Usuario
                {
                    IdUsuario = reader.GetGuid(0),
                    Nombre = reader.GetString(1),
                    Email = reader.GetString(2),
                    Rol = reader.GetString(3)
                };
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return Conflict("Ya existe un usuario con ese email.");
            }

            return Ok(CrearRespuestaAuth(usuario));
        }

        [HttpGet]
        [AllowAnonymous]
        public async Task<string> Hash (string password)
        {
            return HashPassword(password);
        }

        [HttpPost]
        public async Task<ActionResult<AuthResponse>> Login([FromBody] LoginRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
            {
                return BadRequest("Email y password son obligatorios.");
            }

            await EnsureSchemaAsync();

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            await using var command = new NpgsqlCommand(
                """
                SELECT id_usuario, nombre, email, password_hash, rol
                FROM usuarios
                WHERE email = @email;
                """,
                connection);

            command.Parameters.AddWithValue("email", request.Email.Trim().ToLowerInvariant());

            await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(true);
            if (!await reader.ReadAsync())
            {
                return Unauthorized("Credenciales incorrectas.");
            }

            var usuario = new Usuario
            {
                IdUsuario = reader.GetGuid(0),
                Nombre = reader.GetString(1),
                Email = reader.GetString(2),
                Rol = reader.GetString(4)
            };

            var passwordHash = reader.GetString(3);
            if (!VerifyPassword(request.Password, passwordHash))
            {
                return Unauthorized("Credenciales incorrectas.");
            }

            return Ok(CrearRespuestaAuth(usuario));
        }

        private AuthResponse CrearRespuestaAuth(Usuario usuario)
        {
            var expirationMinutes = configuration.GetValue("Jwt:ExpirationMinutes", 120);
            var expiraEn = DateTime.UtcNow.AddMinutes(expirationMinutes);
            var key = configuration["Jwt:Key"]
                ?? throw new InvalidOperationException("No existe la configuracion Jwt:Key.");

            var claims = new List<Claim>
            {
                new(JwtRegisteredClaimNames.Sub, usuario.IdUsuario.ToString()),
                new(JwtRegisteredClaimNames.Email, usuario.Email),
                new(ClaimTypes.Name, usuario.Nombre),
                new(ClaimTypes.Role, usuario.Rol)
            };

            var credentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
                SecurityAlgorithms.HmacSha256);

            var token = new JwtSecurityToken(
                issuer: configuration["Jwt:Issuer"],
                audience: configuration["Jwt:Audience"],
                claims: claims,
                expires: expiraEn,
                signingCredentials: credentials);

            return new AuthResponse
            {
                Token = new JwtSecurityTokenHandler().WriteToken(token),
                ExpiraEn = expiraEn,
                Usuario = usuario
            };
        }

        private async Task EnsureSchemaAsync()
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            await using var command = new NpgsqlCommand(
                """
                CREATE TABLE IF NOT EXISTS usuarios (
                    id_usuario uuid PRIMARY KEY,
                    nombre text NOT NULL,
                    email text NOT NULL UNIQUE,
                    password_hash text NOT NULL,
                    rol text NOT NULL CHECK (rol IN ('Admin', 'User')),
                    creado_en timestamptz NOT NULL DEFAULT now()
                );
                """,
                connection);

            await command.ExecuteNonQueryAsync();
        }

        private static string? NormalizarRol(string rol)
        {
            return rol.Trim().ToLowerInvariant() switch
            {
                "admin" => "Admin",
                "user" => "User",
                "usuario" => "User",
                _ => null
            };
        }

        private static string HashPassword(string password)
        {
            var salt = RandomNumberGenerator.GetBytes(16);
            var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);

            return $"{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
        }

        private static bool VerifyPassword(string password, string storedHash)
        {
            var parts = storedHash.Split('.', 2);
            if (parts.Length != 2)
            {
                return false;
            }

            var salt = Convert.FromBase64String(parts[0]);
            var expectedHash = Convert.FromBase64String(parts[1]);
            var actualHash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);

            return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
        }
    }
}
