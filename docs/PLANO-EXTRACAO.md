# Plano — extrair a comunicação com o CLP para um daemon

**Origem:** MR `factory/foxconn/smart-glue/v1/backend!14` (`feat/sgf-162`, merge `3534800`,
31/08/2026) — *"integra eixo de inspeção via Modbus TCP com o simulador CLP"*.
**Repositório novo:** `smartglue/clp-gateway` → `SmartGlue.ClpGateway`.
**Branch de trabalho no backend:** `feat/deamon`.
**Revisão:** 2026-09-03 (4ª versão — diagrama atualizado: o gateway virou cliente do backend
e passa a executar rotinas por conta própria).

---

## 1. Ponto de partida

`feat/deamon` está limpo, em `3534800` = `origin/dev`. O código de origem é o do MR!14. O
engine da SGF-141 segue fora de escopo, preservado em `stash@{0}` e em
`smartglue/.backup-sgf-141/` (§11).

| Ativo | Onde | Tamanho |
| --- | --- | --- |
| **`Features/Axis/`** — Modbus + REST do eixo de inspeção | ✅ no branch | 907 linhas / 15 arquivos |
| **`Features/InspectionPoints/`** + entidades + migration | ✅ no branch | 330 linhas |

⚠️ **Esta revisão muda a natureza da task.** Nas versões anteriores era lift-and-shift. Com as
duas decisões da §3, o gateway ganha responsabilidades que o MR!14 não tem: um cliente HTTP
para o backend e um executor de rotinas. Passa a ser *mover + construir*.

---

## 2. O nome

**`clp-gateway`** (pasta) / **`SmartGlue.ClpGateway`** (assembly e namespace raiz) /
**`smartglue-clp-gateway`** (serviço Windows).

Vem do diagrama: a caixa dentro do PC da cabine se chama **"CLP API"** — "CLP" é o
vocabulário do time. Troco "API" por "gateway" porque o processo é dono do socket, do
heartbeat e da licença de escrita, e agora também executa rotinas: "API" sugeriria camada de
roteamento. "Gateway" já é a palavra do SGF-140 (*"gateway de hardware agnóstico"*).

Convenções: pasta irmã de `backend/`, `desktop/`, `frontend/`; solução `clp-gateway.sln`;
projeto `SmartGlue.ClpGateway/`; porta `5180`; path base `/api`, então `/api/axis/*`
mantém a forma exata das rotas de hoje.

---

## 3. O que o diagrama decide

```
┌─ MSI (PC da cabine) ─────────────────────┐     ┌─ HW CLP ──────────┐
│  Desktop ─────► CLP API ─────────────────┼─────┤  Camera    Cola   │
│     │             │  └──► Simulador HD   │     └───────────────────┘
│     │             │                      │
└─────┼─────────────┼──────────────────────┘
      │             │  (gateway é cliente)
┌─ Backend ─────────▼──────────────────────┐
│  Rest ──── DB                            │
└──────────────────────────────────────────┘
```

### 3.1 O gateway é cliente do backend — e só nesse sentido

A linha nova é **gateway → backend**. O backend nunca chama o gateway, e por isso o gateway
**continua escutando só em `127.0.0.1`**: nenhuma porta na LAN, nenhum TLS, nenhuma
identidade de serviço a receber. A decisão 2 da ADR-01-R sobrevive intacta.

Isso é uma propriedade de segurança, não um detalhe: no momento em que o backend precisasse
chamar o gateway, o PC da cabine viraria um servidor exposto na rede da fábrica.

### 3.2 O gateway executa a rotina inteira

`POST /api/routines/run { programId }` → o gateway busca os pontos no backend, executa a
sequência e reporta progresso. O desktop deixa de conduzir ponto a ponto.

### 3.3 🔴 A regra que essas duas decisões tornam obrigatória

> **Buscar antes. Reportar depois. Nunca ler o backend entre dois pontos.**

A ADR-01-R decisão 5 e o SGF-140 §10 dizem que *nenhum comando ao CLP espera o banco*. Com o
gateway virando cliente do backend, essa regra deixa de ser automática e passa a depender de
disciplina de implementação. Na prática:

- a rotina **inteira** é carregada em memória **antes do primeiro pulso** de coil;
- resultados sobem **depois**, de forma assíncrona, por uma fila local que sobrevive ao
  backend fora do ar (é a *"resultados retidos localmente"* da ADR-01-R decisão 5);
- entre o ponto *n* e o ponto *n+1* **não existe chamada HTTP ao backend**.

Se essa regra for violada, um soluço de rede até `172.100.11.112` trava um eixo em
movimento. É o risco §14 do Plano de Trabalho, e ele volta a existir exatamente por causa da
seta nova.

### 3.4 Consequências menores, mas que já mudam o desenho da API

- O gateway deixa de ser sem estado: guarda a rotina carregada e o progresso da execução
  **em memória, por execução**, descartados no fim. Continua sem banco. A fonte da verdade
  segue no backend.
- Como a execução é longa, `POST /routines/run` responde **202 + `runId`**; o progresso vem
  por `GET /routines/{runId}`. Manter a requisição HTTP aberta por uma rotina inteira não é
  opção.
- A câmera continua atrás do gateway (caixa HW CLP), e o MR!14 não entrega nada disso (§4.2).

---

## 4. O que o MR!14 entrega — e o que falta

### 4.1 O código é bom; o plano é movê-lo, não reescrevê-lo

Correção que já registrei na revisão anterior e que continua valendo: dois defeitos que eu
havia atribuído ao MR!14 **não existem**. `ErrorCode` é lido como uma word
(`ReadUInt16Async`); e HOME não espera `MOVE_DONE` — há um `PollForHomeCompletionAsync`
separado que conclui por `!BUSY && ReferencedX && ReferencedY`, com comentário explicando o
porquê.

Verificado e sólido: `_ioGate` (uma transação por vez) e `_commandGate` (um ciclo por vez);
reconexão 3×/5 s só em falha de conectividade, nunca em `ModbusException`; ACK obrigatório do
`MOVE_DONE`; recusa de re-pulsar comando com conclusão indeterminada; heartbeat em task
própria com contador que sempre muda de valor.

### 4.2 🔴 A lacuna que a decisão 3.2 transforma em bloqueio: **não existe STOP**

`InspectionStationAddresses.Stop = 5003` está declarado — e **nada no MR!14 escreve nesse
endereço**. Não há `StopAsync` em `IAxisControlService`, não há rota `/axis/stop`. Confirmei
com busca no diretório inteiro: a constante aparece uma vez, na própria declaração.

Isso era tolerável enquanto todo comando era curto, iniciado por um operador olhando a tela e
esperando a resposta. **Com rotina autônoma deixa de ser:** uma sequência de N pontos passa a
rodar sem que exista qualquer maneira de interrompê-la a não ser matando o processo — o que
derruba junto o heartbeat e faz a máquina inteira cair em `1009 HEARTBEAT_LOST`.

STOP tem de existir **antes** do executor de rotinas, não depois. É o que ordena as fases da
§9. E, seguindo a ADR-01-R decisão 3, STOP **nunca** exige lease nem nível de perfil elevado:
qualquer operador autenticado tem de conseguir parar a máquina.

### 4.3 As demais lacunas

| Lacuna | Impacto |
| --- | --- |
| **Só a estação `inspection`** | O diagrama tem "Cola" no HW CLP. Estação de aplicação, eixo Z e esteira não existem no MR!14 |
| **Nenhum código de câmera** | §3.4 põe a imagem atrás do gateway |
| **Sem simulação in-process** | Todo teste exige o `SmartGlue_Machine_Mock` no ar. Inviabiliza CI |
| **Heartbeat não lê o eco (`19982`) nem trata `1009 HEARTBEAT_LOST`** | Máquina que ficou sem mestre chega travada; o 1º comando devolve 409 pedindo `fault-reset`. Numa rotina autônoma isso vira uma rotina que falha no primeiro ponto — melhor uma abertura de sessão que renove o heartbeat e limpe 1009 uma vez |
| **Sem stream de eventos** | Progresso de rotina por *polling*. Aceitável na v1; é onde o `MachineEventHub` da SGF-141 passaria a valer muito (§11) |
| **Endereços fixos em `InspectionStationAddresses.cs`** | Decisão consciente e documentada. Trocar de máquina exige recompilar |
| `PulsesPerMillimeter = 1000`, `DefaultSpeedPulsesPerSecond = 20000` | **Placeholders** — o kit não define escala pulso↔mm. Comentado no próprio código. Numa rotina autônoma o erro de escala se multiplica por N pontos |

---

## 5. Acoplamento do código que vai

| Depende de | O que é | Como resolver |
| --- | --- | --- |
| `Common.Exceptions` (`IntegrationException`, `ConflictException`) | tipos de exceção | copiar (~30 linhas) |
| `Common.Returns` (`CustomResults`) | envelope de resposta | copiar (~40 linhas) |
| `Common.Interceptors` (`ValidationFilter`, `ProfileLevels`) | filtros | copiar (~50 linhas) |
| `Common.Authentication` (`IRequesterContext`) | perfil do requisitante, só *claims* — **não consulta banco** | copiar (~30 linhas) |
| `AppDbContext` | ⚠️ só em `AxisLimitsProvider` | ver §6 |

Fora o `AxisLimitsProvider`, nenhum acesso a banco e nenhuma fila. ~150 linhas de
infraestrutura transversal viram cópia.

---

## 6. Os limites do eixo — agora com uma saída melhor

Dois consumidores dos limites hoje: `AxisControlService.MoveToAsync` (vai para o gateway,
sem problema) e `InspectionPointService.EnsureWithinAxisLimitsAsync` (**fica no backend**, é
cadastro, e chama `IAxisLimitsProvider`).

A seta nova abre uma saída que não existia na revisão anterior — e ainda assim **não é a que
recomendo**, porque a validação boa acontece em outro lugar:

**Recomendado — validar a rotina inteira no carregamento.** Quando o gateway busca os pontos
(§3.3, antes do primeiro pulso), ele valida **todos** contra os limites lidos ao vivo do CLP e
recusa a rotina inteira, dizendo quais pontos estão fora. Isso é estritamente melhor que
validar no cadastro: pega o caso de o programa ter sido cadastrado numa máquina e executado
noutra, e falha **antes** de mover qualquer coisa em vez de no meio.

Somado a isso, duas medidas baratas:

- `InspectionPointService` **perde** `EnsureWithinAxisLimitsAsync` e a dependência de
  `IAxisLimitsProvider`. Validar cadastro contra uma linha em cache é adivinhação — é o
  próprio argumento que o autor do MR!14 escreveu no `catch` do `AxisLimitsProvider`;
- o **desktop** — que fala com os dois — consulta `GET /api/axis/limits` no gateway e avisa o
  operador na tela de cadastro. Feedback imediato, sem sincronização nenhuma.

Sobre publicar os limites para o backend (parte do "devolve resultados/limites depois"): fica
para quando o endpoint existir — ver a dependência da §7.4. Não é bloqueio, porque a
validação de verdade passou a ser no carregamento da rotina.

Sobre a tabela `AxisLimits`: fica **órfã**. Deixar de pé e só remover o caminho de escrita;
migration de drop numa história separada — o banco de dev é **compartilhado**
(`172.100.11.112,1435`).

👉 *§6 precisa de aval do PO/arquitetura antes da F4.*

---

## 7. Escopo

### 7.1 Vai para o gateway (movido do `smt_glue`)

| Origem | Destino | Linhas |
| --- | --- | --- |
| `Features/Axis/Modbus/**` | `SmartGlue.ClpGateway/Modbus/` | 361 |
| `Features/Axis/Services/` (`AxisControlService`, `AxisLimitsProvider`, interfaces) | `SmartGlue.ClpGateway/Services/` | 389 |
| `Features/Axis/{Endpoints,DTOs,Docs,Validators}/` | `SmartGlue.ClpGateway/Api/` | 157 |
| `Common/{Exceptions,Returns,Interceptors,Authentication}` — só o da §5 | `SmartGlue.ClpGateway/Common/` | ~150 |
| Seção `AxisHardware` de `appsettings.*.json` | `appsettings.json` do gateway | — |

`MockAxisLimitsProvider` **não vai**: existia para quem não tinha simulador. No gateway, o
processo *é* a integração — sem hardware ele deve falhar alto, não fingir.

### 7.2 Construído do zero no gateway

| Peça | Por quê | Estimativa |
| --- | --- | --- |
| **`StopAsync` + `POST /axis/stop`** | §4.2 — pré-requisito de segurança do executor | ~60 linhas |
| **Cliente HTTP do backend** (`IBackendClient`) — busca pontos, repassa o JWT do chamador | §3.1 | ~120 linhas |
| **Executor de rotina** — máquina de estados, um `runId`, um `run` por vez, cancelamento por STOP | §3.2 | ~350 linhas |
| **Store de execução em memória** + `GET /routines/{runId}` | §3.4 | ~100 linhas |
| **Fila local de resultados** (arquivo, sobrevive a backend fora do ar) | §3.3 | ~120 linhas |

Ou seja: ~750 linhas novas contra ~900 movidas. Vale dizer isso em voz alta no refinamento —
**esta task não é só uma extração.**

### 7.3 Fica no backend

`Features/InspectionPoints/**` (menos a validação de limites, §6); `InspectionPointEntity`;
`AxisLimitsEntity` (órfã); migration `20260825190718...` — **não reverter**; alteração em
`ProgramEntity`.

### 7.4 Depende de trabalho novo no backend

| O que o gateway precisa | Existe? |
| --- | --- |
| `GET /api/programs/{id}/inspection-points` — buscar os pontos da rotina | ✅ **já existe**, entregue pelo próprio MR!14 |
| endpoint para receber resultado de execução | ❌ **não existe** — história separada. Até lá, a fila local só acumula |
| endpoint para receber os limites lidos do CLP | ❌ **não existe** — e a §6 explica por que não é urgente |

O caminho de leitura está pronto; o de escrita não. Por isso a §9 corta a F5 em duas.

### 7.5 Sai do backend

`Features/Axis/` inteiro; `DependencyInjection.cs` linhas 15-16 e 155-171; `FluentModbus` do
`smt_glue.csproj:14`; seções `AxisHardware` de `appsettings.Development.json:44` e
`appsettings.Test.json:43`.

---

## 8. Autenticação

O gateway valida o JWT do desktop (`JwtSettings`, HS256) e **não emite token**. Para chamar o
backend, recomendo **repasse do token do chamador**: o JWT que chegou no `POST /routines/run`
vai como `Bearer` na busca dos pontos.

Por quê: não cria credencial de serviço nova, e o backend continua vendo o usuário real no
log de auditoria — quem mandou rodar a rotina. E como a busca acontece **no início** da
execução (§3.3), a validade de 60 min do token é folgada.

O que isso exige: o segredo simétrico `JwtSettings:Secret` no PC da cabine. Aceitável porque
o gateway escuta só em loopback; a ADR-01-R §5 já previa *"reaproveita a identidade da
suíte"*. Quando houver front web em LAN, migrar o backend para RS256 e o gateway valida com a
chave pública.

Continua **rejeitado**: o gateway validar cada requisição chamando o backend. Buscar dados uma
vez no início de uma execução é uma coisa; pôr a rede no caminho de cada requisição é outra.

Higiene: `appsettings.Development.json` do backend está versionado com senha de banco, segredo
de JWT, `ClientSecret` do BIP e senha de SMTP. O gateway não repete isso —
`appsettings.Local.json` no `.gitignore` e `UserSecretsId` no csproj desde o 1º commit.

---

## 9. Fases

Status da execução de 2026-09-03 na coluna da direita.

| # | Entrega | Situação |
| --- | --- | --- |
| **F0** | Repositório fundado: `.gitignore`, `README`, este plano | ✅ feito |
| **F1** | Esqueleto (`.sln`, `.csproj` net10.0, `Program.cs`, Carter, JWT, health) + `Common` copiado + `Features/Axis/**` movido com namespace `SmartGlue.ClpGateway` | ✅ **feito** — compila limpo. `dotnet run` sobe em `:5180`. ⏳ falta exercitar contra o `SmartGlue_Machine_Mock` |
| **F2** | 🔴 **Segurança primeiro:** `StopAsync` + `POST /axis/stop` (sem exigência de perfil), abertura de sessão que renova heartbeat e limpa `1009` uma vez. **Primeiro projeto de testes da suíte** | ✅ **feito** — 21 testes verdes **sem simulador no ar**, incl. STOP atravessando um comando em voo. ⏳ falta o STOP contra a máquina real |
| **F3** | Cliente HTTP do backend com repasse de token; busca de pontos por `programId` | ✅ **feito** — `BackendClient`. ⏳ falta exercitar contra o backend de pé |
| **F4** | Executor de rotina: 202 + `runId`, progresso, validação de **todos** os pontos antes do 1º pulso, uma execução por vez, comando avulso recusado **exceto STOP** | ✅ **feito** — coberto por 10 testes. ⏳ falta rodar no simulador |
| **F5a** | Fila local de resultados (arquivo) | ✅ **feito** — `ResultQueue`, JSONL em `logs/runs/` |
| **F5b** | Envio dos resultados ao backend | ⛔ **bloqueada** pela §7.4 — depende de endpoint que não existe |
| **F6** | Limpar o backend no `feat/deamon` (§7.5) | ✅ **feito** — `smt_glue` compila; `grep -ri modbus` só acha o comentário que explica a saída; `InspectionPointService` perdeu a validação de limites (§6) |
| **F7** | Desktop: 2º cliente HTTP para `:5180`; tela de ajuste do eixo; aviso de limites no cadastro | ⏸️ **não executada de propósito** — ver nota abaixo |
| **F8** | Empacotamento: publish, Windows Service, auto-restart, logs fora do diretório do binário | ✅ **feito** — `scripts/publish.ps1`, `install-service.ps1`, `uninstall-service.ps1`. ⏳ falta instalar num PC de verdade |
| **F9** | Documentação: **ADR-01-R2**, CHANGELOG dos dois repos, `CLAUDE.md`, contrato congelado | ✅ **feito** — falta só `smt_glue/Documentation/Architecture.md` (ver nota) |

### Por que a F7 não foi executada

`smartglue/desktop` está no branch `feat/SGF-141` com 11 arquivos modificados e 5 não
versionados (`Models/Machine/`, `Services/Machine/`, `TestRunViewModel`, `TestRunPanel`) — o
trabalho da SGF-141 que você deixou de lado. Mexer ali agora misturaria a extração do gateway
com código que está em suspenso, num repositório cujo estado você ainda não resolveu.

A F7 é barata quando for a hora: o desktop já separa a URL da camada de máquina em
`AppSettings`, e nenhum arquivo versionado dele chama `/api/axis/*`.

### `Architecture.md` também ficou de fora

`smt_glue/Documentation/Architecture.md` é declarado no `CLAUDE.md` do backend como fonte da
verdade do time e continua atribuindo `PLC Communication` ao **desktop** — errado desde a
ADR-01-R. Corrigi-lo é reescrever a visão de produto dos quatro módulos, o que é decisão sua e
do PO, não efeito colateral desta task. Está registrado como pendência na ADR-01-R2.

**A ordem F2 → F4 não é negociável.** Construir o executor de rotinas antes do STOP entrega
uma máquina que se move sozinha e que só para quando alguém mata o processo — o que derruba o
heartbeat e joga tudo em `1009`.

Dependências: F1 → F2 → F3 → F4 → F5a. F6 depende de F1. F7 depende de F4. F8/F9 a partir da
F4.

Roteiro depois, na ordem que o diagrama pede: estação de aplicação ("Cola"), canal de imagem
da câmera, transporte simulado in-process, stream de eventos.

---

## 10. Riscos

| Risco | Mitigação |
| --- | --- |
| 🔴 **Rotina autônoma sem STOP** | F2 antes da F4, sem exceção (§4.2) |
| 🔴 **Backend entrar no laço de controle** | a regra da §3.3, escrita no `CLAUDE.md`/README do gateway e verificada em revisão: nenhuma chamada HTTP entre dois pontos |
| Erro de escala pulso↔mm multiplicado por N pontos | `PulsesPerMillimeter` continua placeholder. Não fechar a F4 fingindo que está resolvido — precisa de PO/engenharia mecânica |
| Rotina em voo quando o serviço reinicia | fail-closed: sem retomada automática; execução vira indeterminada e o estado é relido do CLP (F8) |
| Token expira durante rotina longa | busca acontece só no início (§3.3); se ainda assim expirar, a rotina já está em memória e não depende mais do backend |
| Backend fora do ar no início da rotina | rotina não inicia — correto e fail-closed. Considerar aceitar `{ points: [...] }` inline como alternativa ao `programId`: caminho de contingência barato, e o desktop já tem os pontos em tela |
| Reescrever `/axis/*` sem consumidor nem teste | **nenhum consumidor de `/api/axis/*`** no desktop nem no frontend hoje — a janela para mover é agora. Congelar o contrato atual em `docs/contrato-axis.md` antes da F1 |
| Nenhum dos repositórios tem projeto de testes | repo novo, sem legado: `SmartGlue.ClpGateway.Tests` já na F2 |
| Pressão e ângulo X/Y/Z não têm endereço no contrato público | o eixo de inspeção é movimento, não receita de cola. Tem de estar escrito na história |
| `Documentation/Architecture.md` atribui `PLC Communication` ao **desktop** | errado desde a ADR-01-R; a F9 corrige |

---

## 11. E o engine da SGF-141?

Fora de escopo — mas **as decisões da §3 aumentaram bastante o valor dele**, e vale registrar
por honestidade: das cinco peças que a §7.2 manda construir do zero, o engine da SGF-141 já
tem três prontas e provadas contra o simulador — máquina de estados de comando com
cancelamento (`CommandCycle`), store de execução com `runId` e sumário (`DryRunPlanner`,
`GetRunSummary`) e canal de progresso (`MachineEventHub` + WebSocket, que resolve o *polling*
da §4.3). Também traz o transporte simulado in-process, que destravaria CI.

Não estou reabrindo a decisão — é sua. Só que ela agora tem um preço diferente do que tinha
quando a rotina autônoma não estava no desenho. Vale reavaliar antes da F4, que é onde a
duplicação apareceria.

Onde está: `stash@{0}` do `backend` (árvore `e50123a`, 30 arquivos, 4.886 linhas) e
`smartglue/.backup-sgf-141/`.
⚠️ Reflog de stash não é replicado e some depois de um `drop` + `gc`. Enquanto a decisão não
for tomada, **não descartar o stash nem rodar coleta de lixo** no `backend`.

---

## 12. O que este plano muda na ADR-01-R

A decisão **4** (*"Módulo dentro do projeto `smt_glue` — sem repositório nem projeto novo"*) é
contrariada e precisa virar **ADR-01-R2**. As decisões **1, 2, 3 e 5 continuam valendo** — e a
2 (loopback) só sobrevive porque a seta nova é unidirecional (§3.1).

Duas coisas que o diagrama acrescenta e que merecem entrar na R2:

1. **o gateway é cliente do backend, nunca servidor dele** — é o que mantém o PC da cabine
   fora da rede como servidor;
2. **o gateway executa rotinas** — com a regra da §3.3 anexada, porque sem ela a decisão 5
   ("nada no laço de controle espera o banco") deixa de se sustentar sozinha.

---

## 13. Publicação do repositório

Comandos de versionamento são executados pelo Orange (política do ambiente e
`.claude/rules/gitflow.md` do backend). O repositório foi montado no **padrão dos irmãos**,
conferido contra `smartglue/desktop`:

| Convenção | Onde vi | Como ficou aqui |
| --- | --- | --- |
| Layout achatado — solução na raiz, projeto e testes como pastas irmãs | `desktop/SmartGlue.Desktop.sln` + `SmartGlue.Desktop.Tests/`; `backend/backend.sln` + `smt_glue/` | `clp-gateway.sln`, `SmartGlue.ClpGateway/`, `SmartGlue.ClpGateway.Tests/` — sem `src/` nem `tests/` |
| `.gitignore` do template Visual Studio | `desktop/.gitignore`, 74 linhas | mesmo arquivo + bloco do gateway (`logs/`, `*.jsonl`, `appsettings.Local.json`, `artifacts/`) |
| Remoto em **HTTPS**, não SSH | `desktop/.git/config` | `https://gitlab.conecthus.org.br/factory/foxconn/smart-glue/v1/clp-gateway.git` — **confirmado pelo Orange em 2026-09-03** |
| Branch de integração é **`dev`**, não `main` | `origin/HEAD → refs/remotes/origin/dev` no desktop; tabela do `gitflow.md` | primeiro commit em `main`, `dev` sai dele, trabalho em `feature/SGF-XXX` |
| Commit `tipo[JIRA-KEY]: descrição` — **chave obrigatória** | `gitflow.md`, e o histórico dos dois repos | ver abaixo |
| README na raiz | desktop e backend | ✅ |

O desktop não tem `.gitattributes`, `.editorconfig` nem `.gitlab-ci.yml` — não inventei
nenhum. `CLAUDE.md` só existe no backend; mantive um aqui porque o repositório tem três
regras que não se descobrem lendo o código.

### Ordem

1. inicializar em `C:\orange\projects\smartglue\clp-gateway` na branch `main`;
2. primeiro commit com tudo — mensagem no padrão da casa:
   `chore[SGF-XXX]: funda o repositório do gateway do CLP`
   ⚠️ **substituir `SGF-XXX` pela chave real da história.** O `gitflow.md` trata a chave do
   Jira como obrigatória, e eu não tenho a desta task: o branch do backend (`feat/deamon`)
   também não traz uma;
3. criar `dev` a partir de `main`;
4. criar o projeto no GitLab em `factory/foxconn/smart-glue/v1/clp-gateway`;
5. apontar o remoto para
   `https://gitlab.conecthus.org.br/factory/foxconn/smart-glue/v1/clp-gateway.git`
   e publicar `main` **e** `dev`;
6. no GitLab, definir `dev` como branch padrão e proteger `main`/`dev`, como nos irmãos.

### Checklist do `gitflow.md`, conferido

- ✅ build passa (`dotnet build clp-gateway.sln`)
- ✅ 21 testes verdes (`dotnet test clp-gateway.sln`)
- ✅ CHANGELOG atualizado nos dois repositórios
- ✅ nenhum segredo versionado — `JwtSettings:Secret` só em User Secrets /
  `appsettings.Local.json`, ambos no `.gitignore`. O único valor no repo é a chave de
  desenvolvimento, que já é pública no `smt_glue`
- n/a migrations — este projeto não tem banco
- ⚠️ mensagem de commit depende da chave do Jira (item 2)
