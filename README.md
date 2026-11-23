# SmartGlue CLP Gateway

**Daemon de célula do Smart Glue.** Processo único que roda no PC da cabine e é o **único
mestre do CLP**: dono do socket Modbus TCP, do heartbeat e do ciclo de comando. Expõe REST
em loopback para o desktop Avalonia. É a caixa **"CLP API"** do diagrama de arquitetura.

Extraído do `smt_glue` (backend), a partir do MR!14 — ver `docs/PLANO-EXTRACAO.md`.

| | |
| --- | --- |
| Remoto | `https://gitlab.conecthus.org.br/factory/foxconn/smart-glue/v1/clp-gateway.git` |
| Repositórios irmãos | `smartglue/backend` (`smt_glue`), `smartglue/desktop`, `smartglue/frontend` |
| Runtime | .NET 10, `Microsoft.NET.Sdk.Web` + `Microsoft.Extensions.Hosting.WindowsServices` |
| Escuta | `http://127.0.0.1:5180`, path base `/api` |
| Fala com | Simulador `SmartGlue_Machine_Mock` 0.2.0 **ou** CLP Delta AS332T — Modbus TCP `:5020`, contrato CLP **v2** (kit 2.0.0) |
| Serviço | Windows Service `smartglue-clp-gateway` |
| Base arquitetural | SGF-140 ADR-01-R, revisada pela ADR-01-R2 (ver plano §11) |

## O que este processo é — e o que ele não é

**É:** heartbeat, ciclo de comando (verificar → escrever parâmetros → pulsar → poll → ack),
STOP, limites do eixo, tradução mm ↔ pulsos, reconexão — e a execução de uma rotina inteira
de pontos de inspeção.

**Não é:** banco de dados, fila de mensagens, cadastro, emissão de token, BIP/MES. Nada aqui
abre conexão com SQL Server ou RabbitMQ.

## A regra que rege este processo

> **Buscar antes. Reportar depois. Nunca ler o backend entre dois pontos.**

O gateway é **cliente** do backend — busca os pontos de uma rotina antes de executar e devolve
resultados depois. O backend **nunca** chama o gateway, e é por isso que este processo escuta
apenas em `127.0.0.1`.

A rotina inteira é carregada em memória **antes do primeiro pulso de coil**; resultados sobem
de forma assíncrona por uma fila local que sobrevive ao backend fora do ar. Entre o ponto *n*
e o ponto *n+1* não existe chamada HTTP. Violar isso põe a rede do datacenter dentro do laço
de controle de um eixo em movimento — ver `docs/PLANO-EXTRACAO.md` §3.3.

## Estado

**Fases F0–F6, F8 e F9 implementadas** (2026-09-03), e a **SGF-141** em 2026-09-15: estação de aplicação, ensaio a seco e rastro/evidência. O código do MR!14 foi movido do
`smt_glue`, e a ele se somaram STOP, cliente do backend, executor de rotinas, fila local de
resultados e o primeiro projeto de testes da suíte Smart Glue.

```
dotnet test clp-gateway.sln     # 100 testes, verdes, sem simulador e sem CLP
dotnet run --project SmartGlue.ClpGateway
```

Em ambiente `Development` (incluindo o perfil padrão do `launchSettings.json`), chamadas
originadas do próprio computador usam uma identidade local de teste e não precisam de JWT.
Esse modo só é registrado em `Development` e só autentica conexões loopback; nos demais
ambientes, JWT emitido pelo `smt_glue` continua obrigatório.

Nada foi versionado nem publicado — ver `docs/PLANO-EXTRACAO.md` §13.

**Contrato v2 (kit 2.0.0) em 2026-10-16**, exercitado contra o `SmartGlue_Machine_Mock 0.2.0`:
identidade lida (build `202607281`, contrato `2`), limites reais da aplicação (±100 mm X/Y,
±50 mm Z), HOME concluído pelo `MOVE_DONE` (20 s a partir de X = Y = 100 mm), movimentos X/Y/Z,
alvo fora da faixa recusado antes do pulso, JOG nos dois sentidos, colisão da agulha (1011) e
recuperação, câmera centrada pelo `X-Pose-Pulses`, e o 1009 das duas estações limpo ao reabrir
a sessão. Spec e o que ficou de fora: `smartglue/specs/planos/GATEWAY-contrato-v2-simulador-0.2.0.md`.
**Nada disso foi validado em CLP real.**

**Exercitado contra o simulador em 2026-09-15** (`SmartGlue_Machine_Mock 0.1.0`, contrato v1): estação de
aplicação referenciada e movida em X/Y e em Z, JOG segurando e soltando o eixo, câmera por
estação, e um ensaio a seco de 4 pontos percorrido inteiro com rastro e `.zip` de evidência
baixado ao fim.

Um defeito real apareceu nessa bancada e está corrigido: o heartbeat é **um contador para a
máquina inteira**, então o `1009 HEARTBEAT_LOST` trava as duas estações — e o gateway só
limpava a da inspeção, deixando a aplicadora travada para sempre.

**Falta:** o caminho com o backend de pé de ponta a ponta (a migration da configuração de
aplicação ainda não foi aplicada em banco nenhum), e a evidência da SGF-144 com o desktop
dirigindo a tela em vez de `curl`.

## Documentação

- `docs/PLANO-EXTRACAO.md` — plano da task: escopo, fases, decisões abertas, riscos
- `smartglue/documentos/SGF-140-arquitetura-gateway-agnostico.md` — a arquitetura
- `smartglue/simulador/` (kit 2.0.0) — contrato público do CLP v2 (fonte da verdade do protocolo);
  cópia conferida por SHA em `SmartGlue.ClpGateway.Tests/Contract/`
