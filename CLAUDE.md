# SmartGlue CLP Gateway (`SmartGlue.ClpGateway`)

Daemon .NET 10 que roda no PC da cabine e é o **único mestre do CLP**. Extraído do
`smt_glue` a partir do MR!14. É a caixa "CLP API" do diagrama de arquitetura.

Repositórios irmãos: `smartglue/backend` (`smt_glue`), `smartglue/desktop` (Avalonia).

## Comandos

| O que | Comando |
| --- | --- |
| Rodar em dev | `dotnet run --project SmartGlue.ClpGateway` (sobe em `:5180`) |
| Testes | `dotnet test clp-gateway.sln` — **não precisa de simulador nem de CLP** |
| Build | `dotnet build clp-gateway.sln` |
| Publicar | `./scripts/publish.ps1` |
| Instalar serviço | `./scripts/install-service.ps1 -BinaryPath <...>` (Admin) |
| Doc da API (Scalar) | `/api/scalar` (só em `Development`) |
| Health | `/api/health` — inclui socket Modbus, rotina em curso e resultados pendentes |
| Requests manuais | `SmartGlue.ClpGateway/clp-gateway.http` |

Tudo é servido sob `/api` (`app.UsePathBase("/api")`), igual ao `smt_glue`, para o desktop
só trocar host e porta.

## As três regras que não se negociam

**1. Buscar antes. Reportar depois. Nunca ler o backend entre dois pontos.**
A rotina inteira é carregada e validada **antes do primeiro pulso de coil**; resultados sobem
depois, por arquivo local. Se alguém puser uma chamada HTTP dentro de
`RoutineExecutor.ExecuteWaypointsAsync`, um soluço de rede até o datacenter passa a travar um
eixo em movimento. Ver `docs/PLANO-EXTRACAO.md` §3.3.

**2. STOP sempre passa.**
Não pega o lock de comando, não exige nível de perfil, não é recusado por haver rotina
rodando. Um stop que espera o movimento que ele deveria interromper não é um stop.
Se você adicionar `.RequireProfileLevels(...)` em `/axis/stop`, está errado.

**3. Um processo, um socket, um mestre.**
O simulador e o CLP licenciam **um** escritor. Nunca abra uma segunda conexão Modbus, nem
aqui nem em outro processo da mesma célula. Foi exatamente isso que originou este
repositório.

## Arquitetura em uma tela

```
Api/          endpoints Carter (Axis, Application, Camera, Routines, Trace) + docs + validators
Routines/     executor de rotina e de ensaio a seco, modelos, fila local de resultados
Services/     StationController (o ciclo de comando, uma vez, apontado para as duas estações),
              AxisControlService (inspeção), ApplicationAxisService (aplicação), limites
Modbus/       IAxisConnection, ModbusAxisConnection (o socket), TracingAxisConnection (o rastro
              da camada de transporte), heartbeat, mapas de endereço das duas estações
Trace/        JSONL append-only em três camadas + leitor e pacote de evidência (SGF-144)
Vision/       CameraService — as câmeras, por HTTP; o segundo (e último) fio até a máquina
Backend/      BackendClient — a ÚNICA saída HTTP deste processo
Common/       cópia enxuta do transversal do smt_glue (retornos, filtros, exceções, claims)
```

**Não existe camada de dados.** Nenhum `DbContext`, nenhum broker, nenhuma migration. Se uma
mudança começar a pedir banco aqui, ela provavelmente pertence ao `smt_glue`.

Todo serviço é **singleton**: uma máquina, um socket, sem unidade de trabalho por requisição.

## Contratos externos

- **Desktop → gateway:** REST em `127.0.0.1:5180`, JWT emitido pelo `smt_glue`.
- **Gateway → câmera:** HTTP no servidor de visão (`Vision:BaseUrl`, `127.0.0.1:8080` do simulador
  hoje). `GET /camera/snapshot` devolve o quadro **junto com** o centro X/Y em que ele foi tirado:
  separar as duas coisas em chamadas distintas faria um quadro capturado durante um movimento ser
  emparelhado com uma posição de outro instante, e o clique do usuário viraria um alvo errado.
- **Gateway → backend:** só `GET /programs/{id}/inspection-points`, repassando o token do
  chamador. O gateway **não** tem identidade de serviço.
- **Backend → gateway:** não existe, e é isso que mantém este processo em loopback. Se
  aparecer, o PC da cabine vira servidor exposto na rede da fábrica.

## Configuração

`JwtSettings:Secret` é a **mesma chave simétrica** com que o `smt_glue` assina. Nunca vai para
o repositório: User Secrets em dev, `appsettings.Local.json` no PC da cabine (ambos no
`.gitignore`). `appsettings.Development.json` traz só a chave de desenvolvimento, que já é
pública no repositório do backend.

## Armadilhas conhecidas

- **A geometria do quadro (`Vision:FieldWidthPulses`/`FieldHeightPulses`) é configuração, não
  medição.** Espelha o bloco `visual.inspection_fov` do simulador; na câmera real vem da
  calibração de lente/sensor. Se ela divergir do que a câmera realmente enquadra, o clique na
  imagem converte para o milímetro errado — e a tela não tem como perceber.
- **`PulsesPerMillimeter` é placeholder** (1000). O kit não define a escala pulso↔mm; ela
  depende de calibração mecânica que ainda não existe. Numa rotina o erro multiplica por N
  pontos. Não feche história de precisão fingindo que está resolvido.
- Bancada: só o simulador, por `simulador/01_Simulador_Maquina_CLP/EXECUTAR_EXE.bat`
  (`SmartGlue_Machine_Mock.exe` 0.2.0, contrato v2) — **não** o `INICIAR_DEMO.bat`, que abre a
  app de controle com `--auto-connect` e toma a licença de escritor. O 0.1.0 não serve mais: o
  gateway o identifica como incompatível e recusa os comandos.
- **Contrato v2 (kit 2.0.0).** Watchdog de **1 000 ms**, heartbeat a cada 200 ms. Limites da
  aplicação em **D23060..D23120**, na ordem do ladder (Y mín, X mín, Z máx, Y máx, X máx, Z mín);
  D21600..D21850 agora são as **velocidades de JOG com sinal** — o gateway as lê para conferir o
  sinal e nunca as escreve. O `ContractV2Tests` confere cada endereço contra a cópia do
  `contrato_publico_clp.v2.json`.
- **O HOME termina com `MOVE_DONE`** e o gateway faz o ACK, como em qualquer movimento, com prazo
  próprio (`HomeTimeoutSeconds`, 90 s). O ladder baseline, sem a camada SG, não tem DONE no HOME:
  para ele existe `HomeCompletion: "Referenced"`. Qual vale na máquina real ainda não foi confirmado.
- **Alvo fora da faixa satura no limite**, não dá mais 1002. A validação contra os limites, antes do
  pulso, é a única barreira; `reachedTarget: false` na resposta diz que um movimento terminou longe
  do alvo.
- **Recusas sem FAULT** (1001, 1004, 1005, 1008, 1010, 1012) respondem 409 na hora — o poller olha a
  mudança do código de erro, não só FAULT e DONE.
- **Identidade:** na abertura de sessão o heartbeat lê build (D19984, `202607281`) e contrato
  (D24508, `2`). Máquina incompatível → comandos recusados com 409 e health `Unhealthy`; **STOP
  continua passando**.
- **Inspeção exige o Z referenciado** (M5015) para mover, embora o gateway nunca comande esse Z
  (`InspectionRequiresReferencedZ`; desligar só para uma máquina com `z_axis_mode=absent`).
- **Câmera:** o centro vem do `X-Pose-Pulses` do próprio quadro quando ele vier (simulador); sem o
  cabeçalho, de uma leitura Modbus. O centro é a **câmera**, não a agulha — converter clique em alvo
  da agulha é do serviço de calibração.
- **1011 `NEEDLE_COLLISION`** só existe no simulador (agulha além de Z = 5,1 mm): `fault-reset` e
  recuar Z.
- Depois de um STOP a máquina fica em `1006 STOPPED`: é preciso `POST /axis/fault-reset`
  (ou `/application/fault-reset`) antes de comandar de novo.
- Rotinas vivem em memória e somem no restart, de propósito. O backend é a verdade sobre o
  que um programa é; a máquina é a verdade sobre onde o eixo está.
- **O heartbeat é um contador só para a máquina inteira** (`D19980`): quando ele expira, o
  `1009 HEARTBEAT_LOST` trava **as duas** estações. Limpar só a da inspeção deixava a
  aplicadora travada para sempre — verificado contra o simulador, os dois painéis acusando a
  mesma falha. `AxisHeartbeatService` limpa as duas na abertura de sessão.
- **Pressão, delay e ângulo não existem no CLP.** O contrato v2 tem um setpoint de pressão
  (D24030) e a dispensa (M4016/M4017), mas são `RESERVED`: só o simulador implementa, o hardware
  da válvula não está definido e o ladder não os tem. São receita que o `smt_glue` guarda; este
  processo nunca os recebe. Um endpoint daqui que aceitasse "pressão" estaria mentindo para a
  máquina real.
- **JOG é nível mantido, não pulso.** Quem segura o bit é o operador, do outro lado de uma
  chamada HTTP — por isso `SetJogAsync` tem deadman: uma janela fechada não pode deixar um
  eixo andando até o limite de software.

## Fonte da verdade do protocolo

`smartglue/simulador/` — kit **2.0.0**, contrato CLP **v2**: `contracts/contrato_publico_clp.v2.json`,
`01_Simulador_Maquina_CLP/codigo_fonte/docs/MODBUS_ADDRESS_MAP.md` e `ERROR_CATALOG.md`,
`docs/PENDENCIAS.md` (o que é só do simulador e o que não foi validado), `01_MANUAL_COMUNICACAO_CLP.pdf`.
Em qualquer divergência, o kit ganha deste repositório — e avise sobre a divergência. Um kit
novo começa trocando a cópia do JSON em `SmartGlue.ClpGateway.Tests/Contract/` e o SHA no
`ContractV2Tests`, e vendo o que a suíte diz que mudou.

O kit 1.0.0 (`SmartGlue_Kit_Desenvolvedores_1.0.0/`, contrato v1) está obsoleto para este
repositório.
