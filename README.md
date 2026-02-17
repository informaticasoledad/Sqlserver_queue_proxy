# TDSQueue.Proxy

Proxy TDS (Tabular Data Stream) para SQL Server como Worker Service en .NET 10.

## Mejoras de rendimiento y escalabilidad implementadas

- Motor de cola pluggable: `Channel`, `RabbitMq` o `Redis`.
- Health checks: `/health/live` y `/health/ready` en puerto de management.
- Endpoint admin seguro para tuning runtime (`/admin/state`, `/admin/tuning`, `/admin/limiter`).
- Circuit breaker en conexión a SQL target para evitar cascadas de fallo.
- OpenTelemetry con exportación OTLP configurable.
- RabbitMQ en modo push (`BasicConsume` + `AsyncEventingBasicConsumer`) con `ack/nack` explícito.
- Publicación batched a RabbitMQ (`PublishBatchSize`, `PublishBatchMaxDelayMs`).
- Reintentos y backoff en publicación RabbitMQ.
- Control de concurrencia adaptativo (`AdaptiveConcurrencyLimiter`) con ajuste por cola y latencia target.
- Límite global de conexiones salientes a SQL (`MaxTargetConnections`) con reintentos de conexión y jitter.
- Limpieza de requests pendientes expirados (`PendingRequestJanitorService`).
- Replay control por `RequestId` para evitar requeue infinito (`MaxReplayAttempts`).
- TTL de pending adaptativo según latencia EWMA al target.
- Framing TDS con buffers rentados (`ArrayPool`) para reducir allocations por request.
- Telemetría ampliada por etapa (`read_client`, `enqueue`, `target_connect`, `write_target`, `target_roundtrip`, `wait_response`, `write_client`).
- Hardening de seguridad: allowlist CIDR de clientes, resolución de secretos vía env (`${ENV:VAR}`), soporte TLS en RabbitMQ/Redis.
- Fault injection configurable para pruebas de resiliencia/chaos.

## Arquitectura

- `TdsProxyListenerService`: acepta conexiones, hace framing TDS, registra pendientes y encola `RequestId`.
- `TdsProxyWorkerService`: consume entregas de cola, resuelve contexto pendiente, procesa contra SQL y hace `ack/nack`.
- `PendingRequestStore`: correlación request/response por `RequestId`.
- `IProxyQueueEngine`: contrato común de motor de cola.
- `ProxyMetrics`: métricas, gauges e histogramas.

## Modos de operación

- `QueuePipeline`:
  - Framing TDS por mensaje, cola + workers, correlación request/response.
  - Se usa cuando no hay passthrough y no aplica cola opaca.
- `Passthrough`:
  - Túnel full-duplex cliente <-> SQL.
  - Sin cola por sesión.
- `ForceQueueOnly` (recomendado para "cola sí o sí"):
  - Prohíbe passthrough.
  - Si hay cifrado (`0x01/0x01`), usa **cola cifrada opaca**: el proxy no descifra TLS, encola y reenvía bytes.
  - Compatible con productor/consumidor, pero sin inspección de payload SQL.

### Tabla rápida de decisión

| EnableMarsPassthrough | ForceQueueOnly    | FallbackToPassthroughOnEncryptOff | Resultado                                     |
|-----------------------|-------------------|-----------------------------------|-----------------------------------------------|
| true                  | false             | cualquier valor                   | Passthrough full-duplex (sin cola por sesión) |
| false                 | false             | true                              | QueuePipeline normal; si PreLogin da `0x00/0x00`, fallback a passthrough          |
| false                 | false             | false                             | QueuePipeline normal; si PreLogin da `0x00/0x00`, falla sesión |
| false                 | true              | true/false                        | Cola obligatoria: sin passthrough; con TLS usa cola cifrada opaca |

## Motores de cola

### Channel (in-memory)

- `QueueEngine = "Channel"`
- Baja latencia, sin dependencia externa.

### RabbitMQ

- `QueueEngine = "RabbitMq"`
- Consumo push con `ack/nack`.
- Batching y reintentos en publicación.

### Redis

- `QueueEngine = "Redis"`
- Cola confiable con `BRPOPLPUSH` (`QueueKey` -> `ProcessingQueueKey`).
- `ack` elimina de `ProcessingQueueKey`; `nack` opcionalmente reencola.

## Qué es MARS

`MARS` significa **Multiple Active Result Sets**.

En SQL Server permite que una misma conexión tenga múltiples operaciones activas (por ejemplo, varias lecturas/comandos superpuestos) sin abrir conexiones adicionales.

En este proxy:

- `EnableMarsPassthrough = false`: se usa el flujo Productor/Consumidor del proxy.
- `EnableMarsPassthrough = true`: el proxy entra en túnel full-duplex y delega el comportamiento MARS al canal cliente <-> SQL Server.

## Configuración (`appsettings.json`)

```json
{
  "Proxy": {
    "ListeningPort": 14330,
    "WorkerCount": 64,
    "ChannelCapacity": 1000,
    "MetricsReportIntervalSeconds": 10,
    "QueueEngine": "Channel",
    "EnableMarsPassthrough": false,
    "ForceQueueOnly": false,
    "FallbackToPassthroughOnEncryptOff": true,
    "TargetHost": "127.0.0.1",
    "TargetPort": 1433,
    "TargetConnectMaxRetries": 5,
    "TargetConnectRetryDelayMs": 200,
    "CircuitBreakerFailuresThreshold": 20,
    "CircuitBreakerOpenSeconds": 30,
    "MaxTargetConnections": 2000,
    "MinInFlightRequests": 16,
    "MaxInFlightRequests": 256,
    "AdaptiveBackpressureEnabled": true,
    "QueueLowWatermark": 100,
    "QueueHighWatermark": 500,
    "TargetLatencyLowMs": 120,
    "TargetLatencyHighMs": 800,
    "RequestProcessingRetries": 2,
    "RequestRetryDelayMs": 50,
    "MaxReplayAttempts": 3,
    "PendingRequestTtlSeconds": 120,
    "Tls": {
      "Enabled": false,
      "CertificatePath": "",
      "CertificatePassword": "",
      "ClientCertificateRequired": false,
      "CheckCertificateRevocation": true,
      "HandshakeTimeoutSeconds": 20
    },
    "RabbitMq": {
      "HostName": "localhost",
      "Port": 5672,
      "UserName": "guest",
      "Password": "guest",
      "VirtualHost": "/",
      "QueueName": "tdsqueue.proxy.requests",
      "Durable": true,
      "PrefetchCount": 100,
      "PublishBatchSize": 64,
      "PublishBatchMaxDelayMs": 5,
      "PublishMaxRetries": 5,
      "PublishRetryDelayMs": 100,
      "ConsumeMaxRetries": 5,
      "ConsumeRetryDelayMs": 200,
      "ConsumerChannelRestartDelayMs": 500,
      "RequeueOnFailure": true,
      "UseTls": false,
      "TlsServerName": "",
      "TlsAcceptInvalidCertificates": false
    },
    "Redis": {
      "Configuration": "localhost:6379",
      "QueueKey": "tdsqueue:requests",
      "ProcessingQueueKey": "tdsqueue:requests:processing",
      "PopBlockTimeoutSeconds": 1,
      "EnqueueMaxRetries": 5,
      "EnqueueRetryDelayMs": 100,
      "ConsumeMaxRetries": 5,
      "ConsumeRetryDelayMs": 200,
      "RequeueOnFailure": true,
      "UseTls": false,
      "SslHost": "",
      "AbortOnConnectFail": false
    },
    "FaultInjection": {
      "Enabled": false,
      "ThrowProbability": 0,
      "DelayProbability": 0,
      "DelayMs": 50
    }
  },
  "Observability": {
    "HealthPort": 18080,
    "EnableOtlpExporter": false,
    "OtlpEndpoint": "http://localhost:4317",
    "EnableAdminEndpoint": false,
    "AdminToken": "${ENV:TDS_PROXY_ADMIN_TOKEN}"
  }
}
```

## Referencia de parámetros

### `Proxy`

- `ListeningPort`: puerto TCP donde el proxy escucha clientes TDS.
- `WorkerCount`: número de workers consumidores.
- `ChannelCapacity`: capacidad del `Channel` interno (o buffer de publish en motores externos).
- `MetricsReportIntervalSeconds`: intervalo de log del snapshot de métricas.
- `QueueEngine`: motor de cola (`Channel`, `RabbitMq`, `Redis`).
- `EnableMarsPassthrough`: si `true`, desactiva el flujo productor/consumidor por mensaje y habilita túnel full-duplex cliente<->SQL para delegar MARS al servidor; si `false`, usa pipeline de cola/workers.
- `ForceQueueOnly`: si `true`, prohíbe passthrough y obliga modo cola. Con TLS activo, usa cola opaca (sin terminación TLS en proxy).
- `FallbackToPassthroughOnEncryptOff`: si `true`, cuando PreLogin negocia `ENCRYPT_OFF (0x00/0x00)` fuerza compatibilidad por sesión en passthrough; si `false`, no hace fallback y falla esa sesión para evitar salir del modo cola.
- `EnforceClientAllowlist`: si `true`, solo acepta IPs permitidas.
- `AllowedClientCidrs`: lista CIDR IPv4 permitida (ej. `127.0.0.1/32`).
- `TargetHost`: host/IP del SQL Server destino.
- `TargetPort`: puerto del SQL Server destino.
- `TargetConnectMaxRetries`: reintentos de conexión al target.
- `TargetConnectRetryDelayMs`: delay base entre reintentos de conexión.
- `CircuitBreakerFailuresThreshold`: cantidad de fallos consecutivos para abrir circuito.
- `CircuitBreakerOpenSeconds`: tiempo que el circuito permanece abierto.
- `MaxTargetConnections`: límite global de conexiones concurrentes al target.
- `MinInFlightRequests`: mínimo de concurrencia permitida por limiter adaptativo.
- `MaxInFlightRequests`: máximo de concurrencia permitida por limiter adaptativo.
- `AdaptiveBackpressureEnabled`: habilita ajuste automático del limiter.
- `QueueLowWatermark`: umbral bajo de cola para reducir concurrencia.
- `QueueHighWatermark`: umbral alto de cola para aumentar concurrencia.
- `TargetLatencyLowMs`: umbral bajo de latencia EWMA del target.
- `TargetLatencyHighMs`: umbral alto de latencia EWMA del target.
- `RequestProcessingRetries`: reintentos internos de procesamiento por worker.
- `RequestRetryDelayMs`: delay base de reintentos de procesamiento.
- `MaxReplayAttempts`: máximo de requeue por `RequestId` antes de fallar definitivo.
- `PendingRequestTtlSeconds`: TTL base de requests pendientes (con ajuste adaptativo por EWMA).

### `Proxy.Tls` (upgrade TLS consciente de PreLogin)

- `Enabled`: si `true`, el proxy realiza upgrade TLS cuando PreLogin negocia cifrado (cliente y target).
- `CertificatePath`: ruta del certificado PFX usado por el proxy.
- `CertificatePassword`: password del PFX (acepta `${ENV:VAR}`).
- `ClientCertificateRequired`: exige certificado de cliente (mTLS).
- `CheckCertificateRevocation`: valida revocación de certificados (puede deshabilitarse en desarrollo local).
- `TrustTargetServerCertificate`: si `true`, omite validación estricta del certificado del SQL target (solo pruebas).
- `TargetServerName`: override de SNI/hostname esperado al abrir TLS hacia el SQL target.
- `HandshakeTimeoutSeconds`: timeout del handshake TLS.

Nota:

- Cuando `ForceQueueOnly=true`, el proxy prioriza cola opaca cifrada y no hace terminación TLS MITM para esa sesión.

### `Proxy.RabbitMq`

- `HostName`: host del broker RabbitMQ.
- `Port`: puerto del broker.
- `UserName`: usuario (acepta `${ENV:VAR}`).
- `Password`: password (acepta `${ENV:VAR}`).
- `VirtualHost`: vhost.
- `QueueName`: nombre de cola principal.
- `Durable`: cola durable.
- `PrefetchCount`: prefetch del consumidor.
- `PublishBatchSize`: tamaño de lote de publish.
- `PublishBatchMaxDelayMs`: latencia máxima para completar un lote de publish.
- `PublishMaxRetries`: reintentos de publish.
- `PublishRetryDelayMs`: delay base de reintentos de publish.
- `ConsumeMaxRetries`: reintentos de re-inicialización de consumidor.
- `ConsumeRetryDelayMs`: delay base de reintentos del consumidor.
- `ConsumerChannelRestartDelayMs`: pausa al recrear canal consumidor.
- `RequeueOnFailure`: si `true`, reencola delivery fallida (además de `MaxReplayAttempts`).
- `UseTls`: habilita TLS.
- `TlsServerName`: nombre esperado del certificado remoto.
- `TlsAcceptInvalidCertificates`: permite certificados inválidos (solo pruebas).

### `Proxy.Redis`

- `Configuration`: connection string de Redis (acepta `${ENV:VAR}` dentro del string).
- `QueueKey`: lista principal Redis.
- `ProcessingQueueKey`: lista de procesamiento (`BRPOPLPUSH`).
- `PopBlockTimeoutSeconds`: timeout bloqueante de pop.
- `EnqueueMaxRetries`: reintentos de push.
- `EnqueueRetryDelayMs`: delay base de reintentos de push.
- `ConsumeMaxRetries`: reintentos al consumir.
- `ConsumeRetryDelayMs`: delay base de reintentos de consumo.
- `RequeueOnFailure`: si `true`, vuelve a encolar tras fallo.
- `UseTls`: habilita TLS en Redis.
- `SslHost`: host esperado para validación TLS.
- `AbortOnConnectFail`: control de comportamiento ante fallo inicial de conexión.

### `Proxy.FaultInjection`

- `Enabled`: activa inyección de fallos sintéticos.
- `ThrowProbability`: probabilidad `[0..1]` de lanzar excepción antes de llamar al target.
- `DelayProbability`: probabilidad `[0..1]` de inyectar delay.
- `DelayMs`: duración del delay inyectado.

### `Observability`

- `HealthPort`: puerto del endpoint de health/admin.
- `EnableOtlpExporter`: habilita exportación OTLP de métricas y trazas.
- `OtlpEndpoint`: endpoint OTLP collector (acepta `${ENV:VAR}`).
- `EnableAdminEndpoint`: habilita endpoints administrativos.
- `AdminToken`: token admin para `X-Admin-Token` (acepta `${ENV:VAR}`).

### Certificado TLS de desarrollo (ejemplo)

El proxy necesita un certificado PFX cuando `Proxy:Tls:Enabled=true`.

1. Genera un certificado PFX local:

```bash
mkdir -p /Users/carlosleyvagarcia/workfolder/TDSQueue/certs
dotnet dev-certs https -ep /Users/carlosleyvagarcia/workfolder/TDSQueue/certs/tds-proxy-dev.pfx -p devpassword
```

2. Configura `Proxy:Tls` en `/Users/carlosleyvagarcia/workfolder/TDSQueue/appsettings.Development.json`:

```json
{
  "Proxy": {
    "Tls": {
      "Enabled": true,
      "CertificatePath": "${ENV:TDS_PROXY_TLS_CERT_PATH}",
      "CertificatePassword": "${ENV:TDS_PROXY_TLS_CERT_PASSWORD}",
      "ClientCertificateRequired": false,
      "CheckCertificateRevocation": false
    }
  }
}
```

3. Exporta variables de entorno:

```bash
export TDS_PROXY_TLS_CERT_PATH=/Users/carlosleyvagarcia/workfolder/TDSQueue/certs/tds-proxy-dev.pfx
export TDS_PROXY_TLS_CERT_PASSWORD=devpassword
```

4. Verifica variables:

```bash
echo $TDS_PROXY_TLS_CERT_PATH
echo $TDS_PROXY_TLS_CERT_PASSWORD
```

5. Ejecuta en Development:

```bash
DOTNET_ENVIRONMENT=Development dotnet run --project /Users/carlosleyvagarcia/workfolder/TDSQueue/TDSQueue.Proxy.csproj
```

Errores típicos:

- `Environment variable 'TDS_PROXY_TLS_CERT_PATH' is not set`: falta exportar variables.
- `TLS negotiation required but Proxy:Tls:Enabled=false`: el servidor negoció cifrado y el proxy no tiene TLS habilitado.
- `Cannot determine the frame size...`: normalmente indica desalineación en upgrade TLS; revisar que estés usando la versión actual del proxy y que `Proxy:Tls` esté bien configurado.

Nota:

- El proxy ahora no hace TLS inmediato al aceptar socket: primero pasa por PreLogin TDS.
- Si PreLogin negocia cifrado y `Proxy.Tls.Enabled=false`, la sesión falla por configuración incompatible.
- `EnableMarsPassthrough` y `Proxy.Tls.Enabled` resuelven problemas distintos: MARS controla multiplexación TDS; TLS controla cifrado.
- Si quieres forzar uso de cola, pon `FallbackToPassthroughOnEncryptOff=false` y configura cliente/SQL para evitar `0x00/0x00` (normalmente `Encrypt=True`/TLS obligatorio).
- Modo estricto recomendado para forzar cola: `ForceQueueOnly=true`, `EnableMarsPassthrough=false`, `FallbackToPassthroughOnEncryptOff=false`.
- En `ForceQueueOnly=true` con TLS, `sqlcmd`/ADS pueden usar `Encrypt=True` y `TrustServerCertificate=True`; el certificado visible para cliente es el del SQL target (tráfico opaco en el proxy).

## Métricas

- `proxy.requests.enqueued`
- `proxy.requests.completed`
- `proxy.requests.failed`
- `proxy.worker.errors`
- `proxy.queue.publish.errors`
- `proxy.queue.consume.errors`
- `proxy.target.connect.retries`
- `proxy.request.latency.ms`
- `proxy.stage.latency.ms` (tag: `stage`)
- `proxy.queue.depth`
- `proxy.sessions.active`
- `proxy.pending.requests`
- `proxy.inflight.requests`
- `proxy.target.latency.ewma.ms`

## Health Checks

- `GET /health/live`: liveness (proceso activo).
- `GET /health/ready`: readiness (estado de cola + circuito hacia target).
- Puerto configurable: `Observability:HealthPort` (default `18080`).

Ejemplo:

```bash
curl http://localhost:18080/health/live
curl http://localhost:18080/health/ready
```

## Admin Runtime (seguro)

Requiere:

- `Observability:EnableAdminEndpoint = true`
- header `X-Admin-Token` con `Observability:AdminToken`
- Por seguridad, `Observability:AllowRemoteAdmin = false` por defecto (solo origen local).
- `Observability:ListenAddress` controla el bind del listener (`127.0.0.1` recomendado fuera de contenedores).

Endpoints:

- `GET /admin/state`: estado de limiter, tuning y métricas snapshot.
- `POST /admin/tuning`: actualiza umbrales de backpressure en caliente.
- `POST /admin/limiter`: actualiza límites de concurrencia del limiter en caliente.

Ejemplo:

```bash
curl -H "X-Admin-Token: dev-admin-token" http://localhost:18081/admin/state

curl -X POST \
  -H "Content-Type: application/json" \
  -H "X-Admin-Token: dev-admin-token" \
  -d '{"adaptiveBackpressureEnabled":true,"queueLowWatermark":80,"queueHighWatermark":300}' \
  http://localhost:18081/admin/tuning
```

## OpenTelemetry OTLP

Config:

- `Observability:EnableOtlpExporter`
- `Observability:OtlpEndpoint` (ej. `http://otel-collector:4317`)

Cuando está habilitado, exporta trazas y métricas del proxy al collector OTLP.

## Build / Run

```bash
dotnet build /Users/carlosleyvagarcia/workfolder/TDSQueue/TDSQueue.Proxy.csproj
dotnet run --project /Users/carlosleyvagarcia/workfolder/TDSQueue/TDSQueue.Proxy.csproj
```

Conecta el cliente SQL al proxy en `localhost,14330`.

## Build de producción

### Opción 1: framework-dependent (recomendada en servidores con .NET 10 runtime)

```bash
dotnet publish /Users/carlosleyvagarcia/workfolder/TDSQueue/TDSQueue.Proxy.csproj \
  -c Release \
  -o /Users/carlosleyvagarcia/workfolder/TDSQueue/publish/prod
```

Ejecución:

```bash
cd /Users/carlosleyvagarcia/workfolder/TDSQueue/publish/prod
DOTNET_ENVIRONMENT=Production \
TDS_PROXY_ADMIN_TOKEN=\"<admin-token>\" \
RABBITMQ_PASSWORD=\"<rabbit-pass>\" \
REDIS_PASSWORD=\"<redis-pass>\" \
dotnet TDSQueue.Proxy.dll
```

### Opción 2: self-contained (sin runtime preinstalado)

Ejemplo para Linux x64:

```bash
dotnet publish /Users/carlosleyvagarcia/workfolder/TDSQueue/TDSQueue.Proxy.csproj \
  -c Release \
  -r linux-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -o /Users/carlosleyvagarcia/workfolder/TDSQueue/publish/prod-linux-x64
```

## Docker

El repositorio incluye `Dockerfile` en:

- `/Users/carlosleyvagarcia/workfolder/TDSQueue/Dockerfile`

### Build de imagen

```bash
cd /Users/carlosleyvagarcia/workfolder/TDSQueue
docker build -t tdsqueue-proxy:latest .
```

### Run de contenedor (producción)

```bash
docker run --rm \
  --name tdsqueue-proxy \
  -p 14330:14330 \
  -p 18080:18080 \
  -e DOTNET_ENVIRONMENT=Production \
  -e TDS_PROXY_ADMIN_TOKEN=\"<admin-token>\" \
  -e RABBITMQ_PASSWORD=\"<rabbit-pass>\" \
  -e REDIS_PASSWORD=\"<redis-pass>\" \
  tdsqueue-proxy:latest
```

Notas Docker:

- Ajusta `TargetHost` y endpoints de broker/cache en `appsettings.Production.json`.
- Si prefieres no bakear config en imagen, monta archivo:
  - `-v /ruta/appsettings.Production.json:/app/appsettings.Production.json:ro`
- Health:
  - `curl http://localhost:18080/health/live`
  - `curl http://localhost:18080/health/ready`

### Docker + TLS (certificado en contenedor)

Si `Proxy:Tls:Enabled=true`, debes montar el `.pfx` dentro del contenedor y pasar las variables:

```bash
docker run --rm \
  --name tdsqueue-proxy \
  -p 14330:14330 \
  -p 18080:18080 \
  -e DOTNET_ENVIRONMENT=Production \
  -e TDS_PROXY_TLS_CERT_PATH=/app/certs/tds-proxy-dev.pfx \
  -e TDS_PROXY_TLS_CERT_PASSWORD=devpassword \
  -v /Users/carlosleyvagarcia/workfolder/TDSQueue/certs/tds-proxy-dev.pfx:/app/certs/tds-proxy-dev.pfx:ro \
  tdsqueue-proxy:latest
```

Requisito en config:

- `Proxy:Tls:Enabled=true`
- `Proxy:Tls:CertificatePath=${ENV:TDS_PROXY_TLS_CERT_PATH}`
- `Proxy:Tls:CertificatePassword=${ENV:TDS_PROXY_TLS_CERT_PASSWORD}`

### Docker Compose con Redis

Archivo:

- `docker-compose.redis.yml`

Levantar:

```bash
cp .env.example .env
# Edita .env con tus credenciales/hosts reales antes de levantar
docker compose -f docker-compose.redis.yml up -d --build
```

Parar:

```bash
docker compose -f docker-compose.redis.yml down
```

### Docker Compose con RabbitMQ

Archivo:

- `docker-compose.rabbitmq.yml`

Levantar:

```bash
docker compose -f docker-compose.rabbitmq.yml up -d --build
```

Parar:

```bash
docker compose -f docker-compose.rabbitmq.yml down
```

### Docker Compose con Channel (in-memory)

Archivo:

- `docker-compose.channels.yml`

Levantar:

```bash
docker compose -f docker-compose.channels.yml up -d --build
```

Parar:

```bash
docker compose -f docker-compose.channels.yml down
```

Notas Compose:

- Los compose leen variables desde `.env` (incluyendo host/puerto de SQL Server y credenciales).
- Para compartir configuración base sin secretos, usa `.env.example`.
- Cambia `PROXY_QUEUE_ENGINE` en `.env` para seleccionar motor: `Channel`, `Redis` o `RabbitMq`.
- Cambia `PROXY_TARGET_HOST`/`PROXY_TARGET_PORT` en `.env` si tu SQL está en otra red.
- Para perfil dev rápido con Redis, usa `REDIS_APPENDONLY=no` (menos durabilidad, menor latencia).
- Ajusta `PROXY_MAX_MESSAGE_BYTES` para limitar tamaño máximo de mensaje TDS y reducir riesgo de DoS por memoria.
- Controla exposición de endpoints con `OBS_LISTEN_ADDRESS` y `OBS_ALLOW_REMOTE_ADMIN`.
- En RabbitMQ puedes abrir UI en `http://localhost:${RABBITMQ_MANAGEMENT_PORT}`.
- Si habilitas TLS en el proxy, añade al servicio:
  - variables `TDS_PROXY_TLS_CERT_PATH`, `TDS_PROXY_TLS_CERT_PASSWORD`
  - volumen de solo lectura con el `.pfx` (ej. `/app/certs/tds-proxy-dev.pfx`).

### Tests de integración de motores de cola

- Proyecto: `tests/TDSQueue.Proxy.IntegrationTests/TDSQueue.Proxy.IntegrationTests.csproj`
- Compose de soporte (Redis + RabbitMQ): `tests/TDSQueue.Proxy.IntegrationTests/docker-compose.integration.yml`

Ejecución:

```bash
dotnet test tests/TDSQueue.Proxy.IntegrationTests/TDSQueue.Proxy.IntegrationTests.csproj
```

Cobertura incluida:

- `Channel`: enqueue -> read -> ack.
- `Redis`: enqueue -> read -> ack y `nack(requeue=true)` con redelivery.
- `RabbitMq`: enqueue -> read -> ack y `nack(requeue=true)` con redelivery.

## Benchmark y Chaos

Scripts incluidos:

- `scripts/bench/run-benchmarks.sh`
  - Compara `Channel`, `RabbitMq` y `Redis` ejecutando `SELECT 1`.
  - Requiere `sqlcmd` y `jq`.
- `scripts/chaos/run-chaos-soak.sh`
  - Ejecuta prueba soak consultando readiness bajo configuración de stress.

Ejemplo:

```bash
./scripts/bench/run-benchmarks.sh
./scripts/chaos/run-chaos-soak.sh
```

## Perfiles recomendados por entorno

- `appsettings.Development.json`
  - Perfil local para depurar comportamiento y métricas.
  - `QueueEngine=Channel`, menor concurrencia y logs en `Debug`.
- `appsettings.Stress.json`
  - Perfil para pruebas de carga (benchmark/saturation).
  - `QueueEngine=RabbitMq`, concurrencia y batches agresivos.
- `appsettings.Production.json`
  - Perfil balanceado para operación estable.
  - `QueueEngine=RabbitMq`, límites altos pero conservadores en latencia.

Activación:

```bash
# Development
DOTNET_ENVIRONMENT=Development dotnet run --project /Users/carlosleyvagarcia/workfolder/TDSQueue/TDSQueue.Proxy.csproj

# Stress
DOTNET_ENVIRONMENT=Stress dotnet run --project /Users/carlosleyvagarcia/workfolder/TDSQueue/TDSQueue.Proxy.csproj

# Production
DOTNET_ENVIRONMENT=Production dotnet run --project /Users/carlosleyvagarcia/workfolder/TDSQueue/TDSQueue.Proxy.csproj
```

Notas:

- Ajusta `TargetHost`, credenciales y `RabbitMq` antes de usar `Stress`/`Production`.
- Si usas Redis, cambia `QueueEngine=Redis` y configura `Proxy:Redis:*`.
- El archivo base `appsettings.json` sigue siendo fallback común y los perfiles lo sobrescriben por clave.

## Roadmap recomendado

### Fase 1 (Quick Wins, 1-2 días)

1. Health checks operativos y alertas básicas (implementado).
2. Circuit breaker y umbrales por entorno (implementado).
3. OpenTelemetry con métricas y trazas internas (implementado).

### Fase 2 (1 semana)

1. Exportación OTLP end-to-end a collector (implementado, configurable).
2. Endpoint admin seguro para tuning runtime (implementado).
3. Pruebas de carga comparativas por motor (`Channel` / `RabbitMq` / `Redis`) con script base (implementado).

### Fase 3 (Hardening, 2+ semanas)

1. TTL inteligente de pending + replay controlado ante fallos de broker (implementado).
2. Hardening de seguridad (TLS, secretos por env, policy de red por CIDR) implementado en el proxy.
3. Testing de resiliencia (fault injection y soak script base) implementado.
