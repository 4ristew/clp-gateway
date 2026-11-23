# CHANGELOG

Formato herdado do `smt_glue` (ver `smt_glue/Documentation/CHANGELOG_pattern.md`).

## [Unreleased]

### Contrato CLP v2 (kit 2.0.0)

#### Changed

- **Contrato CLP v2 (kit 2.0.0, simulador `0.2.0`).** O kit trocou o contrato de forma incompatível para espelhar o ladder real, e o gateway escrito contra a 1.0.0 funcionava pela metade. Spec: `smartglue/specs/planos/GATEWAY-contrato-v2-simulador-0.2.0.md`.
  - **Limites da aplicação em D23060..D23120**, na ordem do ladder (Y mín, X mín, Z máx, Y máx, X máx, Z mín). O gateway lia os limites em D21600..D21850, que no v2 são as velocidades de JOG com sinal: validava cada movimento e cada rotina contra ±(velocidade de JOG).
  - **Watchdog de 1 000 ms** (`TMR T450 K10`): heartbeat a cada **200 ms** (era 500 ms para uma janela de 2 000 ms). Timeout explícito de 400 ms por transação Modbus, porque uma transação pendurada segura o socket e, com ele, o heartbeat. Alerta no log quando o intervalo entre escritas passa de metade da janela.
  - **HOME concluído pelo `MOVE_DONE`**, com ACK, como qualquer movimento — no v2 o HOME tem duração e termina com DONE. Prazo próprio (`HomeTimeoutSeconds`, 90 s): X → Y → Z em sequência a partir do fim do curso leva os 30 s inteiros do prazo de movimento. `HomeCompletion: "Referenced"` fica como alternativa para um ladder sem a camada SG, que não tem DONE no HOME; nesse modo o HOME só conclui depois de ter sido visto começar.
  - **JOG da aplicação** deixa de escrever `SPEED_XY` antes do bit — o DJOG usa a frequência retida em D21600..D21750 — e passa a **recusar** (409) um sentido cuja velocidade retida é zero ou tem o sinal trocado: no CLP real, o sinal decide o sentido.
  - **Câmera** centrada pelo `X-Pose-Pulses` do próprio quadro, quando ele vier (a pose do instante exato da imagem, em vez de uma leitura Modbus feita antes); sem o cabeçalho, como antes.
  - **Inspeção exige o Z referenciado (M5015)** antes de mover, sem expor esse eixo: com `z_axis_mode=simulated` a máquina recusa X/Y com 1001 até o Z estar referenciado, e um HOME interrompido depois do Y deixava exatamente esse estado (`InspectionRequiresReferencedZ`).

#### Added

- **Identidade da máquina** na abertura de sessão: build (D19984, `202607281`) e versão do contrato (D24508, `2`). Máquina incompatível → movimentos, HOME e JOG recusados com 409 e health `Unhealthy`; **STOP continua passando**. O build passa a ir para o rastro (`SetBuildCode` existia e nunca era chamado; o pacote de evidência saía com build 0). O eco do heartbeat (D19982) é conferido, e um eco parado deixa o health `Degraded`.
- **`reachedTarget`** nas respostas de status e no resultado de cada ponto da rotina: no v2 um alvo fora da faixa satura no limite e o movimento termina com DONE; o registro `saturated` vai para o rastro. A validação contra os limites antes do pulso continua sendo a barreira.
- **Catálogo de erros v2:** 1007, 1008, 1010, 1011 (`NEEDLE_COLLISION`, só simulador, com a instrução de recuar Z) e 1012.
- **`ContractV2Tests`**: cópia do `contrato_publico_clp.v2.json` conferida por SHA e cada endereço das duas estações, do watchdog e da identidade comparado com ela. **`ContractV2BehaviourTests`**: um teste por comportamento que o v2 mudou. 62 → 100 testes.

#### Fixed

- **Recusas sem FAULT viravam timeout de 30 s.** 1001, 1004, 1005, 1008, 1010 e 1012 não levantam FAULT; o poller só olhava FAULT e DONE e respondia "estado indeterminado" depois do prazo inteiro. Agora reconhece a recusa pela mudança do código de erro (ou, quando o mesmo código já estava lá, por ele continuar sem BUSY nem DONE depois de `RefusalGraceMs`) e responde 409 na hora.
- **Exceção Modbus 03 na escrita do pedido** (bloco de parâmetros incompleto) virava 502 "falha de comunicação". Agora é 409 dizendo que é defeito do gateway.

#### Tests changed (comportamento v1 → v2, corrigidos e não apagados)

- `Home_completes_through_the_referencing_bits_and_never_waits_for_move_done` → `Home_completes_on_move_done_and_acknowledges_it`.
- `Jog_writes_the_speed_before_holding_the_bit` → `Jog_holds_the_bit_and_writes_no_speed`.
- `Capture_refuses_a_frame_it_cannot_locate_on_the_board`: o quadro agora é buscado antes (a pose pode vir com ele), então o servidor é chamado em cada tentativa — e a captura continua recusada.
- `FakeMachine` imita o simulador 0.2.0: HOME com BUSY e DONE, saturação, recusas sem FAULT, identidade, velocidades de JOG com sinal.

### Added

- [SGF-163] **Câmera.** `GET /camera/snapshot` devolve o quadro atual do campo de visão da estação de inspeção em base64 **junto com a geometria que o localiza na placa**: o centro X/Y (mm, lido por Modbus no momento da captura) e o tamanho que o quadro cobre. É essa geometria que permite à tela de engenharia converter um clique na imagem em um alvo X/Y em milímetros — sem ela um JPEG não diz *onde* foi tirado.

  A imagem e a posição vêm na mesma resposta de propósito. Fossem duas chamadas, um quadro capturado durante uma movimentação acabaria emparelhado com uma posição lida um instante depois, e o clique do usuário viraria um alvo no lugar errado. Pela mesma razão a captura é recusada quando a posição não pôde ser lida: um quadro centrado em 0,0 fictício é pior do que quadro nenhum.

  O quadro vem do servidor HTTP de visão configurado na seção `Vision` — o do simulador do kit hoje (`http://127.0.0.1:8080/{estação}/snapshot.jpg`), o da câmera real depois. Uma captura que falha é repetida `Vision:FetchRetryAttempts` vezes, `Vision:FetchRetryDelaySeconds` entre elas (3 × 5 s por padrão, a mesma política da reconexão Modbus), antes de responder 502.
- **Repositório novo.** `SmartGlue.ClpGateway` — daemon que roda no PC da cabine e é o único mestre do CLP. Extraído do `smt_glue` a partir do MR!14 (`feat/sgf-162`). Escuta em `http://127.0.0.1:5180`, path base `/api`, sem porta na LAN.
- **Movido do `smt_glue` sem reescrita** (o código do MR!14 já estava provado contra o simulador): `ModbusAxisConnection` (socket único, `_ioGate` por transação e `_commandGate` por ciclo de comando, reconexão 3×/5 s só em falha de conectividade), `AxisHeartbeatService`, `AxisControlService` (handshake de 6 passos, ACK obrigatório de `MOVE_DONE`, HOME concluindo pelos bits de referenciamento), `AxisHardwareOptions`, `InspectionStationAddresses` e as rotas `/axis/status|move|zero|limits|fault-reset`. Junto vieram ~150 linhas de infraestrutura transversal (`CustomResults`, `ValidationFilter`, `ProfileLevels`, `IRequesterContext`, `GlobalExceptionHandler`), copiadas para o gateway não depender do `smt_glue`.
- **`POST /axis/stop` — a capacidade que faltava.** O MR!14 declarava `InspectionStationAddresses.Stop = 5003` e nada escrevia nesse endereço: não havia `StopAsync` nem rota. Era tolerável enquanto todo comando era curto e um operador esperava a resposta na tela; deixou de ser no momento em que o gateway passou a executar rotinas sozinho. STOP não pega o lock de comando (um stop que espera o movimento que ele deveria interromper não é um stop), não tem restrição de perfil além de estar autenticado, e nunca é recusado por haver rotina rodando.
- **Executor de rotinas.** `POST /routines/run` aceita `programId` (o gateway busca os pontos no backend repassando o token do chamador) ou `points` inline (contingência, funciona com o backend fora do ar), responde **202 + `runId`**, e `GET /routines/{runId}` acompanha o progresso. Uma execução por vez; comando avulso em `/axis/move` e `/axis/zero` é recusado com 409 enquanto uma rotina roda — **menos STOP**.
- **Validação da rotina inteira antes do primeiro pulso.** Todos os pontos são conferidos contra os limites lidos ao vivo do CLP; se qualquer um estiver fora de faixa a rotina é recusada por completo e nada se move. Ir até o ponto 1 e só então descobrir que o ponto 7 é inalcançável deixa a máquina onde ninguém pediu.
- **Abertura de sessão no heartbeat.** Máquina que ficou sem mestre chega travada em `1009 HEARTBEAT_LOST` e não sai sozinha. O gateway limpa **essa** falha, uma vez por sessão, e só ela — qualquer outro código de erro fica intacto para o operador ver e decidir.
- **Documentação da REST em Scalar**, em `/api/scalar` (só em `Development`), com o documento OpenAPI 3.1 em `/api/openapi/v1.json`. Todas as 10 operações descritas com summary e descrição longa, os envelopes `SuccessResponseOf*` nos esquemas, e os grupos `Axis` e `Routines` com o contrato explicado por tag. Traz o **esquema de segurança Bearer** por document transformer, no mesmo formato do `smt_glue`: sem ele a página renderizaria uma API em que nada pode ser chamado, já que toda rota exige token.
- **`ResultQueue`** — resultados de execução em JSONL local (`logs/runs/`), gravados **depois** que a rotina termina. O envio ao backend está deliberadamente ausente: não existe endpoint que receba uma execução ainda. Até existir, o rastro fica em disco em vez de se perder.
- **Projeto de testes — o primeiro da suíte Smart Glue.** `SmartGlue.ClpGateway.Tests`, 26 testes, **verdes sem simulador e sem CLP**, sobre um `FakeMachine` que implementa `IAxisConnection` imitando o simulador do kit. Cobre a ordem de escrita do ciclo de comando, o ACK do `MOVE_DONE`, os caminhos de BUSY/FAULT/não-referenciado/fora-de-limite, o timeout indeterminado que nunca re-pulsa, HOME concluindo pelos bits de referenciamento, o STOP atravessando um comando em voo, e as regras da rotina (ordem, recusa em bloco, home automático, parada, execução única, uma leitura só do backend).

### Changed

- [SGF-278] **`POST /routines/dry-run`: `delayMs` virou `preDispenseMs` + `postDispenseMs`.** Os dois vêm no request (padrão 0) e, opcionalmente, em cada ponto da trajetória (os da unidade que começa nele). Ponto: o bico espera pré + pós. Linha: cada vértice espera o pós do trecho que termina mais o pré do trecho que começa. Negativos dão 400. O `summary.json` grava `preDispenseMs`, `postDispenseMs` e o `dwellMs` de cada ponto; o relatório de evidência troca "Dwell" por "Pre-dispense"/"Post-dispense" e lê resumos antigos (`delayMs`) como pré. Com dispensa de verdade (história da Operação), os mesmos tempos vão para D24032 (`+pré`) e D24034 (`−pós`). **Quebra de contrato com o desktop**: os MRs entram juntos.

- [SGF-212] `AxisHardware:CommandPollIntervalMs` cai de **200 para 50 ms**, no `appsettings.json` e no padrão de `AxisHardwareOptions`. Esse intervalo é o tempo que o `MOVE_DONE` fica ligado esperando o ACK do gateway, e com ele quase toda a latência de um movimento curto: um passo de 0,1 mm das setas do teclado levava ~210 ms, dos quais ~5 ms de movimento. No simulador, o mesmo intervalo aparecia a cada toque como "aguardando limpeza de MOVE_DONE pelo mestre". O custo são mais leituras Modbus **só enquanto há comando em voo**. Conferir com o CLP real na bancada.
- `AxisLimitsProvider` guarda o cache de fallback **em memória** em vez da tabela `AxisLimits` — o gateway não tem banco. A mesma janela de confiança (`LimitsCacheMaxAgeSeconds`) continua valendo. Perder o cache num restart não é regressão: o gateway remonta o que sabe da máquina lendo a máquina, nunca um cache que sobreviveu ao processo.
- `IAxisConnection` extraído de `ModbusAxisConnection`. O MR!14 dependia da classe concreta, o que tornava o ciclo de comando testável só com bancada — inaceitável para código que agora move o eixo sem ninguém olhando.
- `AxisHardware:Enabled` passa a ter default **`true`**. No backend era `false` para ninguém precisar do simulador no ar; aqui o processo *é* a integração, e um gateway que silenciosamente não fala com o CLP é um gateway que falha em silêncio. O health check reporta `Unhealthy` quando o flag está desligado ou o socket está caído.
- Tudo é singleton. Os tempos de vida `Scoped` do backend existiam para escopar um `DbContext`; sem banco, só reconstruiriam os mesmos objetos a cada requisição — e teriam tornado o cache de limites inútil.

### Fixed

- [SGF-187] **`POST /axis/move` referencia a máquina sozinho na primeira movimentação.** Máquina recém-ligada não está referenciada e o handshake do CLP recusa qualquer alvo absoluto até o HOME; o gateway devolvia `400 "The axis has not been homed yet"` e o desktop, que só distingue "moveu" de "não moveu", traduzia isso no modal de falha de comunicação — nomeando a causa errada logo na primeira ação do operador. Agora, se `IsReferencedAsync` diz que não, o HOME sai antes do movimento, exatamente como o `RoutineExecutor` já fazia antes de percorrer os pontos.

  O referenciamento continua acontecendo porque **alguém pediu para mover**, e não ao abrir a sessão Modbus: o daemon sobe junto com o PC da cabine, e uma máquina que se move sozinha no boot é assunto de segurança, não de conveniência. A exigência `requireReferenced: true` do ciclo de comando fica de pé — se o HOME não acender os bits, mover assim mesmo seria confiar num referenciamento que a máquina não confirmou.


- [SGF-163] **Enums nas respostas saíam como ordinal, não como texto.** Faltava o
  `ConfigureHttpJsonOptions` com `JsonStringEnumConverter` que o `smt_glue` tem: `GET
  /axis/status` devolvia `"status": 0` em vez de `"status": "Ready"`. O desktop lê os dois
  servidores com o mesmo conversor, então caía no ramo "status desconhecido" e pintava **"Falha
  de comunicação" com a máquina conectada e respondendo** — o estado que a tela de configuração
  de inspeção existe para mostrar corretamente. Nada disso quebrava em compilação (são
  processos separados) nem nos testes de então; apareceu ao subir a arquitetura inteira
  localmente. Coberto agora por `JsonContractTests`, que serializa cada `AxisStatus` com as
  opções reais do host e exige o nome que o desktop faz o parse.

### Security

- `Microsoft.OpenApi` fixado em 2.7.5, acima do 2.0.0 transitivo com GHSA-v5pm-xwqc-g5wc (alta). O `smt_glue` ainda carrega o 2.0.0.
- **Limitação conhecida:** o backend revoga sessão por RabbitMQ para o próprio cache em memória. O gateway não consome esse feed (não tem broker, por desenho), então um token revogado continua válido aqui até expirar — no máximo `AccessTokenExpirationMinutes`. Aceitável enquanto o processo escuta só em loopback, na mesma máquina do operador. Deixa de ser no dia em que escutar na LAN.
