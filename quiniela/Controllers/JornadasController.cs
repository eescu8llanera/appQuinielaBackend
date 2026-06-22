using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using quiniela.Modelos;

namespace quiniela.Controllers;

[ApiController]
[Route("[controller]/[action]")]
public class JornadasController(IConfiguration configuration) : ControllerBase
{
    private readonly string cs = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("No existe DefaultConnection.");

    [HttpGet, Authorize(Policy = "User")]
    public async Task<ActionResult<List<Jornada>>> GetJornadas()
    {
        await EnsureAsync();
        var result = new List<Jornada>();
        await using var cn = new NpgsqlConnection(cs); await cn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT id_jornada,numero,nombre,banca_actualizada FROM jornadas ORDER BY numero DESC", cn);
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync()) result.Add(new Jornada { IdJornada=rd.GetGuid(0), Numero=rd.GetInt32(1), Nombre=rd.GetString(2), BancaActualizada=rd.GetBoolean(3) });
        return Ok(result);
    }

    [HttpPost, Authorize(Policy = "Admin")]
    public async Task<ActionResult<Jornada>> CrearJornada(Jornada jornada)
    {
        await EnsureAsync();
        await using var cn = new NpgsqlConnection(cs); await cn.OpenAsync();
        jornada.IdJornada = Guid.NewGuid();
        await using var cmd = new NpgsqlCommand("INSERT INTO jornadas(id_jornada,numero,nombre) VALUES(@id,@n,@nombre)", cn);
        cmd.Parameters.AddWithValue("id", jornada.IdJornada); cmd.Parameters.AddWithValue("n", jornada.Numero);
        cmd.Parameters.AddWithValue("nombre", string.IsNullOrWhiteSpace(jornada.Nombre) ? $"Jornada {jornada.Numero}" : jornada.Nombre.Trim());
        try { await cmd.ExecuteNonQueryAsync(); } catch (PostgresException e) when (e.SqlState == "23505") { return Conflict("Ya existe esa jornada."); }
        return Ok(jornada);
    }

    [HttpPost, Authorize(Policy = "Admin")]
    public async Task<IActionResult> AsignarPartidos(Guid idJornada, List<Guid> idsPartido)
    {
        if (idsPartido.Count != 15 || idsPartido.Distinct().Count() != 15) return BadRequest("Debes seleccionar exactamente 15 partidos distintos; el ultimo sera el pleno al 15.");
        await EnsureAsync();
        await using var cn = new NpgsqlConnection(cs); await cn.OpenAsync(); await using var tx = await cn.BeginTransactionAsync();
        for (var i=0; i<15; i++) { await using var cmd = new NpgsqlCommand("UPDATE partidos SET id_jornada=@j,orden=@o,es_pleno=@p WHERE id_partido=@id",cn,tx); cmd.Parameters.AddWithValue("j",idJornada); cmd.Parameters.AddWithValue("o",i+1); cmd.Parameters.AddWithValue("p",i==14); cmd.Parameters.AddWithValue("id",idsPartido[i]); if(await cmd.ExecuteNonQueryAsync()!=1)return NotFound($"No existe el partido {idsPartido[i]}"); }
        await tx.CommitAsync(); return NoContent();
    }

    [HttpGet, Authorize(Policy = "User")]
    public async Task<ActionResult<List<Partidos>>> GetPartidos(Guid idJornada)
    {
        await EnsureAsync();

        var result = new List<Partidos>();

        await using var cn = new NpgsqlConnection(cs);
        await cn.OpenAsync();

        const string sql = """
        WITH base AS (
            SELECT 
                p.id_partido,
                p.orden,
                p.es_pleno,
                CASE 
                    WHEN p.es_pleno THEN 0
                    ELSE 
                        CASE 
                            WHEN pr.signo = CASE 
                                WHEN p.goles_local > p.goles_visitante THEN '1'
                                WHEN p.goles_local = p.goles_visitante THEN 'X'
                                ELSE '2'
                            END
                            THEN 1 
                            ELSE 0 
                        END
                END AS acierto
            FROM partidos p
            LEFT JOIN pronosticos pr ON pr.id_partido = p.id_partido
            WHERE p.id_jornada = @j
              AND p.goles_local IS NOT NULL
        ),
        e8 AS (
            SELECT id_partido
            FROM base
            WHERE NOT es_pleno
            GROUP BY id_partido, orden
            ORDER BY COUNT(*) FILTER (WHERE acierto = 1) DESC, orden
            LIMIT 8
        )
        SELECT 
            p.id_partido,
            p.local,
            p.visitante,
            p.goles_local,
            p.goles_visitante,
            p.id_jornada,
            p.orden,
            p.es_pleno,
            e8.id_partido IS NOT NULL AS es_elige8
        FROM partidos p
        LEFT JOIN e8 ON e8.id_partido = p.id_partido
        WHERE p.id_jornada = @j
        ORDER BY p.orden;
        """;

        await using var cmd = new NpgsqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("j", idJornada);

        await using var rd = await cmd.ExecuteReaderAsync();

        while (await rd.ReadAsync())
        {
            result.Add(new Partidos
            {
                IdPartido = rd.GetGuid(0),
                Local = rd.GetString(1),
                Visitante = rd.GetString(2),
                GolesLocal = rd.IsDBNull(3) ? null : rd.GetInt32(3),
                GolesVisitante = rd.IsDBNull(4) ? null : rd.GetInt32(4),
                IdJornada = rd.GetGuid(5),
                Orden = rd.GetInt32(6),
                EsPleno = rd.GetBoolean(7),
                EsElige8 = rd.GetBoolean(8)
            });
        }

        return Ok(result);
    }

    [HttpGet, Authorize(Policy = "User")]
    public async Task<IActionResult> GetClasificacion(Guid idJornada)
    {
        await EnsureAsync(); await using var cn=new NpgsqlConnection(cs);await cn.OpenAsync();
        const string sql= """
            WITH base AS (
            SELECT 
                pr.jugador,
                p.orden,
                p.es_pleno,
                CASE 
                    WHEN p.es_pleno THEN 
                        CASE 
                            WHEN LEAST(pr.goles_local, 3) = LEAST(p.goles_local, 3)
                             AND LEAST(pr.goles_visitante, 3) = LEAST(p.goles_visitante, 3)
                            THEN 1 ELSE 0 
                        END
                    ELSE 
                        CASE 
                            WHEN pr.signo = CASE 
                                WHEN p.goles_local > p.goles_visitante THEN '1'
                                WHEN p.goles_local = p.goles_visitante THEN 'X'
                                ELSE '2'
                            END
                            THEN 1 ELSE 0 
                        END
                END AS acierto
            FROM pronosticos pr 
            JOIN partidos p USING(id_partido) 
            WHERE p.id_jornada = @j
              AND p.goles_local IS NOT NULL
        ),
        e8 AS (
            SELECT orden 
            FROM base 
            WHERE NOT es_pleno 
            GROUP BY orden 
            ORDER BY COUNT(*) FILTER (WHERE acierto = 1) DESC, orden
            LIMIT 8
        ),
        clasificacion AS (
            SELECT 
                u.nombre,
                COALESCE(SUM(b.acierto) FILTER (WHERE NOT b.es_pleno), 0)::int AS aciertos,
                COALESCE(MAX(b.acierto) FILTER (WHERE b.es_pleno), 0)::int AS pleno_acierto_num,
                COALESCE(ba.saldo, 5)::numeric AS banca,
                COALESCE(SUM(b.acierto) FILTER (
                    WHERE NOT b.es_pleno 
                      AND b.orden IN (SELECT orden FROM e8)
                ), 0)::int AS aciertos_elige8,
                ARRAY_AGG(
                    COALESCE(b.acierto, 0) 
                    ORDER BY b.orden
                ) AS orden_aciertos
            FROM usuarios u 
            LEFT JOIN base b ON LOWER(b.jugador) = LOWER(u.nombre)
            LEFT JOIN bancas ba ON ba.id_usuario = u.id_usuario
            GROUP BY u.id_usuario, u.nombre, ba.saldo
        )
        SELECT 
            nombre,
            aciertos + (pleno_acierto_num * 2) AS puntos,
            aciertos,
            banca,
            pleno_acierto_num = 1 AS pleno_acertado,
            aciertos_elige8
        FROM clasificacion
        ORDER BY 
            puntos DESC,
            pleno_acertado DESC,
            aciertos_elige8 DESC,
            orden_aciertos DESC,
            nombre;
        """;
        var rows=new List<JugadorClasificacion>();await using var cmd=new NpgsqlCommand(sql,cn);cmd.Parameters.AddWithValue("j",idJornada);await using var rd=await cmd.ExecuteReaderAsync();while(await rd.ReadAsync())rows.Add(new JugadorClasificacion{Nombre=rd.GetString(0),Puntos=rd.GetInt32(1),Aciertos=rd.GetInt32(2),Banca=rd.GetDecimal(3),PlenoAcertado=rd.GetBoolean(4),AciertosElige8=rd.GetInt32(5)});return Ok(new Clasificacion{IdClasificacion=idJornada,Jugadores=rows});
    }

    [HttpPost, Authorize(Policy = "Admin")]
    public async Task<IActionResult> ActualizarBanca(Guid idJornada)
    {
        await EnsureAsync(); await using var cn=new NpgsqlConnection(cs);await cn.OpenAsync();await using var tx=await cn.BeginTransactionAsync();
        await using(var guard=new NpgsqlCommand("UPDATE jornadas SET banca_actualizada=true WHERE id_jornada=@j AND banca_actualizada=false",cn,tx)){guard.Parameters.AddWithValue("j",idJornada);if(await guard.ExecuteNonQueryAsync()!=1)return Conflict("La banca de esta jornada ya fue actualizada.");}
        const string sql = """
            WITH ranking AS (
                SELECT 
                    u.id_usuario,
                    b.saldo,
                    ROW_NUMBER() OVER (
                        ORDER BY COALESCE(SUM(
                            CASE 
                                WHEN pr.signo = CASE 
                                    WHEN p.goles_local > p.goles_visitante THEN '1'
                                    WHEN p.goles_local = p.goles_visitante THEN 'X'
                                    ELSE '2'
                                END THEN 1 
                                ELSE 0 
                            END
                        ), 0) DESC, u.nombre
                    ) pos
                FROM usuarios u
                LEFT JOIN bancas b ON b.id_usuario = u.id_usuario
                LEFT JOIN pronosticos pr ON pr.jugador = u.nombre
                LEFT JOIN partidos p ON p.id_partido = pr.id_partido 
                    AND p.id_jornada = @j
                GROUP BY u.id_usuario, u.nombre, b.saldo
            )
            INSERT INTO bancas(id_usuario, saldo)
            SELECT 
                id_usuario,
                saldo + CASE pos
                    WHEN 1 THEN 0
                    WHEN 2 THEN -0.10
                    WHEN 3 THEN -0.35
                    WHEN 4 THEN -0.60
                    WHEN 5 THEN -0.85
                    WHEN 6 THEN -1.10
                    WHEN 7 THEN -1.35
                    WHEN 8 THEN -1.60
                    ELSE -1.80
                END
            FROM ranking
            ON CONFLICT(id_usuario) DO UPDATE 
            SET saldo = bancas.saldo + EXCLUDED.saldo - 5
            """;
        await using(var cmd=new NpgsqlCommand(sql,cn,tx)){cmd.Parameters.AddWithValue("j",idJornada);await cmd.ExecuteNonQueryAsync();}await tx.CommitAsync();return NoContent();
    }

    private async Task EnsureAsync(){await using var cn=new NpgsqlConnection(cs);await cn.OpenAsync();await using var cmd=new NpgsqlCommand("""
      CREATE TABLE IF NOT EXISTS jornadas(id_jornada uuid PRIMARY KEY,numero integer UNIQUE NOT NULL,nombre text NOT NULL,banca_actualizada boolean NOT NULL DEFAULT false,creado_en timestamptz NOT NULL DEFAULT now());
      ALTER TABLE partidos ADD COLUMN IF NOT EXISTS id_jornada uuid REFERENCES jornadas(id_jornada),ADD COLUMN IF NOT EXISTS orden integer,ADD COLUMN IF NOT EXISTS es_pleno boolean NOT NULL DEFAULT false;
      ALTER TABLE pronosticos ADD COLUMN IF NOT EXISTS signo text;
      CREATE UNIQUE INDEX IF NOT EXISTS ux_pronostico_jugador_partido ON pronosticos(jugador,id_partido);
      CREATE TABLE IF NOT EXISTS bancas(id_usuario uuid PRIMARY KEY REFERENCES usuarios(id_usuario),saldo numeric(8,2) NOT NULL DEFAULT 5);
      """,cn);await cmd.ExecuteNonQueryAsync();}
}
