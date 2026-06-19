-- Ejecutar en Supabase SQL Editor.
-- Crea la estructura necesaria para la API de la quiniela.

create extension if not exists pgcrypto;

create table if not exists public.usuarios (
    id_usuario uuid primary key default gen_random_uuid(),
    nombre text not null,
    email text not null unique,
    password_hash text not null,
    rol text not null check (rol in ('Admin', 'User')),
    creado_en timestamptz not null default now(),
    actualizado_en timestamptz not null default now()
);

create table if not exists public.partidos (
    id_partido uuid primary key default gen_random_uuid(),
    local text not null,
    visitante text not null,
    goles_local integer null check (goles_local is null or goles_local >= 0),
    goles_visitante integer null check (goles_visitante is null or goles_visitante >= 0),
    creado_en timestamptz not null default now(),
    actualizado_en timestamptz not null default now(),
    constraint partidos_equipos_distintos check (lower(trim(local)) <> lower(trim(visitante)))
);

create table if not exists public.pronosticos (
    id_pronostico uuid primary key default gen_random_uuid(),
    id_partido uuid not null references public.partidos(id_partido) on delete cascade,
    jugador text not null,
    goles_local integer not null check (goles_local >= 0),
    goles_visitante integer not null check (goles_visitante >= 0),
    creado_en timestamptz not null default now(),
    actualizado_en timestamptz not null default now()
);

create index if not exists ix_pronosticos_id_partido
    on public.pronosticos(id_partido);

create index if not exists ix_pronosticos_jugador
    on public.pronosticos(jugador);

create index if not exists ix_partidos_creado_en
    on public.partidos(creado_en);

create or replace function public.set_actualizado_en()
returns trigger
language plpgsql
as $$
begin
    new.actualizado_en = now();
    return new;
end;
$$;

drop trigger if exists trg_usuarios_actualizado_en on public.usuarios;
create trigger trg_usuarios_actualizado_en
before update on public.usuarios
for each row execute function public.set_actualizado_en();

drop trigger if exists trg_partidos_actualizado_en on public.partidos;
create trigger trg_partidos_actualizado_en
before update on public.partidos
for each row execute function public.set_actualizado_en();

drop trigger if exists trg_pronosticos_actualizado_en on public.pronosticos;
create trigger trg_pronosticos_actualizado_en
before update on public.pronosticos
for each row execute function public.set_actualizado_en();

-- Vista opcional para consultar la clasificacion directamente desde Supabase.
create or replace view public.clasificacion as
select
    pr.jugador as nombre,
    sum(
        case
            when pr.goles_local = p.goles_local and pr.goles_visitante = p.goles_visitante then 3
            when sign(pr.goles_local - pr.goles_visitante) = sign(p.goles_local - p.goles_visitante) then 1
            else 0
        end
    )::integer as puntos,
    sum(
        case
            when pr.goles_local = p.goles_local and pr.goles_visitante = p.goles_visitante then 1
            else 0
        end
    )::integer as aciertos
from public.pronosticos pr
inner join public.partidos p on p.id_partido = pr.id_partido
where p.goles_local is not null
  and p.goles_visitante is not null
group by pr.jugador;
