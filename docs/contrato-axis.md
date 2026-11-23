# Contrato `/axis/*` — congelado antes da extração

Registro do que o MR!14 servia em `smt_glue` sob `/api/axis/*`, para que a mudança de processo
possa ser conferida resposta a resposta. **Nenhum campo mudou na extração** — só o host e a
porta.

Origem: `smt_glue` commit `3534800` (`feat/sgf-162`, MR!14).
Destino: `SmartGlue.ClpGateway`, `http://127.0.0.1:5180/api`.

## Envelope

Idêntico ao do backend — o desktop desserializa os dois com o mesmo código:

```json
{ "statusCode": 200, "message": "Axis status retrieved successfully!", "data": { } }
```

Erros saem como `application/problem+json` (`GlobalExceptionHandler`), com o mesmo mapa de
status: 400 `ArgumentException`, 403 `ForbiddenException`, 404 `KeyNotFoundException`,
409 `ConflictException`, 502 `IntegrationException`.

## Rotas herdadas — inalteradas

| Rota | `data` | Perfis |
| --- | --- | --- |
| `GET /axis/status` | `{ status, positionX, positionY }` | Full, High, Medium |
| `POST /axis/move` | `{ status, positionX, positionY }` | Full, High, Medium |
| `POST /axis/zero` | `{ status, positionX, positionY }` | Full, High, Medium |
| `GET /axis/limits` | `{ xMin, xMax, yMin, yMax }` | Full, High, Medium |
| `POST /axis/fault-reset` | `{ status, positionX, positionY }` | Full, High, Medium |

`status` ∈ `Ready | Moving | CommunicationFailure | Fault`. Posições em **mm**, `decimal`.
`POST /axis/move` recebe `{ "positionX": <decimal>, "positionY": <decimal> }`.

### Diferença de comportamento (não de contrato)

`POST /axis/move` e `POST /axis/zero` passam a responder **409** quando uma rotina está em
execução. Nenhum campo novo; um código de status a mais numa condição que antes não existia,
porque antes não havia rotinas.

`GET /axis/limits` deixou de ter a tabela `AxisLimits` como fallback e passou a usar cache em
memória — mesma janela (`LimitsCacheMaxAgeSeconds`), mesmo 502 quando envelhece. O formato da
resposta é o mesmo.

## Rotas novas

| Rota | O que faz |
| --- | --- |
| `POST /axis/stop` | Pulsa STOP e cancela a rotina em curso. **Sem restrição de perfil.** |
| `POST /routines/run` | 202 + `runId`. Corpo: `{ programId }` ou `{ points: [...] }` |
| `GET /routines/{runId}` | Progresso e resultado por ponto |
| `GET /routines?take=25` | Execuções recentes (memória, no máximo 50) |
| `POST /routines/{runId}/stop` | Cancela a rotina e pulsa STOP. Sem restrição de perfil |

## Consumidores no momento da extração

Nenhum. Varredura em `smartglue/desktop` e `smartglue/frontend` não encontrou uma única
chamada a `/api/axis/*` — a tela de ajuste do eixo ainda não existe. Foi por isso que mover
essas rotas custou uma linha de configuração em vez de uma migração coordenada.
