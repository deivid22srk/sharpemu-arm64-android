<!-- Copyright (C) 2026 SharpEmu Emulator Project -->
<!-- SPDX-License-Identifier: GPL-2.0-or-later -->

# Decisão de Arquitetura — Estratégia de Execução da CPU no Port Android/ARM64

- **Status:** Aceita e implementada (Fase 1)
- **Data:** 2026-09-15
- **Decisores:** Port SharpEmu ARM64 (fork não oficial)
- **Escopo:** Backend de execução da CPU convidada (x86-64 do PS5) em hosts Android ARM64

---

## 1. Contexto

O SharpEmu emula o PS5, cuja CPU convidada é **x86-64** (AMD Zen 2, 8 núcleos). O alvo deste fork é
**Android ARM64**, onde o código convidado não pode ser executado nativamente — toda instrução
x86-64 precisa ser interpretada ou traduzida para ARM64.

### 1.1 Estado atual (evidências do código)

A implementação atual no caminho Android é um **interpretador instrução-a-instrução** com um
**cache de decodificação por instrução** (não por bloco):

- `src/SharpEmu.Core/Cpu/CpuDispatcher.cs` (`DispatchEntryCore`) — seleciona
  `CpuExecutionEngine.Interpreter` no Android; o caminho `NativeOnly`
  (`DirectExecutionBackend`) executa o código convidado **nativamente** e é
  explicitamente proibido em Android/ARM64 (`PlatformNotSupportedException`).
- `src/SharpEmu.Core/Cpu/Interpreter/X64InterpreterBackend.cs` — loop principal
  (`Execute`, linhas 97–267). Para **cada instrução executada** ele:
  1. testa o dicionário de stubs de importação (`TryGetValue` por RIP);
  2. consulta o `_decodeCache` direto-mapeado de 65.536 entradas
     (hash multiplicativo, comparação de tag, **releitura dos bytes convidados**
     para guarda de SMC, comparação de bytes);
  3. executa um `switch` gigante por mnemônico (`TryExecuteInstruction`);
  4. avança RIP.
- A decodificação Iced (`Iced.Intel.Decoder`) é, por decomposição do loop, o **custo
  dominante** — o comentário do próprio `_decodeCache` (linhas 31–45) registra que o cache
  existe para evitar re-decodificar "o mesmo endereço milhões de vezes".
- **Não existe** infraestrutura de recompilação para ARM64: `JitStubs.cs`/`StubManager.cs`
  emitem apenas trampolinos **x86-64** escritos à mão para o caminho nativo de desktop;
  não há IR de CPU, emissor ARM64, nem cache de blocos traduzidos em lugar nenhum do repositório.
- O README do projeto já antecipa que o backend ARM64 definitivo virá do port **rpPS4**
  (baseado em shadPS4) — hoje este fork está pausado como "proof of concept" que faz
  *Dreaming Sarah* apenas dar boot.

### 1.2 Restrições do alvo Android

- **W^X (Android 10+, targetSdk ≥ 29):** uma página não pode ser RWX; um futuro cache de
  código exigirá esquema write→`mprotect(PROT_EXEC)`→flush de icache. A API
  `HostMemory.FlushInstructionCache` já existe, mas o caminho RWX atual do desktop não é
  reutilizável diretamente no Android.
- **Espaço de endereçamento de 39 bits** em dispositivos comuns (confirmado no Galaxy S23,
  ver comentário em `CpuDispatcher.cs`) — já tratado no fork.
- O app Android roda o interpretador sob **Mono JIT** (`RunAOTCompilation=false`,
  `AndroidEnableMarshalMethods=false` — ver `SharpEmu.Android.csproj`), que é menos agressivo
  que o RyuJIT desktop: custo por instrução interpretada é ainda maior no Android.

---

## 2. Opções consideradas

| Opção | Ganho esperado | Complexidade | Risco de instabilidade | Manutenção |
|---|---|---|---|---|
| **A.** Interpretador atual, sem mudanças | — (baseline) | — | — | Trivial |
| **B.** Interpretador otimizado com **cache de blocos básicos pré-decodificados** ("cached/block interpreter") | Alto (elimina re-decodificação, hash, probe de dicionário e guarda de SMC por instrução → 1× por bloco) | Moderada (~400 linhas, sem mudança semântica) | **Baixo** (mesmos handlers de execução; semântica idêntica por construção) | Baixa |
| **C.** Threaded interpretation (despacho direto a handlers, estilo computed goto) | Médio | Alta em C# puro (sem `goto computed`; exigiria gerar tabela de delegates ou codegen IL) | Médio | Média |
| **D.** JIT recompiler x86-64→ARM64 (dynarec completo) | Muito alto (10–50× sobre interpretador) | **Extrema** (≈200 mnemônicos, flags parciais lazy, SMC, W^X, sinais/exceções, trampolinos HLE) | **Alto** sem anos de testes | Alta |
| **E.** Reaproveitar um dynarec externo pronto (**box64**) como backend de execução | Alto em CPU crua (dynarec maduro, ~70–85% do nativo em código não-exótico) | **Inviável na integração** com a arquitetura HLE deste projeto (ver §2.3) | Inviável | — |

### 2.1 Por que não o recompilador JIT agora (opção D)

Um dynarec x86-64→ARM64 é o **objetivo correto de longo prazo** — é o que shadPS4/RPCS3/Dolphin
usam e o que o README deste fork planeja reusar do rpPS4. Porém, nesta fase:

1. **Escopo:** o interpretador atual suporta ≈200 mnemônicos (ALU completo, SSE/AVX scalar,
   BMI1/BMI2, ABM). Um emissor ARM64 correto exige levantamento de flags por instrução,
   emulação de EFLAGS lazy, tratamento de cada modo de endereçamento, e um cache de código
   com invalidação SMC correta sob W^X.
2. **Risco:** um dynarec parcial entregaria *corrupção silenciosa de estado* — a classe de bug
   mais cara que existe em emulação. O valor de estabilidade do projeto seria destruído.
3. **Viabilidade:** o backend planejado deve vir do rpPS4 ( decisão já registrada no README);
   reimplementá-lo do zero aqui duplicaria esforço com qualidade inferior.
4. **Evidência de maturidade:** o jogo-alvo (*Dreaming Sarah*) nem completa o boot ainda;
   o gargalo imediato é *conseguir executar mais rápido e com diagnósticos fiéis*, não pico
   absoluto de desempenho.

### 2.2 Por que não threaded interpretation (opção C)

Em C/C++ o threaded dispatch (computed goto) reduz o custo do `switch`. Em C# gerenciado,
o `switch` do JIT já compila para jump table; o ganho restante é pequeno comparado ao custo
de decodificação/validação que a opção B elimina, e a implementação (tabelas de
delegates por opcode → indireção de vtable em Mono) pode até *piorar* o desempenho no
Mono do Android.

### 2.3 Por que não box64 (opção E)

O [box64](https://github.com/ptitSeb/box64) (MIT) é um dynarec x86_64→ARM64 maduro — e a
ideia de "não escrever um recompilador, reaproveitar um pronto" é atraente à primeira vista.
A rejeição é **arquitetural, não de qualidade**: o box64 é um *emulador de processo inteiro*
("Linux Userspace x86_64 Emulator") — ele carrega o ELF x86-64, monta seu próprio espaço de
endereçamento, instala seus tratadores de sinais, provê TLS e um modelo de threads próprios.
O SharpEmu precisa do controle exatamente oposto:

1. **Dono do processo invertido:** aqui o emulador possui o processo e dirige cada thread
   convidada individualmente (contextos de CPU por thread, agendamento pelo kernel emulado,
   falhas de página roteadas para a GPU/memória, SMC via `MappingGeneration`, stubs HLE
   interceptados por RIP, debugger e anel de diagnósticos). Embutir o box64 exigiria que ele
   fosse o dono do espaço de endereçamento — conflito direto com o rastreador de memória
   gerenciado que sustenta HLE, SMC e faults.
2. **O convidado não é um ELF Linux:** um jogo de PS5 é um ELF Orbis com módulos `.sprx`,
   TLS SCE e syscalls do kernel Orbis. Todo o HLE (VideoOut, AGC, AJM/ACM, GNM, kevents)
   vive em C# integrado à memória gerenciada. Com box64, cada serviço teria que virar uma
   "wrapped library" nativa em C e o loader Orbis teria que ser reimplementado dentro do
   loader do box64 — meses de cola nativa para descartar o core C# (a parte que funciona).
3. **Android packaging:** box64 no Android exige um userspace glibc (proot/Termux ou ponte
   nativa estilo Winlator) — dezenas de MB, fragilidade por device, e um segundo runtime
   convivendo com o app .NET.
4. **Evidência empírica:** o modelo box64 encaixa de verdade na arquitetura do
   Kyty/KytyPS5 — emulador x86-64 que executa o código convidado *nativamente* (por isso
   precisa de Rosetta 2 no Apple Silicon) — e lá o box64 é a única forma de chegar ao
   ARM64: ele traduz o próprio emulador **e** o jogo. É exatamente o modelo do
   KytyPS5-Android, cujo estado ("nenhum jogo funcionando ainda") ilustra o custo dessa
   forma: tradução em duas camadas + ambiente glibc-on-bionic + emulador upstream
   early-stage, tudo acumulado antes do primeiro frame. Ferramenta certa, arquitetura
   errada para este projeto.
5. **O que se aproveita mesmo assim:** o box64 permanece referência de design para as
   Fases 3–5 (detecção de hot blocks, heurísticas de SMC por página, modos strong/weak
   memory, estratégias de block linking).

---

## 3. Decisão

> **Implementar a opção B — interpretador com cache de blocos básicos pré-decodificados
> ("cached interpreter") — como o modo de execução padrão em todos os hosts (desktop e
> Android), estruturando o cache de blocos como fundação direta do futuro JIT recompiler
> (opção D).**

### 3.1 O que foi implementado

- **`X64BlockCache.cs`** (`src/SharpEmu.Core/Cpu/Interpreter/`): cache de blocos básicos
  por thread (o backend é instanciado por thread convidada — sem necessidade de locks).
  Um bloco é uma sequência reta de instruções terminada por instrução de controle de fluxo
  (`FlowControl != Next`), por limite de tamanho, ou por endereçar um stub de importação.
- **Loop de execução** (`X64InterpreterBackend.Execute`): caminho rápido por bloco —
  1 consulta de dicionário de stubs e 1 validação de SMC **por bloco** em vez de por
  instrução; o caminho legado instrução-a-instrução permanece intacto como fallback
  universal (falha de decodificação, bloco não construído, etc.), garantindo paridade
  semântica total para todos os casos de borda.
- **Validação SMC (self-modifying code) em três camadas, com paridade total com o caminho
  legado:**
  1. *Entre execuções de bloco:* blocos em regiões não-graváveis confiam na
     `MappingGeneration` (invalidação conservadora global); blocos em regiões graváveis
     revalidam o intervalo de bytes completo a cada despacho. Um bloco que cruza regiões
     usa a política mais conservadora (validação por bytes).
  2. *SMC intra-bloco (a janela que um cache pré-decodificado abriria):* toda instrução
     do bloco classificada como escrita em memória (via operand-access do Iced — incluindo
     `ReadCondWrite`, a forma de memória do cmpxchg — mais push/call/enter/pushfq)
     dispara uma revalidação do intervalo do bloco **antes da próxima instrução cacheada
     executar**; se os bytes mudaram, o bloco é descartado e a próxima instrução é
     decodificada fresca pelo caminho legado — exatamente o que o caminho
     instrução-a-instrução faria. Isso fecha a janela que pre-decodificação abriria
     (coberta pelos testes de self-patch via `mov` store e via `cmpxchg`).
     Blocos confiáveis (região não-gravável + geração estável) pulam a revalidação: nenhum
     store convidado alcança suas páginas.
  3. *SMC entre threads:* idêntico ao legado em semântica de corrida — a revalidação por
     instrução do caminho legado também não oferece sincronização (sem fences/atomics);
     o cache de blocos apenas amplia a granularidade da mesma corrida inerente, que é
     indefinida no próprio programa convidado.
- **Cache limitado:** o dicionário de blocos tem teto de 32.768 entradas (varredura
  completa ao atingir o teto — reconstruir é barato e o código quente re-cacheia no
  despacho seguinte), eliminando crescimento sem limite em sessões que recarregam código.
- **Zero alocação na validação:** intervalos curtos usam `stackalloc`; intervalos longos
  reutilizam um buffer scratch por backend (o backend é por thread convidada). Blocos que
  cruzam duas regiões mapeadas (cuja leitura contígua é rejeitada pela memória) caem para
  validação por instrução, que fica dentro de uma região.
- **Invariante de 2 páginas:** blocos têm no máximo 960 bytes (< 2 páginas), o que torna
  a checagem de gravabilidade nas duas pontas do bloco suficiente para cobrir todas as
  páginas que ele toca — invariante travada por teste.
- **Sessões múltiplas no mesmo processo (Android):** os latches one-shot de shutdown do
  VideoOut são re-armados por sessão (`VideoOutExports.PrepareNewSession()`, chamado no
  início de cada `GameSession.RunOnCurrentThread`), e a thread de vblank se re-arma
  simetricamente ao parar — a segunda sessão do app tem shutdown cooperativo completo.
- **Opção de runtime:** `CpuExecutionOptions.InterpreterBlockCacheDisabled` (padrão:
  **cache ativado** — nome invertido de propósito para que `default(...)` nunca desligue
  o cache silenciosamente), propagada por `CpuDispatcher` →
  `X64InterpreterOptions.DisableBlockCache`, com flag CLI `--cpu-no-block-cache` para A/B
  e diagnóstico.
- **Preservação estrita de semântica observável:** contagem de instruções (`MaxInstructions`
  e `TotalInstructions`), conteúdo do trace, anel de instruções recentes (`PushRecent`),
  diagnósticos de falha (`MemoryFault`/`Trap`/`NotImplemented` com o RIP e bytes da
  instrução exata), e a ordem stub→bloco→execução são idênticos ao caminho legado.
- **Opção de runtime:** `CpuExecutionOptions.InterpreterBlockCacheEnabled` (padrão:
  **ativado**), propagada por `CpuDispatcher` → `X64InterpreterOptions.EnableBlockCache`,
  com flag CLI `--cpu-no-block-cache` para A/B e diagnóstico.

### 3.2 Por que esta é a melhor estratégia a longo prazo

1. **Desempenho mensurável e honesto:** o custo por *dispatch* cai de "validação de
   decodificação por instrução" para "uma validação de intervalo por bloco". O benchmark
   da suíte (`BlockCache_ThroughputBenchmark`, mediana de 5 rodadas aquecidas e
   alternadas, com faixa de rodadas impressa) mediu medianas de **~1,0× a ~2,2×** entre
   processos/máquinas no formato representativo (corpo straight-line de 10 instruções por
   bloco) — e ~paridade em micro-loops de 3 instruções, onde o cache de decodificação por
   instrução do caminho legado já acerta e o custo domina nos handlers (idênticos nos dois
   caminhos). É um sinal de smoke, não uma especificação: a variância entre processos é
   maior que a intra-processo, e o número absoluto depende de hardware/JIT. Os ganhos
   maiores são estruturais e aparecem onde o cache legado é fraco: conjuntos quentes
   grandes (o cache legado é direto-mapeado com 65.536 entradas — colisões forçam
   re-decodificação Iced que o cache de blocos, indexado por dicionário exato, elimina),
   eliminação da sonda de dicionário de stubs por instrução, e o Mono JIT do Android —
   onde o custo por instrução de qualquer trabalho de dispatch é maior que no RyuJIT
   desktop.
2. **Estabilidade:** a execução continua usando **os mesmos handlers** já validados pelos
   ~2.100 linhas de testes existentes do interpretador; não há tradução de código — apenas
   *reuso* de decodificação. Paridade de comportamento é testada (testes de paridade
   cache ligado/desligado).
3. **Fundação do recompilador:** o cache de blocos é, estruturalmente, o esqueleto do
   futuro cache de tradução: mesma chave (RIP + geração de mapeamento), mesma política de
   invalidação SMC, mesmos pontos de entrada/saída de blocos. A Fase 2 (IR leve por bloco)
   e a Fase 3 (emissor ARM64 + W^X flip + `FlushInstructionCache`) plugam **no nível do
   bloco**, substituindo "executar instruções interpretadas do bloco" por "executar código
   ARM64 do bloco" — sem redesenho.
4. **Manutenção:** um único caminho de semântica (handlers compartilhados entre o modo
   legado e o modo cacheado) elimina a classe inteira de divergências "funciona no modo X,
   quebra no modo Y".

### 3.3 Roadmap para o JIT recompiler (opção D)

| Fase | Escopo | Dependência |
|---|---|---|
| **2** | IR leve por bloco (operadores já normalizados a partir das instruções Iced) | Este cache de blocos |
| **3** | Emissor ARM64 do IR + cache de código RWX→RX (W^X flip) + `FlushInstructionCache` | Fase 2; referência: dynarec do rpPS4/shadPS4 |
| **4** | Linking direto de blocos (patch de saídas de blocos para entradas diretas), flags lazy | Fase 3 |
| **5** | Trampolinos HLE nativos (substituir retorno ao host em cada stub por salto direto) | Fase 3 |

O caminho legado (interpretador instrução-a-instrução) permanece como fallback permanente
e como oráculo de referência para testes de paridade do recompilador.

---

## 4. Consequências

**Positivas**
- Ganho de desempenho imediato em Android (Mono JIT) e desktop, sem risco de corrupção de
  estado convidado.
- Diagnósticos e testes existentes continuam válidos; novos testes de paridade travam o
  comportamento.
- Caminho de migração claro e incremental para o dynarec ARM64.

**Negativas / custos**
- Memória adicional por thread para os blocos (proporcional ao conjunto de trabalho quente;
  blocos em regiões não-graváveis têm vida longa, idêntica ao cache de decodificação atual).
- Dois caminhos de execução no mesmo arquivo (rápido por bloco + legado por instrução),
  mitigado por testes de paridade obrigatórios em cada mudança.

## 5. Referências

- `src/SharpEmu.Core/Cpu/Interpreter/X64InterpreterBackend.cs` — loop original e cache por instrução
- `src/SharpEmu.Core/Cpu/Interpreter/X64BlockCache.cs` — cache de blocos (implementação desta decisão)
- `src/SharpEmu.Core/Cpu/CpuDispatcher.cs` — seleção de backend e guarda Android
- `src/SharpEmu.HLE/ICpuMemory.cs` — contrato `MappingGeneration`/`TryIsRegionNonWritable` (guarda SMC)
- `platform/android` e `src/SharpEmu.Android` — host Android (SDL3 + .NET Android)
