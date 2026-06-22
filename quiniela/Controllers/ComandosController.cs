using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using quiniela.Modelos;

namespace quiniela.Controllers
{
    [ApiController]
    [Route("[controller]/[action]")]
    public class ComandosController(IConfiguration configuration) : ControllerBase
    {
        private readonly string _connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("No existe la cadena de conexion 'DefaultConnection'.");

        [HttpGet]
        [Authorize(Policy = "User")]
        public async Task<ActionResult<List<Partidos>>> GetPartidos()
        {
            await EnsureSchemaAsync();

            var partidos = new List<Partidos>();
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            await using var command = new NpgsqlCommand(
                """
                SELECT id_partido, local, visitante, goles_local, goles_visitante
                FROM partidos
                ORDER BY creado_en, local, visitante;
                """,
                connection);

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                partidos.Add(new Partidos
                {
                    IdPartido = reader.GetGuid(0),
                    Local = reader.GetString(1),
                    Visitante = reader.GetString(2),
                    GolesLocal = reader.IsDBNull(3) ? null : reader.GetInt32(3),
                    GolesVisitante = reader.IsDBNull(4) ? null : reader.GetInt32(4)
                });
            }

            return Ok(partidos);
        }

        [HttpGet]
        [Authorize(Policy = "User")]
        public async Task<ActionResult<Clasificacion>> GetClasificacion()
        {
            await EnsureSchemaAsync();

            var jugadores = new List<JugadorClasificacion>();
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            await using var command = new NpgsqlCommand(
                """
                SELECT
                    pr.jugador,
                    SUM(
                        CASE
                            WHEN pr.goles_local = p.goles_local AND pr.goles_visitante = p.goles_visitante THEN 3
                            WHEN SIGN(pr.goles_local - pr.goles_visitante) = SIGN(p.goles_local - p.goles_visitante) THEN 1
                            ELSE 0
                        END
                    )::int AS puntos,
                    SUM(
                        CASE
                            WHEN pr.goles_local = p.goles_local AND pr.goles_visitante = p.goles_visitante THEN 1
                            ELSE 0
                        END
                    )::int AS aciertos
                FROM pronosticos pr
                INNER JOIN partidos p ON p.id_partido = pr.id_partido
                WHERE p.goles_local IS NOT NULL AND p.goles_visitante IS NOT NULL
                GROUP BY pr.jugador
                ORDER BY puntos DESC, aciertos DESC, pr.jugador;
                """,
                connection);

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                jugadores.Add(new JugadorClasificacion
                {
                    Nombre = reader.GetString(0),
                    Puntos = reader.GetInt32(1),
                    Aciertos = reader.GetInt32(2)
                });
            }

            return Ok(new Clasificacion
            {
                IdClasificacion = Guid.NewGuid(),
                Jugadores = jugadores
            });
        }

        [HttpGet]
        [Authorize(Policy = "User")]
        public async Task<ActionResult<List<Pronostico>>> GetPronosticos()
        {
            await EnsureSchemaAsync();

            var pronosticos = new List<Pronostico>();
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            await using var command = new NpgsqlCommand(
                """
                SELECT id_pronostico, id_partido, jugador, goles_local, goles_visitante, COALESCE(signo, '')
                FROM pronosticos
                ORDER BY creado_en, jugador;
                """,
                connection);

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                pronosticos.Add(new Pronostico
                {
                    IdPronostico = reader.GetGuid(0),
                    IdPartido = reader.GetGuid(1),
                    Jugador = reader.GetString(2),
                    GolesLocal = reader.GetInt32(3),
                    GolesVisitante = reader.GetInt32(4),
                    Signo = reader.GetString(5)
                });
            }

            return Ok(pronosticos);
        }

        [HttpPost]
        [Authorize(Policy = "Admin")]
        public async Task<ActionResult<List<Partidos>>> InsertPartidos([FromBody] List<Partidos> partidos)
        {
            if (partidos.Count == 0)
            {
                return BadRequest("Debes enviar al menos un partido.");
            }

            await EnsureSchemaAsync();
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var insertados = new List<Partidos>();
            foreach (var partido in partidos)
            {
                if (string.IsNullOrWhiteSpace(partido.Local) || string.IsNullOrWhiteSpace(partido.Visitante))
                {
                    return BadRequest("Cada partido debe tener local y visitante.");
                }

                partido.IdPartido = partido.IdPartido == Guid.Empty ? Guid.NewGuid() : partido.IdPartido;

                await using var command = new NpgsqlCommand(
                    """
                    INSERT INTO partidos (id_partido, local, visitante)
                    VALUES (@id_partido, @local, @visitante)
                    ON CONFLICT (id_partido) DO UPDATE
                    SET local = EXCLUDED.local,
                        visitante = EXCLUDED.visitante
                    RETURNING id_partido, local, visitante, goles_local, goles_visitante;
                    """,
                    connection);

                command.Parameters.AddWithValue("id_partido", partido.IdPartido);
                command.Parameters.AddWithValue("local", partido.Local.Trim());
                command.Parameters.AddWithValue("visitante", partido.Visitante.Trim());

                await using var reader = await command.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    insertados.Add(new Partidos
                    {
                        IdPartido = reader.GetGuid(0),
                        Local = reader.GetString(1),
                        Visitante = reader.GetString(2),
                        GolesLocal = reader.IsDBNull(3) ? null : reader.GetInt32(3),
                        GolesVisitante = reader.IsDBNull(4) ? null : reader.GetInt32(4)
                    });
                }
            }

            return Ok(insertados);
        }

        [HttpPost]
        [Authorize(Policy = "User")]
        public async Task<ActionResult<List<Pronostico>>> InsertPronosticos([FromBody] List<Pronostico> pronosticos)
        {
            if (pronosticos.Count == 0)
            {
                return BadRequest("Debes enviar al menos un pronostico.");
            }

            await EnsureSchemaAsync();
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var insertados = new List<Pronostico>();
            foreach (var pronostico in pronosticos)
            {
                if (pronostico.IdPartido == Guid.Empty || string.IsNullOrWhiteSpace(pronostico.Jugador))
                {
                    return BadRequest("Cada pronostico debe tener IdPartido y Jugador.");
                }

                pronostico.IdPronostico = pronostico.IdPronostico == Guid.Empty ? Guid.NewGuid() : pronostico.IdPronostico;

                await using var command = new NpgsqlCommand(
                    """
                    INSERT INTO pronosticos (id_pronostico, id_partido, jugador, goles_local, goles_visitante, signo)
                    VALUES (@id_pronostico, @id_partido, @jugador, @goles_local, @goles_visitante, @signo)
                    ON CONFLICT (jugador, id_partido) DO UPDATE
                    SET id_partido = EXCLUDED.id_partido,
                        jugador = EXCLUDED.jugador,
                        goles_local = EXCLUDED.goles_local,
                        goles_visitante = EXCLUDED.goles_visitante,
                        signo = EXCLUDED.signo
                    RETURNING id_pronostico, id_partido, jugador, goles_local, goles_visitante;
                    """,
                    connection);

                command.Parameters.AddWithValue("id_pronostico", pronostico.IdPronostico);
                command.Parameters.AddWithValue("id_partido", pronostico.IdPartido);
                command.Parameters.AddWithValue("jugador", pronostico.Jugador.Trim());
                command.Parameters.AddWithValue("goles_local", pronostico.GolesLocal);
                command.Parameters.AddWithValue("goles_visitante", pronostico.GolesVisitante);
                command.Parameters.AddWithValue("signo", pronostico.Signo);

                await using var reader = await command.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    insertados.Add(new Pronostico
                    {
                        IdPronostico = reader.GetGuid(0),
                        IdPartido = reader.GetGuid(1),
                        Jugador = reader.GetString(2),
                        GolesLocal = reader.GetInt32(3),
                        GolesVisitante = reader.GetInt32(4)
                    });
                }
            }

            return Ok(insertados);
        }

        [HttpPost]
        [Authorize(Policy = "Admin")]
        public async Task<ActionResult<List<Partidos>>> InsertResultados([FromBody] List<Resultado> resultados)
        {
            if (resultados.Count == 0)
            {
                return BadRequest("Debes enviar al menos un resultado.");
            }

            await EnsureSchemaAsync();
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var actualizados = new List<Partidos>();
            foreach (var resultado in resultados)
            {
                if (resultado.IdPartido == Guid.Empty)
                {
                    return BadRequest("Cada resultado debe tener IdPartido.");
                }

                await using var command = new NpgsqlCommand(
                    """
                    UPDATE partidos
                    SET goles_local = @goles_local,
                        goles_visitante = @goles_visitante
                    WHERE id_partido = @id_partido
                    RETURNING id_partido, local, visitante, goles_local, goles_visitante;
                    """,
                    connection);

                command.Parameters.AddWithValue("id_partido", resultado.IdPartido);
                command.Parameters.AddWithValue("goles_local", resultado.GolesLocal);
                command.Parameters.AddWithValue("goles_visitante", resultado.GolesVisitante);

                await using var reader = await command.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                {
                    return NotFound($"No existe el partido {resultado.IdPartido}.");
                }

                actualizados.Add(new Partidos
                {
                    IdPartido = reader.GetGuid(0),
                    Local = reader.GetString(1),
                    Visitante = reader.GetString(2),
                    GolesLocal = reader.IsDBNull(3) ? null : reader.GetInt32(3),
                    GolesVisitante = reader.IsDBNull(4) ? null : reader.GetInt32(4)
                });
            }

            return Ok(actualizados);
        }

        private async Task EnsureSchemaAsync()
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            await using var command = new NpgsqlCommand(
                """
                CREATE TABLE IF NOT EXISTS partidos (
                    id_partido uuid PRIMARY KEY,
                    local text NOT NULL,
                    visitante text NOT NULL,
                    goles_local integer NULL,
                    goles_visitante integer NULL,
                    creado_en timestamptz NOT NULL DEFAULT now()
                );

                CREATE TABLE IF NOT EXISTS pronosticos (
                    id_pronostico uuid PRIMARY KEY,
                    id_partido uuid NOT NULL REFERENCES partidos(id_partido) ON DELETE CASCADE,
                    jugador text NOT NULL,
                    goles_local integer NOT NULL,
                    goles_visitante integer NOT NULL,
                    creado_en timestamptz NOT NULL DEFAULT now()
                );
                """,
                connection);

            await command.ExecuteNonQueryAsync();
        }
    }
}
